#!/usr/bin/env python3
"""
Updates manifest.json with a new version entry after a release build.
Usage: python3 update_manifest.py <version> <checksum>
  version  - e.g. 1.0.0.0
  checksum - MD5 hex of the release zip
"""
from __future__ import annotations

import argparse
import os
import re
import json
import datetime
import yaml


MANIFEST_FILE = "manifest.json"
BUILD_YAML = "build.yaml"
REPO = "Awatatah/Trailers4Jellyfin"
ZIP_PREFIX = "trailers4jellyfin"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("version")
    parser.add_argument("checksum")
    parser.add_argument("--build-file", default=BUILD_YAML)
    parser.add_argument("--repository", default=os.environ.get("GITHUB_REPOSITORY", REPO))
    args = parser.parse_args()
    version = args.version
    checksum = args.checksum.lower()
    if not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", version):
        parser.error("version must have four numeric components")
    if not re.fullmatch(r"[0-9a-f]{32}", checksum):
        parser.error("checksum must be the release ZIP's MD5 hex digest")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", args.repository):
        parser.error("repository must be owner/name")

    with open(args.build_file) as f:
        build = yaml.safe_load(f)
    if str(build["version"]) != version:
        parser.error("release version does not match build.yaml")

    target_abi = build["targetAbi"]
    changelog = "".join(f"- {item}\n" for item in build.get("changelog", []))
    source_url = f"https://github.com/{args.repository}/releases/download/v{version}/{ZIP_PREFIX}_{version}.zip"
    timestamp = datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")

    new_entry = {
        "checksum": checksum,
        "changelog": changelog,
        "targetAbi": target_abi,
        "sourceUrl": source_url,
        "timestamp": timestamp,
        "version": version,
    }

    with open(MANIFEST_FILE) as f:
        manifest = json.load(f)

    existing_versions = [v["version"] for v in manifest[0]["versions"]]
    if version in existing_versions:
        existing = next(v for v in manifest[0]["versions"] if v["version"] == version)
        if any(existing[key] != new_entry[key] for key in ("checksum", "targetAbi", "sourceUrl")):
            parser.error("existing version differs from this release; publish a new version")
        print(f"Version {version} already exists in manifest. Nothing to do.")
        return

    manifest[0]["versions"].append(new_entry)
    manifest[0]["versions"].sort(
        key=lambda entry: tuple(int(part) for part in entry["version"].split(".")),
        reverse=True,
    )

    with open(MANIFEST_FILE, "w") as f:
        json.dump(manifest, f, indent=4)
        f.write("\n")

    print(f"manifest.json updated with version {version}")


if __name__ == "__main__":
    main()
