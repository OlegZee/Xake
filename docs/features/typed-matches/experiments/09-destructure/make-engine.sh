#!/bin/sh
# Experiment 09: the engine of 08 (src/core + 08's engine.patch), built into 09/engine (git-ignored).
#   sh make-engine.sh   ->   engine/src/core/Xake.fsproj
here=$(cd "$(dirname "$0")" && pwd)
repo="$here/../../../../.."
rm -rf "$here/engine"
mkdir -p "$here/engine/src"
rsync -a --exclude bin --exclude obj "$repo/src/core" "$here/engine/src/"
(cd "$here/engine" && patch -p1 --quiet < "$here/../08-consolidated/engine.patch") && echo "engine/src/core: src/core + 08's engine.patch"
