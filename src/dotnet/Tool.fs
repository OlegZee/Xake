namespace Xake.Dotnet

open Xake
open Xake.Tasks

/// Helpers shared by every task that runs an external tool: how its output lines are
/// classified for the log, and what a non-zero exit code means. Public so that packages built
/// on `Xake.Dotnet` can run their own tools the same way the compiler tasks do.
module Tool =

    /// <summary>
    /// Quotes one argument for a command line by the Windows argv rules, which .NET applies to
    /// <c>ProcessStartInfo.Arguments</c> on every OS and csc applies to its response file.
    /// </summary>
    /// <remarks>
    /// Quoted only when it contains a space or a <c>"</c>; an inner <c>"</c> is escaped, and a
    /// run of backslashes before an inner or the closing quote is doubled
    /// (<c>C:\Program Files\dotnet\</c> becomes <c>"C:\Program Files\dotnet\\"</c>). An empty
    /// argument stays empty. Not for fsc's response file: fsc does not unquote a switch line
    /// (FS2020), it takes each line verbatim.
    /// </remarks>
    let quoteArgument (str: string) =
        let escape c s =
            match c,s with
            | '"',  (_,    str) -> (true,  '\\' :: '\"' ::    str)
            | '\\', (true, str) -> (true,  '\\' :: '\\' :: str)
            | '\\', (false,str) -> (false, '\\' :: str)
            | c,    (_,    str) -> (false, c :: str)

        if str |> String.exists (fun c -> c = '"' || c = ' ') then
            let ca = str.ToCharArray()
            let res = Array.foldBack escape ca (true,['"'])
            "\"" + System.String(res |> snd |> List.toArray)
        else
            str

    /// msbuild's diagnostic without a source position: `<origin> : error CODE: text`, where the
    /// origin is a project file, a tool (`CSC`, `MSBUILD`) or anything else msbuild names.
    let private originShape =
        System.Text.RegularExpressions.Regex (
            @"^\s*(?<origin>\S.*?) : (?<kind>error|warning) (?<code>[A-Za-z]+[0-9]+)\s*:",
            System.Text.RegularExpressions.RegexOptions.Compiled)

    /// The `[entry project]` msbuild appends to a diagnostic of a multi-project run.
    let private entrySuffix =
        System.Text.RegularExpressions.Regex (@"\s+\[[^\[\]]+\]\s*$", System.Text.RegularExpressions.RegexOptions.Compiled)

    /// A NuGet diagnostic of `dotnet restore` printed without an origin: `warning NU1603: ...`.
    let private isNuGetWarning (text: string) =
        System.Text.RegularExpressions.Regex.IsMatch (text, @"^\s*warning NU[0-9]+\s*:")

    /// <summary>
    /// Classifies a line of tool output by log level.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Diagnostics tied to a source position read "file.cs(1,1): error CS0103: ...", while
    /// whole-compilation ones read "error FS0084: ..." with no position at all. Both have to be
    /// recognized, otherwise a failing compile reports nothing above the default verbosity.
    /// </para>
    /// <para>
    /// msbuild and NuGet print a third shape, "&lt;origin&gt; : error CODE: text [entry]", the
    /// origin being a project file or a tool ("MSBUILD : error MSB1001", "CSC : error CS2001",
    /// "/p/a.csproj : error NU1101"). Its errors are <c>Error</c>, its warnings <c>Info</c>: a
    /// large solution prints the same NETSDK/NU/MSB warnings for every project and framework,
    /// so they belong in a Chatty file log, not on the console. A NuGet warning with no origin
    /// ("warning NU1603: ...") is <c>Info</c> too. Positioned and compiler warnings stay
    /// <c>Warning</c>.
    /// </para>
    /// </remarks>
    let diagnosticLevel defaultLevel (text:string) :Level =
        let m = originShape.Match text
        let hasDiagnostic kind =
            text.Contains ("): " + kind + " ") || text.TrimStart().StartsWith (kind + " ")
        if m.Success then
            if m.Groups.["kind"].Value = "error" then Level.Error else Level.Info
        else if isNuGetWarning text then Level.Info
        else if hasDiagnostic "warning" then Level.Warning
        else if hasDiagnostic "error" then Level.Error
        else defaultLevel

    /// Whether a line is a warning diagnostic of any shape `diagnosticLevel` knows.
    let internal isWarningLine (text: string) =
        let m = originShape.Match text
        if m.Success then m.Groups.["kind"].Value = "warning"
        else text.Contains "): warning " || text.TrimStart().StartsWith "warning "

    /// An error line as a failure message repeats it: trimmed, and for msbuild's origin shape
    /// without the `[entry]` suffix (the origin already names the project).
    let internal errorKey (text: string) =
        let line = text.Trim ()
        if originShape.IsMatch line then entrySuffix.Replace (line, "") else line

    /// <summary>
    /// The error part of a failure message: the first <paramref name="count"/> distinct error
    /// lines, one per line, then "+N more, see the log". Empty when there is no error line.
    /// </summary>
    /// <param name="count">How many errors to repeat</param>
    /// <param name="errors">The error lines a tool printed, in order</param>
    let errorSummary count (errors: string seq) =
        let distinct = errors |> Seq.map errorKey |> Seq.distinct |> List.ofSeq
        match distinct with
        | [] -> ""
        | _ ->
            let shown = distinct |> List.truncate count
            let more = distinct.Length - shown.Length
            (shown |> List.map (fun e -> "\n  " + e) |> String.concat "")
            + (if more > 0 then sprintf "\n  +%d more, see the log" more else "")

    /// <summary>
    /// The output classifier of one tool run: the level of each line by a classifier
    /// (<see cref="diagnosticLevel"/> by default), each distinct warning logged once (a repeat
    /// is <c>Never</c>), and the distinct error lines kept for the failure message.
    /// </summary>
    /// <remarks>
    /// One instance per run, shared by its stdout and stderr; thread-safe.
    /// </remarks>
    type DiagnosticLog (classify: string -> Level) =
        let gate = obj ()
        let warnings = System.Collections.Generic.HashSet<string> ()
        let errorSet = System.Collections.Generic.HashSet<string> ()
        let errors = System.Collections.Generic.List<string> ()

        /// A run classified by <see cref="diagnosticLevel"/> with this default level.
        new (defaultLevel: Level) = DiagnosticLog (diagnosticLevel defaultLevel)

        /// The level to log a line at; <c>Never</c> for a warning already logged in this run.
        member _.Level (line: string) : Level =
            let level = classify line
            lock gate (fun () ->
                if level = Level.Error then
                    let key = errorKey line
                    if errorSet.Add key then errors.Add key
                    level
                elif isWarningLine line && not (warnings.Add (line.Trim ())) then Level.Never
                else level)

        /// The distinct error lines seen so far, in order, as <see cref="errorKey"/> shapes them.
        member _.Errors : string list = lock gate (fun () -> List.ofSeq errors)

        /// <see cref="errorSummary"/> of the first 5 errors: empty, or lines starting with a newline.
        member this.Summary = errorSummary 5 this.Errors

    /// <summary>
    /// The exit-code epilogue shared by the compiler tasks: reports a non-zero exit code and
    /// fails the build when the task is configured to.
    /// </summary>
    /// <param name="failOnError">Whether a non-zero exit code has to fail the build</param>
    /// <param name="name">The target being built, for the diagnostic message</param>
    /// <param name="exitCode">The tool's exit code</param>
    let failOnExitCode failOnError (name: string) exitCode = recipe {
        if exitCode <> 0 then
            do! trace Error "('%s') failed with exit code '%i'" name exitCode
            if failOnError then failwithf "Exiting due to FailOnError set on '%s'" name
    }

    /// <summary>
    /// <see cref="failOnExitCode"/> whose failure repeats the first errors the run printed
    /// (<see cref="DiagnosticLog.Summary"/>), so the message says what failed, not only the code.
    /// </summary>
    /// <param name="failOnError">Whether a non-zero exit code has to fail the build</param>
    /// <param name="name">The target being built, for the diagnostic message</param>
    /// <param name="diagnostics">The classifier the run's output went through</param>
    /// <param name="exitCode">The tool's exit code</param>
    let failOnExitCodeWith failOnError (name: string) (diagnostics: DiagnosticLog) exitCode = recipe {
        if exitCode <> 0 then
            do! trace Error "('%s') failed with exit code '%i'" name exitCode
            if failOnError then failwithf "Exiting due to FailOnError set on '%s'%s" name diagnostics.Summary
    }
