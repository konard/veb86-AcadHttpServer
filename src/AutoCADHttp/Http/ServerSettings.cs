using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace AutoCADHttp.Http
{
    /// <summary>HTTP settings stored next to the assembly, independent of AutoCAD and the working directory.</summary>
    public sealed class ServerSettings
    {
        public const string FileName = "AutoCADHttp.settings.json";

        public IPAddress Address { get; private set; }
        public int Port { get; private set; }
        public string WidgetsDirectory { get; private set; }

        public static string FilePath
        {
            get { return Path.Combine(Path.GetDirectoryName(typeof(ServerSettings).Assembly.Location), FileName); }
        }

        public static ServerSettings Load()
        {
            return LoadFromDirectory(Path.GetDirectoryName(FilePath));
        }

        /// <summary>Load from an assembly directory. Missing files/fields retain the existing defaults.</summary>
        public static ServerSettings LoadFromDirectory(string directory)
        {
            directory = Path.GetFullPath(directory);
            string path = Path.Combine(directory, FileName);
            var settings = new ServerSettings
            {
                Address = IPAddress.Loopback,
                Port = LocalHttpServer.DefaultPort,
                // Compatibility for installations that configured widgets before JSON settings existed.
                WidgetsDirectory = Environment.GetEnvironmentVariable("ACADHTTP_WIDGETS_DIR")
            };
            string json;
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new StreamReader(file, new UTF8Encoding(false, true)))
                {
                    if (file.Length > LocalHttpServer.MaxBodyBytes)
                        throw new FormatException("Settings file is too large (maximum 1 MiB).");
                    json = reader.ReadToEnd();
                }
            }
            catch (FileNotFoundException) { return settings; }
            catch (DirectoryNotFoundException) { return settings; }
            catch (FormatException ex) { throw Invalid(path, ex.Message, ex); }
            catch (DecoderFallbackException ex) { throw Invalid(path, "Settings must be valid UTF-8.", ex); }

            try
            {
                IpcJson root = IpcJson.Parse(json);
                if (root.Kind != "object")
                    throw new FormatException("Settings must be a JSON object.");
                IpcJson field;
                if (root.Members.TryGetValue("address", out field))
                {
                    IPAddress address;
                    if (field.Kind != "string" || string.IsNullOrWhiteSpace(field.String))
                        throw new FormatException("address must be a loopback IP address or localhost.");
                    if (string.Equals(field.String, "localhost", StringComparison.OrdinalIgnoreCase))
                        address = IPAddress.Loopback;
                    else if (!IPAddress.TryParse(field.String, out address) || !IPAddress.IsLoopback(address))
                        throw new FormatException("address must be a loopback IP address or localhost.");
                    settings.Address = address;
                }
                if (root.Members.TryGetValue("port", out field))
                {
                    int port;
                    if (field.Kind != "number" || !int.TryParse(field.Raw, NumberStyles.None, CultureInfo.InvariantCulture, out port) ||
                        port < 1 || port > 65535)
                        throw new FormatException("port must be an integer from 1 to 65535.");
                    settings.Port = port;
                }
                if (root.Members.TryGetValue("widgetsDirectory", out field))
                {
                    if (field.Kind != "string" && field.Kind != "null")
                        throw new FormatException("widgetsDirectory must be a string or null.");
                    // Empty/null explicitly disables widgets; an omitted field keeps the environment fallback.
                    settings.WidgetsDirectory = string.IsNullOrWhiteSpace(field.String) ? null :
                        Path.GetFullPath(Path.Combine(directory, field.String));
                }
                return settings;
            }
            catch (FormatException ex) { throw Invalid(path, ex.Message, ex); }
            catch (ArgumentException ex) { throw Invalid(path, "Invalid widgetsDirectory: " + ex.Message, ex); }
            catch (NotSupportedException ex) { throw Invalid(path, "Invalid widgetsDirectory: " + ex.Message, ex); }
        }

        private static FormatException Invalid(string path, string message, Exception inner)
        {
            return new FormatException("Invalid settings in '" + path + "': " + message, inner);
        }
    }
}
