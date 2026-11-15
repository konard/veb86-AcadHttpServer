using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutoCADHttp.Http;
using Xunit;

namespace AutoCADHttp.Tests
{
    public class IpcServiceTests
    {
        /// <summary>Accepts every POST to the external /ipc and records the envelope (no network).</summary>
        private sealed class RecordingHandler : HttpMessageHandler
        {
            public readonly ConcurrentQueue<IpcMessage> Sent = new ConcurrentQueue<IpcMessage>();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Sent.Enqueue(IpcMessage.Parse(await request.Content.ReadAsStringAsync()));
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }
        }

        private static readonly Uri External = new Uri("http://127.0.0.1:5001/ipc");

        private static RecordingHandler StartRecordingTransport(IpcService service)
        {
            var handler = new RecordingHandler();
            service.StartTransport(new IpcOutbox(External, null, null, handler));
            return handler;
        }

        private static Task<IpcMessage> NextSent(RecordingHandler handler)
        {
            return Dequeue(handler.Sent);
        }

        private static async Task<IpcMessage> Dequeue(ConcurrentQueue<IpcMessage> queue)
        {
            IpcMessage message;
            for (int i = 0; i < 500; i++)
            {
                if (queue.TryDequeue(out message))
                    return message;
                await Task.Delay(10);
            }
            throw new TimeoutException("No message within five seconds.");
        }

        // Argument errors are thrown synchronously, before any task is created.
        private static void StartSend(IpcService service, string command, TimeSpan? timeout)
        {
            service.SendCommandAsync(command, "{}", timeout);
        }

