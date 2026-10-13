# ActiveReports, direct route: descriptions compiled with `csc { project; config }`, no import, no locks

Date 2026-10-13. Folder `~/Projects-work/ar/xake-ar/direct/` (own script, `projects/`, `out/`, `.xake`, `logs/`;
run from inside it; nothing outside `direct/` and nothing in the AR worktrees was touched). Xake 3.7.0.26,
Xake.Hermetic.Dotnet 0.3.0.28, SDK 8.0.425 (parent `global.json`), MESCIUS brand, Release only. Packages come
from the shared `../.packages` (warm). Script copy: [ar-direct.fsx](../ar-direct.fsx). Round 1: [ar-experiment.md](ar-experiment.md).

## Result

All 15 projects x 2 frameworks = **30 dlls (+30 xml)**, `.resx` projects included (G1 worked around). No skipped project.
The script is **48 lines** (round 1: 179). Descriptions are the round-1 MESCIUS ones, copied and edited by hand.

## Script (verbatim, 48 lines)

```fsharp
// ActiveReports (MESCIUS brand, Release) compiled straight from hand-kept project descriptions: no import,
// no locks, no fingerprints. Run from this folder. Layout: out/<cfg>/<fwk>/<name>/<assembly>.dll
#r "nuget: Xake, 3.7.0.26"
#r "nuget: Xake.Hermetic.Dotnet, 0.3.0.28"

open System.IO
open Xake
open Xake.Dotnet
open Xake.Hermetic.Dotnet

let fwks = [ "netstandard2.0"; "net472" ]
let names = Directory.GetDirectories "projects" |> Array.map Path.GetFileName |> List.ofArray
let descOf name = $"projects/{name}/xake.project.json"
let dllOf cfg fwk name =
    let d = Description.read (descOf name)
    $"out/{cfg}/{fwk}/{name}/{defaultArg d.AssemblyName d.Name}.dll"    // assembly names are branded

do xakeScript {
    var "NUGET_PACKAGES" "../.packages"
    rules [
        "main" <<< [ for n in names do for f in fwks -> dllOf "Release" f n ]

        target "out/(cfg:*)/(fwk:*)/(name:*)/*.dll" {
            let! cfg = getRuleMatch "cfg"
            let! fwk = getRuleMatch "fwk"
            let! name = getRuleMatch "name"
            let! d = Description.load (descOf name)
            let! pkgs = Described.restore d fwk
            do! need [ for r in d.ProjectRefs -> dllOf cfg fwk r ]
            do! csc {
                targetfwk fwk
                ref (d.ProjectRefs |> List.fold (fun fs r -> fs ++ dllOf cfg fwk r) Fileset.Empty)
                project (descOf name)
                config cfg
                packages pkgs
            }
        }
        target "obj/resx/(name:*)/**/*.resources" {            // G1: .resx -> .resources by the script
            let! name = getRuleMatch "name"
            let! target = getTargetFile ()
            let! d = Description.load (descOf name)
            let dir = Path.GetDirectoryName (Path.GetFullPath (Path.Combine (Path.GetDirectoryName (descOf name), d.Project)))
            let resx = Path.Combine (dir, Path.ChangeExtension (Path.GetRelativePath (Path.GetFullPath $"obj/resx/{name}", target.FullName), ".resx"))
            do! needFiles (Filelist [ File.make resx ])
            Resx.compile resx target.FullName
        }
    ]
}
```

Note: the first-try `ref (!! "")` for a project without refs fails ("Neither rule nor file is found for '<root>'"),
hence the fold over `Fileset.Empty`; `for ... do ref` inside `csc {}` is not accepted either.

## Description edits (hand, copied from `mescius/projects/`)

| edit | projects | why |
|---|---|---|
| `../../../mescius/projects/X/AssemblyAttributes.cs` -> `../../../direct/projects/X/...` (file copied; it is the one-line `[assembly: System.CLSCompliant(true)]`) | DataEngine, ExpressionInfo, Rdl, Rendering | G4 (Import drops the attribute) |
| `.resources` path `obj/resx/mescius/<name>/...` -> `direct/obj/resx/<name>/...` | Rdl, Rendering (2), Export.Excel | G1: the description names the `.resources`; the script rule `obj/resx/(name:*)/**/*.resources` compiles it |
| add `System.Text.Json 8.0.5` and `System.Memory 4.6.0` to `packages` | 11 projects with `Rdl` in `projectRefs` (all page projects except Rdl) | **G14 (new)**: msbuild flows a project reference's packages to the referrer; a description lists only its own. Without it: `error CS0246: The type or namespace name 'Matrix3x2' could not be found` and `'JsonPropertyNameAttribute'` in Rendering |
| `"!**/.*/**/*"` mask removed | all C# projects | G8, only for the "G8" timing rows below |

