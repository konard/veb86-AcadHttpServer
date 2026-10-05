using System;
using System.Threading;
using System.Threading.Tasks;
using AutoCADHttp.Http;

namespace StandaloneHost
{
    // Usage: dotnet run --project examples/StandaloneHost -- [port] [widgets-directory] [external-/ipc-url]
    internal static class Program
    {
        private static async Task Main(string[] args)
        {
            int port = args.Length > 0 ? int.Parse(args[0]) : LocalHttpServer.DefaultPort;
            string widgets = args.Length > 1 ? args[1] : Environment.GetEnvironmentVariable("ACADHTTP_WIDGETS_DIR");
            string external = args.Length > 2 ? args[2] : Environment.GetEnvironmentVariable("ACADHTTP_EXTERNAL_IPC");
            Action<string> log = m => Console.WriteLine("[HTTP] " + m);
            var router = new ApiRouter("AutoCAD", "2021", widgetsDirectory: widgets);
            using (var outbox = string.IsNullOrWhiteSpace(external) ? null : new IpcOutbox(new Uri(external), log) { Verbose = true })
            using (var server = new LocalHttpServer(port, router.Handle, log) { Verbose = true })
            using (var stop = new CancellationTokenSource())
            {
                var dispatcher = new IpcDispatcher(router.Incoming, m =>
                {
                    if (outbox != null) outbox.TryEnqueue(m);
                    else log("IPC result (external endpoint not configured): " + m.Json);
                }, log);
                dispatcher.Register("PING", m => IpcMessage.CreateResponse(m.Id));
                dispatcher.MessageReceived += m => log("IPC received: " + m.Json);
                log("Start(): " + server.Start() + " http://" + server.Address + ":" + server.Port + "/ping");
                log("second Start(): " + server.Start());
                // Standalone application context, separate from HTTP request handlers. AutoCAD uses Application.Idle.
                Task application = Task.Run(async () =>
                {
                    try
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            dispatcher.Drain();
                            await Task.Delay(10, stop.Token);
                        }
                    }
                    catch (OperationCanceledException) { }
                });
                Console.WriteLine("Press Enter to stop...");
                Console.ReadLine();
                log("Stop(): " + server.Stop());
                stop.Cancel();
                await application;
                if (outbox != null)
                {
                    outbox.Dispose();
                    await outbox.Completion;
                }
            }
        }
    }
}
