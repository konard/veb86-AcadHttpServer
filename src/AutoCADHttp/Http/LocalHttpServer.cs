using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AutoCADHttp.Http
{
    public enum StartResult
    {
        /// <summary>The server was stopped and has been started now.</summary>
        Started,

        /// <summary>The server was already running; nothing was changed.</summary>
        AlreadyRunning
    }

    /// <summary>
    /// Minimal HTTP/1.1 server bound strictly to 127.0.0.1.
    /// <para>
    /// It is built on <see cref="TcpListener"/> rather than <see cref="HttpListener"/>:
    /// HttpListener goes through the Windows http.sys driver, which requires administrator rights
    /// or a <c>netsh http add urlacl</c> reservation for a non-admin AutoCAD process, and http.sys
    /// prefixes are matched by the Host header rather than by the local interface.
    /// A socket bound to <see cref="IPAddress.Loopback"/> needs no extra rights and is physically
    /// reachable only from the local machine.
    /// </para>
    /// <para>
    /// Threading: <see cref="Start"/> and <see cref="Stop"/> return immediately; connections are
    /// accepted on a dedicated background thread and handled with asynchronous I/O on the thread pool,
    /// so the caller (the AutoCAD main thread) is never blocked. The <see cref="Log"/> callback is invoked from
    /// background threads too - the caller must marshal it to its own thread if needed.
    /// </para>
    /// </summary>
    public sealed class LocalHttpServer : IDisposable
    {
        public const int DefaultPort = 5000;
        public const int MaxHeaderBytes = 16 * 1024;
        public const int DefaultRequestTimeoutMs = 5000;

        private readonly object _sync = new object();
        private readonly int _requestedPort;
        private readonly Func<HttpRequestInfo, HttpResponseInfo> _handler;
        private readonly Action<string> _log;

        private Session _session;
        private long _requestCount;
        private string _lastError;
        private int _requestTimeoutMs = DefaultRequestTimeoutMs;

        public LocalHttpServer(int port, Func<HttpRequestInfo, HttpResponseInfo> handler, Action<string> log)
        {
            if (port < 0 || port > 65535)
                throw new ArgumentOutOfRangeException("port");
            if (handler == null)
                throw new ArgumentNullException("handler");

            _requestedPort = port;
            _handler = handler;
            _log = log;
        }

        /// <summary>Listening address. Always the IPv4 loopback address.</summary>
        public IPAddress Address
        {
            get { return IPAddress.Loopback; }
        }

        /// <summary>Actual listening port while running (useful when constructed with port 0), otherwise the configured port.</summary>
        public int Port
        {
            get
            {
                var session = _session;
                return session != null ? session.Port : _requestedPort;
            }
        }

        public bool IsRunning
        {
            get { return _session != null; }
        }

        /// <summary>UTC time of the last successful start, or null when stopped.</summary>
        public DateTime? StartedAtUtc
        {
            get
            {
                var session = _session;
                return session != null ? session.StartedAtUtc : (DateTime?)null;
            }
        }

        /// <summary>Number of HTTP requests answered since the object was created.</summary>
        public long RequestCount
        {
            get { return Interlocked.Read(ref _requestCount); }
        }

        /// <summary>Last error message (start failure or request handling error), or null.</summary>
        public string LastError
        {
            get { return _lastError; }
        }

        /// <summary>
        /// Time a client has to send the request and receive the response; then the connection is closed.
        /// </summary>
        public int RequestTimeoutMs
        {
            get { return _requestTimeoutMs; }
            set
            {
                if (value <= 0)
                    throw new ArgumentOutOfRangeException("value");
                _requestTimeoutMs = value;
            }
        }

        /// <summary>When true every request is reported through the log callback (off by default).</summary>
        public bool Verbose { get; set; }

        /// <summary>
        /// Starts listening on 127.0.0.1:<see cref="Port"/>. Calling it while the server is
        /// running does nothing and returns <see cref="StartResult.AlreadyRunning"/>.
        /// </summary>
        /// <exception cref="SocketException">The port cannot be bound (e.g. it is already in use).</exception>
        public StartResult Start()
        {
            lock (_sync)
            {
                if (_session != null)
                    return StartResult.AlreadyRunning;

                // ExclusiveAddressUse is intentionally not set: on Windows SO_EXCLUSIVEADDRUSE prevents
                // re-binding the port while server-closed connections are in TIME_WAIT,
                // which would break HTTPSTOP followed by HTTPSTART.
                var listener = new TcpListener(IPAddress.Loopback, _requestedPort);

                try
                {
                    listener.Start();
                }
                catch (SocketException ex)
                {
                    _lastError = "Cannot listen on " + IPAddress.Loopback + ":" + _requestedPort +
                                 ": " + ex.Message + " (SocketError." + ex.SocketErrorCode + ")";
                    try { listener.Stop(); } catch { }
                    throw;
                }

                var session = new Session(listener, ((IPEndPoint)listener.LocalEndpoint).Port);
                session.AcceptThread = new Thread(() => AcceptLoop(session))
                {
                    IsBackground = true,
                    Name = "AutoCADHttp accept loop"
                };
                _session = session;
                _lastError = null;
                session.AcceptThread.Start();
                return StartResult.Started;
            }
        }

        /// <summary>
        /// Stops listening and closes open connections. Returns false if the server was not running.
        /// After this call the port is released and new connections are refused.
        /// </summary>
        public bool Stop()
        {
            Session session;
            lock (_sync)
            {
                session = _session;
                if (session == null)
                    return false;
                _session = null;

                // Release the port inside the lock so that a following Start() can bind it again.
                session.Stopping = true;
                try { session.Listener.Stop(); } catch (Exception ex) { Trace("Listener stop: " + ex.Message); }
            }

            foreach (var client in session.Clients.Keys)
                CloseQuietly(client);

            if (session.AcceptThread != null && session.AcceptThread != Thread.CurrentThread)
                session.AcceptThread.Join(2000);

            return true;
        }

        public void Dispose()
        {
            Stop();
        }

        private void AcceptLoop(Session session)
        {
            while (!session.Stopping)
            {
                TcpClient client;
                try
                {
                    client = session.Listener.AcceptTcpClient();
                }
                catch (SocketException ex)
                {
                    if (session.Stopping)
                        break;
                    ReportError("Accept failed: " + ex.Message);
                    Thread.Sleep(50);
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    // Listener was stopped.
                    break;
                }

                if (session.Stopping)
                {
                    CloseQuietly(client);
                    break;
                }

                session.Clients.TryAdd(client, 0);
                if (session.Stopping)
                {
                    // Stop() may have enumerated the clients before this one was added.
                    CloseQuietly(client);
                    break;
                }
                // Fire and forget: HandleClientAsync never throws.
                Task.Run(() => HandleClientAsync(session, client));
            }
        }

        private async Task HandleClientAsync(Session session, TcpClient client)
        {
            // Asynchronous I/O: a slow or idle client does not occupy a thread-pool thread
            // (blocking reads on pool threads starve the pool under many parallel connections).
            // Whole-request deadline: the connection is closed if it is not served in time,
            // which also aborts pending ReadAsync/WriteAsync calls.
            int timeoutMs = _requestTimeoutMs;
            using (var deadline = new CancellationTokenSource(timeoutMs))
            using (deadline.Token.Register(() => CloseQuietly(client)))
            {
                try
                {
                    client.NoDelay = true;

                    NetworkStream stream = client.GetStream();
                    HttpRequestInfo request;
                    HttpResponseInfo response;

                    RequestHead head = await ReadRequestHeadAsync(stream).ConfigureAwait(false);
                    if (head.Text == null)
                    {
                        if (head.Error == null)
                            return; // Connection closed before a request was sent.
                        request = null;
                        response = head.Error == "too large"
                            ? HttpResponseInfo.JsonError(431, "Request Header Fields Too Large", "Request headers too large")
                            : HttpResponseInfo.JsonError(400, "Bad Request", "Bad request");
                    }
                    else
                    {
                        request = ParseRequestHead(head.Text);
                        if (request == null)
                            response = HttpResponseInfo.JsonError(400, "Bad Request", "Bad request");
                        else if (!IsAllowedHost(request))
                            response = HttpResponseInfo.JsonError(403, "Forbidden", "Host not allowed");
                        else
                            response = InvokeHandler(request);
                    }

                    byte[] bytes = response.ToBytes(request == null || request.Method != "HEAD");
                    await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                    Interlocked.Increment(ref _requestCount);

                    if (Verbose)
                    {
                        Trace(request != null
                            ? request.Method + " " + request.Target + " -> " + response.StatusCode
                            : "malformed request -> " + response.StatusCode);
                    }
                }
                catch (Exception ex)
                {
                    if (session.Stopping)
                        return;
                    if (deadline.IsCancellationRequested)
                        Trace("Connection closed: request not served within " + timeoutMs + " ms");
                    else
                        ReportError("Request handling failed: " + ex.GetType().Name + ": " + ex.Message);
                }
                finally
                {
                    byte ignored;
                    session.Clients.TryRemove(client, out ignored);
                    CloseQuietly(client);
                }
            }
        }

        private static void CloseQuietly(TcpClient client)
        {
            try { client.Close(); } catch { }
        }

        private HttpResponseInfo InvokeHandler(HttpRequestInfo request)
        {
            try
            {
                var response = _handler(request);
                if (response == null)
                    throw new InvalidOperationException("Handler returned null");
                return response;
            }
            catch (Exception ex)
            {
                ReportError("Handler error for " + request.Method + " " + request.Target + ": " +
                            ex.GetType().Name + ": " + ex.Message);
                return HttpResponseInfo.JsonError(500, "Internal Server Error", "Internal server error");
            }
        }

        internal struct RequestHead
        {
            /// <summary>Header block text, or null.</summary>
            public string Text;

            /// <summary>Null when the connection was closed before any data, otherwise the reason of failure.</summary>
            public string Error;
        }

        /// <summary>Reads bytes until the end of the header block ("\r\n\r\n" or bare "\n\n").</summary>
        internal static async Task<RequestHead> ReadRequestHeadAsync(Stream stream)
        {
            var buffer = new byte[MaxHeaderBytes];
            int total = 0;

            while (true)
            {
                if (total == buffer.Length)
                    return new RequestHead { Error = "too large" };

                int read = await stream.ReadAsync(buffer, total, buffer.Length - total).ConfigureAwait(false);
                if (read <= 0)
                    return new RequestHead { Error = total > 0 ? "incomplete" : null };

                int searchFrom = Math.Max(0, total - 3);
                total += read;

                for (int i = searchFrom; i < total; i++)
                {
                    if (buffer[i] != '\n')
                        continue;
                    if ((i >= 3 && buffer[i - 1] == '\r' && buffer[i - 2] == '\n' && buffer[i - 3] == '\r') ||
                        (i >= 1 && buffer[i - 1] == '\n'))
                    {
                        return new RequestHead { Text = Encoding.ASCII.GetString(buffer, 0, i + 1) };
                    }
                }
            }
        }

        internal static HttpRequestInfo ParseRequestHead(string head)
        {
            string[] lines = head.Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 0)
                return null;

            string[] parts = lines[0].Split(' ');
            if (parts.Length != 3)
                return null;

            string method = parts[0];
            string target = parts[1];
            string protocol = parts[2];

            if (method.Length == 0 || !protocol.StartsWith("HTTP/1.", StringComparison.Ordinal))
                return null;
            foreach (char c in method)
            {
                if (c < 'A' || c > 'Z')
                    return null;
            }

            // Absolute form: "http://127.0.0.1:5000/ping" -> "/ping".
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                int slash = target.IndexOf('/', "http://".Length);
                target = slash >= 0 ? target.Substring(slash) : "/";
            }
            if (!target.StartsWith("/", StringComparison.Ordinal))
                return null;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0)
                    break;
                int colon = line.IndexOf(':');
                if (colon <= 0)
                    return null;
                headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }

            return new HttpRequestInfo(method, target, protocol, headers);
        }

        /// <summary>
        /// Protection against DNS rebinding: a web page from another site that resolves its name to
        /// 127.0.0.1 sends its own host name in the Host header, so only local host names are accepted.
        /// </summary>
        internal static bool IsAllowedHost(HttpRequestInfo request)
        {
            string host;
            if (!request.Headers.TryGetValue("Host", out host) || host.Length == 0)
                return true; // HTTP/1.0 clients may omit Host.

            if (host.StartsWith("[", StringComparison.Ordinal))
            {
                int end = host.IndexOf(']');
                host = end > 0 ? host.Substring(0, end + 1) : host;
            }
            else
            {
                int colon = host.IndexOf(':');
                if (colon >= 0)
                {
                    int port;
                    if (!int.TryParse(host.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port))
                        return false;
                    host = host.Substring(0, colon);
                }
            }

            return string.Equals(host, "127.0.0.1", StringComparison.Ordinal) ||
                   string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(host, "[::1]", StringComparison.Ordinal);
        }

        private void ReportError(string message)
        {
            _lastError = message;
            Trace("ERROR: " + message);
        }

        private void Trace(string message)
        {
            var log = _log;
            if (log == null)
                return;
            try { log(message); } catch { }
        }

        private sealed class Session
        {
            public Session(TcpListener listener, int port)
            {
                Listener = listener;
                Port = port;
                StartedAtUtc = DateTime.UtcNow;
                Clients = new ConcurrentDictionary<TcpClient, byte>();
            }

            public readonly TcpListener Listener;
            public readonly int Port;
            public readonly DateTime StartedAtUtc;
            public readonly ConcurrentDictionary<TcpClient, byte> Clients;
            public Thread AcceptThread;
            public volatile bool Stopping;
        }
    }
}
