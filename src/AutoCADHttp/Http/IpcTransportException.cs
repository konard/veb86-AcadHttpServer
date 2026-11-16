using System;

namespace AutoCADHttp.Http
{
    /// <summary>An outgoing message could not be queued, or the transport ended before a response arrived.</summary>
    public class IpcTransportException : Exception
    {
        public IpcTransportException(string message) : base(message) { }
    }

    /// <summary>The outgoing transport was stopped (<c>HTTPSTOP</c> or shutdown) while a command awaited its response.</summary>
    public sealed class IpcTransportStoppedException : IpcTransportException
    {
        public IpcTransportStoppedException(string message) : base(message) { }
    }
}
