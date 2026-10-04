namespace Xake.Dotnet

open System.IO

open Xake
open Xake.Tasks

/// The part of a compiler's command line both dialects share: the tables that say which switch
/// names a file and how its value is shaped, and the functions over them. `CscArgs` and
/// `FscArgs` differ only in how an argument is spelled (`/name:value` vs `--name:value`) and in
/// their tables.
module internal ArgsCore =

    /// How a switch's value is shaped, for the ones whose value names files.
    type Shape =
        /// one path
        | Path
        /// comma-separated paths; with `ListAliases`, an item may carry an `alias=` prefix
        | PathList
        /// `path,name,...` -- only the first element is a path
        | PathFirst

    /// One compiler's switch tables, by canonical name.
    type Dialect = {
        /// short name -> canonical name
        Aliases: Map<string, string>
        Shapes: Map<string, Shape>
        /// switches whose paths the compiler reads
        Inputs: Set<string>
        /// switches whose paths the compiler writes
        Outputs: Set<string>
        /// whether a `PathList` item may be `alias=path` (csc's `/reference:`)
        ListAliases: bool
    }

    /// Splits a switch value on ',' the way csc itself does: a comma inside a `"..."` quoted
    /// segment does not split; the quotes themselves are not part of the path and are dropped.
    let splitList (value: string) =
        let items = ResizeArray<string>()
        let current = System.Text.StringBuilder()
        let mutable inQuotes = false
        for ch in value do
            match ch with
            | '"' -> inQuotes <- not inQuotes
            | ',' when not inQuotes -> items.Add (current.ToString()); current.Clear() |> ignore
            | c -> current.Append c |> ignore
        items.Add (current.ToString())
        List.ofSeq items

    let quoteIfNeeded (s: string) = if s.Contains "," then "\"" + s + "\"" else s

    let aliasSplitIndex (p: string) =
        match p.IndexOf '=' with
        | -1 -> -1
        | i -> match p.IndexOf '/' with | slash when slash >= 0 && slash < i -> -1 | _ -> i

    let canonical (d: Dialect) name = d.Aliases |> Map.tryFind name |> Option.defaultValue name

    /// Paths a switch's value names.
    let switchPaths (d: Dialect) name (value: string) =
        match d.Shapes |> Map.tryFind (canonical d name) with
        | Some Path -> [value]
        | Some PathList ->
            splitList value |> List.filter ((<>) "")
            |> List.map (fun p ->
                match (if d.ListAliases then aliasSplitIndex p else -1) with
                | -1 -> p
                | i -> p.Substring (i + 1))
        | Some PathFirst -> [(splitList value).[0]]
        | None -> []

    /// A switch's value with every path it names rewritten, the rest left as it is.
    let mapSwitchPaths (d: Dialect) (f: string -> string) name (value: string) =
        match d.Shapes |> Map.tryFind (canonical d name) with
        | Some Path -> f value
        | Some PathList ->
            let mapped =
                splitList value |> List.map (fun p ->
                    if p = "" then p else
                    (match (if d.ListAliases then aliasSplitIndex p else -1) with
                     | -1 -> f p
                     | i -> p.Substring (0, i + 1) + f (p.Substring (i + 1)))
                    |> quoteIfNeeded)
            System.String.Join (",", mapped)
        | Some PathFirst ->
            let parts = splitList value |> List.mapi (fun i p -> if i = 0 then quoteIfNeeded (f p) else p)
            System.String.Join (",", parts)
        | None -> value

    /// The command line's arguments as the core sees them: `Choice1Of2` a source,
    /// `Choice2Of2` a switch (name as written, value).
    let inputs (d: Dialect) (view: string -> Choice<string, string * string>) (args: string list) =
        args |> List.collect (fun arg ->
            match view arg with
            | Choice1Of2 path -> [path]
            | Choice2Of2 (name, value) when d.Inputs.Contains (canonical d name) -> switchPaths d name value
            | _ -> [])

    let outputs (d: Dialect) (view: string -> Choice<string, string * string>) (args: string list) =
        args |> List.collect (fun arg ->
            match view arg with
            | Choice2Of2 (name, value) when d.Outputs.Contains (canonical d name) -> switchPaths d name value
            | _ -> [])

    let sources (view: string -> Choice<string, string * string>) (args: string list) =
        args |> List.choose (fun arg -> match view arg with Choice1Of2 path -> Some path | _ -> None)

    let switchValues (d: Dialect) (view: string -> Choice<string, string * string>) name (args: string list) =
        args |> List.choose (fun arg ->
            match view arg with
            | Choice2Of2 (n, value) when canonical d n = name -> Some (switchPaths d n value)
            | _ -> None) |> List.concat

    /// `p` relative to `dir` made absolute, with forward slashes ("" stays "").
    let absolutePath (dir: string) (p: string) =
        let p = p.Replace ('\\', '/')
        if p = "" then p
        else System.IO.Path.GetFullPath (System.IO.Path.Combine (dir, p)) |> fun p -> p.Replace ('\\', '/')

