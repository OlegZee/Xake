#!/usr/bin/env bash
# End-to-end self-verification of the hermetic build on ar-net-core-dataengine, across both
# brands and both target frameworks (netstandard2.0, net472): import -> build -> byte-identity
# against `dotnet build` -> determinism -> incremental -> SBOM, with timings for every phase
# and for the stock .NET scenario (restore + build) it is compared against.
#
#   docs/features/hermetic-build/verify-dataengine.sh [work dir] [source repo]
#
# Layout note: one lock per brand (`locks/MESCIUS.json`, `locks/GCCN.json`), each holding an
# entry per (project, target framework) -- `Project.import` covers the whole framework matrix
# of one variant in one call, so the two brands import concurrently with no `-t 1`.
#
# Defaults: work dir /tmp/xake-verify-de, source repo ~/Projects-work/ar/ar-net-core-dataengine
# (a `git archive origin/develop` copy is made -- the source checkout is never touched).
# Needs: .NET SDK 8 (dataengine pins 8.0.100 rollForward:latestFeature), network for the first
# restore, and Xake staged in `.bootstrap/` next to this checkout (see build.fsc.fsx):
#
#   cd <xake> && dotnet fsi build.fsx -- -- build && mkdir -p .bootstrap && cp out/netstandard2.0/*.dll .bootstrap/
#
# Everything it prints is a measurement of this machine; the numbers recorded in
# verify-dataengine.md are from one run of this script, not a promise.

set -u

WORK="${1:-/tmp/xake-verify-de}"
SRC="${2:-$HOME/Projects-work/ar/ar-net-core-dataengine}"
XAKE="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
FSX="$XAKE/docs/features/hermetic-build/verify-dataengine.fsx"
DE="$WORK/de"
SAVED="$WORK/xake-out"
CACHE="$WORK/nugetcache"

FRAMEWORKS=(netstandard2.0 net472)
BRANDS=(MESCIUS GCCN)
PROJECTS=(DataEngine ExpressionInfo VBFunctionLib)

now () { python3 -c 'import time; print("%.2f" % time.time())'; }
say () { printf '\n\033[1m== %s\033[0m\n' "$*"; }
# run a command silently, print "<label>: <seconds>s", keep the log
phase () {
    local label="$1" log="$2"; shift 2
    local t0 t1
    t0=$(now); "$@" > "$log" 2>&1; local rc=$?; t1=$(now)
    printf '%-46s %6.2fs  (exit %d, log %s)\n' "$label" "$(python3 -c "print($t1-$t0)")" "$rc" "$(basename "$log")"
    return $rc
}

if [ ! -f "$XAKE/.bootstrap/Xake.Dotnet.dll" ]; then
    echo "no $XAKE/.bootstrap/Xake.Dotnet.dll -- stage it first (see the header of this script)" >&2
    exit 1
fi

say "0. fixture: $SRC origin/develop -> $DE"
rm -rf "$DE" "$SAVED" && mkdir -p "$DE"
( cd "$SRC" && git archive origin/develop ) | tar -x -C "$DE" || exit 1
echo "commit: $(cd "$SRC" && git rev-parse origin/develop)"
echo "sdk:    $(dotnet --version)"

cd "$DE" || exit 1
rm -rf locks src/*/obj src/*/bin .xake sbom

say "1. import: 2 locks (one per brand, both frameworks inside), concurrently"
# No `-t 1` any more. `Project.import` takes `Frameworks: string list`: one lock per brand
# holds both target frameworks, each project is restored once *without* `TargetFramework` (so
# its project.assets.json holds every target) and with `-p:RestoreRecursive=false` (so the
# restore stops rewriting the referenced projects' assets files), and the design-time builds
# then run per framework with no `-restore` at all. Restore and every framework's build of one
# project happen inside that project's `withProjectLock`, so two brands importing the same
# project still serialize -- but the two frameworks no longer race. See verify-dataengine.md §6.
phase "xake locks (cold, concurrent)" "$WORK/1-locks.log" \
    dotnet fsi "$FSX" -- -- locks || { tail -20 "$WORK/1-locks.log"; exit 1; }
for b in "${BRANDS[@]}"; do
    printf '   %-32s %6s KB\n' "locks/$b.json" "$(( $(wc -c < "locks/$b.json") / 1024 ))"
    python3 - "locks/$b.json" <<'PYEOF'
import json, sys
d = json.load(open(sys.argv[1]))
for e in d["Entries"]:
    print("     %-16s %-16s %3d src / %3d refs / %2d defines / %d pkgs" % (
        e["Name"].split(".")[-1], e["Framework"], len(e["Compilation"]["Sources"]),
        len(e["Dependencies"]["References"]), len(e["Compilation"]["Defines"]),
        len(e["Dependencies"]["Packages"])))
bad = [e["Name"] + "/" + e["Framework"] for e in d["Entries"] if not e["Dependencies"]["Packages"]]
print("     EMPTY PACKAGE GRAPH: " + ", ".join(bad) if bad else "     every entry has a package graph")
PYEOF
done