        /// <summary>Background "application context" (AutoCAD: Application.Idle) that drains the service.</summary>
        private static Task DrainLoop(IpcService service, CancellationToken stop)
        {
            return Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    service.Drain(32);
                    try { await Task.Delay(5, stop); } catch (OperationCanceledException) { }
                }
            });
        }

        [Fact]
        public async Task WithoutTransport_RegistrationAndIncomingWork_OutgoingReturnsFalseAndIsLogged()
        {
            var logs = new ConcurrentQueue<string>();
            using (var service = new IpcService(logs.Enqueue))
            {
                int calls = 0;
                // Registration does not depend on the HTTP server or the outgoing transport.
                service.RegisterCommand("Devices", "DEVICEINSERT", m => { calls++; return IpcMessage.CreateResponse(m.Id); });
                service.SubscribeEvent("Devices", "PROJECT.UPDATED", m => calls++);
                Assert.False(service.IsTransportRunning);
                Assert.Null(service.ExternalEndpoint);

                service.Incoming.Enqueue(IpcMessage.CreateCommand("c1", "deviceinsert"));
                service.Incoming.Enqueue(IpcMessage.CreateEvent("e1", "PROJECT.UPDATED"));
                Assert.Equal(2, service.Drain());
                Assert.Equal(2, calls);

                Assert.False(service.PublishEvent("DEVICE.CREATED", "{\"deviceId\":123}"));
                Assert.False(service.TrySend(IpcMessage.CreateEvent("x", "E")));
                var failed = await Assert.ThrowsAsync<IpcTransportException>(() => service.SendCommandAsync("GRISTGET_DEVICE"));
                Assert.IsNotType<IpcTransportStoppedException>(failed);
                Assert.Equal(0, service.PendingCommandCount);

                Assert.Contains(logs, l => l.Contains("c1") && l.Contains("not running") && l.Contains("externalIpc"));
                Assert.Contains(logs, l => l.Contains("event DEVICE.CREATED") && l.Contains("not running"));
                Assert.Contains(logs, l => l.Contains("command GRISTGET_DEVICE") && l.Contains("not running"));
            }
        }

        [Fact]
        public async Task PublishEvent_And_TrySend_QueueThroughTheSingleOutbox_WithUniqueIds()
        {
            using (var service = new IpcService())
            {
                var handler = StartRecordingTransport(service);
                Assert.Equal(External, service.ExternalEndpoint);
                Assert.True(service.PublishEvent("DEVICE.CREATED", "{\"deviceId\":123}"));
                Assert.True(service.PublishEvent("DEVICE.CREATED", null));
                Assert.True(service.TrySend(IpcMessage.CreateResponse("r1", "{\"x\":1}")));

                IpcMessage first = await NextSent(handler), second = await NextSent(handler), third = await NextSent(handler);
                Assert.Equal("event", first.Type);
                Assert.Equal("DEVICE.CREATED", first.Event);
                Assert.Equal("{\"deviceId\":123}", first.ParametersJson);
                Assert.Equal("{}", second.ParametersJson);
                Assert.NotEqual(first.Id, second.Id);
                Assert.Equal("r1", third.Id);
                Assert.Throws<ArgumentException>(() => service.PublishEvent(" "));
                Assert.Throws<ArgumentNullException>(() => service.TrySend(null));
            }
        }

        [Fact]
        public async Task SendCommandAsync_RoundTripOverRealHttp_BetweenTwoServices()
        {
            // "AutoCAD": the service behind the single HTTP server's POST /ipc.
            // "External application": another IpcService that answers GRISTGET_DEVICE with the same id.
            using (var acad = new IpcService())
            using (var external = new IpcService())
            using (var acadServer = new LocalHttpServer(0, new ApiRouter("AutoCAD", "2021", acad.Incoming).Handle, null))
            using (var externalServer = new LocalHttpServer(0, new ApiRouter("External", "1", external.Incoming).Handle, null))
            using (var stop = new CancellationTokenSource())
            {
                Assert.Equal(StartResult.Started, acadServer.Start());
                Assert.Equal(StartResult.Started, externalServer.Start());
                acad.StartTransport(new IpcOutbox(new Uri(externalServer.BaseUrl + "ipc")));
                external.StartTransport(new IpcOutbox(new Uri(acadServer.BaseUrl + "ipc")));
                var events = new ConcurrentQueue<IpcMessage>();
                external.RegisterCommand("Grist", "GRISTGET_DEVICE", m =>
                    m.ParametersJson.Contains("404") ? IpcMessage.CreateError(m.Id, "NOT_FOUND")
                                                     : IpcMessage.CreateResponse(m.Id, "{\"name\":\"Pump\"}"));
                external.SubscribeEvent("Grist", "DEVICE.CREATED", events.Enqueue);
                Task loops = Task.WhenAll(DrainLoop(acad, stop.Token), DrainLoop(external, stop.Token));
                try
                {
                    var ok = acad.SendCommandAsync("GRISTGET_DEVICE", "{\"deviceId\":1}", TimeSpan.FromSeconds(5));
                    var error = acad.SendCommandAsync("gristget_device", "{\"deviceId\":404}", TimeSpan.FromSeconds(5));
                    IpcMessage okResponse = await ok, errorResponse = await error;
                    Assert.Equal("response", okResponse.Type);
                    Assert.Equal("ok", okResponse.Status);
                    Assert.Equal("{\"name\":\"Pump\"}", okResponse.ResultJson);
                    // Error responses are results, not exceptions.
                    Assert.Equal("error", errorResponse.Status);
                    Assert.Equal("NOT_FOUND", errorResponse.ErrorCode);
                    Assert.NotEqual(okResponse.Id, errorResponse.Id);
                    Assert.Equal(0, acad.PendingCommandCount);

                    Assert.True(acad.PublishEvent("DEVICE.CREATED", "{\"deviceId\":123}"));
                    IpcMessage published = await Dequeue(events);
                    Assert.Equal("{\"deviceId\":123}", published.ParametersJson);

                    // Unknown external command: the peer answers UNKNOWN_COMMAND with the same id.
                    IpcMessage unknown = await acad.SendCommandAsync("NO_SUCH_COMMAND", "{}", TimeSpan.FromSeconds(5));
                    Assert.Equal("UNKNOWN_COMMAND", unknown.ErrorCode);
                }
                finally
                {
                    stop.Cancel();
                    await loops;
                    await acad.StopTransport();
                    await external.StopTransport();
                }
            }
        }

        [Fact]
        public async Task SendCommandAsync_ResponseIsMatchedById_AndUnknownResponsesAreIgnored()
        {
            using (var service = new IpcService())
            {
                var handler = StartRecordingTransport(service);
                var received = new ConcurrentQueue<IpcMessage>();
                service.Dispatcher.MessageReceived += received.Enqueue;
                Task<IpcMessage> first = service.SendCommandAsync("A", "{\"n\":1}", TimeSpan.FromSeconds(5));
                Task<IpcMessage> second = service.SendCommandAsync("B", "{\"n\":2}", TimeSpan.FromSeconds(5));
                IpcMessage a = await NextSent(handler), b = await NextSent(handler);
                Assert.Equal("command", a.Type);
                Assert.Equal("A", a.Command);
                Assert.Equal("{\"n\":1}", a.ParametersJson);
                Assert.Equal(2, service.PendingCommandCount);

                service.Incoming.Enqueue(IpcMessage.CreateResponse("unrelated", "{}"));
                service.Incoming.Enqueue(IpcMessage.CreateResponse(b.Id, "{\"r\":\"B\"}"));
                Assert.Equal(2, service.Drain());
                Assert.Equal("{\"r\":\"B\"}", (await second).ResultJson);
                Assert.False(first.IsCompleted);

                service.Incoming.Enqueue(IpcMessage.CreateResponse(a.Id, "{\"r\":\"A\"}"));
                service.Incoming.Enqueue(IpcMessage.CreateResponse(a.Id, "{\"r\":\"duplicate\"}"));
                service.Drain();
                Assert.Equal("{\"r\":\"A\"}", (await first).ResultJson);
                Assert.Equal(0, service.PendingCommandCount);
                // Compatibility: MessageReceived still observes every response.
                Assert.Equal(4, received.Count);
            }
        }

        [Fact]
        public async Task SendCommandAsync_ContinuationsDoNotRunInsideDrain()
        {
            using (var service = new IpcService())
            {
                var handler = StartRecordingTransport(service);
                bool draining = false;
                var observed = new TaskCompletionSource<bool>();
                Task<IpcMessage> task = service.SendCommandAsync("SLOW", "{}", TimeSpan.FromSeconds(5));
                Task continuation = task.ContinueWith(t =>
                {
                    observed.TrySetResult(Volatile.Read(ref draining));
                    // A continuation that blocks must not block the application context.
                    Thread.Sleep(300);
                }, TaskContinuationOptions.ExecuteSynchronously);
                IpcMessage sent = await NextSent(handler);
                service.Incoming.Enqueue(IpcMessage.CreateResponse(sent.Id));
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Volatile.Write(ref draining, true);
                service.Drain();
                Volatile.Write(ref draining, false);
                watch.Stop();
                Assert.False(await observed.Task, "Continuation ran inline inside Drain.");
                Assert.True(watch.ElapsedMilliseconds < 250, "Drain waited for a continuation: " + watch.ElapsedMilliseconds + " ms.");
                await continuation;
            }
        }

        [Fact]
        public async Task SendCommandAsync_TimesOut_AndALateResponseIsHarmless()
        {
            var logs = new ConcurrentQueue<string>();
            using (var service = new IpcService(logs.Enqueue))
            {
                var handler = StartRecordingTransport(service);
                var error = await Assert.ThrowsAsync<TimeoutException>(() =>
                    service.SendCommandAsync("GRISTGET_DEVICE", "{}", TimeSpan.FromMilliseconds(100)));
                Assert.Contains("GRISTGET_DEVICE", error.Message);
                Assert.Equal(0, service.PendingCommandCount);
                IpcMessage sent = await NextSent(handler);
                service.Incoming.Enqueue(IpcMessage.CreateResponse(sent.Id));
                Assert.Equal(1, service.Drain());
                Assert.Equal(0, service.PendingCommandCount);
            }
        }

        [Fact]
        public async Task SendCommandAsync_Cancellation_AndArgumentValidation()
        {
            using (var service = new IpcService())
            {
                StartRecordingTransport(service);
                using (var cancel = new CancellationTokenSource())
                {
                    Task<IpcMessage> task = service.SendCommandAsync("WAIT", "{}", TimeSpan.FromSeconds(30), cancel.Token);
                    Assert.Equal(1, service.PendingCommandCount);
                    cancel.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
                    Assert.True(task.IsCanceled);
                    Assert.Equal(0, service.PendingCommandCount);
                    Task<IpcMessage> already = service.SendCommandAsync("WAIT", "{}", null, cancel.Token);
                    Assert.True(already.IsCanceled);
                }
                Assert.Throws<ArgumentException>(() => StartSend(service, " ", null));
                Assert.Throws<ArgumentOutOfRangeException>(() => StartSend(service, "A", TimeSpan.Zero));
                Assert.Throws<ArgumentOutOfRangeException>(() => StartSend(service, "A", Timeout.InfiniteTimeSpan));
                Assert.Throws<ArgumentOutOfRangeException>(() => StartSend(service, "A", TimeSpan.MaxValue));
                Assert.Equal(0, service.PendingCommandCount);
            }
        }

        [Fact]
        public async Task StopTransport_FailsPendingCommands_KeepsRegistrations_AndRestartWorks()
        {
            var logs = new ConcurrentQueue<string>();
            using (var service = new IpcService(logs.Enqueue))
            {
                var commands = new ConcurrentQueue<string>();
                service.RegisterCommand("Devices", "DEVICEINSERT", m => { commands.Enqueue(m.Id); return IpcMessage.CreateResponse(m.Id); });
                int events = 0;
                service.SubscribeEvent("Devices", "PROJECT.UPDATED", m => events++);
                StartRecordingTransport(service);
                Task<IpcMessage> first = service.SendCommandAsync("A", "{}", TimeSpan.FromSeconds(30));
                Task<IpcMessage> second = service.SendCommandAsync("B", "{}", TimeSpan.FromSeconds(30));

                // HTTPSTOP: no request may stay pending forever.
                Task stopped = service.StopTransport("IPC transport stopped by HTTPSTOP.");
                var error = await Assert.ThrowsAsync<IpcTransportStoppedException>(() => first);
                Assert.Contains("HTTPSTOP", error.Message);
                await Assert.ThrowsAsync<IpcTransportStoppedException>(() => second);
                Assert.Same(stopped, await Task.WhenAny(stopped, Task.Delay(5000)));
                Assert.False(service.IsTransportRunning);
                Assert.Equal(0, service.PendingCommandCount);
                Assert.Contains(logs, l => l.Contains("HTTPSTOP") && l.Contains("2 pending"));
                Assert.False(service.PublishEvent("E"));
                await service.StopTransport(); // Idempotent.

                // HTTPSTART again: registrations were kept, outgoing commands work with the new transport.
                var handler = StartRecordingTransport(service);
                service.Incoming.Enqueue(IpcMessage.CreateCommand("c2", "DEVICEINSERT"));
                service.Incoming.Enqueue(IpcMessage.CreateEvent("e2", "PROJECT.UPDATED"));
                Assert.Equal(2, service.Drain());
                Assert.Equal(new[] { "c2" }, commands);
                Assert.Equal(1, events);
                IpcMessage response = await NextSent(handler);
                Assert.Equal("c2", response.Id);
                Task<IpcMessage> third = service.SendCommandAsync("C", "{}", TimeSpan.FromSeconds(5));
                IpcMessage sent = await NextSent(handler);
                service.Incoming.Enqueue(IpcMessage.CreateResponse(sent.Id));
                service.Drain();
                Assert.Equal("ok", (await third).Status);
            }
        }

        [Fact]
        public async Task StartTransport_ReplacesThePreviousTransport_AndFailsItsPendingCommands()
        {
            using (var service = new IpcService())
            {
                StartRecordingTransport(service);
                Task<IpcMessage> pending = service.SendCommandAsync("A", "{}", TimeSpan.FromSeconds(30));
                var handler = StartRecordingTransport(service);
                await Assert.ThrowsAsync<IpcTransportStoppedException>(() => pending);
                Assert.True(service.PublishEvent("E"));
                Assert.Equal("E", (await NextSent(handler)).Event);
            }
        }

        [Fact]
        public async Task Dispose_FailsPendingCommands_AndRejectsFurtherUse()
        {
            var service = new IpcService();
            service.RegisterCommand("Devices", "A", m => IpcMessage.CreateResponse(m.Id));
            service.SubscribeEvent("Devices", "*", m => { });
            StartRecordingTransport(service);
            Task<IpcMessage> pending = service.SendCommandAsync("A", "{}", TimeSpan.FromSeconds(30));
            service.Incoming.Enqueue(IpcMessage.CreateCommand("left", "A"));
            service.Dispose();
            service.Dispose();
            await Assert.ThrowsAsync<IpcTransportStoppedException>(() => pending);
            Assert.Equal(0, service.Dispatcher.CommandCount);
            Assert.Equal(0, service.Dispatcher.SubscriptionCount);
            Assert.True(service.Incoming.IsEmpty);
            Assert.False(service.IsTransportRunning);
            Assert.Throws<ObjectDisposedException>(() => service.RegisterCommand("Devices", "B", m => m));
            Assert.Throws<ObjectDisposedException>(() => service.SubscribeEvent("Devices", "E", m => { }));
            Assert.Throws<ObjectDisposedException>(() => service.StartTransport(new IpcOutbox(External)));
            Assert.False(service.PublishEvent("E"));
        }

        [Fact]
        public void IpcModule_DisposeReleasesOnlyItsOwnRegistrations()
        {
            using (var service = new IpcService())
            {
                var responses = new ConcurrentQueue<IpcMessage>();
                service.Dispatcher.MessageReceived += responses.Enqueue;
                var devices = new IpcModule(service, "Devices");
                var reports = new IpcModule(service, "Reports");
                int deviceEvents = 0, reportEvents = 0;
                devices.RegisterCommand("DEVICEINSERT", m => IpcMessage.CreateResponse(m.Id));
                devices.SubscribeEvent("PROJECT.UPDATED", m => deviceEvents++);
                reports.RegisterCommand("REPORT", m => IpcMessage.CreateResponse(m.Id));
                reports.SubscribeEvent("PROJECT.UPDATED", m => reportEvents++);
                var duplicate = Assert.Throws<InvalidOperationException>(() => reports.RegisterCommand("DeviceInsert", m => m));
                Assert.Contains("Devices", duplicate.Message);
                Assert.Equal("Devices", devices.Name);
                Assert.Equal(2, service.Dispatcher.CommandCount);
                Assert.Equal(2, service.Dispatcher.SubscriptionCount);

                devices.Dispose();
                devices.Dispose();
                Assert.Equal(1, service.Dispatcher.CommandCount);
                Assert.Equal(1, service.Dispatcher.SubscriptionCount);
                string owner;
                Assert.False(service.Dispatcher.TryGetCommandOwner("DEVICEINSERT", out owner));
                Assert.True(service.Dispatcher.TryGetCommandOwner("REPORT", out owner));
                Assert.Equal("Reports", owner);
                service.Incoming.Enqueue(IpcMessage.CreateEvent("e", "PROJECT.UPDATED"));
                service.Drain();
                Assert.Equal(0, deviceEvents);
                Assert.Equal(1, reportEvents);
                Assert.Throws<ObjectDisposedException>(() => devices.RegisterCommand("X", m => m));
                Assert.Throws<ObjectDisposedException>(() => devices.SubscribeEvent("X", m => { }));

                // Once released, the name can be registered by another module.
                reports.RegisterCommand("DEVICEINSERT", m => IpcMessage.CreateResponse(m.Id));
                reports.Dispose();
                Assert.Equal(0, service.Dispatcher.CommandCount);
                Assert.Throws<ArgumentNullException>(() => new IpcModule(null, "X"));
                Assert.Throws<ArgumentException>(() => new IpcModule(service, " "));
            }
        }

        [Fact]
        public void FailingCommandHandler_DoesNotStopLaterMessages()
        {
            var logs = new ConcurrentQueue<string>();
            using (var service = new IpcService(logs.Enqueue))
            {
                int later = 0;
                service.RegisterCommand("Broken", "FAIL", m => { throw new InvalidOperationException("boom"); });
                service.RegisterCommand("Devices", "OK", m => { later++; return IpcMessage.CreateResponse(m.Id); });
                service.Incoming.Enqueue(IpcMessage.CreateCommand("1", "FAIL"));
                service.Incoming.Enqueue(IpcMessage.CreateCommand("2", "OK"));
                Assert.Equal(2, service.Drain());
                Assert.Equal(1, later);
                Assert.Contains(logs, l => l.Contains("Broken") && l.Contains("boom"));
            }
        }
    }
}
