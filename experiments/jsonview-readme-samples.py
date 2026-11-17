#!/usr/bin/env python3
"""Print real JSONView lines for the README samples (issue #17). Build StandaloneHost (Release) first."""
import json, pathlib, shutil, socket, subprocess, tempfile, time, urllib.request, urllib.error, http.client

output = pathlib.Path("examples/StandaloneHost/bin/Release/net8.0").resolve()
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

def post(port, body, ct="application/json", method="POST"):
    data = body if isinstance(body, bytes) else body.encode()
    req = urllib.request.Request(f"http://127.0.0.1:{port}/ipc", data if method == "POST" else None,
                                 {"Content-Type": ct} if ct else {}, method=method)
    try:
        with opener.open(req, timeout=5) as r: return r.status
    except urllib.error.HTTPError as e: return e.code

with socket.socket() as s:
    s.bind(("127.0.0.1", 0)); port = s.getsockname()[1]
with tempfile.TemporaryDirectory() as d:
    plugin = pathlib.Path(d) / "plugin"; shutil.copytree(output, plugin)
    (plugin / "AutoCADHttp.settings.json").write_text(json.dumps({"port": port, "JSONView": 1}))
    log = pathlib.Path(d) / "log.txt"
    with log.open("w") as f:
        host = subprocess.Popen(["dotnet", str(plugin / "StandaloneHost.dll")], cwd=d, stdin=subprocess.PIPE, stdout=f, stderr=subprocess.STDOUT)
        time.sleep(3)
        bodies = [
            '{"id":"1","type":"command","command":"PING","parameters":{}}',
            '{"id":"2","type":"command","command":"PING"}',
            '{\n  "id": "3",\n  "type": "command",\n  "command": "PING",\n  "parameters": {},\n}',
            '{"id":"4","type":"command","command":"PING","parameters":"{}"}',
            "{'id':'5','type':'command','command':'PING','parameters':{}}",
            '﻿{"id":"6","type":"command","command":"PING","parameters":{}}',
            '{"id":"7","type":"request","command":"PING","parameters":{}}',
        ]
        for b in bodies: print(post(port, b))
        print(post(port, '{"id":"cp1251","text":"Привет"}'.encode("cp1251")))
        print(post(port, bodies[0], "text/plain"))
        print(post(port, b"", None, "GET"))
        c = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
        c.putrequest("POST", "/ipc"); c.putheader("Content-Type", "application/json"); c.putheader("Content-Length", str(10**9)); c.endheaders()
        print(c.getresponse().status); c.close()
        time.sleep(1)
        host.communicate(b"\n", timeout=10)
    for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
        print(line)
