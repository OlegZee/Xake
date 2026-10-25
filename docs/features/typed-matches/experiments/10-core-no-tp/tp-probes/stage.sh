#!/bin/sh
# Stages 10's Xake.dll (with TypedPatterns.fs) next to 08's provider runtime dll, and 08's
# design-time output where the compiler looks for it (../../typeproviders/fsharp41/netstandard2.0
# relative to the runtime dll). One Xake.dll at run time, so no type-load clash between builds.
here=$(cd "$(dirname "$0")" && pwd)
e08="$here/../../08-consolidated/patterns/bin"
s="$here/.stage"
rm -rf "$s"
mkdir -p "$s/bin/Release/netstandard2.0" "$s/bin/typeproviders/fsharp41"
cp "$here/../engine/src/core/bin/Release/netstandard2.0/Xake.dll" "$s/bin/Release/netstandard2.0/"
cp "$e08/Release/netstandard2.0/Xake.Patterns.Exp08.dll" "$s/bin/Release/netstandard2.0/"
cp -R "$e08/typeproviders/fsharp41/netstandard2.0" "$s/bin/typeproviders/fsharp41/"
ls -R "$s"
cp "$here/../engine/src/core/bin/Release/netstandard2.0/Xake.dll" "$s/bin/typeproviders/fsharp41/netstandard2.0/"   # the same Xake.dll at design time: fsi hosts both in one process
