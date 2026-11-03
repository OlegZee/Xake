# rm fileset syntax: research

Goal: `do! rm { !!"out/*.*" }` instead of `do! rm { files (!!"out/*.*") }` /
`files (fileset { ... })`, with excludes, several entries, plain strings and the existing
operations mixed in. Backward compatible.

## Today

`src/core/Tasks/Rm.fs`: `RmArgsBuilder` builds `RmArgs = { targets: RmTarget list; verbose }`
(`RmTarget = Dir | File | Files of Fileset`) through custom operations `file`, `dir`, `files`,
`verbose`; the state starts at `Yield(())`, `Run` turns it into the `Rm` recipe. A fileset gets
in only through `files <expr>`. `del` is an alias. There is no dry run. `cp`
(`src/core/Tasks/Cp.fs`, `CopyBuilder`) has the same shape: `file`, `dir`, `files`, plus
`todir`, `flatten`, `overwrite`, `verbose`, `dryrun`.

## Options (prototyped in fsi against the built netstandard2.0 Xake.dll)

1. **`Yield` overloads + `Combine` + `Delay` on the existing builder.** `Yield(Fileset)` makes a
   `Files` target, `Yield(string)` a `File` target; `Combine` concatenates targets and ORs flags.
   With implicit yields (F# 4.7+) all of these compile and give the expected `RmArgs`:
   `rm { !!"out/*.*" }`, `rm { !!"out/*.*" -- "out/keep.txt" }`, `rm { !!"a"; !!"b" }`
   (one per line too), `rm { "out/a.txt" }`, `rm { yield !!"out/*.*" }`,
   `rm { !!"a"; verbose }`, `rm { !!"a"; dir "out" }`, and every old form.
   **Does not compile** with these members alone: an entry *after* a custom operation,
   `rm { verbose; !!"a" }` / `rm { dir "out"; !!"a" }`: FS0708, "the builder must define a
   'For' method" (F# translates `op; expr` into `For(opResult, fun () -> expr)`).
2. **Option 1 + `For(RmArgs, unit -> RmArgs)`** (combine the state with the rest). Fixes the
   above: entries and operations mix in any order, e.g. `rm { dir "out"; !!"a"; verbose; "z" }`.
   On `cp` the existing (unused) `For(seq, f)` stays as an overload; no ambiguity, since
   `CpArgs` is not a sequence. `for x in xs do ...` and `yield!` inside `rm` stay unsupported
   (no seq `For`/`YieldFrom`); not needed.
3. **List-style builder** (everything is a yield of a DU: `rm { !!"a"; Verbose }`). Drops the
   custom operations, so it breaks every existing script. Rejected.
4. **Function form `rm !!"out/*.*"`.** `rm` is a builder instance; F# cannot make one value both
   a builder and a function, so it needs a new name (`rmFiles !!"..."`). Adds a second spelling
   for the same thing; option 2 already gives the short form. Not done.
5. Operator on the fileset (`!!"out/*.*" |> rm.Files`) or a `files` overload taking a string:
   no shorter than option 2. Not done.

## Other builders

Only `cp` takes files (`Shell.fs` has no file inputs; `fileset`, `xakeScript` are not tasks).
`cp { files (!!"bin/*.dll"); todir "deploy" }` has the same awkwardness, and the change is the
same few members, so it is applied here: `cp { !!"bin/*.dll" -- "bin/*.Tests.dll"; "README.md";
todir "deploy" }`. Its `Combine` keeps the later non-null `todir` and ORs the flags; a string
entry is a `file` source (always flattened), a fileset a `files` source (honours `flatten`).

## Recommendation (implemented)

Option 2 on `rm`/`del` and `cp`/`copy`. Additive members only: every existing form compiles
and behaves as before. A string entry is a file mask (as `file`), never a directory.
