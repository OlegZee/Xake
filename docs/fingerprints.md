# Fingerprints

Some generated files are committed: project descriptions written by `import`, locks written by
`locks`. No rule regenerates them; they are produced by explicit commands and reviewed via git diff.
The `Fingerprints` module (Xake core, a library, no engine change) records how each such file was
produced, so a build can notice that it is out of date without regenerating it:

- **stale**: an input changed since the target was generated (e.g. a shared `.props` file);
- **hand-edited**: the target differs from what was generated;
- **missing**: the target file is absent.

`build` warns and continues with the committed files (as CI does); `check` fails.

## The file

One committed text file, by convention `xake.fingerprints` in the project root. One line per target,
sorted by target (ordinal), LF line endings, final newline. Fields are tab-separated:

```
target <TAB> target-sha256 <TAB> inputs-sha256 <TAB> input1 <TAB> input2 ...
```

```
desc/a.json	3f0c…e1	9b2d…07	Directory.Build.props	src/a/a.fsproj
locks/a/Release.json	77aa…4c	c1e9…5d	desc/a.json
```

Paths are relative to the project root with `/` separators (a path outside the root stays absolute).
Inputs are de-duplicated and sorted.

Hashing:

- a file hash is SHA-256 (lower-case hex) of its bytes with every CRLF pair replaced by LF, so
  `autocrlf` checkouts hash equal; other bytes, including a lone CR or a BOM, are hashed as is
  (binary files go through the same rule);
- the inputs hash is SHA-256 of the UTF-8 text `path TAB file-hash LF` over the sorted inputs; a
  missing input contributes `missing`. Renaming an input therefore changes the hash too.

Only the fingerprint file and the files it names are read: a fresh clone without `.xake` checks fine.

## Functions

```fsharp
Fingerprints.record file target inputs : Recipe<ExecContext, unit> // add or replace target's line
Fingerprints.warn   file               : Recipe<ExecContext, unit> // one warning per problem target
Fingerprints.check  file               : Recipe<ExecContext, unit> // fail, listing problem targets
```

`record` writes the file only when its content changes. Paths are resolved against the project root.
Pure helpers for plain code and tests: `hashFile`, `hashBytes`, `compute`, `parse`, `format`, `load`,
`save`, `recordIn`, `status`, `verify`, `describe`.

## Wiring in a script

```fsharp
command "import" { do! Description.import projects Description.beside [] }   // records lines
"update-locks" <== [ for n in projects -> $"lock::{n}::Release" ]              // hermetic/lock.md
"check" => Fingerprints.check "xake.fingerprints"

command "build" {
    do! Fingerprints.warn "xake.fingerprints"
    do! Lock.needAll descriptions "Release" lockOf outOf
}
```

`import` records each description with every msbuild file read as inputs; the lock rule behind
`update-locks` could record each lock with the description and the referenced projects' locks as inputs.
