using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoCADHttp.Http;
using Xunit;

namespace AutoCADHttp.Tests
{
    public class LocalHttpServerTests
    {
        private const string ExpectedPingJson = "{\"status\":\"ok\",\"application\":\"AutoCAD\",\"version\":\"2021\"}";

        private static LocalHttpServer CreateServer(int port = 0, ConcurrentQueue<string> log = null)
        {
            var router = new ApiRouter("AutoCAD", "2021");
            return new LocalHttpServer(port, router.Handle, log == null ? (Action<string>)null : log.Enqueue);
        }

        private static HttpClient CreateClient()
        {
            return new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        }

        /// <summary>
        /// Cross-process lock for tests that bind the real port 5000. `dotnet test` runs the net48 and net8.0
        /// test assemblies in parallel processes; without the lock one process can stop its server while the other
        /// starts one on the same port, and the "refused after Stop()" request then reaches the other process.
        /// A file lock is used because named mutexes are thread-affine (unusable across await).
        /// </summary>
        private static IDisposable AcquireRealPortLock()
        {
            string path = Path.Combine(Path.GetTempPath(), "AutoCADHttp.Tests.port" + LocalHttpServer.DefaultPort + ".lock");
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException) when (stopwatch.Elapsed < TimeSpan.FromSeconds(60))
                {
                    Thread.Sleep(50);
                }
            }
        }

        /// <summary>Sends raw bytes and returns the whole response (the server always closes the connection).</summary>
        private static string SendRaw(int port, string request)
        {
            using (var client = new TcpClient())
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                client.Connect(IPAddress.Loopback, port);
                var stream = client.GetStream();
                byte[] bytes = Encoding.ASCII.GetBytes(request);
                stream.Write(bytes, 0, bytes.Length);

                var sb = new StringBuilder();
                var buffer = new byte[4096];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                return sb.ToString();
            }
        }

        private static string Body(string rawResponse)
        {
            int index = rawResponse.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            return index < 0 ? null : rawResponse.Substring(index + 4);
        }

        [Fact]
        public void DefaultPortIs5000()
        {
            Assert.Equal(5000, LocalHttpServer.DefaultPort);
            using (var server = CreateServer(LocalHttpServer.DefaultPort))
            {
                Assert.Equal(5000, server.Port);
                Assert.Equal(IPAddress.Loopback, server.Address);
                Assert.False(server.IsRunning);
            }
        }

        [Fact]
        public void PingJsonMatchesSpecification()
        {
            Assert.Equal(ExpectedPingJson, new ApiRouter("AutoCAD", "2021").PingJson);
        }

        [Fact]
        public void IsNotStartedByConstructor()
        {
            using (var server = CreateServer())
            {
                Assert.False(server.IsRunning);
                Assert.Null(server.StartedAtUtc);
            }
        }

        [Fact]
        public async Task GetPing_ReturnsOkJson()
        {
            using (var server = CreateServer())
            using (var http = CreateClient())
            {
                Assert.Equal(StartResult.Started, server.Start());

                var response = await http.GetAsync("http://127.0.0.1:" + server.Port + "/ping");
                string body = await response.Content.ReadAsStringAsync();

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("application/json", response.Content.Headers.ContentType.MediaType);
                Assert.Equal(ExpectedPingJson, body);
                Assert.Equal(1, server.RequestCount);
            }
        }

        [Fact]
        public async Task GetPing_ViaLocalhostName_Works()
        {
            using (var server = CreateServer())
            {
                server.Start();
                string raw = SendRaw(server.Port, "GET /ping HTTP/1.1\r\nHost: localhost:" + server.Port + "\r\n\r\n");
                Assert.StartsWith("HTTP/1.1 200 OK\r\n", raw);
                Assert.Equal(ExpectedPingJson, Body(raw));
                await Task.FromResult(0);
            }
        }

        [Fact]
        public void GetPing_WithQueryString_Works()
        {
            using (var server = CreateServer())
            {
                server.Start();
                string raw = SendRaw(server.Port, "GET /ping?x=1 HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
                Assert.StartsWith("HTTP/1.1 200 OK\r\n", raw);
                Assert.Equal(ExpectedPingJson, Body(raw));
            }
        }

        [Fact]
        public void HeadPing_ReturnsHeadersWithoutBody()
        {
            using (var server = CreateServer())
            {
                server.Start();
                string raw = SendRaw(server.Port, "HEAD /ping HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
                Assert.StartsWith("HTTP/1.1 200 OK\r\n", raw);
                Assert.Contains("Content-Length: " + ExpectedPingJson.Length + "\r\n", raw);
                Assert.Equal(string.Empty, Body(raw));
            }
        }

        [Fact]
        public void UnknownPath_Returns404()
        {
            using (var server = CreateServer())
            {
                server.Start();
                string raw = SendRaw(server.Port, "GET /unknown HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
                Assert.StartsWith("HTTP/1.1 404 Not Found\r\n", raw);
            }
        }

        [Fact]
        public void PostPing_Returns405()
        {
            using (var server = CreateServer())
            {
                server.Start();
                string raw = SendRaw(server.Port, "POST /ping HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 0\r\n\r\n");
                Assert.StartsWith("HTTP/1.1 405 Method Not Allowed\r\n", raw);
                Assert.Contains("Allow: GET, HEAD\r\n", raw);
            }
        }

        [Fact]
        public void MalformedRequest_Returns400()
        {
            using (var server = CreateServer())
            {
                server.Start();
                string raw = SendRaw(server.Port, "garbage\r\n\r\n");
                Assert.StartsWith("HTTP/1.1 400 Bad Request\r\n", raw);
            }
        }

        [Fact]
        public void ForeignHostHeader_Returns403()
        {
            // DNS rebinding: a page from evil.example resolved to 127.0.0.1 sends its own Host.
            using (var server = CreateServer())
            {
                server.Start();
                string raw = SendRaw(server.Port, "GET /ping HTTP/1.1\r\nHost: evil.example:" + server.Port + "\r\n\r\n");
                Assert.StartsWith("HTTP/1.1 403 Forbidden\r\n", raw);
            }
        }

        [Fact]
        public void PingIsNotReachableAfterStop()
        {
            using (var server = CreateServer())
            {
                server.Start();
                int port = server.Port;
                Assert.StartsWith("HTTP/1.1 200 OK", SendRaw(port, "GET /ping HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));

                Assert.True(server.Stop());
                Assert.False(server.IsRunning);

                var ex = Assert.ThrowsAny<SocketException>(() => SendRaw(port, "GET /ping HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));
                Assert.Equal(SocketError.ConnectionRefused, ex.SocketErrorCode);
            }
        }

        [Fact]
        public void StopWhenNotRunning_ReturnsFalse()
        {
            using (var server = CreateServer())
            {
                Assert.False(server.Stop());
                server.Start();
                Assert.True(server.Stop());
                Assert.False(server.Stop());
            }
        }

        [Fact]
        public void SecondStart_DoesNotFailAndDoesNotStartSecondServer()
        {
            using (var server = CreateServer())
            {
                Assert.Equal(StartResult.Started, server.Start());
                int port = server.Port;
                DateTime? startedAt = server.StartedAtUtc;

                Assert.Equal(StartResult.AlreadyRunning, server.Start());
                Assert.Equal(StartResult.AlreadyRunning, server.Start());

                Assert.True(server.IsRunning);
                Assert.Equal(port, server.Port);
                Assert.Equal(startedAt, server.StartedAtUtc);
                Assert.StartsWith("HTTP/1.1 200 OK", SendRaw(port, "GET /ping HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));

                // One Stop is enough to release the port: there is no hidden second listener.
                Assert.True(server.Stop());
                Assert.ThrowsAny<SocketException>(() => SendRaw(port, "GET /ping HTTP/1.1\r\n\r\n"));
            }
        }

        [Fact]
        public void CanRestartOnSamePortAfterStop()
        {
            int port;
            var probe = new TcpListener(IPAddress.Loopback, 0); // Not IDisposable on .NET Framework.
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            using (var server = CreateServer(port))
            {
                for (int i = 0; i < 3; i++)
                {
                    Assert.Equal(StartResult.Started, server.Start());
                    Assert.StartsWith("HTTP/1.1 200 OK", SendRaw(port, "GET /ping HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));
                    Assert.True(server.Stop());
                }
            }
        }

        [Fact]
        public void Start_WhenPortIsBusy_ThrowsAndStaysStopped()
        {
            using (var server = CreateServer())
            {
                server.Start();
                int busyPort = server.Port;

                using (var second = CreateServer(busyPort))
                {
                    var ex = Assert.ThrowsAny<SocketException>(() => second.Start());
                    Assert.Equal(SocketError.AddressAlreadyInUse, ex.SocketErrorCode);
                    Assert.False(second.IsRunning);
                    Assert.Contains(busyPort.ToString(), second.LastError);
                }

                // The first server is unaffected.
                Assert.StartsWith("HTTP/1.1 200 OK", SendRaw(busyPort, "GET /ping HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));
            }
        }

        [Fact]
        public void ListensOnLoopbackOnly()
        {
            using (var server = CreateServer())
            {
                server.Start();

                IPAddress external = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Select(a => a.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

                if (external == null)
                    return; // No non-loopback IPv4 address on this machine - nothing to check.

                using (var client = new TcpClient())
                {
                    var ex = Assert.ThrowsAny<SocketException>(() => client.Connect(external, server.Port));
                    Assert.Equal(SocketError.ConnectionRefused, ex.SocketErrorCode);
                }
            }
        }

        [Fact]
        public void StartAndStop_ReturnImmediately()
        {
            using (var server = CreateServer())
            {
                var sw = Stopwatch.StartNew();
                server.Start();
                Assert.True(sw.ElapsedMilliseconds < 1000, "Start took " + sw.ElapsedMilliseconds + " ms");

                sw.Restart();
                server.Stop();
                Assert.True(sw.ElapsedMilliseconds < 3000, "Stop took " + sw.ElapsedMilliseconds + " ms");
            }
        }

        [Fact]
        public void IdleConnection_DoesNotBlockOtherRequests_AndStopClosesIt()
        {
            using (var server = CreateServer())
            using (var idle = new TcpClient())
            {
                server.Start();
                idle.Connect(IPAddress.Loopback, server.Port); // Connected but never sends a request.

                Assert.StartsWith("HTTP/1.1 200 OK", SendRaw(server.Port, "GET /ping HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));

                var sw = Stopwatch.StartNew();
                server.Stop();
                Assert.True(sw.ElapsedMilliseconds < 3000, "Stop took " + sw.ElapsedMilliseconds + " ms");
            }
        }

        [Fact]
        public void ManyIdleConnections_DoNotStarveNewRequests()
        {
            // Regression: connections used to be served with blocking reads on thread-pool threads,
            // so idle/slow clients exhausted the pool and new requests waited for seconds.
            using (var server = CreateServer())
            {
                server.Start();
                var idle = Enumerable.Range(0, 64).Select(_ => new TcpClient()).ToList();
                try
                {
                    foreach (var c in idle)
                        c.Connect(IPAddress.Loopback, server.Port);

                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < 5; i++)
                        Assert.StartsWith("HTTP/1.1 200 OK", SendRaw(server.Port, "GET /ping HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));
                    Assert.True(sw.ElapsedMilliseconds < 1000, "5 requests took " + sw.ElapsedMilliseconds + " ms");
                }
                finally
                {
                    foreach (var c in idle)
                        c.Dispose();
                }
            }
        }

        [Fact]
        public void IdleConnection_IsClosedAfterRequestTimeout()
        {
            using (var server = CreateServer())
            using (var idle = new TcpClient())
            {
                server.RequestTimeoutMs = 300;
                server.Start();
                idle.ReceiveTimeout = 5000;
                idle.Connect(IPAddress.Loopback, server.Port);

                var sw = Stopwatch.StartNew();
                int read;
                try
                {
                    read = idle.GetStream().Read(new byte[16], 0, 16);
                }
                catch (System.IO.IOException)
                {
                    read = 0; // Connection reset is also a close.
                }

                Assert.Equal(0, read);
                Assert.True(sw.ElapsedMilliseconds < 3000, "Closed after " + sw.ElapsedMilliseconds + " ms");
                Assert.Null(server.LastError); // A timeout is not an error.
            }
        }

        [Fact]
        public async Task ParallelRequests_AreAllServed()
        {
            using (var server = CreateServer())
            using (var http = CreateClient())
            {
                server.Start();
                string url = "http://127.0.0.1:" + server.Port + "/ping";

                string[] bodies = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => http.GetStringAsync(url)));

                Assert.All(bodies, b => Assert.Equal(ExpectedPingJson, b));
                Assert.Equal(50, server.RequestCount);
            }
        }

        [Fact]
        public void HandlerException_Returns500AndIsLogged()
        {
            var log = new ConcurrentQueue<string>();
            using (var server = new LocalHttpServer(0, r => { throw new InvalidOperationException("boom"); }, log.Enqueue))
            {
                server.Start();
                string raw = SendRaw(server.Port, "GET /ping HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");

                Assert.StartsWith("HTTP/1.1 500 Internal Server Error\r\n", raw);
                Assert.Contains("boom", server.LastError);
                Assert.Contains(log, m => m.StartsWith("ERROR:") && m.Contains("boom"));
            }
        }

        [Fact]
        public void Verbose_LogsRequests()
        {
            var log = new ConcurrentQueue<string>();
            using (var server = CreateServer(0, log))
            {
                server.Start();
                SendRaw(server.Port, "GET /ping HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
                Assert.Empty(log); // Off by default.

                server.Verbose = true;
                SendRaw(server.Port, "GET /ping HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
                SpinWait.SpinUntil(() => !log.IsEmpty, 2000);
                Assert.Contains("GET /ping -> 200", log);
            }
        }

        [Fact]
        public async Task RealPort5000_PingWorksAndIsRefusedAfterStop()
        {
            // End-to-end check of the exact configuration used inside AutoCAD.
            using (AcquireRealPortLock())
            using (var server = CreateServer(LocalHttpServer.DefaultPort))
            using (var http = CreateClient())
            {
                try
                {
                    server.Start();
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
                        return; // Port 5000 is taken by something else on this machine.
                    throw;
                }

                Assert.Equal(ExpectedPingJson, await http.GetStringAsync("http://127.0.0.1:5000/ping"));

                server.Stop();
                await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetStringAsync("http://127.0.0.1:5000/ping"));
            }
        }
    }
}
