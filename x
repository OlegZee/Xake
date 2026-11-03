#!/bin/sh
# ./x <target>... [-d NAME=value]...: dotnet fsi build.fsx -- -- <args> from this folder, with the compiler server on
cd "$(dirname "$0")" || exit 1
exec dotnet fsi build.fsx -- -- -d CSC_SERVER=on "$@"
