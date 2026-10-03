using System;
using System.Collections.Concurrent;
using Autodesk.AutoCAD.ApplicationServices;
using Application = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AutoCADHttp
{
    /// <summary>
    /// Thread-safe output to the AutoCAD command line.
    /// <para>
    /// The AutoCAD API (including <c>Editor.WriteMessage</c>) may only be used from the AutoCAD main
    /// thread. Messages from the HTTP background threads are therefore queued and written by the
    /// <see cref="Application.Idle"/> handler, which AutoCAD raises on its main thread.
    /// </para>
    /// </summary>
    internal static class CommandLineLog
    {
        private const string Prefix = "[HTTP] ";

        private static readonly ConcurrentQueue<string> Pending = new ConcurrentQueue<string>();
        private static bool _idleAttached;

        /// <summary>Subscribes to <see cref="Application.Idle"/>. Must be called on the main thread.</summary>
        public static void Attach()
        {
            if (_idleAttached)
                return;
            Application.Idle += OnIdle;
            _idleAttached = true;
        }

        /// <summary>Unsubscribes from <see cref="Application.Idle"/>. Must be called on the main thread.</summary>
        public static void Detach()
        {
            if (!_idleAttached)
                return;
            Application.Idle -= OnIdle;
            _idleAttached = false;
        }

        /// <summary>Queues a message from any thread; it is printed when AutoCAD becomes idle.</summary>
        public static void Post(string message)
        {
            Pending.Enqueue(message);
        }

        /// <summary>Writes a message immediately. Must be called on the main thread (e.g. from a command).</summary>
        public static void Write(string message)
        {
            Flush();
            WriteNow(message);
        }

        /// <summary>Prints queued messages. Must be called on the main thread.</summary>
        public static void Flush()
        {
            if (Pending.IsEmpty || GetEditorDocument() == null)
                return;

            string message;
            while (Pending.TryDequeue(out message))
                WriteNow(message);
        }

        private static void OnIdle(object sender, EventArgs e)
        {
            try
            {
                Flush();
            }
            catch
            {
                // Never let an exception escape from an AutoCAD event handler.
            }
        }

        private static void WriteNow(string message)
        {
            Document doc = GetEditorDocument();
            if (doc == null)
            {
                // No open drawing - nowhere to print; keep the message until a drawing is available.
                Pending.Enqueue(message);
                return;
            }

            doc.Editor.WriteMessage("\n" + Prefix + message);
        }

        private static Document GetEditorDocument()
        {
            DocumentCollection docs = Application.DocumentManager;
            return docs != null ? docs.MdiActiveDocument : null;
        }
    }
}
