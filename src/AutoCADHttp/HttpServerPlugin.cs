using System;
using System.Globalization;
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
        /// <summary>Year of the running AutoCAD (2020-2024) reported by GET /ping; must be initialized before Router.</summary>
        public static readonly string ApplicationVersion = DetectApplicationVersion();

        // The single IPC service of the AutoCAD process: one incoming queue, one dispatcher, one outgoing transport.
        // It exists independently of HTTPSTART/HTTPSTOP, so modules may register before the server starts.
        private static readonly IpcService Service = CreateService();
        private static ApiRouter Router = new ApiRouter(AppName, ApplicationVersion, Service.Incoming);

        /// <summary>
        /// The public IPC service shared by all AutoCAD modules. Modules register commands / subscribe to events
        /// (dispose the results on unload), send commands and publish events through it; they never create HTTP
        /// servers, clients or queues of their own.
        /// </summary>
        public static IIpcService Ipc { get { return Service; } }

        /// <summary>Compatibility: the single dispatcher behind <see cref="Ipc"/>.</summary>
        public static IpcDispatcher Dispatcher { get { return Service.Dispatcher; } }

        /// <summary>Compatibility: same as <c>Ipc.TrySend</c> - queue a command, response or event without waiting for HTTP.</summary>
        public static bool QueueOutgoing(IpcMessage message)
        {
            return Service.TrySend(message);
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
            CommandLineLog.Write("AutoCADHttp loaded (AutoCAD " + ApplicationVersion + "). Commands: HTTPSTART, HTTPSTOP, HTTPSTATUS.");
            CommandLineLog.Write("Settings: " + ServerSettings.FilePath + " (read on HTTPSTART).");
        }

        public void Terminate()
        {
            // AutoCAD is shutting down: release the port, do not touch the editor.
            try { Application.Idle -= OnIdle; } catch { }
            try { Server.Stop(); } catch { }
            // Stops the transport, fails pending SendCommandAsync calls and releases every registration.
            try { Service.Dispose(); } catch { }
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
				CommandLineLog.Write("Widgets directory: " +
					(settings.WidgetsDirectory ?? "<disabled>"));
				CommandLineLog.Write("Widgets directory exists: " +
					(settings.WidgetsDirectory != null &&
					 System.IO.Directory.Exists(settings.WidgetsDirectory)));
                var router = CreateRouter(settings.WidgetsDirectory);
                // JSONView = 1: print every accepted /ipc JSON. Post only queues the text for Application.Idle,
                // so the HTTP thread neither touches the AutoCAD API nor waits for the output.
                if (settings.JsonView)
                    router.JsonView = json => CommandLineLog.Post("IPC JSON: " + json);
                CommandLineLog.Write("JSONView: " + (settings.JsonView ? "1" : "0"));
                var server = new LocalHttpServer(settings.Address, settings.Port, router.Handle, CommandLineLog.Post)
                {
                    Verbose = Environment.GetEnvironmentVariable("ACADHTTP_VERBOSE") == "1"
                };
                Server.Dispose();
                Router = router;
                Server = server;
                Server.Start();
                StartOutbox(settings);
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
                // Order: stop accepting HTTP, then the outgoing transport (fails pending SendCommandAsync).
                // Module registrations are kept for the next HTTPSTART.
                bool stopped = Server.Stop();
                Service.StopTransport("IPC transport stopped by HTTPSTOP.");
                if (stopped)
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

                CommandLineLog.Write("Incoming IPC queued: " + Service.Incoming.Count);
                CommandLineLog.Write("Outgoing IPC: " + (!Service.IsTransportRunning ? "DISABLED" :
                    "RUNNING " + Service.ExternalEndpoint + " (queued: " + Service.OutgoingQueued + ")"));
                CommandLineLog.Write("IPC commands registered: " + Service.Dispatcher.CommandCount +
                                     "; event subscriptions: " + Service.Dispatcher.SubscriptionCount +
                                     "; commands awaiting responses: " + Service.PendingCommandCount);
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
                return new ApiRouter(AppName, ApplicationVersion, Service.Incoming, widgetsDirectory);
            }
            catch (System.Exception ex)
            {
                CommandLineLog.Post("ERROR: invalid widgets directory: " + ex.Message);
                return new ApiRouter(AppName, ApplicationVersion, Service.Incoming);
            }
        }

        private static string DetectApplicationVersion()
        {
            try
            {
                return AcadRelease.YearName(Application.Version);
            }
            catch (System.Exception)
            {
                return AcadRelease.Fallback;
            }
        }

        private static IpcService CreateService()
        {
            var service = new IpcService(CommandLineLog.Post);
            // A transport demonstration only. Drawing handlers belong in the modules that own them.
            service.RegisterCommand(IpcDispatcher.ApplicationModule, "PING", m => IpcMessage.CreateResponse(m.Id));
            return service;
        }

        private static void OnIdle(object sender, EventArgs e)
        {
            // AutoCAD main thread: bounded, so a continuous stream of messages cannot block AutoCAD.
            try
            {
                if (Server.IsRunning)
                    Service.Drain(32);
            }
            catch
            {
                // Never let an exception escape from an AutoCAD event handler.
            }
        }

        private static void StartOutbox(ServerSettings settings)
        {
            Service.StopTransport("IPC transport restarted.");
            if (settings.ExternalIpc == null)
            {
                CommandLineLog.Write("IPC outgoing disabled: set externalIpc in " + ServerSettings.FileName + " or " +
                                     ServerSettings.ExternalIpcVariable + " to the external /ipc URL.");
                return;
            }
            Uri endpoint;
            if (!IpcOutbox.TryParseEndpoint(settings.ExternalIpc, out endpoint))
            {
                // Misconfigured/unavailable external transport must never disable the incoming HTTP server.
                CommandLineLog.Write("ERROR: cannot configure outgoing IPC from " + settings.ExternalIpcSource + " ('" +
                                     settings.ExternalIpc + "'): must be an absolute HTTP(S) URL with the path /ipc. Outgoing IPC disabled.");
                return;
            }
            try
            {
                Service.StartTransport(new IpcOutbox(endpoint, CommandLineLog.Post) { Verbose = Server.Verbose });
                CommandLineLog.Write("IPC outgoing: " + endpoint + " (from " + settings.ExternalIpcSource + ").");
            }
            catch (System.Exception ex)
            {
                CommandLineLog.Write("ERROR: cannot configure outgoing IPC: " + ex.Message);
            }
        }

        private static string BaseUrl()
        {
            return Server.BaseUrl;
        }
    }
}
