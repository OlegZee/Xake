#!/bin/sh
# Experiment 08: the engine as it would be after the change -- a copy of the real src/core with
# engine.patch applied. The patch IS the proposed src/core change (`patch -p1 < engine.patch` at the
# repo root applies it); tests.patch is the matching src/tests change.
#   sh make-engine.sh   ->   engine/src/core/Xake.fsproj (assembly Xake, net462 + netstandard2.0)
here=$(cd "$(dirname "$0")" && pwd)
repo="$here/../../../../.."
rm -rf "$here/engine"
mkdir -p "$here/engine/src"
rsync -a --exclude bin --exclude obj "$repo/src/core" "$here/engine/src/"
(cd "$here/engine" && patch -p1 --quiet < "$here/engine.patch") && echo "engine/src/core: src/core + engine.patch"
