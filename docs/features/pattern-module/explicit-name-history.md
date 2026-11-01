# The explicit-name rule in filesets: history, pins, dependents

Research note (read-only). Subject: `isExplicitRule` in `src/core/Fileset.fs:75-92` (module `Impl`, `listFiles`).
A fileset include mask with no `DirectoryMask`, `FileMask` or `Recurse` element (only literal directory and
file names, `.`, `..`, drive, root) is listed even when the path does not exist; any other mask is filtered
by `Directory.Exists` / `File.Exists`.

## 1. History

- Introduced by `db5c166` (2014-05-03, Oleg Zee, "described and update fileset behavior for "explicit"
  patterns"), file `core/Fileset.fs` (the file later moved to `src/core/` in `cb4e607`, 2018-05-05; the
  repository history goes back to 2014-03, so this is not an initial-import rule).
- Before it, the existence filters were present but commented out, so literal names were always listed:
  `| Directory d -> ... //|> Seq.filter Directory.Exists` and `| FileName f -> ... //|> Seq.filter File.Exists`.
  The commit switched them on for masked patterns only. Hunk (original, note the inverted flag name):

      let isExplicitRule = pat |> List.exists (function | DirectoryMask _ | FileMask _ -> true | _ -> false)
      let filterDir = if isExplicitRule then Seq.filter Directory.Exists else id
      let filterFile = if isExplicitRule then Seq.filter File.Exists else id

  The flag was true for a pattern WITH a mask (misnamed), behaviour as today. The comment line
  ("The pattern without mask become "explicit" file reference ...") was added in the same hunk.
- Motivation, from `core/implnotes.md` added in the same commit (section "Non-existing files"): filesets
  reference both existing files (sources) and **targets which might not exist when the fileset is
  materialized**; a masked name that is absent is silently omitted and "the build will likely fail", so
  "the rule without mask is added to resulting file list explicitly". Same note: "it should depend on the
  context: if fileset defines source files or references 'explicit rule' is ok" (a TODO never done).
  The commit adds no test or sample; `core/todo.md` was added too.
