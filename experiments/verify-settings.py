#!/usr/bin/env python3
"""Verify DLL-adjacent JSON settings from a separate working directory.

Build first: dotnet build examples/StandaloneHost -c Release
Run from the repository root: python3 experiments/verify-settings.py
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
import urllib.request


def stop(host):
    if host.poll() is None:
        host.stdin.write("\n")
        host.stdin.flush()
    try:
        host.wait(timeout=5)
    except subprocess.TimeoutExpired:
        host.kill()
        host.wait()
        raise
    finally:
        host.stdin.close()


output = pathlib.Path("examples/StandaloneHost/bin/Release/net8.0")
logs = pathlib.Path("ci-logs").resolve()
logs.mkdir(exist_ok=True)
environment = os.environ.copy()
environment.pop("ACADHTTP_WIDGETS_DIR", None)
environment.pop("ACADHTTP_EXTERNAL_IPC", None)
with tempfile.TemporaryDirectory(prefix="acadhttp-settings-") as directory:
    root = pathlib.Path(directory)
    plugin = root / "plugin"
    shutil.copytree(output, plugin)
    working = root / "working"
    working.mkdir()
    widgets = plugin / "widgets"
    widgets.mkdir()
    (widgets / "index.html").write_text("<html>configured widget</html>", encoding="utf-8")
    with socket.socket() as probe:
        probe.bind(("127.0.0.2", 0))
        port = probe.getsockname()[1]
    settings = plugin / "AutoCADHttp.settings.json"
    settings.write_text(json.dumps({"address": "127.0.0.2", "port": port, "widgetsDirectory": "widgets"}), encoding="utf-8")
    # A different file in CWD must never supply the settings.
    (working / settings.name).write_text("invalid JSON in working directory", encoding="utf-8")
    log_path = logs / "settings-host.log"
    with log_path.open("w") as log:
        host = subprocess.Popen(["dotnet", str(plugin / "StandaloneHost.dll")], cwd=working,
                                env=environment, stdin=subprocess.PIPE, stdout=log,
                                stderr=subprocess.STDOUT, text=True)
        try:
            base_url = None
            for _ in range(500):
                if host.poll() is not None:
                    raise RuntimeError(log_path.read_text())
                match = re.search(r"Start\(\): Started (http://\S+)/ping", log_path.read_text())
                if match:
                    base_url = match.group(1)
                    break
                time.sleep(0.01)
            assert base_url == f"http://127.0.0.2:{port}", log_path.read_text()
            http = urllib.request.build_opener(urllib.request.ProxyHandler({}))
            with http.open(base_url + "/ping", timeout=5) as response:
                assert json.load(response)["status"] == "ok"
            with http.open(base_url + "/widgets/", timeout=5) as response:
                assert response.read().decode() == "<html>configured widget</html>"
            command = json.dumps({"id": "settings-ping", "type": "command", "command": "PING", "parameters": {}}).encode()
            with http.open(urllib.request.Request(base_url + "/ipc", command, {"Content-Type": "application/json"}), timeout=5) as response:
                assert response.status == 202
                assert json.load(response)["id"] == "settings-ping"
        finally:
            stop(host)
        assert host.returncode == 0, log_path.read_text()
    settings.write_text('{"port": "invalid"}', encoding="utf-8")
    with (logs / "settings-invalid-host.log").open("w") as log:
        host = subprocess.Popen(["dotnet", str(plugin / "StandaloneHost.dll")], cwd=working,
                                env=environment, stdin=subprocess.PIPE, stdout=log,
                                stderr=subprocess.STDOUT, text=True)
        stop(host)
    diagnostic = (logs / "settings-invalid-host.log").read_text()
    assert host.returncode != 0, diagnostic
    assert "AutoCADHttp.settings.json" in diagnostic and "port" in diagnostic, diagnostic
    assert "Start(): Started" not in diagnostic, diagnostic
print("DLL-adjacent address, port, relative widgets, IPC, shutdown, and invalid settings verified.")
