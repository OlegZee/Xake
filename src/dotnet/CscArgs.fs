namespace Xake.Dotnet

open System.IO

open Xake
open Xake.Tasks

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

    let private aliases =
        [ "r", "reference"; "a", "analyzer"; "res", "resource"; "linkres", "linkresource"
          "l", "link"; "d", "define"; "lib", "lib" ] |> Map.ofList

    /// Switch names with their aliases folded.
    let canonical name = aliases |> Map.tryFind name |> Option.defaultValue name

    /// How a switch's value is shaped, for the ones whose value names files.
    type private Shape =
        /// one path
        | Path
        /// comma-separated paths; a reference may carry an `alias=` prefix
        | PathList
        /// `path,name,...` -- only the first element is a path
        | PathFirst

    let private shapes =
        [ "out", Path; "doc", Path; "keyfile", Path; "ruleset", Path; "sourcelink", Path
          "pdb", Path; "refout", Path; "win32icon", Path; "win32res", Path; "win32manifest", Path
          "appconfig", Path; "generatedfilesout", Path; "touchedfiles", Path; "analyzerconfig", Path
          "reference", PathList; "analyzer", PathList; "additionalfile", PathList; "embed", PathList
          "link", PathList; "addmodule", PathList; "lib", PathList
          "resource", PathFirst; "linkresource", PathFirst; "errorlog", PathFirst ]
        |> Map.ofList

    /// Switches that name the compiler's inputs, with the value's file path(s).
    let private inputSwitches =
        set [ "reference"; "analyzer"; "additionalfile"; "embed"; "link"; "addmodule"; "keyfile"
              "ruleset"; "analyzerconfig"; "resource"; "linkresource"; "win32icon"; "win32res"
              "win32manifest"; "appconfig"; "sourcelink" ]

    let private outputSwitches = set [ "out"; "doc"; "refout"; "pdb"; "errorlog"; "generatedfilesout"; "touchedfiles" ]

    /// Splits a switch value on ',' the way csc itself does: a comma inside a `"..."` quoted
    /// segment does not split (msbuild quotes a path in a list when the path itself contains a
    /// comma, e.g. the generated `.NETStandard,Version=vX.Y.AssemblyAttributes.cs` a netstandard
    /// project embeds); the quotes themselves are not part of the path and are dropped.
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

    /// Re-quotes a path list item if joining it back with ',' would make it split again where
    /// it did not before -- the inverse of `splitList`.
    let quoteIfNeeded (s: string) = if s.Contains "," then "\"" + s + "\"" else s

    /// A `/reference:` (etc.) list item may be `alias=path`: the alias is a short identifier
    /// with no path separator in it, always at the very start, so an `=` is only the alias
    /// marker when it comes before the first `/` -- a bare path may itself contain an `=` after
    /// one (e.g. the generated `.NETStandard,Version=vX.Y.AssemblyAttributes.cs`, once its
    /// comma-hiding quotes are gone).
    let aliasSplitIndex (p: string) =
        match p.IndexOf '=' with
        | -1 -> -1
        | i -> match p.IndexOf '/' with | slash when slash >= 0 && slash < i -> -1 | _ -> i

    /// Paths named by one argument.
    let paths arg =
        match arg with
        | Source path -> [path]
        | Switch (name, value) ->
            match shapes |> Map.tryFind (canonical name) with
            | Some Path -> [value]
            | Some PathList ->
                splitList value |> List.filter ((<>) "")
                |> List.map (fun p -> match aliasSplitIndex p with | -1 -> p | i -> p.Substring (i + 1))
            | Some PathFirst -> [(splitList value).[0]]
            | None -> []

    /// Rewrites every path an argument names, leaving the rest of it as it is.
    let mapPaths (f: string -> string) arg =
        match arg with
        | Source path -> Source (f path)
        | Switch (name, value) ->
            match shapes |> Map.tryFind (canonical name) with
            | Some Path -> Switch (name, f value)
            | Some PathList ->
                let mapped =
                    splitList value |> List.map (fun p ->
                        if p = "" then p else
                        (match aliasSplitIndex p with
                         | -1 -> f p
                         | i -> p.Substring (0, i + 1) + f (p.Substring (i + 1)))
                        |> quoteIfNeeded)
                Switch (name, System.String.Join (",", mapped))
            | Some PathFirst ->
                let parts = splitList value |> List.mapi (fun i p -> if i = 0 then quoteIfNeeded (f p) else p)
                Switch (name, System.String.Join (",", parts))
            | None -> arg

    let format = function
        | Source path -> path
        | Switch (name, "") -> "/" + name
        | Switch (name, value) -> sprintf "/%s:%s" name value

    /// The files a command line reads: the sources and everything an input switch names.
    let inputs (args: string list) =
        args |> List.map parse |> List.collect (function
            | Source path -> [path]
            | Switch (name, _) as arg when inputSwitches.Contains (canonical name) -> paths arg
            | _ -> [])

    /// The files a command line writes.
    let outputs (args: string list) =
        args |> List.map parse |> List.collect (function
            | Switch (name, _) as arg when outputSwitches.Contains (canonical name) -> paths arg
            | _ -> [])

    let sources (args: string list) =
        args |> List.choose (parse >> function | Source path -> Some path | _ -> None)

    let switchValues name (args: string list) =
        args |> List.choose (parse >> function
            | Switch (n, _) as arg when canonical n = name -> Some (paths arg)
            | _ -> None) |> List.concat

    /// Makes every path relative to `dir` absolute, with forward slashes.
    let absolutize (dir: string) (args: string list) =
        let absolute (p: string) =
            let p = p.Replace ('\\', '/')
            if p = "" then p
            else System.IO.Path.GetFullPath (System.IO.Path.Combine (dir, p)) |> fun p -> p.Replace ('\\', '/')
        args |> List.map (parse >> mapPaths absolute >> format)
