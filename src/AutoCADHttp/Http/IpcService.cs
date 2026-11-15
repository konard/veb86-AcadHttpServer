using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace AutoCADHttp.Http
{
    /// <summary>
    /// The single process-wide IPC service: one incoming queue (filled by the HTTP server's <c>POST /ipc</c>),
    /// one <see cref="IpcDispatcher"/>, and one replaceable outgoing <see cref="IpcOutbox"/>.
    /// Registrations live as long as the service, independently of the HTTP server and the outgoing transport.
    /// No application (AutoCAD) API is used here.
    /// </summary>
    public sealed class IpcService : IIpcService, IDisposable
    {
        public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);

        private readonly ConcurrentQueue<IpcMessage> _incoming = new ConcurrentQueue<IpcMessage>();
        private readonly ConcurrentDictionary<string, PendingCommand> _pending =
            new ConcurrentDictionary<string, PendingCommand>(StringComparer.Ordinal);
        private readonly IpcDispatcher _dispatcher;
        private readonly Action<string> _log;
        private IpcOutbox _outbox;
        private int _disposed;

        public IpcService(Action<string> log = null)
        {
            _log = log;
            _dispatcher = new IpcDispatcher(_incoming, m => TrySend(m), log)
            {
                ResponseReceived = CompleteResponse
            };
        }

        /// <summary>Queue shared by every <see cref="ApiRouter"/> the HTTP server is (re)started with.</summary>
        public ConcurrentQueue<IpcMessage> Incoming { get { return _incoming; } }

        /// <summary>The single dispatcher (compatibility: <c>Register</c>, <c>MessageReceived</c>).</summary>
        public IpcDispatcher Dispatcher { get { return _dispatcher; } }

        public bool IsTransportRunning { get { return Volatile.Read(ref _outbox) != null; } }

        /// <summary>External <c>/ipc</c> of the running transport, or null.</summary>
        public Uri ExternalEndpoint
        {
            get
            {
                var outbox = Volatile.Read(ref _outbox);
                return outbox == null ? null : outbox.Endpoint;
            }
        }

        public int OutgoingQueued
        {
            get
            {
                var outbox = Volatile.Read(ref _outbox);
                return outbox == null ? 0 : outbox.PendingCount;
            }
        }

        /// <summary>Commands sent by <see cref="SendCommandAsync"/> that still await a response.</summary>
        public int PendingCommandCount { get { return _pending.Count; } }

        /// <summary>
        /// Uses <paramref name="outbox"/> (ownership is taken) for all outgoing messages. A previously running
        /// transport is stopped first, exactly like <see cref="StopTransport"/>.
        /// </summary>
        public void StartTransport(IpcOutbox outbox)
        {
            if (outbox == null)
                throw new ArgumentNullException("outbox");
            if (Volatile.Read(ref _disposed) != 0)
            {
                outbox.Dispose();
                throw new ObjectDisposedException("IpcService");
            }
            var previous = Interlocked.Exchange(ref _outbox, outbox);
            if (previous != null)
                Stop(previous, "IPC transport was replaced.");
        }

        /// <summary>
        /// Stops the outgoing transport without waiting for the network (queued messages are discarded) and fails
        /// every pending <see cref="SendCommandAsync"/> with <see cref="IpcTransportStoppedException"/>.
        /// Registrations and subscriptions are kept. The returned task completes when the outbox worker has ended;
        /// never wait for it on the AutoCAD main thread.
        /// </summary>
        public Task StopTransport(string reason = "IPC transport stopped.")
        {
            return Stop(Interlocked.Exchange(ref _outbox, null), reason);
        }

        /// <summary>Processes at most <paramref name="maxMessages"/> incoming messages in the caller's context.</summary>
        public int Drain(int maxMessages = 32)
        {
            return _dispatcher.Drain(maxMessages);
        }

        public IDisposable RegisterCommand(string module, string command, Func<IpcMessage, IpcMessage> handler)
        {
            ThrowIfDisposed();
            return _dispatcher.RegisterCommand(module, command, handler);
        }

        public IDisposable SubscribeEvent(string module, string eventName, Action<IpcMessage> handler)
        {
            ThrowIfDisposed();
            return _dispatcher.SubscribeEvent(module, eventName, handler);
        }

        public bool TrySend(IpcMessage message)
        {
            if (message == null)
                throw new ArgumentNullException("message");
            var outbox = Volatile.Read(ref _outbox);
            if (outbox != null && Volatile.Read(ref _disposed) == 0)
                return outbox.TryEnqueue(message); // Logs its own rejection (stopped or full).
            Log("IPC outgoing " + message.Id + " (" + Describe(message) + ") rejected: the external /ipc transport is not running " +
                "(set externalIpc in " + ServerSettings.FileName + " or ACADHTTP_EXTERNAL_IPC, then HTTPSTART).");
            return false;
        }

        public bool PublishEvent(string eventName, string parametersJson = "{}")
        {
            if (string.IsNullOrWhiteSpace(eventName))
                throw new ArgumentException("eventName must not be empty.", "eventName");
            return TrySend(IpcMessage.CreateEvent(NewId(), eventName, parametersJson ?? "{}"));
        }

        public Task<IpcMessage> SendCommandAsync(string command, string parametersJson = "{}", TimeSpan? timeout = null,
                                                 CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(command))
                throw new ArgumentException("command must not be empty.", "command");
            TimeSpan wait = timeout ?? DefaultCommandTimeout;
            // A finite timeout is mandatory: no request may wait for a response forever.
            if (wait <= TimeSpan.Zero || wait.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException("timeout", "timeout must be positive and finite.");
            IpcMessage message = IpcMessage.CreateCommand(NewId(), command, parametersJson ?? "{}");
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<IpcMessage>(cancellationToken);

            // Continuations never run inline in Drain (AutoCAD main thread) or on a timer/HTTP thread that completes the task.
            var pending = new PendingCommand(message.Id,
                new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously));
            // Register before queueing so a fast response, HTTPSTOP or Dispose always finds the request.
            _pending[message.Id] = pending;
            pending.Timer = new Timer(_ => Complete(pending, null, () => new TimeoutException(
                "IPC command " + command + " (" + message.Id + ") received no response within " +
                wait.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s.")), null, wait, Timeout.InfiniteTimeSpan);
            if (cancellationToken.CanBeCanceled)
                pending.Cancellation = cancellationToken.Register(() => Complete(pending, null, null, cancellationToken));
            if (pending.Completion.Task.IsCompleted)
                pending.Release(); // Completed (e.g. by StopTransport) before the timer/registration was stored.
            if (!TrySend(message))
                Complete(pending, null, () => new IpcTransportException(
                    "IPC command " + command + " (" + message.Id + ") was not queued: the outgoing transport is not running, stopped or full."));
            else if (Volatile.Read(ref _disposed) != 0)
                Complete(pending, null, () => new IpcTransportStoppedException("IPC service was disposed."));
            return pending.Completion.Task;
        }

        /// <summary>Process shutdown: stops the transport, fails pending commands, removes all registrations.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            StopTransport("IPC service was disposed.");
            _dispatcher.Clear();
            IpcMessage discarded;
            while (_incoming.TryDequeue(out discarded)) { }
        }

        private Task Stop(IpcOutbox outbox, string reason)
        {
            if (outbox != null)
                outbox.Dispose();
            int failed = 0;
            foreach (var pending in _pending.Values)
            {
                if (Complete(pending, null, () => new IpcTransportStoppedException(reason + " Command " + pending.Id + " got no response.")))
                    failed++;
            }
            if (failed > 0)
                Log(reason + " " + failed.ToString(CultureInfo.InvariantCulture) + " pending IPC command(s) failed.");
            return outbox == null ? Task.FromResult(0) : outbox.Completion;
        }

        // Called by the dispatcher in the Drain context.
        private void CompleteResponse(IpcMessage response)
        {
            PendingCommand pending;
            if (_pending.TryGetValue(response.Id, out pending))
                Complete(pending, response, null);
        }

        private bool Complete(PendingCommand pending, IpcMessage response, Func<Exception> error,
                              CancellationToken cancelled = default(CancellationToken))
        {
            PendingCommand removed;
            // Ids are fresh GUIDs, so whoever removes the entry first (response, timeout, cancellation or transport stop) wins.
            if (!_pending.TryRemove(pending.Id, out removed))
                return false;
            pending.Release();
            if (response != null)
                return pending.Completion.TrySetResult(response);
            if (error != null)
                return pending.Completion.TrySetException(error());
            return pending.Completion.TrySetCanceled(cancelled);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException("IpcService");
        }

        private static string Describe(IpcMessage message)
        {
            return message.Type == "command" ? "command " + message.Command :
                   message.Type == "event" ? "event " + message.Event : "response";
        }

        private static string NewId()
        {
            return Guid.NewGuid().ToString("D");
        }

        private void Log(string text)
        {
            try { if (_log != null) _log(text); } catch { }
        }

        private sealed class PendingCommand
        {
            public PendingCommand(string id, TaskCompletionSource<IpcMessage> completion)
            {
                Id = id;
                Completion = completion;
            }
            public string Id { get; private set; }
            public TaskCompletionSource<IpcMessage> Completion { get; private set; }
            public Timer Timer;
            public CancellationTokenRegistration Cancellation;

            public void Release()
            {
                try { if (Timer != null) Timer.Dispose(); } catch { }
                try { Cancellation.Dispose(); } catch { }
            }
        }
    }
}
