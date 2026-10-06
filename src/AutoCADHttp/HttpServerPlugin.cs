using System;
using System.Globalization;
using System.Threading;
using System.Net.Sockets;
using Autodesk.AutoCAD.Runtime;
using AutoCADHttp.Http;
using Application = Autodesk.AutoCAD.ApplicationServices.Core.Application;

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

        private static ApiRouter Router = new ApiRouter(AppName, ApplicationVersion);
        private static readonly IpcDispatcher CommandDispatcher = CreateDispatcher();
        private static IpcOutbox _outbox;

        /// <summary>Register application handlers / subscribe to responses and events on the AutoCAD main thread.</summary>
        public static IpcDispatcher Dispatcher { get { return CommandDispatcher; } }

        /// <summary>Queue an application command, response or event without waiting for HTTP.</summary>
        public static bool QueueOutgoing(IpcMessage message)
        {
            if (message == null)
                throw new ArgumentNullException("message");
            var outbox = Volatile.Read(ref _outbox);
            if (outbox != null)
                return outbox.TryEnqueue(message);
            CommandLineLog.Post("IPC outgoing " + message.Id + " unavailable: start the server with ACADHTTP_EXTERNAL_IPC configured.");
            return false;
        }

        // One server per AutoCAD process (shared by all open drawings).
        private static LocalHttpServer Server = new LocalHttpServer(
            LocalHttpServer.DefaultPort,
            Router.Handle,
            CommandLineLog.Post)
        {
            // Set the environment variable ACADHTTP_VERBOSE=1 before starting AutoCAD
            // to print every HTTP request to the command line.
            Verbose = Environment.GetEnvironmentVariable("ACADHTTP_VERBOSE") == "1"
        };

        public void Initialize()
        {
            CommandLineLog.Attach();
            Application.Idle += OnIdle;
            CommandLineLog.Write("AutoCADHttp loaded. Commands: HTTPSTART, HTTPSTOP, HTTPSTATUS.");
            CommandLineLog.Write("Settings: " + ServerSettings.FilePath + " (read on HTTPSTART).");
        }

        public void Terminate()
        {
            // AutoCAD is shutting down: release the port, do not touch the editor.
            try { Application.Idle -= OnIdle; } catch { }
            try { Server.Stop(); } catch { }
            try { StopOutbox(); } catch { }
            try { CommandLineLog.Detach(); } catch { }
        }

        [CommandMethod("HTTPSTART", CommandFlags.Modal)]
        public void HttpStart()
        {
            try
            {
                if (Server.IsRunning)
                {
                    CommandLineLog.Write("Server is already running on " + BaseUrl() + " - a second server was not started.");
                    return;
                }

                // Read only while stopped, so editing JSON cannot disrupt a running server.
                // Keep the incoming queue shared with the dispatcher across reconfiguration/restart.
                var settings = ServerSettings.Load();
                var router = CreateRouter(settings.WidgetsDirectory);
                var server = new LocalHttpServer(settings.Address, settings.Port, router.Handle, CommandLineLog.Post)
                {
                    Verbose = Environment.GetEnvironmentVariable("ACADHTTP_VERBOSE") == "1"
                };
                Server.Dispose();
                Router = router;
                Server = server;
                Server.Start();
                StartOutbox();
                CommandLineLog.Write("IPC: " + BaseUrl() + "ipc; widgets: " + BaseUrl() + "widgets/");
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
                StopOutbox();
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
                CommandLineLog.Write("Settings: " + ServerSettings.FilePath);

                DateTime? startedAt = Server.StartedAtUtc;
                if (startedAt.HasValue)
                {
                    TimeSpan uptime = DateTime.UtcNow - startedAt.Value;
                    CommandLineLog.Write("Started: " + startedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                                         " (uptime " + ((int)uptime.TotalHours).ToString(CultureInfo.InvariantCulture) +
                                         uptime.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture) + ")");
                    CommandLineLog.Write("URL: " + BaseUrl() + "ping");
                }

                CommandLineLog.Write("Incoming IPC queued: " + Router.Incoming.Count);
                var outbox = Volatile.Read(ref _outbox);
                CommandLineLog.Write("Outgoing IPC: " + (outbox == null ? "DISABLED" : "RUNNING (queued: " + outbox.PendingCount + ")"));
                CommandLineLog.Write("Requests served: " + Server.RequestCount.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(Server.LastError))
                    CommandLineLog.Write("Last error: " + Server.LastError);
            }
            catch (System.Exception ex)
            {
                CommandLineLog.Write("ERROR: cannot get server status: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static ApiRouter CreateRouter(string widgetsDirectory)
        {
            try
            {
                return new ApiRouter(AppName, ApplicationVersion, Router.Incoming, widgetsDirectory);
            }
            catch (System.Exception ex)
            {
                CommandLineLog.Post("ERROR: invalid widgets directory: " + ex.Message);
                return new ApiRouter(AppName, ApplicationVersion, Router.Incoming);
            }
        }

        private static IpcDispatcher CreateDispatcher()
        {
            var dispatcher = new IpcDispatcher(Router.Incoming, m => QueueOutgoing(m), CommandLineLog.Post);
            // A transport demonstration only. Future drawing handlers belong in the application layer.
            dispatcher.Register("PING", m => IpcMessage.CreateResponse(m.Id));
            return dispatcher;
        }

        private static void OnIdle(object sender, EventArgs e)
        {
            if (Server.IsRunning)
                CommandDispatcher.Drain();
        }

        private static void StartOutbox()
        {
            string endpoint = Environment.GetEnvironmentVariable("ACADHTTP_EXTERNAL_IPC");
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                CommandLineLog.Write("IPC outgoing disabled: set ACADHTTP_EXTERNAL_IPC to the external /ipc URL.");
                return;
            }
            try
            {
                Volatile.Write(ref _outbox, new IpcOutbox(new Uri(endpoint), CommandLineLog.Post) { Verbose = Server.Verbose });
            }
            catch (System.Exception ex)
            {
                // Misconfigured/unavailable external transport must never disable the incoming HTTP server.
                CommandLineLog.Write("ERROR: cannot configure outgoing IPC: " + ex.Message);
            }
        }

        private static void StopOutbox()
        {
            var outbox = Interlocked.Exchange(ref _outbox, null);
            if (outbox != null)
                outbox.Dispose();
        }

        private static string BaseUrl()
        {
            return Server.BaseUrl;
        }
    }
}
