using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoCADHttp.Http;
using Xunit;

namespace AutoCADHttp.Tests
{
    public class IpcOutboxTests
    {
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
            public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) { _send = send; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return _send(request, cancellationToken);
            }
        }

        [Fact]
        public async Task RealHttpPeer_ReceivesCommandResponseAndEvent_InOrder()
        {
            var peer = new ApiRouter("External", "1");
            using (var server = new LocalHttpServer(0, peer.Handle, null))
            {
                server.Start();
                var outbox = new IpcOutbox(new Uri("http://127.0.0.1:" + server.Port + "/ipc"));
                try
                {
                    Assert.True(outbox.TryEnqueue(IpcMessage.CreateCommand("1", "PING")));
                    Assert.True(outbox.TryEnqueue(IpcMessage.CreateResponse("1", "{\"x\":100}")));
                    Assert.True(outbox.TryEnqueue(IpcMessage.CreateEvent("2", "OBJECT_CREATED", "{\"name\":\"中文\"}")));
                    await HttpTestHelpers.WaitFor(() => peer.Incoming.Count == 3);
                    IpcMessage message;
                    Assert.True(peer.Incoming.TryDequeue(out message));
                    Assert.Equal("command", message.Type);
                    Assert.True(peer.Incoming.TryDequeue(out message));
                    Assert.Equal("response", message.Type);
                    Assert.Equal("1", message.Id);
                    Assert.Equal("{\"x\":100}", message.ResultJson);
                    Assert.True(peer.Incoming.TryDequeue(out message));
                    Assert.Equal("event", message.Type);
                    Assert.Contains("中文", message.ParametersJson);
                }
                finally
                {
                    outbox.Dispose();
                    await outbox.Completion;
                }
            }
        }

        [Theory]
        [InlineData("timeout")]
        [InlineData("http")]
        [InlineData("unavailable")]
        public async Task FailedDelivery_IsLogged_AndNextMessageIsSent_WhilePingStaysAvailable(string failure)
        {
            var log = new ConcurrentQueue<string>();
            var sent = new ConcurrentQueue<string>();
            int attempts = 0;
            var handler = new StubHandler(async (request, cancellation) =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/ipc", request.RequestUri.AbsolutePath);
                Assert.Equal("application/json", request.Content.Headers.ContentType.MediaType);
                string json = await request.Content.ReadAsStringAsync();
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    if (failure == "timeout")
                        await Task.Delay(Timeout.Infinite, cancellation);
                    if (failure == "unavailable")
                        throw new HttpRequestException("Connection refused");
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
                sent.Enqueue(json);
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            });
            var router = new ApiRouter("AutoCAD", "2021");
            using (var server = new LocalHttpServer(0, router.Handle, null))
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                server.Start();
                var outbox = new IpcOutbox(new Uri("http://127.0.0.1:5001/ipc"), log.Enqueue, TimeSpan.FromMilliseconds(100), handler);
                try
                {
                    Assert.True(outbox.TryEnqueue(IpcMessage.CreateEvent("first", "OBJECT_CREATED")));
                    Assert.True(outbox.TryEnqueue(IpcMessage.CreateResponse("second")));
                    Assert.Equal(router.PingJson, await http.GetStringAsync("http://127.0.0.1:" + server.Port + "/ping"));
                    await HttpTestHelpers.WaitFor(() => !sent.IsEmpty);
                    Assert.Contains("second", Assert.Single(sent));
                    Assert.Contains(log, line => line.Contains("first") && line.Contains(failure == "timeout" ? "timed out" : "failed"));
                    Assert.False(outbox.Completion.IsCompleted);
                }
                finally
                {
                    outbox.Dispose();
                    await outbox.Completion;
                }
            }
        }

        [Fact]
        public async Task QueueIsBounded_EnqueueDoesNotWait_AndDisposeCancelsNetworkIo()
        {
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new StubHandler(async (request, cancellation) =>
            {
                started.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellation);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            var outbox = new IpcOutbox(new Uri("http://127.0.0.1:5001/ipc"), handler: handler, capacity: 1);
            try
            {
                Assert.True(outbox.TryEnqueue(IpcMessage.CreateResponse("in-flight")));
                Assert.Same(started.Task, await Task.WhenAny(started.Task, Task.Delay(5000)));
                var stopwatch = Stopwatch.StartNew();
                Assert.True(outbox.TryEnqueue(IpcMessage.CreateResponse("pending")));
                Assert.False(outbox.TryEnqueue(IpcMessage.CreateResponse("full")));
                Assert.True(stopwatch.ElapsedMilliseconds < 1000);
            }
            finally
            {
                outbox.Dispose();
                Assert.Same(outbox.Completion, await Task.WhenAny(outbox.Completion, Task.Delay(5000)));
                await outbox.Completion;
            }
            Assert.False(outbox.TryEnqueue(IpcMessage.CreateResponse("stopped")));
        }

        // Regression: an HTTP/1.0 peer (e.g. Python's http.server) answers and then closes the connection. With pooled
        // keep-alive connections the next message could be written to that closing connection and was lost with
        // "The response ended prematurely" (no retries). The peer below waits before closing so that any reuse fails.
        [Fact]
        public async Task BackToBackMessages_ToAnHttp10PeerThatClosesAfterEachResponse_AreAllDelivered()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var received = new ConcurrentQueue<string>();
            var log = new ConcurrentQueue<string>();
            var connections = 0;
            Task accept = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(); }
                    catch (Exception) { return; }
                    Interlocked.Increment(ref connections);
                    var _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            var stream = client.GetStream();
                            string request = await ReadRequest(stream);
                            if (request == null)
                                return;
                            received.Enqueue(request.Substring(request.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4));
                            byte[] answer = Encoding.ASCII.GetBytes("HTTP/1.0 202 Accepted\r\nContent-Length: 0\r\n\r\n");
                            await stream.WriteAsync(answer, 0, answer.Length);
                            await Task.Delay(300);
                        }
                    });
                }
            });
            var outbox = new IpcOutbox(new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/ipc"),
                                       log.Enqueue);
            try
            {
                for (int i = 0; i < 3; i++)
                {
                    Assert.True(outbox.TryEnqueue(IpcMessage.CreateEvent("m" + i, "BURST")));
                    Assert.True(outbox.TryEnqueue(IpcMessage.CreateEvent("n" + i, "BURST")));
                }
                await HttpTestHelpers.WaitFor(() => received.Count == 6);
                Assert.Empty(log);
                Assert.Equal(6, Volatile.Read(ref connections));
                Assert.Equal(new[] { "m0", "n0", "m1", "n1", "m2", "n2" }, received.Select(b => IpcMessage.Parse(b).Id).ToArray());
            }
            finally
            {
                outbox.Dispose();
                await outbox.Completion;
                listener.Stop();
                await accept;
            }
        }

        // Reads one request with a Content-Length body; null if the client closed the connection first.
        private static async Task<string> ReadRequest(NetworkStream stream)
        {
            var data = new StringBuilder();
            var buffer = new byte[4096];
            while (true)
            {
                string text = data.ToString();
                int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end >= 0)
                {
                    int length = 0;
                    foreach (string line in text.Substring(0, end).Split(new[] { "\r\n" }, StringSplitOptions.None))
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            length = int.Parse(line.Substring(15).Trim());
                    }
                    if (Encoding.UTF8.GetByteCount(text) - Encoding.UTF8.GetByteCount(text.Substring(0, end + 4)) >= length)
                        return text;
                }
                int read = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (read == 0)
                    return null;
                data.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }

        [Theory]
        [InlineData("/ipc")]
        [InlineData("ftp://127.0.0.1/ipc")]
        [InlineData("http://127.0.0.1/line")]
        [InlineData("http://127.0.0.1/ipc?command=LINE")]
        public void ExternalEndpoint_MustUseIpc(string endpoint)
        {
            Assert.Throws<ArgumentException>(() => new IpcOutbox(new Uri(endpoint, UriKind.RelativeOrAbsolute)));
        }
    }
}
