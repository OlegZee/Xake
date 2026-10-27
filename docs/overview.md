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

## Useful links

- Wiki home: https://github.com/OlegZee/Xake/wiki
- Introduction: https://github.com/OlegZee/Xake/wiki/Introduction
- Command line reference: https://github.com/OlegZee/Xake/wiki/Reference-%7C-Command-Line
- Rules reference: https://github.com/OlegZee/Xake/wiki/Reference-%7C-Rules
- Recipe reference: https://github.com/OlegZee/Xake/wiki/Reference-%7C-Recipe
