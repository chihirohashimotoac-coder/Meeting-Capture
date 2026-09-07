#!/usr/bin/env python3
"""Verify (or refresh) the SHA-256 values pinned in ModelCatalog.cs.

The catalog pins a hash for every AI model the application may download. Those
values must come from the real files, not from a web page, so this script does
what the application itself does: fetch the bytes and hash them.

Usage:
    python3 tools/model_checksums.py selftest  # parse only, no network access
    python3 tools/model_checksums.py verify    # fail if any pin is wrong
    python3 tools/model_checksums.py refresh   # print a table of measured values

It is a maintenance tool. It is not part of the build and nothing it produces is
shipped to users.
"""
from __future__ import annotations

import hashlib
import re
import sys
import urllib.request
from pathlib import Path

CATALOG = Path(__file__).resolve().parent.parent / "src/MeetingRecorder.Core/ModelManagement/ModelCatalog.cs"
CHUNK = 1 << 20


def parse_catalog(text: str) -> list[dict[str, str]]:
    """Extracts one entry per `new ModelDescriptor { ... }` block.

    Two details matter and are easy to get wrong:

    * ``Url`` must not also match ``LicenseUrl``. Without the lookbehind this
      function happily "verifies" the licence pages instead of the models.
    * ``Id`` is written as a ``const`` reference (``Id = WhisperTiny``), so the
      constant has to be resolved to its string value.
    """
    base_match = re.search(r'WhisperBaseUrl\s*=\s*"([^"]+)"', text)
    base_url = base_match.group(1) if base_match else ""

    # public const string WhisperTiny = "whisper-tiny-q5_1";
    constants = dict(re.findall(r'const\s+string\s+(\w+)\s*=\s*"([^"]+)"', text))

    entries = []
    for block in re.findall(r"new ModelDescriptor\s*\{(.*?)\n        \}", text, re.S):
        def field(name: str) -> str | None:
            # (?<![A-Za-z]) stops "Url" from matching inside "LicenseUrl".
            match = re.search(rf'(?<![A-Za-z]){name}\s*=\s*"([^"]*)"', block)
            return match.group(1) if match else None

        identifier = re.search(r"(?<![A-Za-z])Id\s*=\s*(\w+)\s*,", block)
        model_id = constants.get(identifier.group(1), identifier.group(1)) if identifier else field("Id")

        url = field("Url")
        if url is None:
            suffix = re.search(r'(?<![A-Za-z])Url\s*=\s*WhisperBaseUrl\s*\+\s*"([^"]+)"', block)
            url = base_url + suffix.group(1) if suffix else None

        size = re.search(r"ApproximateSizeBytes\s*=\s*([0-9_]+)", block)

        if url:
            entries.append(
                {
                    "id": model_id or "?",
                    "url": url,
                    "file": field("FileName") or url.rsplit("/", 1)[-1],
                    "sha256": field("Sha256"),
                    "size": int(size.group(1).replace("_", "")) if size else 0,
                }
            )

    return entries


def check_parse(entries: list[dict[str, str]]) -> list[str]:
    """Sanity checks that run before a single byte is downloaded.

    A silently mis-parsed catalog is the dangerous failure here: it would hash
    the wrong files and either report a false mismatch or, worse, a false match.
    """
    problems = []
    if len(entries) < 4:
        problems.append(f"only {len(entries)} model(s) parsed; the catalog should hold more")

    for entry in entries:
        if entry["id"] in ("?", ""):
            problems.append(f"an entry has no resolvable Id ({entry['url']})")
        if not entry["url"].startswith("https://"):
            problems.append(f"{entry['id']}: URL is not HTTPS")
        if not entry["url"].endswith((".bin", ".gguf")):
            problems.append(f"{entry['id']}: URL does not point at a model file ({entry['url']})")
        if entry["size"] <= 0:
            problems.append(f"{entry['id']}: no declared size")

    return problems


def measure(url: str) -> tuple[int, str]:
    digest = hashlib.sha256()
    total = 0
    with urllib.request.urlopen(url) as response:  # noqa: S310 - fixed allow-list of HTTPS URLs
        while chunk := response.read(CHUNK):
            digest.update(chunk)
            total += len(chunk)
    return total, digest.hexdigest()


def main() -> int:
    mode = sys.argv[1] if len(sys.argv) > 1 else "verify"
    entries = parse_catalog(CATALOG.read_text(encoding="utf-8"))
    if not entries:
        print("No model entries found in the catalog.", file=sys.stderr)
        return 2

    problems = check_parse(entries)
    if problems:
        for problem in problems:
            print(f"catalog parse problem: {problem}", file=sys.stderr)
        return 2

    if mode == "selftest":
        for entry in entries:
            print(f"{entry['id']:36s} {entry['size']:>13,}  {entry['sha256'] or '(no pin)'}")
        print(f"\nParsed {len(entries)} model(s); no downloads performed.")
        return 0

    failures = 0
    print(f"| id | bytes | sha256 | pinned |")
    print("| --- | --- | --- | --- |")

    for entry in entries:
        if not entry["url"].startswith("https://"):
            print(f"{entry['id']}: refusing a non-HTTPS URL", file=sys.stderr)
            failures += 1
            continue

        size, sha = measure(entry["url"])
        pinned = entry["sha256"]
        state = "no pin" if pinned is None else ("match" if pinned == sha else "MISMATCH")
        print(f"| {entry['id']} | {size} | {sha} | {state} |")

        if mode == "verify":
            if pinned is None:
                print(f"{entry['id']}: no SHA-256 is pinned", file=sys.stderr)
                failures += 1
            elif pinned != sha:
                print(f"{entry['id']}: pinned {pinned}, measured {sha}", file=sys.stderr)
                failures += 1
            elif entry["size"] != size:
                print(f"{entry['id']}: declared size {entry['size']}, actual {size}", file=sys.stderr)
                failures += 1

    if mode == "verify" and failures:
        print(f"\n{failures} model(s) failed verification.", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
