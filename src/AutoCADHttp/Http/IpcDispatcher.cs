using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace AutoCADHttp.Http
{
    /// <summary>Called by the application in its own context; never called by the HTTP server.</summary>
    public sealed class IpcDispatcher
    {
        private readonly ConcurrentQueue<IpcMessage> _incoming;
        private readonly Action<IpcMessage> _send;
        private readonly Action<string> _log;
        private readonly Dictionary<string, Func<IpcMessage, IpcMessage>> _handlers =
            new Dictionary<string, Func<IpcMessage, IpcMessage>>(StringComparer.OrdinalIgnoreCase);

        public IpcDispatcher(ConcurrentQueue<IpcMessage> incoming, Action<IpcMessage> send, Action<string> log = null)
        {
            _incoming = incoming ?? throw new ArgumentNullException("incoming");
            _send = send ?? throw new ArgumentNullException("send");
            _log = log;
        }

        /// <summary>Responses and events, delivered in the caller's application context.</summary>
        public event Action<IpcMessage> MessageReceived;

        /// <summary>Register on the same application thread that calls Drain.</summary>
        public void Register(string command, Func<IpcMessage, IpcMessage> handler)
        {
            if (string.IsNullOrWhiteSpace(command))
                throw new ArgumentException("Command required.", "command");
            _handlers[command] = handler ?? throw new ArgumentNullException("handler");
        }

        /// <summary>Bound each idle tick so a continuous stream cannot monopolize the main thread.</summary>
        public int Drain(int maxMessages = 32)
        {
            if (maxMessages <= 0)
                throw new ArgumentOutOfRangeException("maxMessages");
            int count = 0;
            IpcMessage message;
            while (count < maxMessages && _incoming.TryDequeue(out message))
            {
                count++;
                try
                {
                    if (message.Type != "command")
                    {
                        var received = MessageReceived;
                        if (received != null)
                            received(message);
                        continue; // Never reply to a response/event and create a feedback loop.
                    }
                    Func<IpcMessage, IpcMessage> handler;
                    IpcMessage response;
                    if (!_handlers.TryGetValue(message.Command, out handler))
                        response = IpcMessage.CreateError(message.Id, "UNKNOWN_COMMAND");
                    else
                    {
                        try
                        {
                            response = handler(message);
                            if (response == null || response.Type != "response" || response.Id != message.Id)
                                throw new InvalidOperationException("Handler must return a correlated response.");
                        }
                        catch (Exception ex)
                        {
                            Log("IPC handler " + message.Command + " failed: " + ex.Message);
                            response = IpcMessage.CreateError(message.Id, "COMMAND_FAILED");
                        }
                    }
                    _send(response);
                }
                catch (Exception ex)
                {
                    Log("IPC dispatch failed for " + message.Id + ": " + ex.Message);
                }
            }
            return count;
        }

        private void Log(string text)
        {
            try { if (_log != null) _log(text); } catch { }
        }
    }
}
