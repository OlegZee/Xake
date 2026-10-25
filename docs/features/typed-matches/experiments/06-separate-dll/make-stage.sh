#!/bin/sh
# Experiment 06: lays out stage/ (loose dlls, as a build tree would have them) after
#   (cd DesignTime && dotnet build -c Release)
#   stage/with/lib/netstandard2.0/ {Xake,Xake.Patterns,XakeLike}.dll; stage/with/typeproviders/fsharp41/netstandard2.0/ = whole design-time output folder (what the F# SDK packs)
#   stage/without/lib/netstandard2.0/...same dlls, no design-time dll anywhere
#   stage/xake-only/Xake.dll  (the engine alone)
here=$(cd "$(dirname "$0")" && pwd)
rm -rf "$here/stage"
xake="$here/../../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
for v in with without; do
  d="$here/stage/$v/lib/netstandard2.0"; mkdir -p "$d"
  cp "$xake" "$here/Runtime/bin/Release/netstandard2.0/Xake.Patterns.dll" "$here/Core/bin/Release/netstandard2.0/XakeLike.dll" "$d/"
done
mkdir -p "$here/stage/with/typeproviders/fsharp41/netstandard2.0" "$here/stage/xake-only"
cp "$here"/DesignTime/bin/Release/netstandard2.0/*.dll "$here/stage/with/typeproviders/fsharp41/netstandard2.0/"
cp "$xake" "$here/stage/xake-only/"
find "$here/stage" -type f | sed "s#$here/##" | sort