/// The C# compiler's command line, as data. The import takes the exact invocation msbuild
/// would have made and the compile step replays it, so both sides need to know which of the
/// arguments name files: those get made absolute and tokenized when the lock is written, and
/// they are the inputs the compile step depends on.
module CscArgs =

    /// One argument: a source file, or a switch with its (possibly empty) value.
    type Arg =
        | Source of string
        | Switch of name: string * value: string

    /// `/reference:a.dll` -> Switch ("reference", "a.dll"); the name is lowercased and the
    /// `+`/`-` of a boolean switch stays with it (`/optimize+` -> Switch ("optimize+", "")).
    let parse (arg: string) =
        // an absolute Unix path also starts with '/': it has a second slash before any colon
        let head = match arg.IndexOf ':' with | -1 -> arg | i -> arg.Substring (0, i)
        if arg.Length > 1 && arg.[0] = '/' && head.IndexOf ('/', 1) < 0 then
            match arg.IndexOf ':' with
            | -1 -> Switch (arg.Substring(1).ToLowerInvariant(), "")
            | i -> Switch (arg.Substring(1, i - 1).ToLowerInvariant(), arg.Substring (i + 1))
        else Source arg

    let private dialect : ArgsCore.Dialect = {
        Aliases =
            [ "r", "reference"; "a", "analyzer"; "res", "resource"; "linkres", "linkresource"
              "l", "link"; "d", "define"; "lib", "lib" ] |> Map.ofList
        Shapes =
            [ "out", ArgsCore.Path; "doc", ArgsCore.Path; "keyfile", ArgsCore.Path; "ruleset", ArgsCore.Path
              "sourcelink", ArgsCore.Path; "pdb", ArgsCore.Path; "refout", ArgsCore.Path; "win32icon", ArgsCore.Path
              "win32res", ArgsCore.Path; "win32manifest", ArgsCore.Path; "appconfig", ArgsCore.Path
              "generatedfilesout", ArgsCore.Path; "touchedfiles", ArgsCore.Path; "analyzerconfig", ArgsCore.Path
              "reference", ArgsCore.PathList; "analyzer", ArgsCore.PathList; "additionalfile", ArgsCore.PathList
              "embed", ArgsCore.PathList; "link", ArgsCore.PathList; "addmodule", ArgsCore.PathList; "lib", ArgsCore.PathList
              "resource", ArgsCore.PathFirst; "linkresource", ArgsCore.PathFirst; "errorlog", ArgsCore.PathFirst ]
            |> Map.ofList
        Inputs =
            set [ "reference"; "analyzer"; "additionalfile"; "embed"; "link"; "addmodule"; "keyfile"
                  "ruleset"; "analyzerconfig"; "resource"; "linkresource"; "win32icon"; "win32res"
                  "win32manifest"; "appconfig"; "sourcelink" ]
        Outputs = set [ "out"; "doc"; "refout"; "pdb"; "errorlog"; "generatedfilesout"; "touchedfiles" ]
        ListAliases = true
    }

    let private view arg =
        match parse arg with
        | Source path -> Choice1Of2 path
        | Switch (name, value) -> Choice2Of2 (name, value)

    /// Switch names with their aliases folded.
    let canonical name = ArgsCore.canonical dialect name

    /// Splits a switch value on ',' the way csc itself does: a comma inside a `"..."` quoted
    /// segment does not split (msbuild quotes a path in a list when the path itself contains a
    /// comma, e.g. the generated `.NETStandard,Version=vX.Y.AssemblyAttributes.cs` a netstandard
    /// project embeds); the quotes themselves are not part of the path and are dropped.
    let splitList (value: string) = ArgsCore.splitList value

    /// Re-quotes a path list item if joining it back with ',' would make it split again where
    /// it did not before -- the inverse of `splitList`.
    let quoteIfNeeded (s: string) = ArgsCore.quoteIfNeeded s

    /// A `/reference:` (etc.) list item may be `alias=path`: the alias is a short identifier
    /// with no path separator in it, always at the very start, so an `=` is only the alias
    /// marker when it comes before the first `/` -- a bare path may itself contain an `=` after
    /// one (e.g. the generated `.NETStandard,Version=vX.Y.AssemblyAttributes.cs`, once its
    /// comma-hiding quotes are gone).
    let aliasSplitIndex (p: string) = ArgsCore.aliasSplitIndex p

    /// Paths named by one argument.
    let paths arg =
        match arg with
        | Source path -> [path]
        | Switch (name, value) -> ArgsCore.switchPaths dialect name value

    /// Rewrites every path an argument names, leaving the rest of it as it is.
    let mapPaths (f: string -> string) arg =
        match arg with
        | Source path -> Source (f path)
        | Switch (name, value) -> Switch (name, ArgsCore.mapSwitchPaths dialect f name value)

    let format = function
        | Source path -> path
        | Switch (name, "") -> "/" + name
        | Switch (name, value) -> sprintf "/%s:%s" name value

    /// The files a command line reads: the sources and everything an input switch names.
    let inputs (args: string list) = ArgsCore.inputs dialect view args

    /// The files a command line writes.
    let outputs (args: string list) = ArgsCore.outputs dialect view args

    let sources (args: string list) = ArgsCore.sources view args

    let switchValues name (args: string list) = ArgsCore.switchValues dialect view name args

    /// Makes every path relative to `dir` absolute, with forward slashes.
    let absolutize (dir: string) (args: string list) =
        args |> List.map (parse >> mapPaths (ArgsCore.absolutePath dir) >> format)
