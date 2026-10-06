#!/usr/bin/env python3
"""Run the standalone host and the reusable IPC example, then verify static/binary files.

Build first: dotnet build examples/StandaloneHost -c Release
Run from the repository root: python3 experiments/verify-stage2.py
The host and callback server listen on ephemeral loopback ports; every request is bounded.
"""
import importlib.util
import json
import pathlib
import re
import socket
import subprocess
import tempfile
import time
import urllib.request

spec = importlib.util.spec_from_file_location("check_ipc", "examples/check-ipc.py")
check_ipc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check_ipc)

with socket.socket() as probe:
    probe.bind(("127.0.0.1", 0))
    callback_port = probe.getsockname()[1]

log_path = pathlib.Path("ci-logs/stage2-host.log")
log_path.parent.mkdir(exist_ok=True)
with tempfile.TemporaryDirectory(prefix="acadhttp-widgets-") as directory:
    root = pathlib.Path(directory)
    (root / "index.html").write_text("<html>static transport check</html>", encoding="utf-8")
    for name in ("managerGRIST", "catalog", "common"):
        (root / name).mkdir()
    (root / "managerGRIST" / "index.html").write_text("<html>manager widget</html>", encoding="utf-8")
    (root / "managerGRIST" / "manager.js").write_text("// manager", encoding="utf-8")
    (root / "managerGRIST" / "style.css").write_text("body {}", encoding="utf-8")
    binary = bytes([137, 80, 78, 71, 0, 255, 128])
    (root / "image.png").write_bytes(binary)
    with log_path.open("w") as log:
        host = subprocess.Popen(
            ["dotnet", "run", "--no-build", "-c", "Release", "--project", "examples/StandaloneHost", "--", "0", directory,
             f"http://127.0.0.1:{callback_port}/ipc"],
            stdin=subprocess.PIPE, stdout=log, stderr=subprocess.STDOUT, text=True)
        try:
            base_url = None
            for _ in range(500):
                if host.poll() is not None:
                    raise RuntimeError(log_path.read_text())
                match = re.search(r"Start\(\): Started (http://127\.0\.0\.1:\d+)/ping", log_path.read_text())
                if match:
                    base_url = match.group(1)
                    break
                time.sleep(0.01)
            assert base_url, "Host did not start within five seconds"
            check_ipc.run_check(base_url, callback_port)
            with urllib.request.urlopen(base_url + "/widgets", timeout=5) as response:
                assert response.headers["Content-Type"] == "text/html; charset=utf-8"
                assert response.url == base_url + "/widgets/"
                listing = response.read().decode()
                for name in ("managerGRIST/", "catalog/", "common/", "index.html", "image.png"):
                    assert f'href="{name}"' in listing, listing
                assert "static transport check" not in listing
            with urllib.request.urlopen(base_url + "/widgets/managerGRIST/", timeout=5) as response:
                listing = response.read().decode()
                for name in ("index.html", "manager.js", "style.css"):
                    assert f'href="{name}"' in listing, listing
                assert "manager widget" not in listing
            with urllib.request.urlopen(base_url + "/widgets/managerGRIST/index.html", timeout=5) as response:
                assert response.read().decode() == "<html>manager widget</html>"
            with urllib.request.urlopen(base_url + "/widgets/index.html", timeout=5) as response:
                assert response.read().decode() == "<html>static transport check</html>"
            with urllib.request.urlopen(base_url + "/widgets/image.png", timeout=5) as response:
                assert response.headers["Content-Type"] == "image/png"
                assert response.read() == binary
            event = json.dumps({"id": "external-event", "type": "event", "event": "OBJECT_CREATED", "parameters": {}}).encode()
            with urllib.request.urlopen(urllib.request.Request(base_url + "/ipc", event, {"Content-Type": "application/json"}), timeout=5) as response:
                assert response.status == 202
            for _ in range(500):
                if "IPC received:" in log_path.read_text():
                    break
                time.sleep(0.01)
            assert "IPC received:" in log_path.read_text()
            print("Widget directory listings, explicit HTML files, binary image, and incoming event verified.")
        finally:
            if host.poll() is None:
                host.stdin.write("\n")
                host.stdin.flush()
            try:
                host.wait(timeout=5)
            except subprocess.TimeoutExpired:
                host.kill()
                host.wait()
                raise
            host.stdin.close()
        assert host.returncode == 0, log_path.read_text()
print("Standalone shutdown completed.")
