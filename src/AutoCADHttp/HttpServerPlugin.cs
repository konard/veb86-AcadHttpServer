using System;
using System.Globalization;
using System.Net.Sockets;
using Autodesk.AutoCAD.Runtime;
using AutoCADHttp.Http;

[assembly: ExtensionApplication(typeof(AutoCADHttp.HttpServerPlugin))]
[assembly: CommandClass(typeof(AutoCADHttp.HttpServerPlugin))]

namespace AutoCADHttp
{
    /// <summary>
    /// AutoCAD entry point: HTTPSTART / HTTPSTOP / HTTPSTATUS commands.
    /// The server is NOT started on NETLOAD - only by the HTTPSTART command.
    /// </summary>
    public sealed class HttpServerPlugin : IExtensionApplication
    {
        public const string AppName = "AutoCAD";
        public const string ApplicationVersion = "2021";

        // One server per AutoCAD process (shared by all open drawings).
        private static readonly LocalHttpServer Server = new LocalHttpServer(
            LocalHttpServer.DefaultPort,
            new ApiRouter(AppName, ApplicationVersion).Handle,
            CommandLineLog.Post)
        {
            // Set the environment variable ACADHTTP_VERBOSE=1 before starting AutoCAD
            // to print every HTTP request to the command line.
            Verbose = Environment.GetEnvironmentVariable("ACADHTTP_VERBOSE") == "1"
        };

        public void Initialize()
        {
            CommandLineLog.Attach();
            CommandLineLog.Write("AutoCADHttp loaded. Commands: HTTPSTART, HTTPSTOP, HTTPSTATUS.");
        }

        public void Terminate()
        {
            // AutoCAD is shutting down: release the port, do not touch the editor.
            try { Server.Stop(); } catch { }
            try { CommandLineLog.Detach(); } catch { }
        }

        [CommandMethod("HTTPSTART", CommandFlags.Modal)]
        public void HttpStart()
        {
            try
            {
                StartResult result = Server.Start();
                if (result == StartResult.AlreadyRunning)
                {
                    CommandLineLog.Write("Server is already running on " + BaseUrl() + " - a second server was not started.");
                    return;
                }

                CommandLineLog.Write("Server started: " + BaseUrl() + " (test: " + BaseUrl() + "ping)");
            }
            catch (SocketException ex)
            {
                string reason = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? "port " + Server.Port + " is already in use by another program"
                    : ex.SocketErrorCode == SocketError.AccessDenied
                        ? "access to port " + Server.Port + " denied (reserved or blocked port)"
                        : ex.Message;
                CommandLineLog.Write("ERROR: cannot start server on " + Server.Address + ":" + Server.Port + ": " + reason +
                                     " [SocketError." + ex.SocketErrorCode + "]");
            }
            catch (System.Exception ex)
            {
                CommandLineLog.Write("ERROR: cannot start server: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        [CommandMethod("HTTPSTOP", CommandFlags.Modal)]
        public void HttpStop()
        {
            try
            {
                if (Server.Stop())
                    CommandLineLog.Write("Server stopped. Port " + Server.Port + " released.");
                else
                    CommandLineLog.Write("Server is not running.");
            }
            catch (System.Exception ex)
            {
                CommandLineLog.Write("ERROR: cannot stop server: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        [CommandMethod("HTTPSTATUS", CommandFlags.Modal)]
        public void HttpStatus()
        {
            try
            {
                CommandLineLog.Write("HTTP Server: " + (Server.IsRunning ? "RUNNING" : "STOPPED"));
                CommandLineLog.Write("Address: " + Server.Address);
                CommandLineLog.Write("Port: " + Server.Port);

                DateTime? startedAt = Server.StartedAtUtc;
                if (startedAt.HasValue)
                {
                    TimeSpan uptime = DateTime.UtcNow - startedAt.Value;
                    CommandLineLog.Write("Started: " + startedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                                         " (uptime " + ((int)uptime.TotalHours).ToString(CultureInfo.InvariantCulture) +
                                         uptime.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture) + ")");
                    CommandLineLog.Write("URL: " + BaseUrl() + "ping");
                }

                CommandLineLog.Write("Requests served: " + Server.RequestCount.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(Server.LastError))
                    CommandLineLog.Write("Last error: " + Server.LastError);
            }
            catch (System.Exception ex)
            {
                CommandLineLog.Write("ERROR: cannot get server status: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string BaseUrl()
        {
            return "http://" + Server.Address + ":" + Server.Port + "/";
        }
    }
}
