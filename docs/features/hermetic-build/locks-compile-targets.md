# Locks.compile: one action per target kind

Design note for branch `pr/locks-compile-targets` (from `dev` 0d65475), 2026-10-22. Raised by
the ActiveReports consumer (`xake-ar/build.fsx`), which declares three rules over one recipe:

```fsharp
"out/(b:*)/(cfg:*)/(fwk:*)/(asm:*).dll" ..> compile
"out/(b:*)/(cfg:*)/(fwk:*)/(asm:*).exe" ..> compile
"out/(b:*)/(cfg:*)/(fwk:*)/(asm:*).xml" ..> compile
```

and reads as if the `.xml` were compiled. It is not: `Locks.compile` dispatches on the target's
extension. Three findings of a review of `src/hermetic/Locks.fs` are fixed here, plus a rename;
the rest of that review (undeclared side outputs, the `Lock.Run.Server` default sentinel,
`runFoldersWith` skipping lockless projects, `generate`-only error texts, fsc masks, API surface)
is out of scope. Reviewed once (2026-10-22); the findings are folded in below.

## 1. `Locks.doc`: the `.xml` rule's own function

**Now.** `compile` (`Locks.fs:679-685`): a target ending in `.xml` goes to the private `docOf`
(needs the assembly beside it, whose rule writes the doc as a side output; fails when the lock
asks for no doc, or when the file is missing next to an up-to-date assembly); anything else goes
to `compileDll`. Documented as a trick in `compile`'s summary (`Locks.fs:652-656`) and in
`docs/hermetic/lock.md:71`.

**Change.**
- New public `Locks.doc (settings: Settings) (lockPath: string) (fwk: string) : Recipe<ExecContext, unit>`:
  the target of the calling rule is the `.xml`; body = today's `docOf settings.Roots lockPath fwk target`.
  Same parameters as `compile` (only `Roots` is used; symmetry wins), so a script writes
  `"out/(cfg:*)/(fwk:*)/(asm:*).xml" ..> Locks.doc settings (lockOf ...) fwk`.
