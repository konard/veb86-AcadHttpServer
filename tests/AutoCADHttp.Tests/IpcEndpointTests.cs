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

        private const string PingJson = "{\n  \"id\": \"123\",\n  \"type\": \"command\",\n  \"command\": \"PING\",\n  \"parameters\": {}\n}";

        private static async Task<string> PostIpc(HttpClient http, LocalHttpServer server, string json)
        {
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (var response = await http.PostAsync("http://127.0.0.1:" + server.Port + "/ipc", content))
                return (int)response.StatusCode + " " + await response.Content.ReadAsStringAsync();
        }

        private static string Shown(string body)
        {
            return ApiRouter.JsonViewPrefix + body;
        }

        private static async Task<string> SendIpcBytes(LocalHttpServer server, string contentType, byte[] body)
        {
            byte[] header = Encoding.ASCII.GetBytes("POST /ipc HTTP/1.1\r\nHost: 127.0.0.1\r\n" +
                (contentType == null ? "" : "Content-Type: " + contentType + "\r\n") +
                "Content-Length: " + body.Length + "\r\n\r\n");
            return await HttpTestHelpers.SendRaw(server.Port, header.Concat(body).ToArray());
        }

        private static LocalHttpServer JsonViewServer(ApiRouter router)
        {
            return new LocalHttpServer(0, router.Handle, null) { RequestRejected = router.ReportRejected };
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task JsonView_ShowsOriginalBodyOnlyWhenEnabled_AndDoesNotChangeProcessing(bool enabled)
        {
            var router = new ApiRouter("AutoCAD", "2021");
            var shown = new System.Collections.Concurrent.ConcurrentQueue<string>();
            if (enabled)
                router.JsonView = shown.Enqueue;
            using (var server = JsonViewServer(router))
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                server.Start();
                Assert.Equal("202 " + IpcMessage.CreateResponse("123", "{\"queued\":true}").Json,
                    await PostIpc(http, server, PingJson));
                IpcMessage queued = router.Incoming.Single();
                Assert.Equal(PingJson, queued.Json);
                Assert.Equal("PING", queued.Command);
                // JSON is shown byte-for-byte as received (formatting, key order and values untouched).
                Assert.Equal(enabled ? new[] { "IPC JSON: " + PingJson } : new string[0], shown.ToArray());

                // The queued message reaches IpcDispatcher in both modes.
                var results = new System.Collections.Generic.List<IpcMessage>();
                var dispatcher = new IpcDispatcher(router.Incoming, results.Add);
                dispatcher.Register("PING", m => IpcMessage.CreateResponse(m.Id));
                Assert.Equal(1, dispatcher.Drain());
                Assert.Equal("123", results.Single().Id);
            }
        }

        [Fact]
        public async Task JsonView_SequentialMessages_AreShownInOrder()
        {
            var router = new ApiRouter("AutoCAD", "2021");
            var shown = new System.Collections.Concurrent.ConcurrentQueue<string>();
            router.JsonView = shown.Enqueue;
            var sent = Enumerable.Range(1, 5).Select(i => IpcMessage.CreateCommand(i.ToString(), "PING").Json).ToArray();
            using (var server = JsonViewServer(router))
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                server.Start();
                foreach (string json in sent)
                    Assert.StartsWith("202 ", await PostIpc(http, server, json));
                Assert.Equal(sent.Select(Shown).ToArray(), shown.ToArray());
                Assert.Equal(sent, router.Incoming.Select(m => m.Json).ToArray());
            }
        }

        [Fact]
        public async Task JsonView_ConcurrentMessages_AreShownInIncomingQueueOrder()
        {
            var router = new ApiRouter("AutoCAD", "2021");
            var shown = new System.Collections.Concurrent.ConcurrentQueue<string>();
            router.JsonView = shown.Enqueue;
            using (var server = JsonViewServer(router))
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                server.Start();
                await Task.WhenAll(Enumerable.Range(0, 50).Select(async i =>
                    Assert.StartsWith("202 ", await PostIpc(http, server, IpcMessage.CreateCommand(i.ToString(), "PING").Json))));
                Assert.Equal(50, shown.Count);
                Assert.Equal(router.Incoming.Select(m => m.Json).Select(Shown).ToArray(), shown.ToArray());
            }
        }

        // Issue #17: JSONView = 1 shows incorrect JSON too - verbatim, with the reason - so the sender can be fixed.
        // The HTTP response stays the same and nothing is queued.
        [Theory]
        [InlineData("{", "Invalid IPC JSON at line 1, column 2 (offset 1): unexpected end of JSON.")]
        [InlineData("{}", "Missing required field \"id\".")]
        [InlineData("[]", "IPC message must be a JSON object, not array.")]
        [InlineData("{\"id\":\"1\",\"type\":\"unknown\"}", "type must be \"command\", \"response\" or \"event\", not \"unknown\".")]
        [InlineData("{\"id\":1,\"type\":\"command\",\"command\":\"PING\",\"parameters\":{}}", "id must be a JSON string, not number.")]
        [InlineData("{\"id\":\" \",\"type\":\"command\",\"command\":\"PING\",\"parameters\":{}}", "id must not be empty.")]
        [InlineData("{\"id\":\"1\",\"type\":\"command\",\"command\":\"PING\"}", "Missing required field \"parameters\".")]
        [InlineData("{\"id\":\"1\",\"type\":\"command\",\"command\":\"PING\",\"parameters\":\"{}\"}", "parameters must be a JSON object, not string.")]
        [InlineData("{\"id\":\"1\",\"type\":\"response\",\"status\":\"error\",\"error\":{}}", "Missing required field \"error.code\".")]
        [InlineData("{\"id\":\"1\",\"type\":\"response\",\"status\":\"done\"}", "status must be \"ok\" or \"error\", not \"done\".")]
        [InlineData("{\"id\":\"1\",\"id\":\"2\"}", "Duplicate IPC JSON field: id")]
        [InlineData("{\n  \"id\": \"1\",\n  \"type\": \"command\",\n  \"command\": \"PING\",\n  \"parameters\": {},\n}",
                    "Invalid IPC JSON at line 6, column 1 (offset 77): unexpected '}'.")]
        [InlineData("{'id':'1'}", "Invalid IPC JSON at line 1, column 2 (offset 1): unexpected '''.")]
        [InlineData("﻿{\"id\":\"1\"}", "Invalid IPC JSON at line 1, column 1 (offset 0): unexpected character U+FEFF.")]
        [InlineData("{\"id\":\"line\nbreak\"}", "Invalid IPC JSON at line 1, column 12 (offset 11): unexpected character U+000A.")]
        [InlineData("{\"id\":\"1\"} {}", "Trailing data after IPC JSON at line 1, column 12 (offset 11): unexpected '{'.")]
        [InlineData(" ", "IPC JSON is empty.")]
        public async Task JsonView_InvalidJson_IsShownWithReason_AndNotQueued(string body, string reason)
        {
            var router = new ApiRouter("AutoCAD", "2021");
            var shown = new System.Collections.Concurrent.ConcurrentQueue<string>();
            router.JsonView = shown.Enqueue;
            using (var server = JsonViewServer(router))
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                server.Start();
                Assert.Equal("400 " + IpcMessage.ErrorJson(null, "INVALID_JSON"),
                    await PostIpc(http, server, body));
                Assert.Equal(new[] { "IPC JSON INVALID (400 INVALID_JSON: " + reason + "): " + body }, shown.ToArray());
                Assert.Empty(router.Incoming);
            }
        }

        [Fact]
        public async Task JsonView_ValidAndInvalidJson_AreShownInArrivalOrder()
        {
            var router = new ApiRouter("AutoCAD", "2021");
            var shown = new System.Collections.Concurrent.ConcurrentQueue<string>();
            router.JsonView = shown.Enqueue;
            string first = IpcMessage.CreateCommand("1", "PING").Json, second = IpcMessage.CreateCommand("2", "PING").Json;
            using (var server = JsonViewServer(router))
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                server.Start();
                Assert.StartsWith("202 ", await PostIpc(http, server, first));
                Assert.StartsWith("400 ", await PostIpc(http, server, "{\"id\":\"x\"}"));
                Assert.StartsWith("202 ", await PostIpc(http, server, second));
                Assert.Equal(new[]
                {
                    "IPC JSON: " + first,
                    "IPC JSON INVALID (400 INVALID_JSON: Missing required field \"type\".): {\"id\":\"x\"}",
                    "IPC JSON: " + second
                }, shown.ToArray());
                Assert.Equal(new[] { "1", "2" }, router.Incoming.Select(m => m.Id).ToArray());
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task JsonView_InvalidJson_ResponsesAreIdenticalWithAndWithoutJsonView(bool enabled)
        {
            var router = new ApiRouter("AutoCAD", "2021");
            var shown = new System.Collections.Concurrent.ConcurrentQueue<string>();
            if (enabled)
                router.JsonView = shown.Enqueue;
            using (var server = JsonViewServer(router))
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                server.Start();
                Assert.Equal("400 " + IpcMessage.ErrorJson(null, "INVALID_JSON"), await PostIpc(http, server, "{"));
                string raw = await SendIpcBytes(server, "application/json", new byte[] { (byte)'{', 0xCF });
                Assert.StartsWith("HTTP/1.1 400 Bad Request", raw);
                Assert.EndsWith("{\"status\":\"error\",\"error\":\"Request body must be UTF-8: invalid byte 0xCF at offset 1\"}", raw);
                Assert.Equal(enabled ? 2 : 0, shown.Count);
                Assert.Empty(router.Incoming);
            }
        }

        [Fact]
        public void JsonView_WrongContentTypeOrMethod_IsShownWithReason()
        {
            var router = new ApiRouter("AutoCAD", "2021");
            var shown = new System.Collections.Concurrent.ConcurrentQueue<string>();
            router.JsonView = shown.Enqueue;
            string json = IpcMessage.CreateCommand("1", "PING").Json;
            var text = new System.Collections.Generic.Dictionary<string, string> { { "Content-Type", "text/plain; charset=utf-8" } };
            Assert.Equal(415, router.Handle(new HttpRequestInfo("POST", "/ipc", "HTTP/1.1", text, json)).StatusCode);
            var none = new System.Collections.Generic.Dictionary<string, string>();
            Assert.Equal(415, router.Handle(new HttpRequestInfo("POST", "/ipc", "HTTP/1.1", none, json)).StatusCode);
            var jsonType = new System.Collections.Generic.Dictionary<string, string> { { "Content-Type", "application/json" } };
            Assert.Equal(405, router.Handle(new HttpRequestInfo("PUT", "/ipc", "HTTP/1.1", jsonType, json)).StatusCode);
            Assert.Equal(405, router.Handle(new HttpRequestInfo("GET", "/ipc", "HTTP/1.1", none)).StatusCode);
            // Other paths are not IPC and are never shown.
            Assert.Equal(405, router.Handle(new HttpRequestInfo("POST", "/ping", "HTTP/1.1", jsonType, json)).StatusCode);
            Assert.Equal(new[]
            {
                "IPC JSON INVALID (415 INVALID_CONTENT_TYPE: Content-Type is 'text/plain; charset=utf-8', use application/json): " + json,
                "IPC JSON INVALID (415 INVALID_CONTENT_TYPE: no Content-Type header, use application/json): " + json,
                "IPC JSON INVALID (405 METHOD_NOT_ALLOWED: PUT /ipc, use POST): " + json,
                "IPC JSON INVALID (405 METHOD_NOT_ALLOWED: GET /ipc, use POST): <empty body>"
            }, shown.ToArray());
            Assert.Empty(router.Incoming);
        }

        // The offset reported for a body that is not UTF-8 agrees with the strict decoder the server uses.
        [Theory]
        [InlineData("7B 7D", -1)]
        [InlineData("D0 9F D1 80", -1)]                  // "Пр" in UTF-8
        [InlineData("E4 B8 AD F0 9F 98 80", -1)]         // 3- and 4-byte sequences
        [InlineData("7B CF F0 7D", 1)]                   // Windows-1251
        [InlineData("41 C0 80", 1)]                      // overlong
        [InlineData("41 E0 80 80", 1)]                   // overlong
        [InlineData("41 ED A0 80", 1)]                   // UTF-16 surrogate
        [InlineData("41 F4 90 80 80", 1)]                // above U+10FFFF
        [InlineData("41 42 E4 B8", 2)]                   // truncated at the end
        [InlineData("80", 0)]                            // lone continuation byte
        [InlineData("41 F5 80 80 80", 1)]
        public void InvalidUtf8Offset_MatchesStrictDecoder(string hex, int expected)
        {
            byte[] bytes = hex.Split(' ').Select(b => Convert.ToByte(b, 16)).ToArray();
            Assert.Equal(expected, LocalHttpServer.FindInvalidUtf8(bytes));
            bool strictOk = true;
            try { new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { strictOk = false; }
            Assert.Equal(expected < 0, strictOk);
        }

        [Fact]
        public async Task JsonView_BodyNotUtf8_IsShownWithInvalidByte()
        {
            var router = new ApiRouter("AutoCAD", "2021");
            var shown = new System.Collections.Concurrent.ConcurrentQueue<string>();
            router.JsonView = shown.Enqueue;
            using (var server = JsonViewServer(router))
            {
                server.Start();
                // "Привет" sent in Windows-1251 instead of UTF-8 - a typical legacy client mistake.
                byte[] body = Encoding.ASCII.GetBytes("{\"id\":\"1\",\"text\":\"")
                    .Concat(new byte[] { 0xCF, 0xF0, 0xE8, 0xE2, 0xE5, 0xF2 })
                    .Concat(Encoding.ASCII.GetBytes("\"}")).ToArray();
                string raw = await SendIpcBytes(server, "application/json", body);
                Assert.StartsWith("HTTP/1.1 400 Bad Request", raw);
                Assert.Equal(new[]
                {
                    "IPC JSON INVALID (400 Bad Request: Request body must be UTF-8: invalid byte 0xCF at offset 18): " +
                    "{\"id\":\"1\",\"text\":\"������\"}"
                }, shown.ToArray());
                Assert.Empty(router.Incoming);
            }
        }

        [Fact]
        public async Task JsonView_ServerLevelRejections_AreShownWithReason()
        {
            var router = new ApiRouter("AutoCAD", "2021");
            var shown = new System.Collections.Concurrent.ConcurrentQueue<string>();
            router.JsonView = shown.Enqueue;
            using (var server = JsonViewServer(router))
            {
                server.Start();
                Assert.StartsWith("HTTP/1.1 413 ", await HttpTestHelpers.SendRaw(server.Port, Encoding.ASCII.GetBytes(
                    "POST /ipc HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\nContent-Length: 2000000\r\n\r\n")));
                Assert.StartsWith("HTTP/1.1 411 ", await HttpTestHelpers.SendRaw(server.Port, Encoding.ASCII.GetBytes(
                    "POST /ipc HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\n\r\n")));
                Assert.StartsWith("HTTP/1.1 403 ", await HttpTestHelpers.SendRaw(server.Port, Encoding.ASCII.GetBytes(
                    "POST /ipc HTTP/1.1\r\nHost: example.com\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{}")));
                // Rejections of other paths are not IPC and are not shown.
                Assert.StartsWith("HTTP/1.1 411 ", await HttpTestHelpers.SendRaw(server.Port, Encoding.ASCII.GetBytes(
                    "POST /ping HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n")));
                Assert.Equal(new[]
                {
                    "IPC JSON INVALID (413 Payload Too Large: Request body too large): <body not read>",
                    "IPC JSON INVALID (411 Length Required: Content-Length required): <body not read>",
                    "IPC JSON INVALID (403 Forbidden: Host not allowed): <body not read>"
                }, shown.ToArray());
                Assert.Empty(router.Incoming);
            }
        }

        [Fact]
        public async Task JsonView_FailingOutputOrObserver_DoesNotChangeResponse()
        {
            var router = new ApiRouter("AutoCAD", "2021") { JsonView = line => { throw new InvalidOperationException("output failed"); } };
            using (var server = new LocalHttpServer(0, router.Handle, null)
                   { RequestRejected = (request, response) => { throw new InvalidOperationException("observer failed"); } })
            {
                server.Start();
                Assert.StartsWith("HTTP/1.1 400 Bad Request", await SendIpcBytes(server, "application/json", new byte[] { 0xFF }));
            }
            var headers = new System.Collections.Generic.Dictionary<string, string> { { "Content-Type", "application/json" } };
            Assert.Equal(400, router.Handle(new HttpRequestInfo("POST", "/ipc", "HTTP/1.1", headers, "{")).StatusCode);
            Assert.Empty(router.Incoming);
        }

        [Fact]
        public void JsonView_FailingOutput_DoesNotChangeResponseOrQueue()
        {
            var router = new ApiRouter("AutoCAD", "2021") { JsonView = json => { throw new InvalidOperationException("output failed"); } };
            var headers = new System.Collections.Generic.Dictionary<string, string> { { "Content-Type", "application/json" } };
            var response = router.Handle(new HttpRequestInfo("POST", "/ipc", "HTTP/1.1", headers, PingJson));
            Assert.Equal(202, response.StatusCode);
            Assert.Equal("123", router.Incoming.Single().Id);
        }
    }
}
