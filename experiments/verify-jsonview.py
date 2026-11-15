#!/usr/bin/env python3
"""Verify the JSONView setting end-to-end with the standalone host (no AutoCAD required).

Checks for JSONView = 1, 0, omitted and invalid: accepted /ipc JSON is printed verbatim and in order
only when JSONView = 1, invalid JSON is never printed, and HTTP responses / dispatching stay identical.

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


def post(base_url, body):
    request = urllib.request.Request(base_url + "/ipc", body.encode(), {"Content-Type": "application/json"})
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
                    sequence = [json.dumps({"id": f"seq-{i}", "type": "command", "command": "PING", "parameters": {}})
                                for i in range(1, 4)]
                    for body in [PING] + sequence + ["{", '{"id":"bad","type":"unknown"}']:
                        results.append(post(base_url, body))
                    time.sleep(0.3)  # let the dispatcher drain the queue
            finally:
                if host.poll() is None:
                    host.stdin.write("\n")
                    host.stdin.flush()
                host.wait(timeout=10)
                host.stdin.close()
        return log_path.read_text(), results


sequence_bodies = [f'"id": "seq-{i}"' for i in range(1, 4)]
outputs = {}
for name, settings in [("on", {"JSONView": 1}), ("off", {"JSONView": 0}), ("missing", {})]:
    text, results = run(name, settings)
    outputs[name] = results
    shown = re.findall(r"\[HTTP\] IPC JSON: ", text)
    received = re.findall(r'IPC result \(external endpoint not configured\): \{"id":"(123|seq-\d)"', text)
    assert [code for code, _ in results] == [202, 202, 202, 202, 400, 400], results
    assert received == ["123", "seq-1", "seq-2", "seq-3"], text  # dispatcher got every valid message in every mode
    if name == "on":
        assert "[HTTP] IPC JSON: " + PING in text, text
        positions = [text.index(body) for body in sequence_bodies]
        assert positions == sorted(positions), text
        assert len(shown) == 4, text
        assert '"unknown"' not in text and "IPC JSON: {\n" not in text.replace("IPC JSON: " + PING, ""), text
    else:
        assert not shown, text
    print(f"JSONView {name}: shown={len(shown)} dispatched={len(received)} statuses={[c for c, _ in results]}")
assert outputs["on"] == outputs["off"] == outputs["missing"], outputs  # identical HTTP responses

text, _ = run("invalid", {"JSONView": 2})
assert "JSONView must be the integer 0 or 1" in text and "Start(): Started" not in text, text
print("JSONView invalid value rejected:", next(l for l in text.splitlines() if "JSONView must" in l).strip()[:200])
print("JSONView = 1 / 0 / omitted / invalid verified.")
