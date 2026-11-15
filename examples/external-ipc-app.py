#!/usr/bin/env python3
"""Minimal external application for the two-way IPC service (HttpServerPlugin.Ipc / IIpcService).

It listens on its own /ipc (the URL configured as "externalIpc" in AutoCADHttp.settings.json or
ACADHTTP_EXTERNAL_IPC) and:
  * records every envelope AutoCAD sends (responses, events and commands);
  * answers AutoCAD's outgoing command GRISTGET_DEVICE by POSTing a response with the same id to
    AutoCAD's /ipc (this completes SendCommandAsync in the module that sent the command);
  * can send commands and events to AutoCAD's /ipc.

Example:
  python3 examples/external-ipc-app.py --acad http://127.0.0.1:5000 --port 5001 \
      --event PROJECT.UPDATED '{"projectId":7}' --command DEVICE_INFO '{"deviceId":123}'
Uses only the Python standard library; Python is not needed by the plugin.
"""
import argparse
import json
import queue
import threading
import time
import urllib.request
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class ExternalApp:
    """External /ipc peer. Devices listed in ``ignore`` get no answer (to exercise timeouts/HTTPSTOP)."""

    def __init__(self, acad_url, port=5001, ignore=()):
        self.acad_url = acad_url.rstrip("/")
        self.ignore = set(ignore)
        self.received = queue.Queue()
        self.answered = []
        app = self

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                if self.path != "/ipc":
                    self.send_error(404)
                    return
                length = int(self.headers.get("Content-Length", 0))
                if not 0 < length <= 1024 * 1024:
                    self.send_error(400)
                    return
                message = json.loads(self.rfile.read(length))
                self.send_response(202)
                self.send_header("Content-Length", "0")
                self.end_headers()
                app.received.put(message)
                if message.get("type") == "command":
                    # Answer on a separate thread, like a real application, never inside AutoCAD's HTTP call.
                    threading.Thread(target=app._answer, args=(message,), daemon=True).start()

            def log_message(self, *_args):
                pass

        self.server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
        self.server.daemon_threads = True
        self.port = self.server.server_address[1]
        self.thread = threading.Thread(target=self.server.serve_forever)

    @property
    def endpoint(self):
        return f"http://127.0.0.1:{self.port}/ipc"

    def __enter__(self):
        self.thread.start()
        return self

    def __exit__(self, *_exc):
        self.server.shutdown()
        self.thread.join()
        self.server.server_close()

    def _answer(self, command):
        if command.get("command") != "GRISTGET_DEVICE":
            self.post({"id": command["id"], "type": "response", "status": "error",
                       "error": {"code": "UNKNOWN_COMMAND", "message": "Unknown command."}})
            return
        device = command.get("parameters", {}).get("deviceId")
        if device in self.ignore:
            return
        self.answered.append(command["id"])
        self.post({"id": command["id"], "type": "response", "status": "ok",
                   "result": {"deviceId": device, "name": f"Device {device}", "source": "external"}})

    def post(self, message):
        """POST one envelope to AutoCAD's /ipc; AutoCAD only queues it (202)."""
        body = json.dumps(message, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        request = urllib.request.Request(self.acad_url + "/ipc", body, {"Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=5) as response:
            assert response.status == 202, response.status
            return json.load(response)

    def command(self, name, parameters=None, message_id=None):
        message_id = message_id or str(uuid.uuid4())
        self.post({"id": message_id, "type": "command", "command": name, "parameters": parameters or {}})
        return message_id

    def event(self, name, parameters=None, message_id=None):
        message_id = message_id or str(uuid.uuid4())
        self.post({"id": message_id, "type": "event", "event": name, "parameters": parameters or {}})
        return message_id

    def wait_for(self, predicate, timeout=10):
        """Return the first received envelope matching predicate; other envelopes are kept for later calls."""
        deadline = time.monotonic() + timeout
        skipped = []
        try:
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError("No matching IPC message within %s s" % timeout)
                try:
                    message = self.received.get(timeout=remaining)
                except queue.Empty:
                    raise TimeoutError("No matching IPC message within %s s" % timeout)
                if predicate(message):
                    return message
                skipped.append(message)
        finally:
            for message in skipped:
                self.received.put(message)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--acad", default="http://127.0.0.1:5000", help="AutoCAD base URL")
    parser.add_argument("--port", type=int, default=5001, help="port of this application's /ipc")
    parser.add_argument("--command", nargs=2, action="append", default=[], metavar=("NAME", "JSON"))
    parser.add_argument("--event", nargs=2, action="append", default=[], metavar=("NAME", "JSON"))
    parser.add_argument("--listen", type=float, default=10, help="seconds to print incoming envelopes")
    args = parser.parse_args()
    with ExternalApp(args.acad, args.port) as app:
        print("External /ipc:", app.endpoint)
        for name, parameters in args.event:
            print("event ->", name, app.event(name, json.loads(parameters)))
        for name, parameters in args.command:
            print("command ->", name, app.command(name, json.loads(parameters)))
        deadline = time.monotonic() + args.listen
        while time.monotonic() < deadline:
            try:
                print("<-", json.dumps(app.received.get(timeout=max(0.01, deadline - time.monotonic())), ensure_ascii=False))
            except queue.Empty:
                pass
