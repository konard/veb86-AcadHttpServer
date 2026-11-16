using System;
using System.Threading;
using System.Threading.Tasks;
using AutoCADHttp.Http;

namespace StandaloneHost
{
    // Usage: dotnet run --project examples/StandaloneHost -- [port] [widgets-directory] [external-/ipc-url]
    // Without the third argument the external /ipc comes from ACADHTTP_EXTERNAL_IPC or "externalIpc" in the JSON settings.
    internal static class Program
    {
        private static async Task Main(string[] args)
        {
            var settings = ServerSettings.Load();
            int port = args.Length > 0 ? int.Parse(args[0]) : settings.Port;
            string widgets = args.Length > 1 ? args[1] : settings.WidgetsDirectory;
            string external = args.Length > 2 ? args[2] : settings.ExternalIpc;
            Action<string> log = m => Console.WriteLine("[HTTP] " + m);

            // The same single service the AutoCAD plugin exposes as HttpServerPlugin.Ipc.
            using (var ipc = new IpcService(log))
            using (var stop = new CancellationTokenSource())
            {
                ipc.RegisterCommand(IpcDispatcher.ApplicationModule, "PING", m => IpcMessage.CreateResponse(m.Id));
                ipc.Dispatcher.MessageReceived += m => log("IPC received: " + m.Json);
                // Modules register before the HTTP server starts and keep their registrations across restarts.
                using (var devices = DemoModules.CreateDevices(ipc, log))
                using (var reports = DemoModules.CreateReports(ipc, log))
                {
                    var router = new ApiRouter("AutoCAD", "2021", ipc.Incoming, widgets);
                    if (settings.JsonView)
                        router.JsonView = json => log("IPC JSON: " + json);
                    using (var server = new LocalHttpServer(settings.Address, port, router.Handle, log) { Verbose = true })
                    {
                        log("Start(): " + server.Start() + " " + server.BaseUrl + "ping");
                        log("second Start(): " + server.Start());
                        if (string.IsNullOrWhiteSpace(external))
                            log("IPC outgoing disabled: external /ipc is not configured.");
                        else
                        {
                            ipc.StartTransport(new IpcOutbox(new Uri(external), log) { Verbose = true });
                            log("IPC outgoing: " + ipc.ExternalEndpoint);
                        }
                        // Standalone application context, separate from HTTP request handlers. AutoCAD uses Application.Idle.
                        Task application = Task.Run(async () =>
                        {
                            try
                            {
                                while (!stop.IsCancellationRequested)
                                {
                                    ipc.Drain(32);
                                    await Task.Delay(10, stop.Token);
                                }
                            }
                            catch (OperationCanceledException) { }
                        });
                        Console.WriteLine("Press Enter to stop...");
                        Console.ReadLine();
                        log("Stop(): " + server.Stop());
                        await ipc.StopTransport("IPC transport stopped by host shutdown.");
                        stop.Cancel();
                        await application;
                    }
                }
            }
        }
    }

    /// <summary>Two independent "modules" using the shared service, as AutoCAD modules would.</summary>
    internal static class DemoModules
    {
        public static IpcModule CreateDevices(IIpcService ipc, Action<string> log)
        {
            var module = new IpcModule(ipc, "Devices");
            // DEVICE_INFO forwards its parameters to the external command GRISTGET_DEVICE and later publishes
            // the external response as the event DEVICE_INFO.COMPLETED. The handler itself never waits.
            module.RegisterCommand("DEVICE_INFO", message =>
            {
                string requestId = message.Id;
                module.SendCommandAsync("GRISTGET_DEVICE", message.ParametersJson, TimeSpan.FromSeconds(5))
                    .ContinueWith(task =>
                    {
                        string status = task.Status == TaskStatus.RanToCompletion ? task.Result.Status
                                      : task.IsCanceled ? "cancelled" : task.Exception.InnerException.GetType().Name;
                        string result = task.Status == TaskStatus.RanToCompletion && task.Result.ResultJson != null
                            ? task.Result.ResultJson : "{}";
                        log("Devices: GRISTGET_DEVICE for " + requestId + " -> " + status);
                        module.PublishEvent("DEVICE_INFO.COMPLETED", "{\"requestId\":" + IpcMessage.Quote(requestId) +
                                                                  ",\"status\":" + IpcMessage.Quote(status) + ",\"result\":" + result + "}");
                    }, TaskScheduler.Default);
                return IpcMessage.CreateResponse(message.Id, "{\"accepted\":true}");
            });
            // DEVICE_CREATE publishes DEVICE.CREATED with the same parameters.
            module.RegisterCommand("DEVICE_CREATE", message =>
            {
                bool queued = module.PublishEvent("DEVICE.CREATED", message.ParametersJson);
                return IpcMessage.CreateResponse(message.Id, "{\"eventQueued\":" + (queued ? "true" : "false") + "}");
            });
            module.SubscribeEvent("PROJECT.UPDATED", m => log("Devices received " + m.Event + " " + m.ParametersJson));
            return module;
        }

        public static IpcModule CreateReports(IIpcService ipc, Action<string> log)
        {
            var module = new IpcModule(ipc, "Reports");
            module.SubscribeEvent("PROJECT.UPDATED", m => log("Reports received " + m.Event + " " + m.ParametersJson));
            try
            {
                module.RegisterCommand("DEVICE_INFO", m => IpcMessage.CreateResponse(m.Id));
            }
            catch (InvalidOperationException ex)
            {
                log("Reports: duplicate registration rejected: " + ex.Message);
            }
            return module;
        }
    }
}
