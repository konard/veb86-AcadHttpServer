using System;
using System.Threading;
using System.Threading.Tasks;

namespace AutoCADHttp.Http
{
    /// <summary>
    /// The public IPC service shared by all AutoCAD modules (one instance per process, see
    /// <c>HttpServerPlugin.Ipc</c>). Modules never create HTTP servers, HTTP clients or queues themselves:
    /// incoming messages arrive through the single HTTP server's <c>POST /ipc</c>, outgoing messages leave
    /// through the single <see cref="IpcOutbox"/>.
    /// </summary>
    /// <remarks>
    /// Command handlers and event subscribers run only inside <c>Drain</c> (AutoCAD: <c>Application.Idle</c>,
    /// main thread), never on HTTP threads. Running from Idle does not by itself make drawing access safe:
    /// handlers that touch a DWG still need the proper command context, <c>DocumentLock</c> and transactions.
    /// Registration does not depend on the HTTP server: it may happen before <c>HTTPSTART</c> and survives
    /// <c>HTTPSTOP</c>/<c>HTTPSTART</c>. Dispose the returned objects when the module is unloaded.
    /// </remarks>
    public interface IIpcService
    {
        /// <summary>
        /// Registers an incoming command for <paramref name="module"/>. Names are compared case-insensitively and must be
        /// unique across all modules: a taken name throws <see cref="InvalidOperationException"/> naming its owner.
        /// The handler returns the correlated response (<see cref="IpcMessage.CreateResponse"/> with the same id);
        /// an exception becomes <c>COMMAND_FAILED</c>. Dispose the result to remove exactly this registration.
        /// </summary>
        IDisposable RegisterCommand(string module, string command, Func<IpcMessage, IpcMessage> handler);

        /// <summary>
        /// Subscribes <paramref name="module"/> to incoming events named <paramref name="eventName"/> (case-insensitive,
        /// <c>"*"</c> = every event). All matching subscribers are called; events never produce automatic responses.
        /// </summary>
        IDisposable SubscribeEvent(string module, string eventName, Action<IpcMessage> handler);

        /// <summary>
        /// Queues any envelope for the external <c>/ipc</c>. Returns immediately: <c>true</c> means queued,
        /// not delivered or executed; <c>false</c> (also logged) if the transport is not configured, stopped or full.
        /// </summary>
        bool TrySend(IpcMessage message);

        /// <summary>Queues an event with a new unique id; same result semantics as <see cref="TrySend"/>.</summary>
        bool PublishEvent(string eventName, string parametersJson = "{}");

        /// <summary>
        /// Sends a command with a new unique id and completes with the external application's response
        /// (<c>status</c> <c>ok</c> or <c>error</c>) received on <c>/ipc</c> with the same id.
        /// Never blocks: the task fails with <see cref="IpcTransportException"/> if the command cannot be queued,
        /// <see cref="IpcTransportStoppedException"/> when the transport stops (<c>HTTPSTOP</c>),
        /// <see cref="TimeoutException"/> after <paramref name="timeout"/> (default 30 s) and is cancelled by
        /// <paramref name="cancellationToken"/>. Continuations never run inline inside <c>Drain</c>.
        /// </summary>
        Task<IpcMessage> SendCommandAsync(string command, string parametersJson = "{}", TimeSpan? timeout = null,
                                          CancellationToken cancellationToken = default(CancellationToken));
    }
}
