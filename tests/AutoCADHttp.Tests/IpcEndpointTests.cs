using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AutoCADHttp.Http;
using Xunit;

namespace AutoCADHttp.Tests
{
    public class IpcEndpointTests
    {
        [Fact]
        public async Task PostIpc_AcceptsCommandEnvelope()
        {
            var router = new ApiRouter("AutoCAD", "2021");
            using (var server = new LocalHttpServer(0, router.Handle, null))
            using (var http = new HttpClient { Timeout = System.TimeSpan.FromSeconds(5) })
            {
                server.Start();
                using (var content = new StringContent(
                    "{\"id\":\"123\",\"type\":\"command\",\"command\":\"LINE\",\"parameters\":{\"x1\":0,\"y1\":0,\"x2\":100,\"y2\":100}}",
                    Encoding.UTF8, "application/json"))
                using (var response = await http.PostAsync("http://127.0.0.1:" + server.Port + "/ipc", content))
                {
                    Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                    string body = await response.Content.ReadAsStringAsync();
                    Assert.Contains("\"id\":\"123\"", body);
                    var acknowledgement = IpcMessage.Parse(body);
                    Assert.Equal("response", acknowledgement.Type);
                    Assert.Equal("{\"queued\":true}", acknowledgement.ResultJson);
                }
            }
        }

        [Theory]
        [InlineData("command")]
        [InlineData("response")]
        [InlineData("event")]
        public async Task AllEnvelopeTypes_AreQueuedWithoutExecutingHandlers(string type)
        {
            var router = new ApiRouter("AutoCAD", "2021");
            var message = type == "command" ? IpcMessage.CreateCommand("123", "INSERT_DEV") :
                type == "response" ? IpcMessage.CreateResponse("123") : IpcMessage.CreateEvent("123", "OBJECT_CREATED");
            using (var server = new LocalHttpServer(0, router.Handle, null))
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                server.Start();
                using (var content = new StringContent(message.Json, Encoding.UTF8, "application/json"))
                using (var response = await http.PostAsync("http://127.0.0.1:" + server.Port + "/ipc", content))
                    Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                IpcMessage queued;
                Assert.True(router.Incoming.TryDequeue(out queued));
                Assert.Equal(type, queued.Type);
                Assert.Equal("123", queued.Id);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BodyBytes_ArePreservedInOneReadAndAcrossUtf8Fragments(bool fragmented)
        {
            var router = new ApiRouter("AutoCAD", "2021");
            var message = IpcMessage.CreateCommand("русский", "LINE", "{\"name\":\"中文\"}");
            byte[] body = Encoding.UTF8.GetBytes(message.Json);
            byte[] header = Encoding.ASCII.GetBytes("POST /ipc HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\n\r\n");
            byte[] bytes = header.Concat(body).ToArray();
            using (var server = new LocalHttpServer(0, router.Handle, null))
            {
                server.Start();
                int split = fragmented ? header.Length + Encoding.UTF8.GetByteCount("{\"id\":\"") + 1 : 0;
                string raw = await HttpTestHelpers.SendRaw(server.Port, bytes, split);
                Assert.StartsWith("HTTP/1.1 202 Accepted", raw);
                Assert.Equal("русский", router.Incoming.Single().Id);
                Assert.Contains("中文", router.Incoming.Single().ParametersJson);
            }
        }

        [Theory]
        [InlineData("Content-Length: -1\r\n", "", 400)]
        [InlineData("Content-Length: 1048577\r\n", "", 413)]
        [InlineData("Content-Length: 999999999999999999999999\r\n", "", 400)]
        [InlineData("Content-Length: 0\r\nContent-Length: 0\r\n", "", 400)]
        [InlineData("Transfer-Encoding: chunked\r\n", "0\r\n\r\n", 400)]
        [InlineData("", "", 411)]
        [InlineData("Content-Length: 1\r\n", "{", 400)]
        [InlineData("Content-Length: 2\r\n", "{}", 400)]
        public async Task BadBodies_ReturnErrors_AndServerStillAnswersPing(string framing, string body, int status)
        {
            var router = new ApiRouter("AutoCAD", "2021");
            using (var server = new LocalHttpServer(0, router.Handle, null))
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                server.Start();
                string request = "POST /ipc HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\n" + framing + "\r\n" + body;
                string raw = await HttpTestHelpers.SendRaw(server.Port, Encoding.UTF8.GetBytes(request));
                Assert.StartsWith("HTTP/1.1 " + status, raw);
                if (body.Length > 0 && !framing.Contains("chunked"))
                    Assert.Contains("INVALID_JSON", raw);
                Assert.Empty(router.Incoming);
                Assert.Equal(router.PingJson, await http.GetStringAsync("http://127.0.0.1:" + server.Port + "/ping"));
            }
        }

        [Fact]
        public async Task ConcurrentCommands_AreAllQueued_WithDistinctIds()
        {
            var router = new ApiRouter("AutoCAD", "2021");
            using (var server = new LocalHttpServer(0, router.Handle, null))
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                server.Start();
                await Task.WhenAll(Enumerable.Range(0, 50).Select(async i =>
                {
                    using (var content = new StringContent(IpcMessage.CreateCommand(i.ToString(), "FUTURE").Json, Encoding.UTF8, "application/json"))
                    using (var response = await http.PostAsync("http://127.0.0.1:" + server.Port + "/ipc", content))
                        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                }));
                Assert.Equal(50, router.Incoming.Count);
                Assert.Equal(50, router.Incoming.Select(m => m.Id).Distinct().Count());
            }
        }

        [Fact]
        public async Task ExpectContinue_IsAnswered_AndCommandIsAccepted()
        {
            var router = new ApiRouter("AutoCAD", "2021");
            using (var server = new LocalHttpServer(0, router.Handle, null))
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                server.Start();
                using (var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:" + server.Port + "/ipc"))
                {
                    request.Headers.ExpectContinue = true;
                    request.Content = new StringContent(IpcMessage.CreateCommand("continue", "PING").Json, Encoding.UTF8, "application/json");
                    using (var response = await http.SendAsync(request))
                        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                }
                Assert.Equal("continue", router.Incoming.Single().Id);
            }
        }

        [Theory]
        [InlineData("text/html")]
        [InlineData("text/plain")]
        [InlineData("")]
        public void NonJsonContentType_IsRejected(string contentType)
        {
            var router = new ApiRouter("AutoCAD", "2021");
            var headers = new System.Collections.Generic.Dictionary<string, string> { { "Content-Type", contentType } };
            var response = router.Handle(new HttpRequestInfo("POST", "/ipc", "HTTP/1.1", headers, IpcMessage.CreateCommand("1", "PING").Json));
            Assert.Equal(415, response.StatusCode);
            Assert.Empty(router.Incoming);
        }

        [Fact]
        public async Task InvalidUtf8_IsRejectedBeforeEnqueue()
        {
            var router = new ApiRouter("AutoCAD", "2021");
            using (var server = new LocalHttpServer(0, router.Handle, null))
            {
                server.Start();
                byte[] header = Encoding.ASCII.GetBytes("POST /ipc HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\nContent-Length: 1\r\n\r\n");
                string raw = await HttpTestHelpers.SendRaw(server.Port, header.Concat(new byte[] { 255 }).ToArray());
                Assert.StartsWith("HTTP/1.1 400 Bad Request", raw);
                Assert.Empty(router.Incoming);
            }
        }

    }
}
