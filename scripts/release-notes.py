#!/usr/bin/env python3
"""Print the CHANGELOG section for a release version."""

from __future__ import annotations

import pathlib
import re
import sys


def main() -> int:
    if len(sys.argv) != 2:
        raise SystemExit("usage: release-notes.py VERSION")

    version = sys.argv[1]
    changelog = pathlib.Path(__file__).resolve().parents[1] / "CHANGELOG.md"
    text = changelog.read_text(encoding="utf-8")
    pattern = re.compile(
        rf"^## \[{re.escape(version)}\](?:[^\n]*)\n(?P<body>.*?)(?=^## \[|\Z)",
        re.MULTILINE | re.DOTALL,
    )
    match = pattern.search(text)
    if not match:
        raise SystemExit(f"CHANGELOG.md has no section for {version}")

    body = match.group("body").strip()
    if not body:
        raise SystemExit(f"CHANGELOG.md section for {version} is empty")

    print(body)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
