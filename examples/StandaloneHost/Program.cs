using System;
using AutoCADHttp.Http;

namespace StandaloneHost
{
    // Usage: dotnet run --project examples/StandaloneHost [port]
    // Then:  curl http://127.0.0.1:5000/ping
    internal static class Program
    {
        private static void Main(string[] args)
        {
            int port = args.Length > 0 ? int.Parse(args[0]) : LocalHttpServer.DefaultPort;
            var router = new ApiRouter("AutoCAD", "2021");
            using (var server = new LocalHttpServer(port, router.Handle, m => Console.WriteLine("[HTTP] " + m)) { Verbose = true })
            {
                Console.WriteLine("[HTTP] Start(): " + server.Start() + " http://" + server.Address + ":" + server.Port + "/ping");
                Console.WriteLine("[HTTP] second Start(): " + server.Start());
                Console.WriteLine("Press Enter to stop...");
                Console.ReadLine();
                Console.WriteLine("[HTTP] Stop(): " + server.Stop());
            }
        }
    }
}
