"""Negative checks for scripts/test-release-package.ps1: a broken archive must be rejected.

Usage: python3 experiments/verify-release-package.py <pwsh> <package-dir> <version>
The package directory must contain the output of scripts/package-release.ps1.
"""
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

pwsh, package, version = sys.argv[1], Path(sys.argv[2]), sys.argv[3]
root = Path(__file__).resolve().parent.parent
manual = f"AcadHttpServer-manual-install-v{version}.zip"
bundle = "AppData/Roaming/Autodesk/ApplicationPlugins/AcadHttpServer.bundle"


def check(directory):
    result = subprocess.run([pwsh, "-NoProfile", "-File", str(root / "scripts/test-release-package.ps1"),
                             "-Version", version, "-PackageDirectory", str(directory)],
                            capture_output=True, text=True)
    return result.returncode, [l for l in result.stdout.splitlines() if l.startswith("FAIL")]


def mutate(name, change):
    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        for f in package.iterdir():
            shutil.copy(f, tmp)
        src = zipfile.ZipFile(package / manual)
        with zipfile.ZipFile(tmp / manual, "w") as dst:
            for item in src.infolist():
                data = change(item.filename, src.read(item))
                if data is not None:
                    dst.writestr(item.filename, data)
            if name == "extra pdb":
                dst.writestr(f"{bundle}/Contents/AutoCAD2020-2024/AutoCADHttp.pdb", b"x")
        code, fails = check(tmp)
        print(f"{name}: exit {code}: {fails}")
        assert code != 0 and fails, name


code, fails = check(package)
print(f"original: exit {code}: {fails}")
assert code == 0 and not fails

mutate("extra pdb", lambda n, d: d)
mutate("missing dll", lambda n, d: None if n.endswith("AutoCADHttp.dll") else d)
mutate("wrong AppVersion", lambda n, d: d.replace(f'AppVersion="{version}"'.encode(), b'AppVersion="9.9.9"')
       if n.endswith("PackageContents.xml") else d)
mutate("AutoCAD 2025 range", lambda n, d: d.replace(b'SeriesMax="R24.3"', b'SeriesMax="R25.0"')
       if n.endswith("PackageContents.xml") else d)
mutate("wrong module", lambda n, d: d.replace(b"AutoCAD2020-2024/AutoCADHttp.dll", b"Other/AutoCADHttp.dll")
       if n.endswith("PackageContents.xml") else d)
print("All negative checks rejected as expected.")
