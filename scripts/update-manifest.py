#!/usr/bin/env python3
"""Add or replace one Plex Bridge release in a Jellyfin repository manifest."""

from __future__ import annotations

import argparse
import datetime as dt
import json
import pathlib

PLUGIN_GUID = "978ac9fe-8d18-4d26-a432-0d3150143c98"
PLUGIN_NAME = "Plex Bridge"
DESCRIPTION = (
    "Browse and stream remote Plex movie and TV libraries through Jellyfin "
    "without exposing Plex credentials to clients."
)
OVERVIEW = "Remote Plex libraries as a Jellyfin Channel"
CATEGORY = "Channels"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", type=pathlib.Path, required=True)
    parser.add_argument("--repository", required=True, help="GitHub owner/repository")
    parser.add_argument("--version", required=True, help="Three-part version, e.g. 1.0.3")
    parser.add_argument("--target-abi", required=True, help="Jellyfin ABI, e.g. 10.11.11.0")
    parser.add_argument("--asset", required=True)
    parser.add_argument("--checksum", required=True)
    parser.add_argument("--changelog-file", type=pathlib.Path, required=True)
    parser.add_argument("--timestamp")
    return parser.parse_args()


def version_key(value: str) -> tuple[int, ...]:
    return tuple(int(part) for part in value.split("."))


def main() -> int:
    args = parse_args()
    owner, separator, repo = args.repository.partition("/")
    if not separator or not owner or not repo:
        raise SystemExit("--repository must be in owner/repository form")

    manifest = json.loads(args.manifest.read_text(encoding="utf-8")) if args.manifest.exists() else []
    if not isinstance(manifest, list):
        raise SystemExit("manifest root must be a JSON array")

    plugin = next((item for item in manifest if item.get("guid") == PLUGIN_GUID), None)
    if plugin is None:
        plugin = {
            "guid": PLUGIN_GUID,
            "name": PLUGIN_NAME,
            "description": DESCRIPTION,
            "overview": OVERVIEW,
            "owner": owner,
            "category": CATEGORY,
            "versions": [],
        }
        manifest.append(plugin)
    else:
        plugin.update(
            {
                "name": PLUGIN_NAME,
                "description": DESCRIPTION,
                "overview": OVERVIEW,
                "owner": owner,
                "category": CATEGORY,
            }
        )

    four_part_version = args.version if args.version.count(".") == 3 else f"{args.version}.0"
    timestamp = args.timestamp or dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    release = {
        "version": four_part_version,
        "changelog": args.changelog_file.read_text(encoding="utf-8").strip(),
        "targetAbi": args.target_abi,
        "sourceUrl": f"https://github.com/{args.repository}/releases/download/v{args.version}/{args.asset}",
        "checksum": args.checksum.lower(),
        "timestamp": timestamp,
    }

    versions = [item for item in plugin.get("versions", []) if item.get("version") != four_part_version]
    versions.append(release)
    versions.sort(key=lambda item: version_key(item["version"]), reverse=True)
    plugin["versions"] = versions

    manifest.sort(key=lambda item: item.get("name", "").casefold())
    args.manifest.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
