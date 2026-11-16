using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading.Tasks;
using AutoCADHttp.Http;

// Usage: dotnet run -- <external /ipc url> [messages] [trace]
internal sealed class NetTrace : EventListener
{
    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Private.InternalDiagnostics.System.Net.Http" || source.Name == "System.Net.Http" || source.Name == "System.Net.Sockets")
            EnableEvents(source, EventLevel.Verbose);
    }
    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        string payload = e.Payload == null ? "" : string.Join(" | ", e.Payload.Select(p => p?.ToString()));
        if (payload.Length > 300) payload = payload.Substring(0, 300);
        Console.WriteLine("[net] " + e.EventSource.Name.Split('.').Last() + " " + e.EventName + " " + payload);
    }
}

internal static class Program
{
    private static async Task Main(string[] args)
    {
        NetTrace trace = args.Length > 2 ? new NetTrace() : null;
        int count = args.Length > 1 ? int.Parse(args[1]) : 20;
        var outbox = new IpcOutbox(new Uri(args[0]), m => Console.WriteLine("[outbox] " + m)) { Verbose = true };
        for (int i = 0; i < count; i++)
        {
            outbox.TryEnqueue(IpcMessage.CreateEvent("m" + i, "E"));
            outbox.TryEnqueue(IpcMessage.CreateEvent("n" + i, "E"));
            await Task.Delay(50);
        }
        await Task.Delay(1000);
        outbox.Dispose();
        await outbox.Completion;
        GC.KeepAlive(trace);
    }
}
