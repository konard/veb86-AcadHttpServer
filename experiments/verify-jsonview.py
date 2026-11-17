#!/usr/bin/env python3
"""Verify the JSONView setting end-to-end with the standalone host (no AutoCAD required).

Checks for JSONView = 1, 0, omitted and invalid: with JSONView = 1 every /ipc JSON is printed verbatim in
arrival order - accepted as "IPC JSON: ...", rejected (syntax, envelope, wrong Content-Type, body not UTF-8)
as "IPC JSON INVALID (<status> <code>: <reason>): ..." (issue #17); with 0 or omitted nothing is printed;
HTTP responses and dispatching are identical in every mode.

Build first: dotnet build examples/StandaloneHost -c Release
Run from the repository root: python3 experiments/verify-jsonview.py
"""
import json
import os
import pathlib
import re
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

output = pathlib.Path("examples/StandaloneHost/bin/Release/net8.0")
logs = pathlib.Path("ci-logs").resolve()
logs.mkdir(exist_ok=True)
environment = os.environ.copy()
for name in ("ACADHTTP_WIDGETS_DIR", "ACADHTTP_EXTERNAL_IPC"):
    environment.pop(name, None)
http = urllib.request.build_opener(urllib.request.ProxyHandler({}))
# Formatted on purpose: the output must keep whitespace, key order and values.
PING = '{\n  "id": "123",\n  "type": "command",\n  "command": "PING",\n  "parameters": {}\n}'


def free_port():
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def post(base_url, body, content_type="application/json"):
    data = body if isinstance(body, bytes) else body.encode()
    request = urllib.request.Request(base_url + "/ipc", data, {"Content-Type": content_type})
    try:
        with http.open(request, timeout=5) as response:
            return response.status, response.read().decode()
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode()


def run(name, settings):
    with tempfile.TemporaryDirectory(prefix="acadhttp-jsonview-") as directory:
        plugin = pathlib.Path(directory) / "plugin"
        shutil.copytree(output, plugin)
        port = free_port()
        settings = dict(settings, port=port)
        (plugin / "AutoCADHttp.settings.json").write_text(json.dumps(settings), encoding="utf-8")
        log_path = logs / f"jsonview-{name}.log"
        with log_path.open("w") as log:
            host = subprocess.Popen(["dotnet", str(plugin / "StandaloneHost.dll")], cwd=directory, env=environment,
                                    stdin=subprocess.PIPE, stdout=log, stderr=subprocess.STDOUT, text=True)
            results = []
            try:
                for _ in range(500):
                    text = log_path.read_text()
                    if "Start(): Started" in text or host.poll() is not None:
                        break
                    time.sleep(0.01)
                if host.poll() is None:
                    base_url = f"http://127.0.0.1:{port}"
                    for body in [PING] + SEQ[:2] + INVALID + SEQ[2:]:
                        results.append(post(base_url, body))
                    results.append(post(base_url, SEQ[0], "text/plain"))
                    time.sleep(0.3)  # let the dispatcher drain the queue
            finally:
                if host.poll() is None:
                    host.stdin.write("\n")
                    host.stdin.flush()
                host.wait(timeout=10)
                host.stdin.close()
        return log_path.read_text(encoding="utf-8", errors="replace"), results


# Rejected bodies: broken syntax, wrong envelope, and Cyrillic text sent in Windows-1251 instead of UTF-8.
INVALID = ["{", '{"id":"bad","type":"unknown"}', '{"id":"cp1251","text":"Привет"}'.encode("cp1251")]
SEQ = [json.dumps({"id": f"seq-{i}", "type": "command", "command": "PING", "parameters": {}}) for i in range(1, 4)]
EXPECTED = [  # printed lines, in sending order
    "[HTTP] IPC JSON: " + PING,
    "[HTTP] IPC JSON: " + SEQ[0],
    "[HTTP] IPC JSON: " + SEQ[1],
    "[HTTP] IPC JSON INVALID (400 INVALID_JSON: Invalid IPC JSON at line 1, column 2 (offset 1): unexpected end of JSON.): {",
    '[HTTP] IPC JSON INVALID (400 INVALID_JSON: type must be "command", "response" or "event", not "unknown".): '
    '{"id":"bad","type":"unknown"}',
    "[HTTP] IPC JSON INVALID (400 Bad Request: Request body must be UTF-8: invalid byte 0xCF at offset 23): "
    '{"id":"cp1251","text":"' + "\ufffd" * 6 + '"}',
    "[HTTP] IPC JSON: " + SEQ[2],
    "[HTTP] IPC JSON INVALID (415 INVALID_CONTENT_TYPE: Content-Type is 'text/plain', use application/json): " + SEQ[0],
]
outputs = {}
for name, settings in [("on", {"JSONView": 1}), ("off", {"JSONView": 0}), ("missing", {})]:
    text, results = run(name, settings)
    outputs[name] = results
    shown = re.findall(r"\[HTTP\] IPC JSON( INVALID \(|: )", text)
    received = re.findall(r'IPC outgoing (123|seq-\d) \(response\) rejected: the external /ipc transport is not running', text)
    assert [code for code, _ in results] == [202, 202, 202, 400, 400, 400, 202, 415], results
    assert received == ["123", "seq-1", "seq-2", "seq-3"], text  # dispatcher got every valid message in every mode
    if name == "on":
        positions = []
        for line in EXPECTED:
            assert line in text, f"missing JSONView line:\n{line}\n--- log ---\n{text}"
            positions.append(text.index(line))
        assert positions == sorted(positions), text  # arrival order, valid and invalid interleaved
        assert len(shown) == len(EXPECTED), text
    else:
        assert not shown, text
    print(f"JSONView {name}: shown={len(shown)} (invalid={shown.count(' INVALID (')}) dispatched={len(received)} "
          f"statuses={[c for c, _ in results]}")
assert outputs["on"] == outputs["off"] == outputs["missing"], outputs  # identical HTTP responses

text, _ = run("invalid", {"JSONView": 2})
assert "JSONView must be the integer 0 or 1" in text and "Start(): Started" not in text, text
print("JSONView invalid value rejected:", next(l for l in text.splitlines() if "JSONView must" in l).strip()[:200])
print("JSONView = 1 / 0 / omitted / invalid verified.")
