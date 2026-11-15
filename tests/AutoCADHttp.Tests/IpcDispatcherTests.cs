using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using AutoCADHttp.Http;
using Xunit;

namespace AutoCADHttp.Tests
{
    public class IpcDispatcherTests
    {
        [Fact]
        public void Commands_RunOnlyWhenCallerDrains_WithCorrelatedResponses()
        {
            var incoming = new ConcurrentQueue<IpcMessage>();
            var responses = new List<IpcMessage>();
            var dispatcher = new IpcDispatcher(incoming, responses.Add);
            int handlerThread = 0;
            dispatcher.Register("PING", m =>
            {
                handlerThread = Thread.CurrentThread.ManagedThreadId;
                return IpcMessage.CreateResponse(m.Id);
            });
            incoming.Enqueue(IpcMessage.CreateCommand("known", "PING"));
            incoming.Enqueue(IpcMessage.CreateCommand("future", "LINE"));
            Assert.Equal(0, handlerThread);
            int callerThread = Thread.CurrentThread.ManagedThreadId;
            Assert.Equal(1, dispatcher.Drain(1));
            Assert.Equal(callerThread, handlerThread);
            Assert.Single(incoming);
            Assert.Equal("known", responses[0].Id);
            Assert.Equal("ok", responses[0].Status);
            Assert.Equal(1, dispatcher.Drain());
            Assert.Equal("future", responses[1].Id);
            Assert.Equal("UNKNOWN_COMMAND", responses[1].ErrorCode);
        }

        [Fact]
        public void HandlerAndSubscriberErrors_DoNotStopLaterMessages()
        {
            var incoming = new ConcurrentQueue<IpcMessage>();
            var responses = new List<IpcMessage>();
            var logs = new List<string>();
            var dispatcher = new IpcDispatcher(incoming, responses.Add, logs.Add);
            dispatcher.Register("THROW", m => { throw new InvalidOperationException("boom"); });
            dispatcher.Register("WRONG_ID", m => IpcMessage.CreateResponse("wrong"));
            dispatcher.MessageReceived += m => { throw new InvalidOperationException("subscriber"); };
            incoming.Enqueue(IpcMessage.CreateCommand("1", "THROW"));
            incoming.Enqueue(IpcMessage.CreateEvent("2", "OBJECT_CREATED"));
            incoming.Enqueue(IpcMessage.CreateCommand("3", "WRONG_ID"));
            incoming.Enqueue(IpcMessage.CreateCommand("4", "UNKNOWN"));
            Assert.Equal(4, dispatcher.Drain());
            Assert.Equal(3, responses.Count);
            Assert.Equal("COMMAND_FAILED", responses[0].ErrorCode);
            Assert.Equal("COMMAND_FAILED", responses[1].ErrorCode);
            Assert.Equal("UNKNOWN_COMMAND", responses[2].ErrorCode);
            Assert.Contains(logs, l => l.Contains("boom"));
            Assert.Contains(logs, l => l.Contains("subscriber"));
        }

        [Fact]
        public void ResponsesAndEvents_ReachApplicationWithoutReplyLoops()
        {
            var incoming = new ConcurrentQueue<IpcMessage>();
            var received = new List<IpcMessage>();
            var responses = new List<IpcMessage>();
            var dispatcher = new IpcDispatcher(incoming, responses.Add);
            dispatcher.MessageReceived += received.Add;
            incoming.Enqueue(IpcMessage.CreateResponse("1"));
            incoming.Enqueue(IpcMessage.CreateEvent("2", "OBJECT_CREATED"));
            Assert.Equal(2, dispatcher.Drain());
            Assert.Equal(2, received.Count);
            Assert.Empty(responses);
        }

