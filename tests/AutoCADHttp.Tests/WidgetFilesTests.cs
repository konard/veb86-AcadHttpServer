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
        [InlineData("/widgets/index.html")]
        [InlineData("/widgets/index.html?cache=1")]
        public void ExplicitIndexPath_ServesFile(string path)
        {
            File.WriteAllText(Path.Combine(_root, "index.html"), "<html>widget</html>", new UTF8Encoding(false));
            var response = Get(path);
            Assert.Equal(200, response.StatusCode);
            Assert.Equal("text/html; charset=utf-8", response.ContentType);
            Assert.Equal("<html>widget</html>", Encoding.UTF8.GetString(response.BodyBytes));
        }

        [Fact]
        public void WidgetRoot_ListsDirectories_InsteadOfServingIndex()
        {
            File.WriteAllText(Path.Combine(_root, "index.html"), "<html>root widget</html>");
            foreach (string name in new[] { "managerGRIST", "catalog", "common" })
                Directory.CreateDirectory(Path.Combine(_root, name));

            var router = new ApiRouter("AutoCAD", "2021", widgetsDirectory: _root);
            var response = router.Handle(new HttpRequestInfo("GET", "/widgets/", "HTTP/1.1", new Dictionary<string, string>()));
            Assert.Equal(200, response.StatusCode);
            Assert.Equal("text/html; charset=utf-8", response.ContentType);
            Assert.Contains("<a href=\"catalog/\">catalog/</a>", response.Body);
            Assert.Contains("<a href=\"common/\">common/</a>", response.Body);
            Assert.Contains("<a href=\"managerGRIST/\">managerGRIST/</a>", response.Body);
            Assert.Contains("<a href=\"index.html\">index.html</a>", response.Body);
            Assert.DoesNotContain("root widget", response.Body);
            Assert.DoesNotContain("href=\"../\"", response.Body);
            Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"]);
            Assert.Empty(router.Incoming);
        }

        [Theory]
        [InlineData("/widgets/managerGRIST/")]
        [InlineData("/widgets/managerGRIST/?cache=1")]
        public void NestedDirectory_ListsItsFiles_EvenWithAnIndex(string path)
        {
            string nested = Path.Combine(_root, "managerGRIST");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "index.html"), "<html>nested widget</html>");
            File.WriteAllText(Path.Combine(nested, "manager.js"), "// manager");
            File.WriteAllText(Path.Combine(nested, "style.css"), "body {}");
            var response = Get(path);
            Assert.Equal(200, response.StatusCode);
            foreach (string name in new[] { "index.html", "manager.js", "style.css" })
                Assert.Contains("<a href=\"" + name + "\">" + name + "</a>", response.Body);
            Assert.DoesNotContain("nested widget", response.Body);
            Assert.Contains("<a href=\"../\">../</a>", response.Body);
            Assert.Equal("<html>nested widget</html>", Encoding.UTF8.GetString(Get("/widgets/managerGRIST/index.html").BodyBytes));
        }

        [Theory]
        [InlineData("/widgets/", "")]
        [InlineData("/widgets/empty/", "empty")]
        public void EmptyDirectory_ReturnsAListing_WithoutRequiringIndex(string path, string directory)
        {
            Directory.CreateDirectory(Path.Combine(_root, directory));
            var response = Get(path);
            Assert.Equal(200, response.StatusCode);
            Assert.Contains("<h1>Index of " + path + "</h1>", response.Body);
            Assert.Equal(404, Get(path + "index.html").StatusCode);
        }

        [Theory]
        [InlineData("/widgets/nested", "/widgets/nested/")]
        [InlineData("/widgets/nested?cache=1", "/widgets/nested/?cache=1")]
        [InlineData("/widgets/nested%20space?cache=1", "/widgets/nested%20space/?cache=1")]
        public void BareDirectory_RedirectsToSlash_ForRelativeLinks(string path, string location)
        {
            Directory.CreateDirectory(Path.Combine(_root, "nested"));
            Directory.CreateDirectory(Path.Combine(_root, "nested space"));
            var response = Get(path);
            Assert.Equal(308, response.StatusCode);
            Assert.Equal(location, response.Headers["Location"]);
        }

        [Fact]
        public void Listing_EscapesNamesAndUrls_AndSortsDirectoriesBeforeFiles()
        {
            Directory.CreateDirectory(Path.Combine(_root, "z folder"));
            string name = "a & ' # % виджет.txt";
            File.WriteAllText(Path.Combine(_root, name), "safe file");
            File.WriteAllText(Path.Combine(_root, "b.txt"), "second file");
            var response = Get("/widgets/");
            string link = "<a href=\"" + Uri.EscapeDataString(name) + "\">" + WebUtility.HtmlEncode(name) + "</a>";
            Assert.Contains(link, response.Body);
            Assert.Contains("<a href=\"z%20folder/\">z folder/</a>", response.Body);
            Assert.True(response.Body.IndexOf("z%20folder/", StringComparison.Ordinal) < response.Body.IndexOf(link, StringComparison.Ordinal));
            Assert.True(response.Body.IndexOf(link, StringComparison.Ordinal) < response.Body.IndexOf("href=\"b.txt\"", StringComparison.Ordinal));
            Assert.Equal("safe file", Encoding.UTF8.GetString(Get("/widgets/" + Uri.EscapeDataString(name)).BodyBytes));
        }

        [Fact]
        public void Listing_EscapesDirectoryPath()
        {
            Directory.CreateDirectory(Path.Combine(_root, "a & ' folder"));
            var response = Get("/widgets/a%20%26%20%27%20folder/");
            Assert.Equal(200, response.StatusCode);
            Assert.Contains("<h1>Index of /widgets/a &amp; &#39; folder/</h1>", response.Body);
            Assert.DoesNotContain(_root, response.Body);
        }

        [Fact]
        public async Task DirectoryGetAndHead_WorkOverHttp_WithoutQueuingIpc()
        {
            Directory.CreateDirectory(Path.Combine(_root, "nested"));
            File.WriteAllText(Path.Combine(_root, "nested", "index.html"), "widget");
            var router = new ApiRouter("AutoCAD", "2021", widgetsDirectory: _root);
            using (var server = new LocalHttpServer(0, router.Handle, null))
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                server.Start();
                string url = "http://127.0.0.1:" + server.Port + "/widgets/nested/";
                byte[] listing;
                using (var response = await http.GetAsync(url))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    listing = await response.Content.ReadAsByteArrayAsync();
                    Assert.Contains("href=\"index.html\"", Encoding.UTF8.GetString(listing));
                }
                using (var request = new HttpRequestMessage(HttpMethod.Head, url))
                using (var response = await http.SendAsync(request))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal("text/html", response.Content.Headers.ContentType.MediaType);
                    Assert.Equal(listing.Length, response.Content.Headers.ContentLength);
                    Assert.Empty(await response.Content.ReadAsByteArrayAsync());
                }
                Assert.Empty(router.Incoming);
            }
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
        [InlineData("/widgets/../")]
        [InlineData("/widgets/%2e%2e/")]
        [InlineData("/widgets/%2e/")]
        [InlineData("/widgets/sub/../../")]
        public void EscapingPaths_AreForbidden(string path)
        {
            Assert.Equal(403, Get(path).StatusCode);
        }

        [Fact]
        public void MissingFilesAndUnconfiguredRoot_Return404()
        {
            Assert.Equal(404, Get("/widgets/missing.js").StatusCode);
            Assert.Equal(404, Get("/widgets/missing/").StatusCode);
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
                Assert.Equal(403, Get("/widgets/linked/").StatusCode);
                Assert.Equal(403, Get("/widgets/linked").StatusCode);
                var listing = Get("/widgets/");
                Assert.Equal(200, listing.StatusCode);
                Assert.DoesNotContain("linked", listing.Body);
                var linkedRoot = new ApiRouter("AutoCAD", "2021", widgetsDirectory: Path.Combine(_root, "linked"));
                Assert.Equal(403, linkedRoot.Handle(new HttpRequestInfo("GET", "/widgets/secret.txt", "HTTP/1.1", new Dictionary<string, string>())).StatusCode);
                Assert.Equal(403, linkedRoot.Handle(new HttpRequestInfo("GET", "/widgets/", "HTTP/1.1", new Dictionary<string, string>())).StatusCode);
            }
            finally { Directory.Delete(outside, true); }
        }

        [Fact]
        public void Listing_HtmlEncodesMarkupInFileNames()
        {
            // Windows forbids these characters in file names; HTML escaping is still needed on Unix.
            if (Path.DirectorySeparatorChar == '\\') return;
            string name = "<img src=x onerror=alert(1)>\"?#%.txt";
            File.WriteAllText(Path.Combine(_root, name), "safe");
            var response = Get("/widgets/");
            Assert.Contains(WebUtility.HtmlEncode(name), response.Body);
            Assert.Contains(Uri.EscapeDataString(name), response.Body);
            Assert.DoesNotContain("<img", response.Body);
        }
#endif
    }
}
