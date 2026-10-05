#!/usr/bin/env python3
"""Check a running standalone host's /ipc using a local HTTP callback server.

Start the host with ACADHTTP_EXTERNAL_IPC=http://127.0.0.1:5001/ipc, then run this script.
Uses only the Python standard library; Python is not needed by the plugin.
"""
import argparse
import json
import queue
import threading
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


def run_check(base_url, callback_port=5001):
    received = queue.Queue()

    class Callback(BaseHTTPRequestHandler):
        def do_POST(self):
            if self.path != "/ipc":
                self.send_error(404)
                return
            length = int(self.headers.get("Content-Length", 0))
            if not 0 < length <= 1024 * 1024:
                self.send_error(400)
                return
            received.put(json.loads(self.rfile.read(length)))
            self.send_response(202)
            self.send_header("Content-Length", "0")
            self.end_headers()

        def log_message(self, *_args):
            pass

    callback = ThreadingHTTPServer(("127.0.0.1", callback_port), Callback)
    callback.daemon_threads = False
    thread = threading.Thread(target=callback.serve_forever)
    thread.start()
    try:
        for message_id, command, expected in [("check-ping", "PING", "ok"), ("check-unknown", "LINE", "error")]:
            body = json.dumps({"id": message_id, "type": "command", "command": command, "parameters": {}}).encode()
            request = urllib.request.Request(base_url + "/ipc", body, {"Content-Type": "application/json"})
            with urllib.request.urlopen(request, timeout=5) as response:
                assert response.status == 202
                assert json.load(response) == {"id": message_id, "type": "response", "status": "ok", "result": {"queued": True}}
            result = received.get(timeout=5)
            assert result["id"] == message_id and result["type"] == "response"
            assert result["status"] == expected
            if expected == "error":
                assert result["error"]["code"] == "UNKNOWN_COMMAND"
            print(json.dumps(result, ensure_ascii=False))
        with urllib.request.urlopen(base_url + "/ping", timeout=5) as response:
            assert json.load(response)["status"] == "ok"
        print("Bidirectional IPC and /ping verified.")
    finally:
        callback.shutdown()
        thread.join()
        callback.server_close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://127.0.0.1:5000")
    parser.add_argument("--callback-port", type=int, default=5001)
    args = parser.parse_args()
    run_check(args.base_url.rstrip("/"), args.callback_port)
