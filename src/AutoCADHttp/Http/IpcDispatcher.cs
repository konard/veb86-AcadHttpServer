using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace AutoCADHttp.Http
{
    /// <summary>
    /// The single incoming dispatcher. Registration is thread-safe; <see cref="Drain"/> is called by the application
    /// in its own context (AutoCAD: <c>Application.Idle</c> on the main thread), never by the HTTP server.
    /// </summary>
    public sealed class IpcDispatcher
    {
        /// <summary>Module name used by the legacy <see cref="Register"/> method.</summary>
        public const string ApplicationModule = "AutoCADHttp";

        /// <summary><see cref="SubscribeEvent"/> with this name receives every incoming event.</summary>
        public const string AnyEvent = "*";

        private readonly ConcurrentQueue<IpcMessage> _incoming;
        private readonly Action<IpcMessage> _send;
        private readonly Action<string> _log;
        private readonly object _sync = new object();
        // Command names keep the existing case-insensitive comparison.
        private readonly Dictionary<string, CommandRegistration> _handlers =
            new Dictionary<string, CommandRegistration>(StringComparer.OrdinalIgnoreCase);
        // Copy-on-write: Drain enumerates a snapshot without holding the lock while subscribers run.
        private EventSubscription[] _subscriptions = new EventSubscription[0];

        public IpcDispatcher(ConcurrentQueue<IpcMessage> incoming, Action<IpcMessage> send, Action<string> log = null)
        {
            _incoming = incoming ?? throw new ArgumentNullException("incoming");
            _send = send ?? throw new ArgumentNullException("send");
            _log = log;
        }

        /// <summary>Every incoming response and event (compatibility), delivered in the caller's application context.</summary>
        public event Action<IpcMessage> MessageReceived;

        /// <summary>
        /// Optional correlation hook for incoming responses (used by <see cref="IpcService"/> to complete
        /// <c>SendCommandAsync</c>). Called in the <see cref="Drain"/> context before <see cref="MessageReceived"/>.
        /// </summary>
        public Action<IpcMessage> ResponseReceived { get; set; }

        /// <summary>Number of registered command names.</summary>
        public int CommandCount
        {
            get { lock (_sync) return _handlers.Count; }
        }

        /// <summary>Number of active event subscriptions.</summary>
        public int SubscriptionCount
        {
            get { return Volatile.Read(ref _subscriptions).Length; }
        }

        /// <summary>
        /// Compatibility shortcut for <see cref="RegisterCommand"/> with the <see cref="ApplicationModule"/> owner.
        /// A name that is already registered is no longer replaced silently.
        /// </summary>
        public IDisposable Register(string command, Func<IpcMessage, IpcMessage> handler)
        {
            return RegisterCommand(ApplicationModule, command, handler);
        }

        /// <summary>
        /// Registers <paramref name="command"/> for <paramref name="module"/>. Throws
        /// <see cref="InvalidOperationException"/> (naming the owner) if the name is already registered;
        /// dispose the returned object to remove exactly this registration.
        /// </summary>
        public IDisposable RegisterCommand(string module, string command, Func<IpcMessage, IpcMessage> handler)
        {
            RequireName(module, "module");
            RequireName(command, "command");
            if (handler == null)
                throw new ArgumentNullException("handler");
            var registration = new CommandRegistration(this, module, command, handler);
            lock (_sync)
            {
                CommandRegistration existing;
                if (_handlers.TryGetValue(command, out existing))
                    throw new InvalidOperationException("IPC command '" + command + "' is already registered by module '" +
                                                        existing.Module + "'; module '" + module +
                                                        "' cannot register it until that registration is disposed.");
                _handlers.Add(command, registration);
            }
            return registration;
        }

        /// <summary>
        /// Subscribes <paramref name="module"/> to incoming events named <paramref name="eventName"/>
        /// (case-insensitive; <see cref="AnyEvent"/> = all). Several subscriptions per event are allowed.
        /// </summary>
        public IDisposable SubscribeEvent(string module, string eventName, Action<IpcMessage> handler)
        {
            RequireName(module, "module");
            RequireName(eventName, "eventName");
            if (handler == null)
                throw new ArgumentNullException("handler");
            var subscription = new EventSubscription(this, module, eventName, handler);
            lock (_sync)
            {
                var updated = new EventSubscription[_subscriptions.Length + 1];
                Array.Copy(_subscriptions, updated, _subscriptions.Length);
                updated[_subscriptions.Length] = subscription;
                Volatile.Write(ref _subscriptions, updated);
            }
            return subscription;
        }

        /// <summary>True if <paramref name="command"/> is registered; the owning module is returned.</summary>
        public bool TryGetCommandOwner(string command, out string module)
        {
            module = null;
            if (command == null)
                return false;
            lock (_sync)
            {
                CommandRegistration registration;
                if (!_handlers.TryGetValue(command, out registration))
                    return false;
                module = registration.Module;
                return true;
            }
        }

        /// <summary>Removes every command registration and event subscription (process shutdown).</summary>
        public void Clear()
        {
            lock (_sync)
            {
                foreach (var registration in _handlers.Values)
                    registration.Detach();
                _handlers.Clear();
                foreach (var subscription in _subscriptions)
                    subscription.Detach();
                Volatile.Write(ref _subscriptions, new EventSubscription[0]);
            }
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
                    switch (message.Type)
                    {
                        case "command":
                            _send(Execute(message));
                            break;
                        case "event":
                            Publish(message);
                            Received(message);
                            break; // Never reply to an event and create a feedback loop.
                        default:
                            var responseReceived = ResponseReceived;
                            if (responseReceived != null)
                                Safe("IPC response " + message.Id, () => responseReceived(message));
                            Received(message);
                            break; // Never reply to a response either.
                    }
                }
                catch (Exception ex)
                {
                    Log("IPC dispatch failed for " + message.Id + ": " + ex.Message);
                }
            }
            return count;
        }

        private IpcMessage Execute(IpcMessage message)
        {
            CommandRegistration registration;
            lock (_sync)
                _handlers.TryGetValue(message.Command, out registration);
            if (registration == null)
                return IpcMessage.CreateError(message.Id, "UNKNOWN_COMMAND");
            try
            {
                IpcMessage response = registration.Handler(message);
                if (response == null || response.Type != "response" || response.Id != message.Id)
                    throw new InvalidOperationException("Handler must return a correlated response.");
                return response;
            }
            catch (Exception ex)
            {
                Log("IPC handler " + message.Command + " (module " + registration.Module + ") failed: " + ex.Message);
                return IpcMessage.CreateError(message.Id, "COMMAND_FAILED");
            }
        }

        private void Publish(IpcMessage message)
        {
            foreach (EventSubscription subscription in Volatile.Read(ref _subscriptions))
            {
                if (!subscription.Active ||
                    (subscription.EventName != AnyEvent &&
                     !string.Equals(subscription.EventName, message.Event, StringComparison.OrdinalIgnoreCase)))
                    continue;
                // One failing subscriber must not prevent the others from receiving the event.
                Safe("IPC subscriber " + subscription.Module + " for " + message.Event, () => subscription.Handler(message));
            }
        }

        private void Received(IpcMessage message)
        {
            var received = MessageReceived;
            if (received == null)
                return;
            foreach (Action<IpcMessage> handler in received.GetInvocationList())
                Safe("IPC subscriber for " + message.Id, () => handler(message));
        }

        private void Safe(string what, Action action)
        {
            try { action(); }
            catch (Exception ex) { Log(what + " failed: " + ex.Message); }
        }

        private void Unregister(CommandRegistration registration)
        {
            lock (_sync)
            {
                CommandRegistration current;
                // Only this exact registration is removed, never a later one with the same name.
                if (_handlers.TryGetValue(registration.Command, out current) && ReferenceEquals(current, registration))
                    _handlers.Remove(registration.Command);
            }
        }

        private void Unsubscribe(EventSubscription subscription)
        {
            lock (_sync)
            {
                int index = Array.IndexOf(_subscriptions, subscription);
                if (index < 0)
                    return;
                var updated = new EventSubscription[_subscriptions.Length - 1];
                Array.Copy(_subscriptions, 0, updated, 0, index);
                Array.Copy(_subscriptions, index + 1, updated, index, updated.Length - index);
                Volatile.Write(ref _subscriptions, updated);
            }
        }

        private static void RequireName(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException(name + " must not be empty.", name);
        }

        private void Log(string text)
        {
            try { if (_log != null) _log(text); } catch { }
        }

        private sealed class CommandRegistration : IDisposable
        {
            private IpcDispatcher _owner;
            public CommandRegistration(IpcDispatcher owner, string module, string command, Func<IpcMessage, IpcMessage> handler)
            {
                _owner = owner;
                Module = module;
                Command = command;
                Handler = handler;
            }
            public string Module { get; private set; }
            public string Command { get; private set; }
            public Func<IpcMessage, IpcMessage> Handler { get; private set; }
            public void Detach() { Volatile.Write(ref _owner, null); }
            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner != null)
                    owner.Unregister(this);
            }
        }

        private sealed class EventSubscription : IDisposable
        {
            private IpcDispatcher _owner;
            public EventSubscription(IpcDispatcher owner, string module, string eventName, Action<IpcMessage> handler)
            {
                _owner = owner;
                Module = module;
                EventName = eventName;
                Handler = handler;
            }
            public string Module { get; private set; }
            public string EventName { get; private set; }
            public Action<IpcMessage> Handler { get; private set; }
            public bool Active { get { return Volatile.Read(ref _owner) != null; } }
            public void Detach() { Volatile.Write(ref _owner, null); }
            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner != null)
                    owner.Unsubscribe(this);
            }
        }
    }
}
