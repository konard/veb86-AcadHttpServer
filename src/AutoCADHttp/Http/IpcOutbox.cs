using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AutoCADHttp.Http
{
    /// <summary>A bounded outgoing queue with one background HTTP consumer. No application APIs.</summary>
    public sealed class IpcOutbox : IDisposable
    {
        private readonly BlockingCollection<IpcMessage> _queue;
        private readonly HttpClient _http;
        private readonly Uri _endpoint;
        private readonly Action<string> _log;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private int _disposed;
        private readonly object _sync = new object();

        public IpcOutbox(Uri endpoint, Action<string> log = null, TimeSpan? timeout = null,
                         HttpMessageHandler handler = null, int capacity = 1024)
        {
            if (endpoint == null || !endpoint.IsAbsoluteUri ||
                (endpoint.Scheme != "http" && endpoint.Scheme != "https") ||
                endpoint.AbsolutePath != "/ipc" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
                throw new ArgumentException("External endpoint must be an absolute HTTP(S) URL ending in /ipc.", "endpoint");
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException("capacity");
            _endpoint = endpoint;
            _log = log;
            _queue = new BlockingCollection<IpcMessage>(new ConcurrentQueue<IpcMessage>(), capacity);
            // Never follow a redirect to an application endpoint outside /ipc.
            _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
            {
                Timeout = timeout ?? TimeSpan.FromSeconds(5)
            };
            Completion = Task.Run(SendLoopAsync);
        }

        public Task Completion { get; private set; }
        public bool Verbose { get; set; }
        public int PendingCount
        {
            get
            {
                try { return _queue.Count; }
                catch (ObjectDisposedException) { return 0; }
            }
        }

        /// <summary>Returns immediately; false if stopped or full. Messages are immutable.</summary>
        public bool TryEnqueue(IpcMessage message)
        {
            if (message == null)
                throw new ArgumentNullException("message");
            try
            {
                if (Volatile.Read(ref _disposed) == 0 && _queue.TryAdd(message))
                    return true;
            }
            catch (InvalidOperationException) { }
            Log("IPC outgoing " + message.Id + " rejected: queue stopped or full.");
            return false;
        }

        /// <summary>Cancels network I/O without waiting on the caller. Await Completion when shutdown must finish.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            lock (_sync)
            {
                _queue.CompleteAdding();
                _stop.Cancel();
            }
        }

        private async Task SendLoopAsync()
        {
            try
            {
                foreach (IpcMessage message in _queue.GetConsumingEnumerable(_stop.Token))
                {
                    try
                    {
                        using (var content = new StringContent(message.Json, Encoding.UTF8, "application/json"))
                        using (var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content })
                        using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _stop.Token).ConfigureAwait(false))
                        {
                            response.EnsureSuccessStatusCode();
                            if (Verbose)
                                Log("IPC outgoing " + message.Id + " -> " + (int)response.StatusCode);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (_stop.IsCancellationRequested)
                            break;
                        Log("IPC outgoing " + message.Id + " timed out at " + _endpoint);
                    }
                    catch (Exception ex)
                    {
                        Log("IPC outgoing " + message.Id + " failed at " + _endpoint + ": " + ex.Message);
                    }
                    // No automatic retry: a timed-out command might already have been executed by the peer.
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                _http.Dispose();
                IpcMessage discarded;
                while (_queue.TryTake(out discarded)) { }
                lock (_sync)
                {
                    _queue.Dispose();
                    _stop.Dispose();
                }
            }
        }

        private void Log(string text)
        {
            try { if (_log != null) _log(text); } catch { }
        }
    }
}
