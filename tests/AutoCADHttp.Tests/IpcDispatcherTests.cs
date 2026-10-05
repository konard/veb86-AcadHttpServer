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
    }
}
