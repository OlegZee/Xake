namespace Xake.Hermetic.Dotnet

open System
open System.Text.RegularExpressions

open Xake
open Xake.Dotnet

/// What an import's msbuild and `dotnet restore` runs need beyond the core's classifier
/// (`Tool.diagnosticLevel`, `Tool.DiagnosticLog`, `Tool.failOnExitCodeWith`): a warning logged
/// once per engine run rather than once per run (an import runs msbuild dozens of times, each
/// printing the same NETSDK/NU/MSB warnings), and the MSBuild version an import needs
/// (`-getResultOutputFile` is MSBuild 17.10, .NET SDK 8.0.300).
module internal MsbuildLog =

    /// The warnings already logged in one engine run, keyed by the engine.
    let private warningSets =
        System.Runtime.CompilerServices.ConditionalWeakTable<obj, Collections.Concurrent.ConcurrentDictionary<string, unit>> ()

    /// The `[entry project]` suffix msbuild appends: the same warning from two entries is one.
    let private entrySuffix = Regex (@"\s+\[[^\[\]]+\]\s*$", RegexOptions.CultureInvariant)

    /// The classifier of one tool run of engine `engine`: the core's (`Tool.diagnosticLevel`,
    /// `def` for a line that is no diagnostic), errors kept for the failure message, and a
    /// warning (`Info`) logged only the first time this engine run sees it (`Never` after).
    let diagnostics (engine: obj) (def: Level) : Tool.DiagnosticLog =
        let seen = warningSets.GetValue (engine, fun _ -> Collections.Concurrent.ConcurrentDictionary<string, unit> (StringComparer.Ordinal))
        Tool.DiagnosticLog (fun line ->
            match Tool.diagnosticLevel def line with
            | Level.Info when def <> Level.Info ->
                if seen.TryAdd (entrySuffix.Replace (line.Trim (), ""), ()) then Level.Info else Level.Never
            | level -> level)

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
        Some (sprintf "the import needs MSBuild 17.10 or later (.NET SDK 8.0.300+, or any 9.x/10.x SDK); found %O (SDK %s): %s" msbuild sdk advice)