say "2. build: 12 assemblies from the locks, no msbuild"
phase "xake build (cold outputs)" "$WORK/2-build.log" dotnet fsi "$FSX" -- -- build || { tail -20 "$WORK/2-build.log"; exit 1; }
echo "   msbuild invocations in this phase: $(grep -c '\[msbuild\]' "$WORK/2-build.log")"
phase "xake build (nothing changed)" "$WORK/3-noop.log" dotnet fsi "$FSX" -- -- build
touch src/VBFunctionLib/Constants.cs
phase "xake build (one source touched)" "$WORK/4-inc.log" dotnet fsi "$FSX" -- -- build
echo "   assemblies recompiled: $(grep -cE 'Completed .*\.dll' "$WORK/4-inc.log") of 12, locks re-imported: $(grep -cE 'Completed .*\.json' "$WORK/4-inc.log")"

say "3. keep the outputs, then let msbuild rebuild over them"
for f in "${FRAMEWORKS[@]}"; do for b in "${BRANDS[@]}"; do
    mkdir -p "$SAVED/$f/$b"
    for p in "${PROJECTS[@]}"; do cp src/$p/obj/xake/$f/$b/*.dll src/$p/obj/xake/$f/$b/*.pdb src/$p/obj/xake/$f/$b/*.xml "$SAVED/$f/$b/" 2>/dev/null; done
done; done
echo "   saved $(find "$SAVED" -type f | wc -l | tr -d ' ') files"
# the baseline must be built in the SAME directory with the SAME IntermediateOutputPath (csc
# embeds absolute source paths in the PDB and, under /deterministic, in the PE), and with
# -t:Rebuild (msbuild's own incremental caches otherwise reuse stale project-reference output)
for f in "${FRAMEWORKS[@]}"; do for b in "${BRANDS[@]}"; do
    phase "dotnet build -t:Rebuild $f/$b" "$WORK/5-baseline-$f-$b.log" \
        dotnet build src/DataEngine/DataEngine.csproj -c Release -t:Rebuild \
            -p:TargetFramework=$f -p:Brand=$b -p:NuGetAudit=false \
            -p:IntermediateOutputPath=obj/xake/$f/$b/ -v:q -nologo
done; done

say "4. byte-identity: Xake's output vs what msbuild just wrote"
ident=0; diff=0
for f in "${FRAMEWORKS[@]}"; do for b in "${BRANDS[@]}"; do for p in "${PROJECTS[@]}"; do for ext in dll pdb xml; do
    x=$(ls "$SAVED/$f/$b"/*."$p"."$ext" 2>/dev/null) || continue
    m="src/$p/obj/xake/$f/$b/$(basename "$x")"
    if cmp -s "$x" "$m"; then ident=$((ident+1));
        printf '   IDENTICAL %-15s %-8s %-19s %9s\n' "$f" "$b" "$p.$ext" "$(wc -c < "$x" | tr -d ' ')"
    else diff=$((diff+1));
        printf '   DIFFERENT %-15s %-8s %-19s %9s %9s\n' "$f" "$b" "$p.$ext" "$(wc -c < "$x" | tr -d ' ')" "$(wc -c < "$m" 2>/dev/null | tr -d ' ')"
    fi
done; done; done; done
echo "   identical=$ident different=$diff"

say "5. determinism: compile the same locks again, compare with the first run"
rm -rf src/*/obj/xake
phase "xake build (compile only, from locks)" "$WORK/6-recompile.log" dotnet fsi "$FSX" -- -- build
echo "   locks skipped (no msbuild): $(grep -cE 'Skipped .*\.json' "$WORK/6-recompile.log") of 2"
ident2=0; diff2=0
for f in "${FRAMEWORKS[@]}"; do for b in "${BRANDS[@]}"; do for p in "${PROJECTS[@]}"; do for ext in dll pdb xml; do
    x=$(ls "$SAVED/$f/$b"/*."$p"."$ext" 2>/dev/null) || continue
    if cmp -s "$x" "src/$p/obj/xake/$f/$b/$(basename "$x")"; then ident2=$((ident2+1)); else diff2=$((diff2+1)); echo "   DIFF $f/$b/$(basename "$x")"; fi
done; done; done; done
echo "   identical=$ident2 different=$diff2"

say "6. SBOM: one CycloneDX 1.6 per assembly, out of the lock alone"
phase "xake sbom" "$WORK/7-sbom.log" dotnet fsi "$FSX" -- -- sbom
echo "   files: $(find sbom -name '*.cdx.json' | wc -l | tr -d ' ')"
rm -rf "$WORK/sbom-run1"; cp -r sbom "$WORK/sbom-run1" 2>/dev/null; rm -rf sbom
phase "xake sbom (again, for determinism)" "$WORK/8-sbom2.log" dotnet fsi "$FSX" -- -- sbom
s_id=0; s_diff=0
for j in $(cd "$WORK/sbom-run1" && find . -name '*.json'); do
    if cmp -s "$WORK/sbom-run1/$j" "sbom/$j"; then s_id=$((s_id+1)); else s_diff=$((s_diff+1)); echo "   DIFF $j"; fi
done
echo "   identical=$s_id different=$s_diff"

say "7. the dependencies, from the lock alone, into a folder of the build's own"
# `PACKAGES=<dir>` is the build-agent story: the same folder expands $(NuGetPackageRoot) when
# the lock is read and receives what `Restore` downloads, so the agent caches one directory
# next to the checkout instead of the machine's NuGet cache.
AGENT="$WORK/agentcache"
rm -rf "$AGENT" src/*/obj/xake
phase "xake restore -> empty folder (compiles nothing)" "$WORK/9-restore.log" \
    env PACKAGES="$AGENT" dotnet fsi "$FSX" -- -- restore
echo "   folder now: $(du -sh "$AGENT" 2>/dev/null | cut -f1), packages: $(ls "$AGENT" 2>/dev/null | wc -l | tr -d ' ')"
phase "xake build (that folder, already populated)" "$WORK/10-build-agent.log" \
    env PACKAGES="$AGENT" dotnet fsi "$FSX" -- -- build

# and the same from nothing at all: the build restores what the lock names, then compiles
FRESH="$WORK/freshcache"
rm -rf "$FRESH" src/*/obj/xake
phase "xake build (empty folder: restores, then compiles)" "$WORK/11-build-fresh.log" \
    env PACKAGES="$FRESH" dotnet fsi "$FSX" -- -- build
echo "   assemblies: $(grep -cE 'Completed .*\.dll' "$WORK/11-build-fresh.log") of 12, folder: $(du -sh "$FRESH" 2>/dev/null | cut -f1)"
# the bytes must not depend on where the packages live
ident3=0; diff3=0
for f in "${FRAMEWORKS[@]}"; do for b in "${BRANDS[@]}"; do for p in "${PROJECTS[@]}"; do for ext in dll pdb xml; do
    x=$(ls "$SAVED/$f/$b"/*."$p"."$ext" 2>/dev/null) || continue
    if cmp -s "$x" "src/$p/obj/xake/$f/$b/$(basename "$x")"; then ident3=$((ident3+1)); else diff3=$((diff3+1)); echo "   DIFF $f/$b/$(basename "$x")"; fi
done; done; done; done
echo "   built against a freshly restored folder, identical to the first build: $ident3/$((ident3+diff3))"

say "8. the stock .NET scenario, from restore"
rm -rf src/*/obj src/*/bin "$CACHE"; mkdir -p "$CACHE"
phase "dotnet restore (cold cache, network)" "$WORK/10-restore-cold.log" \
    env NUGET_PACKAGES="$CACHE" dotnet restore src/DataEngine/DataEngine.csproj -p:NuGetAudit=false
echo "   cache: $(du -sh "$CACHE" | cut -f1), packages: $(ls "$CACHE" | wc -l | tr -d ' ')"
phase "dotnet restore (warm, no-op)" "$WORK/11-restore-warm.log" \
    env NUGET_PACKAGES="$CACHE" dotnet restore src/DataEngine/DataEngine.csproj -p:NuGetAudit=false
for b in "${BRANDS[@]}"; do
    rm -rf src/*/obj src/*/bin
    phase "dotnet restore ($b)" "$WORK/12-restore-$b.log" \
        env NUGET_PACKAGES="$CACHE" dotnet restore src/DataEngine/DataEngine.csproj -p:NuGetAudit=false
    phase "dotnet build  ($b, both frameworks)" "$WORK/13-build-$b.log" \
        env NUGET_PACKAGES="$CACHE" dotnet build src/DataEngine/DataEngine.csproj -c Release --no-restore -p:Brand=$b -p:NuGetAudit=false -v:q -nologo
    phase "dotnet build  ($b, nothing changed)" "$WORK/14-noop-$b.log" \
        env NUGET_PACKAGES="$CACHE" dotnet build src/DataEngine/DataEngine.csproj -c Release --no-restore -p:Brand=$b -p:NuGetAudit=false -v:q -nologo
    touch src/VBFunctionLib/Constants.cs
    phase "dotnet build  ($b, one source touched)" "$WORK/15-inc-$b.log" \
        env NUGET_PACKAGES="$CACHE" dotnet build src/DataEngine/DataEngine.csproj -c Release --no-restore -p:Brand=$b -p:NuGetAudit=false -v:q -nologo
done

say "summary"
echo "  byte-identical vs dotnet build : $ident/$((ident+diff))"
echo "  deterministic across runs      : $ident2/$((ident2+diff2))"
echo "  deterministic SBOMs            : $s_id/$((s_id+s_diff))"
echo "  identical from a fresh package folder : $ident3/$((ident3+diff3))"
echo "  work dir                       : $WORK"
