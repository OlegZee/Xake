## Xake overview

Xake is a build automation tool for .NET that uses F# scripts.
It tracks dependencies, supports incremental builds, and runs independent targets in parallel.

Current project baseline:

- .NET SDK 8.0 or newer
- Xake NuGet package `Xake` — the engine and the .NET Framework tasks in one

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
**last declared** rule wins (as before), and Xake logs a warning naming both patterns so the
ambiguity is visible. Two rules written with the *same* pattern string are the ordinary override
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
| rule pattern | `"out/(name:*).o" ..> ...`, `target "..."` | the whole target path is matched at once |
| fileset mask | `!!`, `ls`, `fileset { includes; excludes }`, `++`, `--` | one path segment at a time |
| local path | `BaseDir`, `@@`, `ProjectRoot`, file names read from disk, `</>` | a plain OS path |

**Rule patterns** match the full target path. `*` is a run of characters within a segment, `?` one
character, `**` anything including `/`. `(name:...)` captures what its body matches
(`getRuleMatch "name"`); bare wildcards are captured by position (`"1"`, `"2"`, ... left to right).
The project root is joined in front of a relative pattern as a literal: a root like
`/work/x(1)` needs no escaping.

**Fileset masks** are split at `/` and resolved segment by segment from the base directory. An
include hands a wildcard segment to the OS directory listing, which knows only `*` and `?`; a
segment without a wildcard is an explicit name (listed even if the file is absent). An exclude
compares the segments of each found file with the mask. Masks have **no named groups**.

**Local paths** are not patterns: no escapes, no warnings, either separator. `</>` is
`Path.Combine`: it joins real paths with the OS separator, so do not build patterns or masks
with it.

**Always write `/`** in patterns and masks, on every OS.

### Reserved characters and the `\` escape

`( ) [ ] { } |` are reserved for future pattern elements (apart from `(name:...)` itself). To
match one literally, put `\` before it. F# keeps `\[` as written in `"..."`, `@"..."` and
`"""..."""` alike.

```fsharp
!! @"src/a\[test.c"                 // the file a[test.c (not atest.c)
!! @"**/odd\{dir\}/*.c"             // .c files in any directory named odd{dir}
!! "*.c" -- @"*\(*\).c"             // all .c files except names like d(2).c
@"out/a\[(name:*).c" ..> recipe { ... }   // builds out/a[x.c with name = "x"
```

`[c]` (a one-character class) is still read as the literal `c` for compatibility, with a
warning: `lib[x].dll` means `libx.dll`, and `sub[1]` means `sub1` -- write `sub\[1\]` for a
directory named `sub[1]`.

### Deprecations

Each is reported once per pattern in the build log ("warning: pattern '...': ...") and will be
an error in a future major release:

- an unescaped reserved character. In this release it keeps its former meaning: in a rule
  pattern `( ) [ ] |` are regex syntax (`{ }` are literal); in a mask they are literal, except
  in an exclude segment with a wildcard, where `( ) [ ] |` are regex syntax as in rules;
- `[c]` instead of `\c`;
- `\` as a path separator (`src\*.cs`): use `/`. A `\` before a reserved character is an escape,
  so `src\(x)\*.cs` now means the directory `src(x)`;
- a named group in a mask: an include reads it as literal text (finds nothing), an exclude
  segment with a wildcard matches the group's body. Write `\( \)` for literal parentheses.

### From a rule pattern to a mask

`PatternAst.glob` turns a rule pattern into a mask for the values you know: bound groups and
wildcards become their (escaped) values, the rest stay wildcards.

```fsharp
let tokens = PatternAst.parse "src/(lib:*)/(name:*).cs"
let mask = tokens |> PatternAst.glob (function "lib" -> Some "core" | _ -> None) (fun _ -> None)
// "src/core/*.cs"
let files = !! mask
```

## Useful links

- Wiki home: https://github.com/OlegZee/Xake/wiki
- Introduction: https://github.com/OlegZee/Xake/wiki/Introduction
- Command line reference: https://github.com/OlegZee/Xake/wiki/Reference-%7C-Command-Line
- Rules reference: https://github.com/OlegZee/Xake/wiki/Reference-%7C-Rules
- Recipe reference: https://github.com/OlegZee/Xake/wiki/Reference-%7C-Recipe
