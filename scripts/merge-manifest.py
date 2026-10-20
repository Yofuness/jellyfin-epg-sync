#!/usr/bin/env python3
"""Merge a single-release manifest (from build.sh) into the repo's manifest.json.

Usage: merge-manifest.py <repo manifest.json> <new dist/manifest.json>
Newest version first; an existing entry with the same version is replaced.
"""
import json
import sys


def version_key(entry):
    return tuple(int(p) for p in entry["version"].split("."))


def main(repo_path, new_path):
    with open(new_path, encoding="utf-8") as f:
        new = json.load(f)[0]
    try:
        with open(repo_path, encoding="utf-8") as f:
            repo = json.load(f)
    except FileNotFoundError:
        repo = []

    if not repo:
        repo = [dict(new, versions=[])]
    plugin = next((p for p in repo if p["guid"] == new["guid"]), None)
    if plugin is None:
        raise SystemExit(f"plugin {new['guid']} not in {repo_path}")

    # Plugin metadata follows the newest build; versions accumulate.
    versions = [v for v in plugin["versions"] if v["version"] not in {n["version"] for n in new["versions"]}]
    plugin.update({k: v for k, v in new.items() if k != "versions"})
    plugin["versions"] = sorted(versions + new["versions"], key=version_key, reverse=True)

    with open(repo_path, "w", encoding="utf-8") as f:
        json.dump(repo, f, indent=2, ensure_ascii=False)
        f.write("\n")


if __name__ == "__main__":
    main(*sys.argv[1:3])
