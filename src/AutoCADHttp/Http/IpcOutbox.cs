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
            if (!IsValidEndpoint(endpoint))
                throw new ArgumentException(EndpointRequirement, "endpoint");
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException("capacity");
            _endpoint = endpoint;
            _log = log;
            _queue = new BlockingCollection<IpcMessage>(new ConcurrentQueue<IpcMessage>(), capacity);
            _http = new HttpClient(handler ?? CreateDefaultHandler())
            {
                Timeout = timeout ?? TimeSpan.FromSeconds(5)
            };
            Completion = Task.Run(SendLoopAsync);
        }

        // Never follow a redirect to an application endpoint outside /ipc. One connection per message: there are no
        // retries, and reusing a pooled keep-alive connection that the peer is closing at that moment (always the case
        // for HTTP/1.0 servers such as Python's http.server) fails the next message with "response ended prematurely".
        private static HttpMessageHandler CreateDefaultHandler()
        {
#if NETCOREAPP
            // SocketsHttpHandler pools the connection even when the request says "Connection: close";
            // a zero lifetime closes it as soon as the response is finished.
            return new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, PooledConnectionLifetime = TimeSpan.Zero };
#else
            // .NET Framework: Connection: close (set per request) maps to HttpWebRequest.KeepAlive = false.
            return new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
#endif
        }

        internal const string EndpointRequirement = "External endpoint must be an absolute HTTP(S) URL ending in /ipc.";

        /// <summary>Absolute <c>http</c>/<c>https</c> URL whose path is exactly <c>/ipc</c>, without query or fragment.</summary>
        public static bool IsValidEndpoint(Uri endpoint)
        {
            return endpoint != null && endpoint.IsAbsoluteUri &&
                   (endpoint.Scheme == "http" || endpoint.Scheme == "https") &&
                   endpoint.AbsolutePath == "/ipc" && endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0;
        }

        /// <summary>Parses and validates an external <c>/ipc</c> URL (settings or environment).</summary>
        public static bool TryParseEndpoint(string text, out Uri endpoint)
        {
            endpoint = null;
            Uri parsed;
            if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out parsed) || !IsValidEndpoint(parsed))
                return false;
            endpoint = parsed;
            return true;
        }

        public Uri Endpoint { get { return _endpoint; } }
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

        // HttpRequestException hides the socket reason ("Connection refused/reset") in inner exceptions.
        private static string Describe(Exception ex)
        {
            string text = ex.Message;
            for (Exception inner = ex.InnerException; inner != null; inner = inner.InnerException)
            {
                if (!string.IsNullOrEmpty(inner.Message) && text.IndexOf(inner.Message, StringComparison.Ordinal) < 0)
                    text += " (" + inner.Message + ")";
            }
            return text;
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
                        {
                            request.Headers.ConnectionClose = true; // see CreateDefaultHandler
                            using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _stop.Token).ConfigureAwait(false))
                            {
                                response.EnsureSuccessStatusCode();
                                if (Verbose)
                                    Log("IPC outgoing " + message.Id + " -> " + (int)response.StatusCode);
                            }
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
                        Log("IPC outgoing " + message.Id + " failed at " + _endpoint + ": " + Describe(ex));
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
