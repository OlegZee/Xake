namespace Xake.Dotnet

/// The F# compiler's command line, as data: the twin of `CscArgs` for fsc's own dialect.
/// Switches are `--name[:value]` or `-x[:value]` (fsc reads a leading `/` as a path on Unix),
/// spelled exactly as given -- the prefix and the case of the name are kept, so `format`
/// returns the argument `parse` was handed. Which switches name files, and which of those the
/// compiler reads or writes, comes from fsc's own tables over the same core as `CscArgs`.
module FscArgs =

    /// One argument: a source file, or a switch with its prefix (`-` or `--`), its name as
    /// written and its (possibly empty) value.
    type Arg =
        | Source of string
        | Switch of prefix: string * name: string * value: string

    /// `--out:a.dll` -> Switch ("--", "out", "a.dll"), `-r:b.dll` -> Switch ("-", "r",
    /// "b.dll"); the `+`/`-` of a boolean switch stays with the name (`--optimize+` ->
    /// Switch ("--", "optimize+", "")). Anything not starting with `-` is a source.
    let parse (arg: string) =
        if arg.Length > 1 && arg.[0] = '-' then
            let prefix = if arg.StartsWith "--" then "--" else "-"
            let body = arg.Substring prefix.Length
            match body.IndexOf ':' with
            | -1 -> Switch (prefix, body, "")
            | i -> Switch (prefix, body.Substring (0, i), body.Substring (i + 1))
        else Source arg

    let private dialect : ArgsCore.Dialect = {
        Aliases = [ "r", "reference"; "o", "out"; "g", "debug"; "d", "define"; "I", "lib" ] |> Map.ofList
        Shapes =
            [ "out", ArgsCore.Path; "doc", ArgsCore.Path; "sig", ArgsCore.Path; "pdb", ArgsCore.Path
              "refout", ArgsCore.Path; "keyfile", ArgsCore.Path; "win32res", ArgsCore.Path
              "win32manifest", ArgsCore.Path; "sourcelink", ArgsCore.Path; "reference", ArgsCore.Path
              "lib", ArgsCore.PathList
              "resource", ArgsCore.PathFirst; "linkresource", ArgsCore.PathFirst ]
            |> Map.ofList
        Inputs = set [ "reference"; "keyfile"; "resource"; "linkresource"; "win32res"; "win32manifest"; "sourcelink" ]
        Outputs = set [ "out"; "doc"; "sig"; "pdb"; "refout" ]
        ListAliases = false
    }

    let private view arg =
        match parse arg with
        | Source path -> Choice1Of2 path
        | Switch (_, name, value) -> Choice2Of2 (name, value)

    /// Switch names with their short forms folded (`r` -> `reference`, `o` -> `out`, `g` ->
    /// `debug`, `d` -> `define`, `I` -> `lib`). Case-sensitive, as fsc is.
    let canonical name = ArgsCore.canonical dialect name

    /// Paths named by one argument.
    let paths arg =
        match arg with
        | Source path -> [path]
        | Switch (_, name, value) -> ArgsCore.switchPaths dialect name value

    /// Rewrites every path an argument names, leaving the rest of it as it is.
    let mapPaths (f: string -> string) arg =
        match arg with
        | Source path -> Source (f path)
        | Switch (prefix, name, value) -> Switch (prefix, name, ArgsCore.mapSwitchPaths dialect f name value)

    let format = function
        | Source path -> path
        | Switch (prefix, name, "") -> prefix + name
        | Switch (prefix, name, value) -> prefix + name + ":" + value

    /// The files a command line reads: the sources and everything an input switch names.
    let inputs (args: string list) = ArgsCore.inputs dialect view args

    /// The files a command line writes (`--out`, `--doc`, `--sig`, `--pdb`, `--refout`).
    let outputs (args: string list) = ArgsCore.outputs dialect view args

    let sources (args: string list) = ArgsCore.sources view args

    /// The paths every switch of canonical name `name` carries.
    let switchValues name (args: string list) = ArgsCore.switchValues dialect view name args

    /// Makes every path relative to `dir` absolute, with forward slashes.
    let absolutize (dir: string) (args: string list) =
        args |> List.map (parse >> mapPaths (ArgsCore.absolutePath dir) >> format)
