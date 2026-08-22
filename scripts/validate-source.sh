#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

python3 - "$ROOT" <<'PY'
import json
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

root = pathlib.Path(sys.argv[1])
project = root / 'Jellyfin.Plugin.PlexBridge' / 'Jellyfin.Plugin.PlexBridge.csproj'
ET.parse(project)
ET.parse(root / 'PlexBridge.slnx')
json.loads((root / 'global.json').read_text())
manifest = json.loads((root / 'manifest.json').read_text())
if not isinstance(manifest, list):
    raise SystemExit('manifest.json must contain a JSON array')

version = (root / 'VERSION').read_text().strip()
if not re.fullmatch(r'\d+\.\d+\.\d+', version):
    raise SystemExit(f'VERSION must be three numeric components, got {version!r}')

build_yaml = (root / 'build.yaml').read_text()
if f'version: "{version}.0"' not in build_yaml:
    raise SystemExit('build.yaml version does not match VERSION')

project_text = project.read_text()
if f'<Version>{version}.0</Version>' not in project_text:
    raise SystemExit('csproj Version does not match VERSION')

required = [
    root / 'LICENSE',
    root / 'README.md',
    root / 'CHANGELOG.md',
    root / 'CONTRIBUTING.md',
    root / 'build.yaml',
    root / 'manifest.json',
    root / 'Jellyfin.Plugin.PlexBridge' / 'Plugin.cs',
    root / 'Jellyfin.Plugin.PlexBridge' / 'PluginServiceRegistrator.cs',
    root / 'Jellyfin.Plugin.PlexBridge' / 'Channels' / 'PlexChannel.cs',
    root / 'Jellyfin.Plugin.PlexBridge' / 'Controllers' / 'PlexBridgeController.cs',
    root / 'Jellyfin.Plugin.PlexBridge' / 'Services' / 'PlexClient.cs',
    root / 'Jellyfin.Plugin.PlexBridge' / 'Services' / 'ProxyTokenService.cs',
    root / 'Jellyfin.Plugin.PlexBridge' / 'Configuration' / 'config.html',
]
for path in required:
    if not path.is_file() or path.stat().st_size == 0:
        raise SystemExit(f'missing or empty required file: {path}')

for path in root.rglob('*'):
    if path.is_file() and path.suffix.lower() in {'.cs', '.html', '.md', '.json', '.yml', '.yaml'}:
        text = path.read_text(errors='ignore')
        if re.search(r'X-Plex-Token\s*[:=]\s*[A-Za-z0-9_-]{10,}', text):
            raise SystemExit(f'possible hard-coded Plex token in {path}')

print('Source structure, versions, XML, JSON, and required-file validation passed.')
PY

bash -n "$ROOT/scripts/package.sh"
bash -n "$ROOT/scripts/package-docker.sh"
python3 -m py_compile "$ROOT/scripts/release-notes.py" "$ROOT/scripts/update-manifest.py"
echo "Shell and Python syntax validation passed."
