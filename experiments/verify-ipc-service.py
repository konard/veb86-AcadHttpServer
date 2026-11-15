#!/usr/bin/env python3
"""End-to-end check of the shared IPC service (issue #15) with the standalone host's demo modules.

The host (examples/StandaloneHost) registers two modules on one IpcService: "Devices" (commands DEVICE_INFO and
DEVICE_CREATE, subscriber of PROJECT.UPDATED) and "Reports" (subscriber of PROJECT.UPDATED, its duplicate
DEVICE_INFO registration is rejected). examples/external-ipc-app.py plays the external application.

Build first: dotnet build examples/StandaloneHost -c Release
Run from the repository root: python3 experiments/verify-ipc-service.py
All ports are ephemeral loopback ports and every wait is bounded.
"""
import importlib.util
import pathlib
import re
import subprocess
import time

spec = importlib.util.spec_from_file_location("external_ipc_app", "examples/external-ipc-app.py")
external_ipc_app = importlib.util.module_from_spec(spec)
spec.loader.exec_module(external_ipc_app)

log_path = pathlib.Path("ci-logs/ipc-service-host.log")
log_path.parent.mkdir(exist_ok=True)


def wait_log(pattern, timeout=10):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        match = re.search(pattern, log_path.read_text())
        if match:
            return match
        time.sleep(0.01)
    raise AssertionError(f"Log did not match {pattern!r}:\n{log_path.read_text()}")


def is_response(message_id):
    return lambda m: m.get("type") == "response" and m.get("id") == message_id


def is_event(name, request_id=None):
    return lambda m: (m.get("type") == "event" and m.get("event") == name and
                      (request_id is None or m.get("parameters", {}).get("requestId") == request_id))


# Devices 404 and 405 get no answer from the external application: timeout and transport stop.
with external_ipc_app.ExternalApp("http://unused", port=0, ignore={404, 405}) as app, log_path.open("w") as log:
    host = subprocess.Popen(
        ["dotnet", "run", "--no-build", "-c", "Release", "--project", "examples/StandaloneHost", "--", "0", "",
         app.endpoint],
        stdin=subprocess.PIPE, stdout=log, stderr=subprocess.STDOUT, text=True)
    try:
        app.acad_url = wait_log(r"Start\(\): Started (http://127\.0\.0\.1:\d+)/ping").group(1)
        wait_log(re.escape("IPC outgoing: " + app.endpoint))

        # §3: a second module cannot take a command that another module registered.
        wait_log(r"Reports: duplicate registration rejected: IPC command 'DEVICE_INFO' is already registered by module "
                 r"'Devices'")

        # §5 + §6: incoming command -> immediate response; the module's outgoing command is answered by the external
        # application with the same id; the module then publishes the result as an event (§7).
        app.command("DEVICE_INFO", {"deviceId": 123}, "info-1")
        assert app.wait_for(is_response("info-1"))["result"] == {"accepted": True}
        outgoing = app.wait_for(lambda m: m.get("type") == "command" and m.get("command") == "GRISTGET_DEVICE" and
                                m["parameters"].get("deviceId") == 123)
        completed = app.wait_for(is_event("DEVICE_INFO.COMPLETED", "info-1"))["parameters"]
        assert completed["status"] == "ok", completed
        assert completed["result"] == {"deviceId": 123, "name": "Device 123", "source": "external"}, completed
        assert outgoing["id"] in app.answered
        wait_log(r"Devices: GRISTGET_DEVICE for info-1 -> ok")
        print("Outgoing command answered with the same id:", outgoing["id"])

        # Case-insensitive command names; PublishEvent from a handler.
        app.command("device_create", {"deviceId": 5}, "create-1")
        assert app.wait_for(is_response("create-1"))["result"] == {"eventQueued": True}
        created = app.wait_for(is_event("DEVICE.CREATED"))
        assert created["parameters"] == {"deviceId": 5}, created
        print("Event published:", created["id"])

        # §7: one incoming event reaches both modules' subscribers and produces no response.
        app.event("PROJECT.UPDATED", {"projectId": 7}, "event-1")
        wait_log(r'Devices received PROJECT\.UPDATED \{"projectId":7\}')
        wait_log(r'Reports received PROJECT\.UPDATED \{"projectId":7\}')
        app.command("PING", {}, "ping-after-event")
        assert app.wait_for(is_response("ping-after-event"))["status"] == "ok"
        leftovers = []
        while not app.received.empty():
            leftovers.append(app.received.get())
        assert not [m for m in leftovers if m.get("id") == "event-1"], leftovers
        print("Both subscribers received PROJECT.UPDATED; no automatic response.")

        # Unknown commands still fail with UNKNOWN_COMMAND.
        app.command("NO_SUCH_COMMAND", {}, "unknown-1")
        assert app.wait_for(is_response("unknown-1"))["error"]["code"] == "UNKNOWN_COMMAND"

        # §6: no response from the external application -> TimeoutException after the module's 5 s timeout.
        app.command("DEVICE_INFO", {"deviceId": 404}, "info-timeout")
        assert app.wait_for(is_response("info-timeout"))["result"] == {"accepted": True}
        timed_out = app.wait_for(is_event("DEVICE_INFO.COMPLETED", "info-timeout"), timeout=15)["parameters"]
        assert timed_out["status"] == "TimeoutException", timed_out
        print("Unanswered outgoing command timed out.")

        # §10: stopping the transport fails a pending SendCommandAsync immediately with a clear error.
        app.command("DEVICE_INFO", {"deviceId": 405}, "info-stop")
        app.wait_for(lambda m: m.get("type") == "command" and m["parameters"].get("deviceId") == 405)
        started = time.monotonic()
        host.stdin.write("\n")
        host.stdin.flush()
        wait_log(r"Devices: GRISTGET_DEVICE for info-stop -> IpcTransportStoppedException", timeout=5)
        assert time.monotonic() - started < 4, "Pending command waited for its timeout instead of the stop"
        wait_log(r"1 pending IPC command\(s\) failed")
        print("Pending outgoing command failed on transport stop.")
    finally:
        if host.poll() is None:
            try:
                host.stdin.write("\n")
                host.stdin.flush()
            except (BrokenPipeError, OSError):
                pass
        try:
            host.wait(timeout=10)
        except subprocess.TimeoutExpired:
            host.kill()
            host.wait()
            raise
        host.stdin.close()
    assert host.returncode == 0, log_path.read_text()
print("Shared IPC service verified end to end.")
