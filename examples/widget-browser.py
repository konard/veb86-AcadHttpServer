#!/usr/bin/env python3
"""Browse a sample widget tree with the standalone host (Enter stops the server).

Build first: dotnet build examples/StandaloneHost -c Release
Run from the repository root: python3 examples/widget-browser.py
"""
import pathlib
import subprocess
import tempfile


files = {
    "managerGRIST/index.html": '<!doctype html><html lang="en"><meta charset="utf-8"><title>Manager widget</title>'
                               '<link rel="stylesheet" href="style.css"><h1>Manager widget</h1>'
                               '<p>This file opens when you request index.html explicitly.</p>'
                               '<script src="manager.js"></script></html>',
    "managerGRIST/manager.js": 'document.body.dataset.widget = "managerGRIST";\n',
    "managerGRIST/style.css": 'body { font-family: sans-serif; margin: 2rem; }\n',
    "catalog/index.html": '<!doctype html><html lang="en"><meta charset="utf-8"><title>Catalog widget</title>'
                          '<h1>Catalog widget</h1><script src="catalog.js"></script></html>',
    "catalog/catalog.js": 'document.body.dataset.widget = "catalog";\n',
    "common/utils.js": '// Shared widget utilities.\n',
    "common/notes & виджет.txt": 'Widget file with spaces, an ampersand, and Unicode.\n',
}

with tempfile.TemporaryDirectory(prefix="acadhttp-widget-browser-") as directory:
    for name, content in files.items():
        path = pathlib.Path(directory, name)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
    subprocess.run(["dotnet", "run", "--no-build", "-c", "Release", "--project", "examples/StandaloneHost",
                    "--", "0", directory], check=True)
