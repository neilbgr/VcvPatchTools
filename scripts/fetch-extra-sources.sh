#!/usr/bin/env bash
# Fetches the plugins of catalog/extra-sources.txt at their pinned commits into out/extra-plugins/<slug>
# (shallow, no submodules: only the sources are read, never built).
set -euo pipefail
cd "$(dirname "$0")/.."
dest=out/extra-plugins
mkdir -p "$dest"
grep -Ev '^\s*(#|$)' catalog/extra-sources.txt | while read -r slug url commit; do
    dir="$dest/$slug"
    if [ -d "$dir/.git" ] && [ "$(git -C "$dir" rev-parse HEAD)" = "$commit" ]; then
        continue
    fi
    rm -rf "$dir"
    git init -q "$dir"
    git -C "$dir" fetch -q --depth 1 "$url" "$commit"
    git -C "$dir" -c advice.detachedHead=false checkout -q FETCH_HEAD
    echo "$slug @ ${commit:0:10}"
done
