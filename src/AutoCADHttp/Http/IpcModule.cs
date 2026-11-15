using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AutoCADHttp.Http
{
    /// <summary>
    /// Convenience scope for one AutoCAD module: binds the module name and releases all of the module's command
    /// registrations and event subscriptions on <see cref="Dispose"/> (call it from <c>IExtensionApplication.Terminate</c>).
    /// It only holds this module's own registration objects, so it cannot remove another module's handlers.
    /// </summary>
    public sealed class IpcModule : IDisposable
    {
        private readonly IIpcService _service;
        private readonly List<IDisposable> _registrations = new List<IDisposable>();
        private bool _disposed;

        public IpcModule(IIpcService service, string name)
        {
            if (service == null)
                throw new ArgumentNullException("service");
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Module name must not be empty.", "name");
            _service = service;
            Name = name;
        }

        public string Name { get; private set; }

        public IDisposable RegisterCommand(string command, Func<IpcMessage, IpcMessage> handler)
        {
            lock (_registrations)
            {
                ThrowIfDisposed();
                return Track(_service.RegisterCommand(Name, command, handler));
            }
        }

        public IDisposable SubscribeEvent(string eventName, Action<IpcMessage> handler)
        {
            lock (_registrations)
            {
                ThrowIfDisposed();
                return Track(_service.SubscribeEvent(Name, eventName, handler));
            }
        }

        public bool TrySend(IpcMessage message) { return _service.TrySend(message); }

        public bool PublishEvent(string eventName, string parametersJson = "{}")
        {
            return _service.PublishEvent(eventName, parametersJson);
        }

        public Task<IpcMessage> SendCommandAsync(string command, string parametersJson = "{}", TimeSpan? timeout = null,
                                                 CancellationToken cancellationToken = default(CancellationToken))
        {
            return _service.SendCommandAsync(command, parametersJson, timeout, cancellationToken);
        }

        /// <summary>Releases every registration and subscription made through this module (idempotent).</summary>
        public void Dispose()
        {
            IDisposable[] registrations;
            lock (_registrations)
            {
                if (_disposed)
                    return;
                _disposed = true;
                registrations = _registrations.ToArray();
                _registrations.Clear();
            }
            foreach (var registration in registrations)
            {
                try { registration.Dispose(); } catch { }
            }
        }

        private IDisposable Track(IDisposable registration)
        {
            _registrations.Add(registration);
            return registration;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException("IpcModule " + Name);
        }
    }
}
