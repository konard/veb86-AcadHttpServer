#!/usr/bin/env python3
"""Plain Python HTTP/1.0 /ipc server for experiments/OutboxBurst (prints each accepted request)."""
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
class H(BaseHTTPRequestHandler):
    def do_POST(self):
        self.rfile.read(int(self.headers.get("Content-Length", 0)))
        self.send_response(202); self.send_header("Content-Length", "0"); self.end_headers()
    def log_message(self, fmt, *args):
        print("server:", fmt % args, flush=True)
s = ThreadingHTTPServer(("127.0.0.1", int(sys.argv[1])), H)
s.daemon_threads = True
s.serve_forever()