- `compile` on an `.xml` target is a **hard error**:
  `"'%s': Locks.compile builds the assembly; the .xml rule is Locks.doc settings lockPath fwk"`.
  The package is 0.x ("may break", fsproj), its one external consumer (xake-ar) pins an exact
  version and is updated together; a warning on every `.xml` (xake-ar's `pack` needs ~45) would
  be noise for one release.
- Callers to switch: `BuildFromLockTests.fs:415`, `RecordTests.fs:367/523/546`,
  `RunFolderTests.fs:278`; xake-ar `build.fsx:229` after the release. Xake's own `build.fsc.fsx`
  has only a `.dll` rule, `build.fsx` uses core `Lock.compileWith` with `targets [dll; xml]`:
  neither changes.
- Docs: `compile`'s summary loses the `.xml` item and points to `doc`; `lock.md:71` gets a
  `Locks.doc` row; `compile`'s row there says "the layout's assembly rule".
- Test: `compile` over an `.xml` target fails with that message.

## 2. `compile` checks the target is the entry's output

**Now.** `compileDll` (`Locks.fs:524-526`) compares only the file name without extension with
`entry.Name` (ordinal). A `.dll` target of an entry whose output is `X.exe` (`outputName`,
recorded `TargetFileName`) compiles an exe into `X.dll`; a `.pdb` (or any other) target compiles
as well.

**Change.** After the entry is found: `Path.GetFileName target` must equal (ordinal) the
entry's output name, else
`failwithf "'%s': the target '%s' is not the output of the lock '%s' (%s)"` naming the expected name(s).
- With `TargetFileName` recorded (every imported entry, C# and F#, `Import.fs:1441/1448`): exactly `outputName entry`.
- Without it (Xake-generated locks: `generate` writes `Evaluation.Empty`, `Locks.fs:254`; a
  `Kind = Exe` description compiles `/target:exe`, `Described.fs:752`, yet `outputName` says
  `<Name>.dll`): `<Name>.dll` or `<Name>.exe`, as today. Deriving the name from `/target:` would
  change what `needAllWith` asks for generated exe locks; not in this change.
- net8 applications are fine: `TargetFileName` is `X.dll`, the apphost is a side output (`Apps.write`).

Tests: the recorded net472 application of `RecordTests` (`"TargetFileName": "app.exe"`, `:395`)
asked as `app.dll` fails with the message; a library asked as `.pdb` fails; a generated lock's
entry asked as `.exe` still compiles.

## 3. `compileDll` renamed `compileAssembly`

Private; it compiles C# and F# alike. Rename, the comment above it, and `docs/resources.md:29,277,318`.

## 4. `$(Version)`: the project folder from the lock

**Now** (`Locks.fs:571-593`). When `Generated` carries `$(Version)`, the project's folder is
guessed from its first non-generated source (the fixed prefix of a mask, or the file's folder),
then `<folder>/xake.project.json`, relative to the project root, is matched against
`settings.Versions`. The compilation records the project's folder as `Directory`
(`Lock.fs:201`): the csproj's folder for an imported entry (`Import.fs:947,1441`), the folder of
the description's `Project` for a described one (`Described.fs:612`) — not the folder of the
description file. The guess is wrong when the first source lies elsewhere (a shared `.projitems`,
a linked file, a sibling root).

**Change.**
- `projectDir` = `c.Directory` (normalised) when it is non-empty and not the project root; else
  the current guess. `Directory = root` is what the builder writes with no project
  (`Csc.fs:715`, `Fsc.fs:455`), so it means "no project" — no `Recorded`/generated discriminator
  (nothing marks a generated lock anyway).
- `relative` when `projectDir = root` is `""` (today it returns the absolute root), so the path is
  `xake.project.json`.
- Masks over paths outside the project root (xake-ar: `$(ArRoot)/...`) are possible today only
  as absolute paths. `versionFor` is matched after expanding the script's roots in each mask
  (`$(ArRoot)/ar-net-win/**` → the absolute path, via `Roots.make settings.Roots`). Expanding the
  mask, not tokenising the path: `(` in a Xake mask starts a capture group, so a `$(Name)` token
  cannot be matched literally.
- Doc comment of `compile` and `lock.md`: "the project's folder (`Directory` of the lock's
  compilation, else where its first source lies) — a path in the project tree, not where the
  description lives; a mask may start with a script root `$(Name)`".
- Consumers: xake-ar sets no `Versions` (`build.fsx:143-146`): unaffected. Xake's own scripts:
  check `build.fsc.fsx`'s masks still pick the same versions (its descriptions sit beside their
  projects, so `Directory` equals the guess).

Tests: a described project whose first source is outside its folder (`../shared/*.cs` in the
description of `p/B`) gets the `p/B/**` version (today `**`'s); a lock with a sibling root
resolves a `$(Sibling)/**` mask; `versionFor`'s unit test gains the root-relative `""` case.

## Release

Xake.Hermetic.Dotnet **0.7.0** (fsproj is at 0.6.0 with #83 and #84 in `dev`): new public
`Locks.doc`, a breaking change in 1, behaviour changes in 2 and 4. `docs/release-notes.md`, the
feature `session.md` (its head still lists #84 as draft) and `tracker.md` get the entry.
Consumer change after release: xake-ar's `.xml` rule uses `Locks.doc`.

## Plan

1. `Locks.fs`: `doc`, the `.xml` error, the output check, rename, `Directory` and root-expanded masks; doc comments.
2. Tests as above; `dotnet test src/hermetic.tests` green; `dotnet fsi build.fsx -- -- build test`.
3. `lock.md`, `resources.md`, `release-notes.md`, `session.md`, `tracker.md`, fsproj 0.7.0.
4. PR to `dev` (squash), description ending with the Claude Code line.
