namespace Xake.Hermetic.Dotnet

open System
open System.IO
open System.Text.RegularExpressions

open Xake

/// What msbuild and `dotnet restore` print, read for the log and for the failure message: the
/// diagnostics of their console logger (`<origin> : error CODE: text [entry]`, the positioned
/// `file(1,2): error CODE: ...`, `MSBUILD : error MSB1001: ...`, a bare `error NU1101: ...`),
/// classified line by line; the errors kept for a failure that names them; and the MSBuild
/// version an import needs (`-getResultOutputFile` is MSBuild 17.10, .NET SDK 8.0.300).
///
/// TODO core 3.12: the core's `Tool.diagnosticLevel` learns these shapes in Xake 3.12; this
/// local classifier goes when the package moves to `Xake [3.12, 3.13)`.
module internal MsbuildLog =

    type Kind = ErrorLine | WarningLine

    /// One diagnostic line: `Origin` is the file (with its position, if any) or `MSBUILD`,
    /// empty for a bare `error CODE: ...`; `Entry` the `[...]` suffix msbuild appends (the
    /// project or solution it was building), empty when there is none.
    type Diagnostic = { Kind: Kind; Origin: string; Code: string; Text: string; Entry: string }

    let private withOrigin =
        Regex (@"^\s*(?<origin>\S.*?)\s*:\s+(?<kind>error|warning)(?:\s+(?<code>[A-Za-z]+[0-9]+))?\s*:\s*(?<text>.*?)(?:\s+\[(?<entry>[^\[\]]+)\])?\s*$",
               RegexOptions.CultureInvariant)
    let private bare =
        Regex (@"^\s*(?<kind>error|warning)\s+(?<code>[A-Za-z]+[0-9]+)\s*:\s*(?<text>.*?)(?:\s+\[(?<entry>[^\[\]]+)\])?\s*$",
               RegexOptions.CultureInvariant)

    /// The diagnostic `line` is, if it is one. Pure.
    let parse (line: string) : Diagnostic option =
        if isNull line then None else
        let ofMatch (m: Match) =
            { Kind = (if m.Groups.["kind"].Value = "error" then ErrorLine else WarningLine)
              Origin = m.Groups.["origin"].Value
              Code = m.Groups.["code"].Value
              Text = m.Groups.["text"].Value
              Entry = m.Groups.["entry"].Value }
        let m = bare.Match line
        if m.Success then Some (ofMatch m) else
        let m = withOrigin.Match line
        if m.Success then Some (ofMatch m) else None

    /// How an error names where it comes from: the origin's file name (with its position), and
    /// the entry project in parentheses when the origin is not a project itself (a source file).
    let private place (d: Diagnostic) =
        let name (p: string) = match Path.GetFileName (p.Replace ('\\', '/')) with "" -> p | n -> n
        let origin = name d.Origin
        let isProject = Regex.IsMatch (d.Origin, @"proj$|\.sln$|^MSBUILD$", RegexOptions.IgnoreCase)
        match origin, d.Entry with
        | "", "" -> ""
        | "", entry -> name entry
        | origin, "" -> origin
        | origin, _ when isProject -> origin
        | origin, entry -> sprintf "%s (%s)" origin (name entry)

    /// One error as the failure message names it: `a.csproj: NU1008: text`. Pure.
    let describe (d: Diagnostic) =
        [ place d; d.Code; d.Text ] |> List.filter ((<>) "") |> String.concat ": "

    /// The first `limit` distinct `errors`, one per line, then `+N more, see the log`. Pure.
    let summarize (limit: int) (errors: Diagnostic list) : string =
        let distinct = errors |> List.map describe |> List.distinct
        let shown = distinct |> List.truncate limit
        [ yield! shown
          if distinct.Length > shown.Length then yield sprintf "+%d more, see the log" (distinct.Length - shown.Length) ]
        |> String.concat "\n  "

    /// How many errors a failure names.
    let shownErrors = 5

    /// The warnings already logged in one engine run: each distinct one once, at Info.
    let private warningSets =
        System.Runtime.CompilerServices.ConditionalWeakTable<obj, Collections.Concurrent.ConcurrentDictionary<string, unit>> ()
    let warningsOf (engine: obj) =
        warningSets.GetValue (engine, fun _ -> Collections.Concurrent.ConcurrentDictionary<string, unit> (StringComparer.Ordinal))

    /// The log levels of one tool run's lines (`Level` for `stdoutlevel` / `erroutlevel`) and
    /// what that run reported: errors at Error and kept; a warning at Info the first time this
    /// engine run sees it (`seen`), Verbose after; any other line as the core classifies it,
    /// at `def` otherwise. The last lines are kept too, for a failure without an error line.
    type Collector (seen: Collections.Concurrent.ConcurrentDictionary<string, unit>) =
        let sync = obj ()
        let errors = ResizeArray<Diagnostic> ()
        let tail = Collections.Generic.Queue<string> ()

        member _.Level (def: Level) (line: string) : Level =
            lock sync (fun () ->
                if not (String.IsNullOrWhiteSpace line) then
                    tail.Enqueue (line.TrimEnd ())
                    if tail.Count > 10 then tail.Dequeue () |> ignore
                match parse line with
                | Some ({ Kind = ErrorLine } as d) -> errors.Add d; Level.Error
                | Some ({ Kind = WarningLine } as d) ->
                    if seen.TryAdd (describe { d with Entry = "" }, ()) then Level.Info else Level.Verbose
                | None -> Xake.Dotnet.Tool.diagnosticLevel def line)

        member _.Errors = lock sync (fun () -> List.ofSeq errors)

        /// What a failure says after "failed with exit code N": the errors, or the last lines.
        member x.Summary =
            match x.Errors with
            | [] ->
                match lock sync (fun () -> List.ofSeq tail) with
                | [] -> "no output"
                | lines -> "no error line; the last lines of its output:\n  " + String.concat "\n  " lines
            | errors -> summarize shownErrors errors

    /// The failure message of a tool run: `'<name>': <tool> failed with exit code N:` and the summary.
    let failure (tool: string) (name: string) (exitCode: int) (log: Collector) =
        sprintf "'%s': %s failed with exit code %d:\n  %s" name tool exitCode log.Summary

    // ---- the MSBuild an import needs ---------------------------------------------------------

    /// `-getResultOutputFile`, which every import passes, came with MSBuild 17.10.
    let floor = Version (17, 10)

    /// The MSBuild version `dotnet msbuild -version` printed (the last line that is a version;
    /// the banner may come first), as major.minor.build. None when there is none. Pure.
    let parseVersion (lines: string list) : Version option =
        lines
        |> List.rev
        |> List.tryPick (fun (line: string) ->
            let text = if isNull line then "" else line.Trim ()
            let m = Regex.Match (text, @"^(\d+)\.(\d+)(?:\.(\d+))?")
            if m.Success then
                let part (i: int) = if m.Groups.[i].Success then int m.Groups.[i].Value else 0
                Some (Version (part 1, part 2, part 3))
            else None)

    /// The refusal for MSBuild `msbuild` of SDK `sdk`, or None when it is new enough. `pin` is
    /// the `global.json` that pins the SDK (its path), if one does. Pure.
    let floorRefusal (msbuild: Version) (sdk: string) (pin: string option) : string option =
        if msbuild >= floor then None else
        let advice =
            match pin with
            | Some file -> sprintf "raise the pin in global.json (%s)" file
            | None -> "install SDK 8.0.300 or later"
        Some (sprintf "the import needs MSBuild 17.10 or later (.NET SDK 8.0.300+); found %O (SDK %s): %s" msbuild sdk advice)
