using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AutoCADHttp.Http;
using Xunit;

namespace AutoCADHttp.Tests
{
    public class WidgetFilesTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "AcadHttpWidgets-" + Guid.NewGuid().ToString("N"));

        public WidgetFilesTests() { Directory.CreateDirectory(_root); }
        public void Dispose() { Directory.Delete(_root, true); }

        private HttpResponseInfo Get(string target, string method = "GET")
        {
            return new ApiRouter("AutoCAD", "2021", widgetsDirectory: _root).Handle(
                new HttpRequestInfo(method, target, "HTTP/1.1", new Dictionary<string, string>()));
        }

        [Fact]
        public void BareWidgetRoot_RedirectsToSlash_SoRelativeAssetsResolveUnderWidgets()
        {
            File.WriteAllText(Path.Combine(_root, "index.html"), "<html>widget</html>");
            var response = Get("/widgets?version=1");
            Assert.Equal(308, response.StatusCode);
            Assert.Equal("/widgets/?version=1", response.Headers["Location"]);
        }

        [Theory]
        [InlineData("/widgets/")]
        [InlineData("/widgets/index.html?cache=1")]
        public void WidgetRoot_ServesIndex_WithoutQueuingIpc(string path)
        {
            File.WriteAllText(Path.Combine(_root, "index.html"), "<html>widget</html>", new UTF8Encoding(false));
            var response = Get(path);
            Assert.Equal(200, response.StatusCode);
            Assert.Equal("text/html; charset=utf-8", response.ContentType);
            Assert.Equal("<html>widget</html>", Encoding.UTF8.GetString(response.BodyBytes));
        }

        [Theory]
        [InlineData(".js", "application/javascript; charset=utf-8")]
        [InlineData(".css", "text/css; charset=utf-8")]
        [InlineData(".svg", "image/svg+xml")]
        [InlineData(".woff2", "font/woff2")]
        [InlineData(".bin", "application/octet-stream")]
        public void NestedFiles_HaveAppropriateMimeTypes(string extension, string contentType)
        {
            Directory.CreateDirectory(Path.Combine(_root, "nested"));
            File.WriteAllBytes(Path.Combine(_root, "nested", "asset" + extension), new byte[] { 0, 255, 128, 13, 10 });
            var response = Get("/widgets/nested/asset" + extension);
            Assert.Equal(200, response.StatusCode);
            Assert.Equal(contentType, response.ContentType);
            Assert.Equal(new byte[] { 0, 255, 128, 13, 10 }, response.BodyBytes);
        }

        [Fact]
        public async Task BinaryGetAndHead_WorkOverHttp_AndWidgetDoesNotBecomeAnIpcRoute()
        {
            byte[] bytes = { 137, 80, 78, 71, 0, 255, 128 };
            File.WriteAllBytes(Path.Combine(_root, "image.png"), bytes);
            var router = new ApiRouter("AutoCAD", "2021", widgetsDirectory: _root);
            using (var server = new LocalHttpServer(0, router.Handle, null))
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                server.Start();
                string url = "http://127.0.0.1:" + server.Port;
                using (var response = await http.GetAsync(url + "/widgets/image.png"))
                {
                    Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
                    Assert.Equal("image/png", response.Content.Headers.ContentType.MediaType);
                }
                using (var request = new HttpRequestMessage(HttpMethod.Head, url + "/widgets/image.png"))
                using (var response = await http.SendAsync(request))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
                    Assert.Empty(await response.Content.ReadAsByteArrayAsync());
                }
                using (var content = new StringContent(IpcMessage.CreateCommand("1", "LINE").Json, Encoding.UTF8, "application/json"))
                using (var response = await http.PostAsync(url + "/widgets/image.png", content))
                    Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
                Assert.Empty(router.Incoming);
                using (var response = await http.GetAsync(url + "/ipc"))
                    Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
                using (var response = await http.GetAsync(url + "/line"))
                    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
        }

        [Theory]
        [InlineData("/widgets/../secret.txt")]
        [InlineData("/widgets/%2e%2e/secret.txt")]
        [InlineData("/widgets/%2e%2e%2fsecret.txt")]
        [InlineData("/widgets/sub/../../secret.txt")]
        [InlineData("/widgets/%5c..%5csecret.txt")]
        [InlineData("/widgets/C:%5csecret.txt")]
        [InlineData("/widgets/index.html:stream")]
        [InlineData("/widgets/%00index.html")]
        public void EscapingPaths_AreForbidden(string path)
        {
            Assert.Equal(403, Get(path).StatusCode);
        }

        [Fact]
        public void MissingFilesAndUnconfiguredRoot_Return404()
        {
            Assert.Equal(404, Get("/widgets/missing.js").StatusCode);
            var router = new ApiRouter("AutoCAD", "2021");
            Assert.Equal(404, router.Handle(new HttpRequestInfo("GET", "/widgets", "HTTP/1.1", new Dictionary<string, string>())).StatusCode);
        }

#if !NETFRAMEWORK
        [Fact]
        public void LinkedFilesAndDirectories_CannotEscapeRoot()
        {
            string outside = Path.Combine(Path.GetTempPath(), "AcadHttpSecret-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outside);
            try
            {
                string secret = Path.Combine(outside, "secret.txt");
                File.WriteAllText(secret, "secret");
                File.CreateSymbolicLink(Path.Combine(_root, "linked.txt"), secret);
                Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), outside);
                Assert.Equal(403, Get("/widgets/linked.txt").StatusCode);
                Assert.Equal(403, Get("/widgets/linked/secret.txt").StatusCode);
                var linkedRoot = new ApiRouter("AutoCAD", "2021", widgetsDirectory: Path.Combine(_root, "linked"));
                Assert.Equal(403, linkedRoot.Handle(new HttpRequestInfo("GET", "/widgets/secret.txt", "HTTP/1.1", new Dictionary<string, string>())).StatusCode);
            }
            finally { Directory.Delete(outside, true); }
        }
#endif
    }
}
