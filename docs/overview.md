## Xake overview

Xake is a build automation tool for .NET that uses F# scripts.
It tracks dependencies, supports incremental builds, and runs independent targets in parallel.

Current project baseline:

- .NET SDK 8.0 or newer
- Xake NuGet package `Xake` — the engine alone; the .NET tasks (`csc`, `fsc`, `msbuild`,
  `resgen`) and the hermetic build library are the `Xake.Dotnet` package, from its own
  repository, [OlegZee/Xake.Dotnet](https://github.com/OlegZee/Xake.Dotnet)

## Minimal script

```fsharp
#r "nuget: Xake"

open Xake

do xakeScript {
    "main" => trace Message "Hello from Xake"
}
```

## Run a build script

Run default target:

```bash
dotnet fsi build.fsx
```

Run specific targets and options after a double separator:

```bash
dotnet fsi build.fsx -- -- clean build test
```

Pass variables:

```bash
dotnet fsi build.fsx -- -- build -d Version=1.2.3
```

## Core concepts

- Rules map targets to recipes
- Recipes define how targets are built
- Filesets collect files with Ant style masks
- Dependencies are recorded during recipe execution

When dependencies do not change, Xake skips rebuilds.

## Rule styles

Rules are written directly in the body of `xakeScript`. Preferred builder style:

```fsharp
do xakeScript {
    command "build" {
        do! sh "dotnet build src/core -c Release" { () }
    }

    target "out/version.txt" {
        let! ver = getVar "VERSION"
        do! writeText <| Option.defaultValue "0.0.0" ver
    }

    "main" <== ["build"]
}
```

Operator style also works:

```fsharp
do xakeScript {
    "main" <== ["build"; "test"]
    "deploy" <<< ["build"; "test"; "package"]
    "clean" => rm {dir "out"}
}
```

Rules may also be generated with `for`, and the older `rules [ ... ]` wrapper is still
accepted — the two forms can be mixed in one script:

```fsharp
do xakeScript {
    for fwk in ["net8.0"; "netstandard2.0"] do
        $"out/{fwk}/lib.dll" ..> recipe {
            do! sh $"dotnet build src/lib -f {fwk} -o out/{fwk}" { () }
        }
}
```

## Rule selection — the most specific rule wins

When more than one rule matches a target, Xake builds it with the **most specific** one. Among the
matching rules the winner is chosen by comparing, in order:

1. **literal characters** — the count of characters that are neither a wildcard nor named-group
   syntax (`/`, dots, fixed segments like `obf`). More literal text wins.
2. **fewer `**`** — `**` matches across `/`, so a rule that uses it is less specific than one that
   does not.
3. **fewer / tighter wildcards** — `?` (one non-`/` character) is tighter than `*` (a run of
   non-`/` characters); a named group `(name:*)` counts exactly like the bare `*` inside it.

Only when two *different* patterns tie on all three does declaration order decide: the
**last declared** rule wins (as before), and Xake logs a warning naming both patterns. The warning is
hidden at the default console level (`Normal`); it shows in `build.log` or with `-ll Loud`. Two rules written with the *same* pattern string are the ordinary override
idiom — the last one silently wins, no warning.

```fsharp
do xakeScript {
    // the general rule may be declared first; the literal `obf` still wins for obfuscated outputs
    "out/(b:*)/(cfg:*)/(fwk:*)/(asm:*).dll" ..> recipe { (* compile *) }
    "out/(b:*)/obf/(fwk:*)/(asm:*).dll"     ..> recipe { (* obfuscate *) }
}
```

Worked examples: `out/(b:*)/obf/...` beats `out/(b:*)/(cfg:*)/...` (more literals, fewer groups);
`a/*/x.dll` beats `a/**/x.dll` (no `**`); `a/?.dll` beats `a/*.dll` (`?` tighter than `*`).

The legacy switch and diagnostics:

- `ruleMatch LastDeclared` in the script restores the old behavior (last declared matching rule
  wins, specificity ignored). It is a one-release migration aid and is **deprecated**: remove the
  reliance on declaration order instead. Default is `ruleMatch MostSpecific`.

  ```fsharp
  do xakeScript {
      ruleMatch LastDeclared
      // ... rules ...
  }
  ```
- `-d EXPLAIN_RULE=<target>` prints every rule that matches that target, with its specificity
  `{lit, **, wc}` and the winner marked — useful when a rule fires unexpectedly.

## Patterns, masks and paths

A script holds three kinds of path strings, parsed differently:

| Kind | Where | Parsed as |
|---|---|---|
| rule pattern | `"out/(name:*).o" ..> ...`, `target`, `targets`, `command`, `=>`, `Xake.Typed` | the whole target path is matched at once; has named groups |
| fileset mask | `!!`, `ls`, `++`, `--`, `fileset { includes; excludes }`, `includefile` | walked over the file system from a base directory; no groups |
| local path | `BaseDir`, `@@`, `ProjectRoot`, file names read from disk, `</>` | a plain OS path, never a pattern |

Rule patterns and masks share **one grammar** (the pattern module, `Xake.PatternAst`; the full
specification is [features/pattern-module/spec.md](features/pattern-module/spec.md)). They differ
in two things only: a rule has named groups `(name:...)` and may not end in `/`; a mask has no
groups and a trailing `/` makes it match directories. Local paths are not patterns: no escapes,
no checks, either separator. `</>` is `Path.Combine`: do not build patterns or masks with it
(`PatternAst.escapePath` turns a real path into a mask).

**Always write `/`** in patterns and masks, on every OS.

### Elements

| Element | Meaning |
|---|---|
| `*` | any run of characters within one segment (dotfiles included) |
| `?` | exactly one character within a segment |
| `**` | as a whole segment: zero or more directories. Inside a segment (`a**b`) it is `*` |
| `[abc]` `[a-z]` | one character from the set |
| `[!abc]` `[^abc]` | one character not in the set |
| `{a,b}` | one of the alternatives, which may hold `/` and wildcards: `src/{core,tools}/*.cs` |
| `(name:body)` | rule patterns only: captures what `body` matches, read as `getRuleMatch "name"` or a typed field |
| `\(` `\)` `\[` `\]` `\{` `\}` `\|` | the literal character |
| `/` | the separator; a run of `/` is one |

Bare wildcards (`*`, `**`, `?`) are captured by position too: `"1"`, `"2"`, ... left to right, at
any depth (`"0"` is the whole path). Groups are greedy and leftmost: `(a:*)-(b:*)` on `x-y-z`
gives `a = x-y`. Case follows the OS: ordinal on Linux and macOS, ignore-case on Windows.

### Rule patterns

A rule pattern matches the full target path, with the project root joined in front of a relative
one as a literal (a root like `/work/x(1)` needs no escaping). A rule takes `*.dll` for
a file in the root and `**/*.dll` for a `.dll` at any depth **including the root** (3.x: one or
more directories; write `*/**/*.dll` to require one). When several rules match, the most specific
wins ([rule selection](#rule-selection--the-most-specific-rule-wins)).

```fsharp
"out/(cfg:*)/(name:*).dll" ..> recipe { ... }              // getRuleMatch "cfg"
"out/(fwk:*)/(ext:{dll,xml})" ..> recipe { ... }           // alternatives go inside the group
```

A group name is a letter or `_` followed by letters, digits and `_`, and appears once per
pattern. A multi-output rule (`*..>`, `targets`) needs the same group names in every output and
no class or braces outside a group, so the other outputs can be named from the match.

### Masks

A mask is resolved from the base directory. A mask without any wildcard is an explicit name, listed
whether or not the file exists. A wildcard include is scanned in one pass: results are in
ordinal order (files before subdirectories within a directory), without duplicates; `**` does not
follow directory symlinks or junctions; an unreadable directory is skipped with a warning. An
exclude removes matches from the includes before it, and `dir/` as an exclude removes everything
below `dir` without entering it.

```fsharp
!! "src/**/*.fs" -- "src/**/obj/"      // the files, minus everything under obj directories
!! "src/{core,tools}/*.cs"             // alternatives
```

| Mask | Selects |
|---|---|
| `dir/*` | files directly in `dir` |
| `dir/**` or `dir/**/*` | every file below `dir` |
| `dir/**/` | the directories below `dir` |
| `dir/*/` | the subdirectories of `dir` |
| `*.*` | names that contain a `.` |

### Reserved characters and escaping

`( ) [ ] { } |` are reserved. A reserved character outside a construct that uses it is an
**error** when the pattern is created, whose text names the 3.x meaning and the 4.0 spelling.
To match one literally put `\` in front. F# keeps `\(` as written in `"..."`, `@"..."` and
`"""..."""` alike; the recommended spelling is `@"..."`.

```fsharp
!! @"src/a\[test.c"                    // the file a[test.c
!! @"**/odd\{dir\}/*.c"                // .c files in directories named odd{dir}
@"out/a\(1\)/(name:*).c" ..> recipe { ... }   // builds out/a(1)/x.c with name = "x"
!! (PatternAst.escapePath toolDir + "/*.dll")  // a real path, escaped
```

`*`, `?` and `,` have no `\` escape in 4.0; the one-character class is their literal form: `[*]`,
`[?]`, `[,]`, and `[\\]` for a backslash. `\` before any other character is still a path
separator in 4.0 (see below).

### Error codes

An invalid pattern raises `PatternException` when the rule or fileset is created, usually when
the script loads:

```text
Invalid pattern 'out/a(1).txt' (rule) at character 6: '(' is reserved (Xake 3.x: a regex group); write \( for a literal '(' [XP003]
```

Codes `XP0nn` are errors, `XP1nn` are warnings in the build log (once per pattern, "Pattern '...'
(rule|include|exclude): ... [XPnnn]"). The complete table, with the exact text of every reason, is
section 7.2 (errors) and 7.3 (warnings) of the [pattern spec](features/pattern-module/spec.md).
The common ones:

| Code | Meaning |
|---|---|
| XP002, XP004 | unbalanced `[`, `{`, or a stray `]`, `}`; write `\[`, `\{` for a literal |
| XP003 | `(`, `)`, `\|` outside a named group; `(ext:dll\|exe)` is spelled `(ext:{dll,exe})` |
| XP005 | `{x}` without a comma; write `\{x\}` or `{a,b}` |
| XP006 | unclosed `(name:` |
| XP007, XP015 | a group name starting with a digit; a name used twice |
| XP008 | a named group in a mask |
| XP010 | `/` in a class, a reversed range, `[[:alpha:]]` |
| XP011 | a rule pattern ending in `/` |
| XP012 | `.` or `..` after a wildcard |
| XP013, XP014 | multi-output rules whose outputs have different groups; a class or braces outside a group in a pattern that is formatted (typed targets, multi-output) |
| XP018, XP019 | an empty pattern; `{a/,b}` at the end |
| XP105, XP106 | `[c]` read as `c` (deprecated) |
| XP110 | `\` used as a separator (deprecated) |
| XP112, XP113, XP114, XP117, XP119, XP120 | evidence warnings for the 3.x readings: segment-local `**`, `dir/**`, `*.*`, `[!a]`, `**/` that took zero directories, an exclude that removes nothing |

### Migration from 3.x

The 4.0 grammar is stricter. What to look at in a script:

| 3.x | 4.0 |
|---|---|
| `"out/a(1).txt" ..> ...` (regex group, matched `out/a1.txt`) | error XP003; write `@"out/a\(1\).txt"` |
| `"(ext:dll\|exe\|nupkg)"` | error XP003; write `"(ext:{dll,exe,nupkg})"` |
| `!! "lib[x].dll"` (literal brackets in a mask) | the same file name is `libx.dll`; write `!! @"lib\[x\].dll"` for the brackets |
| `!! "src/{a,b}.cs"` (literal braces) | an alternation (`src/a.cs`, `src/b.cs`); write `\{ \}` for literal braces |
| `{x}` | error XP005 |
| a group in a mask, `!! "src/(x:*)/*.cs"` | error XP008: `!! "src/*/*.cs"`, or `PatternAst.glob` from a rule pattern |
| `"**/*.dll"` in a rule needed a directory | zero or more directories; `*/**/*.dll` to require one |
| `!! "dir/**"` listed the directories | every file; `dir/**/` for the directories |
| `-- "obj/"` removed nothing | removes everything below `obj` and does not enter it |
| `!! "*.*"` listed any name | names with a `.`; `*` for every name |
| `!! @"src\*.cs"` | still a separator, warns XP110: write `src/*.cs` (an escape from 5.0) |
| an unclosed `(name:` in a string rule | error XP006 |
| outputs of `*..>` with different groups | error XP013 |

Also changed: results of a scan are ordered and duplicate-free; `**` skips directory symlinks;
a literal exclude segment is no longer case-insensitive on Linux and macOS (and a wildcard
include no longer ignores case on macOS); `Path.matchesPattern`, `Fileset.toFileList1` and the
`FileSystem` adapter are removed. The full list, with what each change gives a build, is the
4.0 section of the [release notes](release-notes.md).

### From a rule pattern to a mask

`PatternAst.glob` turns a rule pattern into a mask for the values you know: bound groups and
wildcards become their (escaped) values, the rest stay wildcards.

```fsharp
let tokens = PatternAst.Rule.parse "src/(lib:*)/(name:*).cs"
let mask = tokens |> PatternAst.glob (function "lib" -> Some "core" | _ -> None) (fun _ -> None)
// "src/core/*.cs"
let files = !! mask
```

`Target<G>.Glob` does the same with a typed record ([typed-targets.md](typed-targets.md)).

## Typed targets

Rules with groups can declare them as record fields (`open Xake.Typed` after `open Xake`): `m.cfg`
instead of `getRuleMatch "cfg"`, and paths for `need` generated from the pattern. See
[typed-targets.md](typed-targets.md), which also covers the migration from the string API.

## Useful links

- Wiki home: https://github.com/OlegZee/Xake/wiki
- Introduction: https://github.com/OlegZee/Xake/wiki/Introduction
- Command line reference: https://github.com/OlegZee/Xake/wiki/Reference-%7C-Command-Line
- Rules reference: https://github.com/OlegZee/Xake/wiki/Reference-%7C-Rules
- Recipe reference: https://github.com/OlegZee/Xake/wiki/Reference-%7C-Recipe