Kept from round 1 (already in the copied files, so hand edits of the first round's gaps, not script code): `projectRefs` as the
transitive closure (G5), `/nowarn:1701,1702,1591,0618,0612,1589` in `options.args` (G3; 1589 hides G7), Rdl's
`System.Text.Json` version (G2), `WindowsBase` under `byFramework.net472.references` for OpenXml (G6). Descriptions all
parse and load unchanged by `Description.load`.

## Remaining workarounds

Script: **1**, the `.resx` rule, 9 lines (`// G1`). Description data (hand-kept, no script code): G2, G3 (+G7), G4, G5, G6, G14.
No script workaround for G5/G6 was needed because the data is simply right; they would reappear on every re-import.

## Gaps hit (exact text)

- G1: not hit at build time because the descriptions point at the `.resources`; `.resx` itself still refused
  (`a .resx in a description is not compiled by 'project' yet`, round 1).
- G14: `error CS0246: The type or namespace name 'Matrix3x2' could not be found (are you missing a using directive or an assembly reference?)`
  (Rendering, netstandard2.0; packages of project references are not carried).
- G8: `!**/.*/**/*` still costs (see table).
- `Described.restore` is called per project and framework (30 `dotnet restore` runs, all through msbuild); a no-op
  re-runs none of the compiles but, with the G8 mask, the expansion of the masks takes the 8-13 s.
- No other error; no csc crash (round 1's exit 134 did not appear in 6 clean runs).

## Timing (`tools/cputime.pl`, 18 cores, util = CPU / wall; `rm -rf out .xake*`, `obj/` and `../.packages` kept)

The other agent was measuring the same machine at the same time, so walls are noisy (load average 4-6 during these runs).

| run | wall | CPU | util |
|---|---|---|---|
| clean, `CSC_SERVER=off` | 57.1 s | 104.3 s | 1.83 |
| no-op, server off | 13.0 s | 13.6 s | 1.05 |
| clean, server on | 48.8 s | 91.4 s | 1.87 |
| no-op, server on | 12.9 s | 13.3 s | 1.03 |
| G8 (mask removed), clean, server off | 45.9 s | 89.0 s | 1.94 |
| G8, no-op | 1.1 s | 1.6 s | 1.39 |
| G8, clean, server on | 32.6 s | 35.0 s (servers not counted well) | 1.07 |

(An earlier G8 run: 52.9 s / 99 s, so the noise is about 5-7 s.) The first-ever build, which also created the `obj/xake`
restore projects, took 64 s.

## Against round 1 (lock route)

| | round 1, locks (mescius Release) | direct |
|---|---|---|
| script lines | 179 (about 95 without workarounds and pack) | 48 |
| script workarounds | `fixup`, `transitive`, resx rule: 6 gaps (G1-G6) | 1 (resx rule); the rest is description data |
| committed generated files | 30 locks per brand (15-28 KB each), fingerprints | none; 15 hand-kept descriptions |
| clean, server off | 33.4 s, CPU 100 s, util 3.0 (21.0 s, util 4.1 without the G8 mask) | 57 s, CPU 104 s, util 1.8 (46 s, util 1.9 with G8 fixed) |
| no-op | 7.1 s (4.4 s G8) | 13 s (1.1 s G8) |

The direct route is shorter and simpler but about 1.4-1.7x slower on a clean build with the same CPU, and the util is half.
Reasons, from the structure rather than from a profile: (1) every target runs `Described.restore` (an msbuild `dotnet restore`
of a synthesized csproj, ~1-2 s) before its compile, 30 times, where the lock route restored once at lock time and compiled
from the lock; (2) the C# masks are expanded at build in both routes (G8 hits both). The hand-kept description also has no
fingerprint, so a changed `Directory.Build.props` or a new csproj item is not noticed: that is the price of "no import".
With G8 fixed the no-op is 1.1 s, the lock route's 4.4 s. Not tried (timebox): restoring once per project and sharing it
between frameworks, or caching the restore result on the description's hash, which should bring the clean build toward round 1.