        [Fact]
        public void DuplicateCommand_FromAnotherModule_FailsClearly_AndKeepsOriginalHandler()
        {
            var incoming = new ConcurrentQueue<IpcMessage>();
            var responses = new List<IpcMessage>();
            var dispatcher = new IpcDispatcher(incoming, responses.Add);
            dispatcher.RegisterCommand("Devices", "DEVICEINSERT", m => IpcMessage.CreateResponse(m.Id, "{\"by\":\"Devices\"}"));
            // Case-insensitive names, as before: "deviceinsert" is the same command.
            var error = Assert.Throws<InvalidOperationException>(() =>
                dispatcher.RegisterCommand("Reports", "deviceinsert", m => IpcMessage.CreateResponse(m.Id)));
            Assert.Contains("DEVICEINSERT", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Devices", error.Message);
            Assert.Contains("Reports", error.Message);
            // The same module cannot silently replace its own handler either; the legacy Register does not replace.
            Assert.Throws<InvalidOperationException>(() => dispatcher.RegisterCommand("Devices", "DeviceInsert", m => null));
            dispatcher.Register("PING", m => IpcMessage.CreateResponse(m.Id));
            Assert.Throws<InvalidOperationException>(() => dispatcher.Register("ping", m => IpcMessage.CreateResponse(m.Id)));
            string owner;
            Assert.True(dispatcher.TryGetCommandOwner("deviceInsert", out owner));
            Assert.Equal("Devices", owner);
            Assert.Equal(2, dispatcher.CommandCount);

            incoming.Enqueue(IpcMessage.CreateCommand("1", "DeviceInsert"));
            Assert.Equal(1, dispatcher.Drain());
            Assert.Equal("{\"by\":\"Devices\"}", Assert.Single(responses).ResultJson);
        }

        [Fact]
        public void DisposingRegistration_RemovesOnlyThatRegistration()
        {
            var incoming = new ConcurrentQueue<IpcMessage>();
            var responses = new List<IpcMessage>();
            var dispatcher = new IpcDispatcher(incoming, responses.Add);
            IDisposable first = dispatcher.RegisterCommand("Devices", "STATUS", m => IpcMessage.CreateResponse(m.Id, "{\"v\":1}"));
            first.Dispose();
            Assert.Equal(0, dispatcher.CommandCount);
            // The name is free again; a stale (already disposed) token of another module cannot remove it.
            IDisposable second = dispatcher.RegisterCommand("Reports", "STATUS", m => IpcMessage.CreateResponse(m.Id, "{\"v\":2}"));
            first.Dispose();
            Assert.Equal(1, dispatcher.CommandCount);
            incoming.Enqueue(IpcMessage.CreateCommand("a", "STATUS"));
            dispatcher.Drain();
            Assert.Equal("{\"v\":2}", responses[0].ResultJson);
            second.Dispose();
            second.Dispose();
            incoming.Enqueue(IpcMessage.CreateCommand("b", "STATUS"));
            dispatcher.Drain();
            Assert.Equal("UNKNOWN_COMMAND", responses[1].ErrorCode);
        }

        [Theory]
        [InlineData(null, "C", "h")]
        [InlineData("", "C", "h")]
        [InlineData(" ", "C", "h")]
        [InlineData("M", null, "h")]
        [InlineData("M", " ", "h")]
        [InlineData("M", "C", null)]
        public void InvalidRegistrationArguments_AreRejected(string module, string name, string handler)
        {
            var dispatcher = new IpcDispatcher(new ConcurrentQueue<IpcMessage>(), m => { });
            Func<IpcMessage, IpcMessage> command = handler == null ? null : new Func<IpcMessage, IpcMessage>(m => m);
            Action<IpcMessage> subscriber = handler == null ? null : new Action<IpcMessage>(m => { });
            Assert.ThrowsAny<ArgumentException>(() => dispatcher.RegisterCommand(module, name, command));
            Assert.ThrowsAny<ArgumentException>(() => dispatcher.SubscribeEvent(module, name, subscriber));
            Assert.Equal(0, dispatcher.CommandCount);
            Assert.Equal(0, dispatcher.SubscriptionCount);
        }

        [Fact]
        public void Events_ReachEveryMatchingSubscriber_DespiteFailures_WithoutResponses()
        {
            var incoming = new ConcurrentQueue<IpcMessage>();
            var responses = new List<IpcMessage>();
            var logs = new List<string>();
            var calls = new List<string>();
            var dispatcher = new IpcDispatcher(incoming, responses.Add, logs.Add);
            dispatcher.SubscribeEvent("Broken", "PROJECT.UPDATED", m => { throw new InvalidOperationException("broken subscriber"); });
            IDisposable devices = dispatcher.SubscribeEvent("Devices", "PROJECT.UPDATED", m => calls.Add("Devices:" + m.ParametersJson));
            dispatcher.SubscribeEvent("Reports", "project.updated", m => calls.Add("Reports:" + m.Id));
            dispatcher.SubscribeEvent("Audit", IpcDispatcher.AnyEvent, m => calls.Add("Audit:" + m.Event));
            dispatcher.SubscribeEvent("Other", "DEVICE.CREATED", m => calls.Add("Other:" + m.Event));
            Assert.Equal(5, dispatcher.SubscriptionCount);

            incoming.Enqueue(IpcMessage.CreateEvent("e1", "PROJECT.UPDATED", "{\"projectId\":7}"));
            Assert.Equal(1, dispatcher.Drain());
            Assert.Equal(new[] { "Devices:{\"projectId\":7}", "Reports:e1", "Audit:PROJECT.UPDATED" }, calls);
            Assert.Contains(logs, l => l.Contains("Broken") && l.Contains("broken subscriber"));
            Assert.Empty(responses);

            calls.Clear();
            devices.Dispose();
            incoming.Enqueue(IpcMessage.CreateEvent("e2", "PROJECT.UPDATED"));
            incoming.Enqueue(IpcMessage.CreateEvent("e3", "DEVICE.CREATED"));
            Assert.Equal(2, dispatcher.Drain());
            Assert.Equal(new[] { "Reports:e2", "Audit:PROJECT.UPDATED", "Audit:DEVICE.CREATED", "Other:DEVICE.CREATED" }, calls);
            Assert.Equal(4, dispatcher.SubscriptionCount);
            Assert.Empty(responses);
        }

        [Fact]
        public void Drain_IsBounded_AndClearRemovesEverything()
        {
            var incoming = new ConcurrentQueue<IpcMessage>();
            var responses = new List<IpcMessage>();
            var dispatcher = new IpcDispatcher(incoming, responses.Add);
            IDisposable registration = dispatcher.RegisterCommand("Devices", "PING", m => IpcMessage.CreateResponse(m.Id));
            int events = 0;
            dispatcher.SubscribeEvent("Devices", "*", m => events++);
            for (int i = 0; i < 40; i++)
                incoming.Enqueue(IpcMessage.CreateCommand(i.ToString(), "PING"));
            Assert.Equal(32, dispatcher.Drain(32));
            Assert.Equal(8, incoming.Count);
            dispatcher.Clear();
            Assert.Equal(0, dispatcher.CommandCount);
            Assert.Equal(0, dispatcher.SubscriptionCount);
            registration.Dispose(); // Harmless after Clear.
            incoming.Enqueue(IpcMessage.CreateEvent("e", "ANY"));
            Assert.Equal(9, dispatcher.Drain(32));
            Assert.Equal(0, events);
            Assert.Equal("UNKNOWN_COMMAND", responses[39].ErrorCode);
        }
    }
}
