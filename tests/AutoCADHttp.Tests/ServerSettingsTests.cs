using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AutoCADHttp.Http;
using Xunit;

namespace AutoCADHttp.Tests
{
    public class ServerSettingsTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "AcadHttpSettings-" + Guid.NewGuid().ToString("N"));
        private string SettingsPath { get { return Path.Combine(_directory, ServerSettings.FileName); } }

        public ServerSettingsTests() { Directory.CreateDirectory(_directory); }
        public void Dispose() { Directory.Delete(_directory, true); }

        private ServerSettings Load(string json)
        {
            File.WriteAllText(SettingsPath, json, new UTF8Encoding(false));
            return ServerSettings.LoadFromDirectory(_directory);
        }

        [Fact]
        public void SettingsFilePath_IsBesideAssembly()
        {
            Assert.Equal(Path.Combine(Path.GetDirectoryName(typeof(ServerSettings).Assembly.Location),
                "AutoCADHttp.settings.json"), ServerSettings.FilePath);
        }

        [Fact]
        public void MissingFile_RetainsDefaultsAndExistingEnvironmentFallback()
        {
            var settings = ServerSettings.LoadFromDirectory(_directory);
            Assert.Equal(IPAddress.Loopback, settings.Address);
            Assert.Equal(5000, settings.Port);
            Assert.Equal(Environment.GetEnvironmentVariable("ACADHTTP_WIDGETS_DIR"), settings.WidgetsDirectory);
            Assert.False(settings.JsonView);
            Assert.False(File.Exists(SettingsPath));
        }

        [Fact]
        public void OmittedFields_RetainDefaults()
        {
            var settings = Load("{}");
            Assert.Equal(IPAddress.Loopback, settings.Address);
            Assert.Equal(5000, settings.Port);
            Assert.Equal(Environment.GetEnvironmentVariable("ACADHTTP_WIDGETS_DIR"), settings.WidgetsDirectory);
            Assert.False(settings.JsonView);
        }

        [Theory]
        [InlineData("0", false)]
        [InlineData("1", true)]
        public void JsonView_AcceptsZeroAndOne(string value, bool expected)
        {
            var settings = Load("{\"address\":\"127.0.0.1\",\"port\":5000,\"widgetsDirectory\":\"\",\"JSONView\":" + value + "}");
            Assert.Equal(expected, settings.JsonView);
            Assert.Equal(5000, settings.Port);
        }

        [Fact]
        public void ShippedSettingsTemplate_DisablesJsonView()
        {
            // AppContext.BaseDirectory: on net48 xUnit shadow-copies the assembly without content files.
            string template = Path.Combine(AppContext.BaseDirectory, "template", ServerSettings.FileName);
            File.Copy(template, SettingsPath);
            var settings = ServerSettings.LoadFromDirectory(_directory, name => null);
            Assert.False(settings.JsonView);
            Assert.Equal(5000, settings.Port);
            // The template documents externalIpc but leaves outgoing IPC disabled.
            Assert.Contains("\"externalIpc\"", File.ReadAllText(template));
            Assert.Null(settings.ExternalIpc);
            Assert.Null(settings.ExternalIpcSource);
        }

        [Theory]
        [InlineData("http://127.0.0.1:5001/ipc", "http://127.0.0.1:5001/ipc")]
        [InlineData("https://localhost:5443/ipc", "https://localhost:5443/ipc")]
        [InlineData("  http://127.0.0.1:5001/ipc  ", "http://127.0.0.1:5001/ipc")]
        public void ExternalIpc_IsReadFromJson(string value, string expected)
        {
            File.WriteAllText(SettingsPath, "{\"port\":5050,\"externalIpc\":\"" + value + "\"}", new UTF8Encoding(false));
            var settings = ServerSettings.LoadFromDirectory(_directory, name => null);
            Assert.Equal(expected, settings.ExternalIpc);
            Assert.Equal(ServerSettings.FileName, settings.ExternalIpcSource);
            // Other settings are unchanged by the new field.
            Assert.Equal(5050, settings.Port);
            Assert.Equal(IPAddress.Loopback, settings.Address);
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"externalIpc\":\"\"}")]
        [InlineData("{\"externalIpc\":\"   \"}")]
        [InlineData("{\"externalIpc\":null}")]
        public void MissingOrEmptyExternalIpc_DisablesOutgoingOnly(string json)
        {
            File.WriteAllText(SettingsPath, json, new UTF8Encoding(false));
            var settings = ServerSettings.LoadFromDirectory(_directory, name => null);
            Assert.Null(settings.ExternalIpc);
            Assert.Null(settings.ExternalIpcSource);
            Assert.Equal(5000, settings.Port);
        }

        [Theory]
        [InlineData("{\"externalIpc\":\"http://127.0.0.1:5001/ipc\"}")]
        [InlineData("{\"externalIpc\":\"\"}")]
        [InlineData("{}")]
        public void EnvironmentVariable_HasPriorityOverJson(string json)
        {
            File.WriteAllText(SettingsPath, json, new UTF8Encoding(false));
            string requested = null;
            var settings = ServerSettings.LoadFromDirectory(_directory, name =>
            {
                requested = name;
                return " http://127.0.0.1:6001/ipc ";
            });
            Assert.Equal("ACADHTTP_EXTERNAL_IPC", requested);
            Assert.Equal("http://127.0.0.1:6001/ipc", settings.ExternalIpc);
            Assert.Equal(ServerSettings.ExternalIpcVariable, settings.ExternalIpcSource);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        public void BlankEnvironmentVariable_KeepsJsonValue(string environment)
        {
            File.WriteAllText(SettingsPath, "{\"externalIpc\":\"http://127.0.0.1:5001/ipc\"}", new UTF8Encoding(false));
            var settings = ServerSettings.LoadFromDirectory(_directory, name => environment);
            Assert.Equal("http://127.0.0.1:5001/ipc", settings.ExternalIpc);
            Assert.Equal(ServerSettings.FileName, settings.ExternalIpcSource);
        }

        [Fact]
        public void EnvironmentVariable_IsUsedWithoutSettingsFile()
        {
            var settings = ServerSettings.LoadFromDirectory(_directory, name => "http://127.0.0.1:6001/ipc");
            Assert.Equal("http://127.0.0.1:6001/ipc", settings.ExternalIpc);
            Assert.Equal(5000, settings.Port);
        }

        [Fact]
        public void CustomSettings_ResolveRelativeWidgetsFromAssemblyDirectory()
        {
            var settings = Load("{\"address\":\"127.0.0.2\",\"port\":5050,\"widgetsDirectory\":\"widgets/вложенный каталог\"}");
            Assert.Equal(IPAddress.Parse("127.0.0.2"), settings.Address);
            Assert.Equal(5050, settings.Port);
            Assert.Equal(Path.Combine(_directory, "widgets", "вложенный каталог"), settings.WidgetsDirectory);
        }

        [Fact]
        public void AbsoluteWidgetPath_IsPreserved()
        {
            string absolute = Path.Combine(_directory, "external widgets");
            var settings = Load("{\"widgetsDirectory\":\"" + HttpResponseInfo.JsonEscape(absolute) + "\"}");
            Assert.Equal(absolute, settings.WidgetsDirectory);
        }

        [Theory]
        [InlineData("null")]
        [InlineData("\"\"")]
        [InlineData("\"   \"")]
        public void EmptyWidgetPath_DisablesWidgets(string value)
        {
            Assert.Null(Load("{\"widgetsDirectory\":" + value + "}").WidgetsDirectory);
        }

        [Theory]
        [InlineData("localhost", "127.0.0.1")]
        [InlineData("LOCALHOST", "127.0.0.1")]
        [InlineData("::1", "::1")]
        public void LocalAddresses_AreAccepted(string value, string expected)
        {
            Assert.Equal(IPAddress.Parse(expected), Load("{\"address\":\"" + value + "\"}").Address);
        }

        [Theory]
        [InlineData("", "JSON")]
        [InlineData("{", "JSON")]
        [InlineData("[]", "object")]
        [InlineData("null", "object")]
        [InlineData("{\"port\":5000,\"port\":5050}", "Duplicate")]
        [InlineData("{\"port\":\"5050\"}", "port")]
        [InlineData("{\"port\":null}", "port")]
        [InlineData("{\"port\":0}", "port")]
        [InlineData("{\"port\":-1}", "port")]
        [InlineData("{\"port\":65536}", "port")]
        [InlineData("{\"port\":5000.5}", "port")]
        [InlineData("{\"port\":5e3}", "port")]
        [InlineData("{\"address\":null}", "address")]
        [InlineData("{\"address\":123}", "address")]
        [InlineData("{\"address\":\"\"}", "address")]
        [InlineData("{\"address\":\"0.0.0.0\"}", "address")]
        [InlineData("{\"address\":\"::\"}", "address")]
        [InlineData("{\"address\":\"192.168.1.10\"}", "address")]
        [InlineData("{\"address\":\"example.com\"}", "address")]
        [InlineData("{\"widgetsDirectory\":42}", "widgetsDirectory")]
        [InlineData("{\"JSONView\":2}", "JSONView")]
        [InlineData("{\"JSONView\":-1}", "JSONView")]
        [InlineData("{\"JSONView\":1.0}", "JSONView")]
        [InlineData("{\"JSONView\":1e0}", "JSONView")]
        [InlineData("{\"JSONView\":\"1\"}", "JSONView")]
        [InlineData("{\"JSONView\":true}", "JSONView")]
        [InlineData("{\"JSONView\":null}", "JSONView")]
        [InlineData("{\"widgetsDirectory\":\"bad\\u0000path\"}", "widgetsDirectory")]
        [InlineData("{\"externalIpc\":5001}", "externalIpc")]
        [InlineData("{\"externalIpc\":true}", "externalIpc")]
        [InlineData("{\"externalIpc\":\"127.0.0.1:5001/ipc\"}", "externalIpc")]
        [InlineData("{\"externalIpc\":\"/ipc\"}", "externalIpc")]
        [InlineData("{\"externalIpc\":\"ftp://127.0.0.1:5001/ipc\"}", "externalIpc")]
        [InlineData("{\"externalIpc\":\"http://127.0.0.1:5001/api\"}", "externalIpc")]
        [InlineData("{\"externalIpc\":\"http://127.0.0.1:5001/ipc?x=1\"}", "externalIpc")]
        public void InvalidSettings_ReportFileAndReason(string json, string reason)
        {
            var error = Assert.Throws<FormatException>(() => Load(json));
            Assert.Contains(SettingsPath, error.Message);
            Assert.Contains(reason, error.Message);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(65535)]
        public void PortRange_IncludesEndpoints(int port)
        {
            Assert.Equal(port, Load("{\"port\":" + port + "}").Port);
        }

        [Fact]
        public void Utf8Bom_IsAccepted()
        {
            File.WriteAllText(SettingsPath, "{\"port\":5050}", new UTF8Encoding(true));
            Assert.Equal(5050, ServerSettings.LoadFromDirectory(_directory).Port);
        }

        [Fact]
        public void InvalidUtf8_IsRejectedWithFilePath()
        {
            File.WriteAllBytes(SettingsPath, new byte[] { 255, 255 });
            var error = Assert.Throws<FormatException>(() => ServerSettings.LoadFromDirectory(_directory));
            Assert.Contains(SettingsPath, error.Message);
            Assert.Contains("UTF-8", error.Message);
        }

        [Fact]
        public void OversizedSettings_AreRejectedBeforeParsing()
        {
            File.WriteAllText(SettingsPath, new string(' ', LocalHttpServer.MaxBodyBytes + 1));
            var error = Assert.Throws<FormatException>(() => ServerSettings.LoadFromDirectory(_directory));
            Assert.Contains(SettingsPath, error.Message);
            Assert.Contains("too large", error.Message);
        }

        [Fact]
        public void Load_ReadsEditsOnNextCall()
        {
            Assert.Equal(5050, Load("{\"port\":5050}").Port);
            Assert.Equal(5051, Load("{\"port\":5051}").Port);
        }

        [Theory]
        [InlineData("127.0.0.2")]
        [InlineData("::1")]
        public async Task ConfiguredAddress_ServesPingWidgetsAndRejectsForeignHosts(string address)
        {
            var settings = Load("{\"address\":\"" + address + "\",\"widgetsDirectory\":\"widgets\"}");
            Directory.CreateDirectory(settings.WidgetsDirectory);
            File.WriteAllText(Path.Combine(settings.WidgetsDirectory, "index.html"), "configured widget");
            var router = new ApiRouter("AutoCAD", "2021", widgetsDirectory: settings.WidgetsDirectory);
            using (var server = new LocalHttpServer(settings.Address, 0, router.Handle, null))
            using (var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) })
            {
                server.Start();
                Assert.Equal(settings.Address, server.Address);
                string listing = await http.GetStringAsync(server.BaseUrl + "widgets/");
                Assert.Contains("href=\"index.html\"", listing);
                Assert.DoesNotContain("configured widget", listing);
                Assert.Equal("configured widget", await http.GetStringAsync(server.BaseUrl + "widgets/index.html"));
                using (var response = await http.GetAsync(server.BaseUrl + "ping"))
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using (var request = new HttpRequestMessage(HttpMethod.Get, server.BaseUrl + "ping"))
                {
                    request.Headers.Host = "foreign.example";
                    using (var response = await http.SendAsync(request))
                        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                }
                Assert.True(server.Stop());
                Assert.Equal(StartResult.Started, server.Start());
                using (var response = await http.GetAsync(server.BaseUrl + "ping"))
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }

        [Fact]
        public void ServerAlsoRejectsNonLoopbackAddresses()
        {
            Assert.Throws<ArgumentException>(() => new LocalHttpServer(IPAddress.Any, 0, r => null, null));
        }
    }
}