- Later touches: `e5375ab` (2014-06-03, fileset persistence) and `e51f99f` (2015-02-10, "improving file
  tests, bugfixes"): the latter fixed the inverted name (`... |> not`, ids swapped) and added `Recurse` to
  the mask list, and in the same commit the tests below were written/rewritten (test file
  `XakeLibTests/FilesetTests.fs`, now `src/tests/FilesetTests.fs`). `014af4d` (2019) only fixed compilation.
  `c65d764` (2026-10-31) mentions the symbol in the pattern-module spec draft.

## 2. Tests that pin the behaviour

Direct (both in `src/tests/FilesetTests.fs`):
1. L107-111 ``handles 'explicit' rules, which match file regardless actual file presense``: asserts
   `ls "c:/rpt/aaa.rdl" |> getFiles1 root1 "" |> List.map File.getFullName` is `Is.All.EndsWith("rpt" </> "aaa.rdl")`
   (the file is absent in the fake file system).
2. L113-126 ``explicitly specified file yields non-existing file``: fileset with `includes "*.rdl"` and
   `includes "NonExistingFile.rdl"`; asserts the result `isAny().EndsWith("NonExistingFile.rdl")`.

Indirect (an absent file must be in the resolved list, no assertion on the fileset itself):
3. `src/tests/ProjectPlanTests.fs:233` ``a projectRef needs its output among the block's references``
   (L257): `csc { ref (!! "out/core/Core.dll"); project ...; resolve }`; `out/core/Core.dll` is never
   built, and the test asserts `Args` has an entry ending with `out/core/Core.dll`. Would fail without the rule.
4. `src/tests/DotnetTasksTests.fs:157` ``csc builds a referenced library before checking its hash``:
   `Ref = !!"hashlib.dll"` where `hashlib.dll` is made by another rule in the same script; the app is
   resolved while the library may not be on disk yet (first build). Likely fails without the rule (unclear
   only on ordering: `need` of refs happens in `run`, after `resolve`/`getFiles`).

Counts: 2 direct, 2 indirect. Other literal filesets in tests (`PathSpacesTests.fs:75` `ref !!"lib dir/l.dll"`,
`FromLockTests.fs:367` `includes "HelloResxStrings.resx"`) name files that exist by then.

## 3. Dependents (where a literal name may be a not-yet-built output)

Mechanism: `getFiles`/`dependsOn` (`src/core/ScriptFuncs.fs:137-165`) call `toFileList`, then `needFiles` turns
each listed file into a `FileTarget` need, which runs the producing rule. A literal name dropped by the scan
is never needed, so the producer never runs and the compiler sees a missing reference.

| Place | Expression | Produced by a rule in the script? | Verdict |
|---|---|---|---|
| `src/hermetic/Locks.fs:230` (build from locks) | `refFiles = outputs \|> fold (fs + (!! o))`, passed as `ref refFiles` to `fsc`/`csc` (L235, L240) | yes: `outputs` are the dlls of referenced projects, built by the script's `out/(cfg)/(fwk)/(name).dll` rule (`build.fsc.fsx:72`) | would break (this is the load-bearing case) |
| `src/dotnet/Csc.fs:623-624`, `Fsc.fs:340-341` | `settings.Src \|> getFiles`, `settings.Ref \|> getFiles`, later `needFiles` in `run` (Csc.fs comment L625-627) | refs commonly are other rules' dlls (test 4, `ProjectPlanTests` 3) | would break for `Ref`; `Src` only if generated sources are named literally |
| `src/dotnet/Csc.fs:758`, `Fsc.fs:493` | `filesOf p.References + settings.Ref`, `filesOf p.Sources` (`!! f` per path) | sources exist; `p.References` are description paths, may be built dlls | unclear (sources would not break; references could) |
| `src/hermetic/Described.fs:769` (`filesOf`), `Described.fs:650` | same helper; `toFileList root refs` only checks names of projectRefs | refs are build outputs | unclear (L650 only compares names, but it silently drops an absent ref and warns) |
| `src/dotnet/DotnetTasks.fs:144` | `collectResInfo`: `toFileList` of `ResourceFileset` | resources normally exist | would not break (unclear for generated .resx) |
| `src/dotnet/Dotnet.resgen.fs:69` | `getFiles fileset` then `needFiles` | resx inputs; generated ones possible | unclear |
| `build.fsx:151-157` | `includes "<lib>.fsproj"` + `includes "**/*.fs"` then `needFiles` | fsproj exists; no generated source named | would not break |
| `build.fsc.fsx:22`, `Locks.fs:365,755,1064` | `!! "src/*/xake.project.json"` (masked) | descriptions exist | would not break (masked, never covered by the rule) |
| `samples/features.fsx:140` | `Src = !! "temp/hello.fs"` for `Fsc.compile` | sample `hello.fs` is written by an earlier rule in the sample, not by a rule of its own target | unclear (exists by then in the sample's flow) |
| `samples/features.fsx:60,200-208`, `src/hermetic.tests/FromLockTests.fs:367` | `rm {files (fileset ...)}`, `ls "src/*.cs"`, `includes "X.resx"` | inputs on disk | would not break |
| `src/hermetic/*.fs` `needFiles (Filelist [...])` (Project.fs:468, Lock.fs:1254/1820, Locks.fs:462,557,658,986,1023, Description.fs:366, CompilerRunner.fs:250,273,310) | build `Filelist` directly with `File.make` | no scan involved | not dependents of the rule |
| `FileTasks` `rm`/`cp` (`src/core`), `src/tests/FileTasks*.fs` | filesets of existing files | | would not break |

Summary: would break 2 (Locks.fs:230 chain, Csc/Fsc `Ref` getFiles) plus 2 pinned tests; unclear 4;
would not break the rest. No `docs/*.md` code block relies on a literal-name fileset naming a generated
file (docs search for "regardless", "non-existing", "not yet" in overview/cheatsheet/tasks/implnotes:
no hits).

## 4. Alternatives already in the API

- `need ["out/a.dll"]` (cheatsheet.md:82) with plain names: runs the producing rule, no scan.
- `needFiles (Filelist [ File.make p ])`: how the hermetic code itself demands known paths
  (`Locks.fs:658`, `Lock.fs:1254`). `Filelist` is a plain union (`Fileset.fs:31`), constructing one skips
  the disk scan entirely.
- Fileset `+` / `++` (`fs + (!! f)`, `Fileset.Empty ++ x`) only combines rules; the added `!! f` is still
  scanned, so `+` does NOT replace the explicit rule, it relies on it. Same for `includes`.
- `getFiles` result is a `Filelist` too, so a script can `getFiles masked` and append `Filelist [explicit]`
  by list concatenation before `needFiles`.
- Docs: `docs/overview.md` and `docs/cheatsheet.md` do not mention explicit names at all; the only
  statement of the rule is the source comment and the 2014 `implnotes.md` note (`docs/implnotes.md` was
  searched, no match).

## 5. Conclusion

The rule is load-bearing today, not just a historical convenience: the hermetic build-from-lock path
(`Locks.fs:230`) and the compiler tasks' `Ref` filesets (`Csc.fs`/`Fsc.fs` `getFiles`) pass literal paths of
dlls that other rules produce, and two tests pin it directly, two more indirectly. `need`/`needFiles` with a
`Filelist` cover the same need without a disk scan, but `+` and `includes` do not, so removing the rule
requires rewriting those call sites (turn `Ref` into a typed path list or `Filelist`) first; an alternative
is keeping it, documented, restricted to masks without any wildcard element, which is what the pattern
module spec can preserve as a distinct `Literal` pattern kind.
