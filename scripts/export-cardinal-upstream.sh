#!/bin/sh
# Copies the files "vcvpatch catalog cardinal" reads, as they are on DISTRHO/Cardinal's main branch,
# from a local Cardinal clone with an "upstream" remote: plugins/plugins.cpp, src/override/plugin.cpp
# and every plugins/<dir>/plugin.json (submodule manifests at the commit upstream pins).
# Usage: export-cardinal-upstream.sh <Cardinal clone> <output dir> [ref, default upstream/main]
set -eu
repo=$1
out=$2
ref=${3:-upstream/main}

git -C "$repo" fetch -q upstream
rm -rf "$out"
mkdir -p "$out/plugins" "$out/src/override"
git -C "$repo" show "$ref:plugins/plugins.cpp" > "$out/plugins/plugins.cpp"
git -C "$repo" show "$ref:src/override/plugin.cpp" > "$out/src/override/plugin.cpp"

git -C "$repo" ls-tree "$ref" plugins/ | while read -r mode type sha path; do
    dir=$(basename "$path")
    case $type in
        commit) git -C "$repo/$path" cat-file -e "$sha:plugin.json" 2>/dev/null || { echo "$path: $sha not fetched, skipped" >&2; continue; }
                mkdir -p "$out/plugins/$dir"
                git -C "$repo/$path" show "$sha:plugin.json" > "$out/plugins/$dir/plugin.json" ;;
        tree)   git -C "$repo" cat-file -e "$ref:$path/plugin.json" 2>/dev/null || continue
                mkdir -p "$out/plugins/$dir"
                git -C "$repo" show "$ref:$path/plugin.json" > "$out/plugins/$dir/plugin.json" ;;
    esac
done
echo "$(git -C "$repo" rev-parse --short "$ref")" > "$out/REVISION"
