namespace Xake

open System
open System.IO
open System.Text
open System.Security.Cryptography

/// <summary>
/// Fingerprints of committed generated files (descriptions, locks): one text file, one line per
/// target, recording the target's hash, the hash of its inputs and the inputs. Generation stays an
/// explicit command; <c>warn</c> reports a stale or hand-edited target, <c>check</c> fails on one.
/// Reads only the fingerprint file and the files it names (no build database).
/// </summary>
/// <remarks>
/// Line format (tab-separated, LF, sorted by target, ordinal):
/// <c>target TAB targetSha256 TAB inputsSha256 TAB input1 TAB input2 ...</c>.
/// Paths are relative to the project root with '/' separators.
/// File hash: SHA-256 (lower-case hex) of the raw bytes with every CRLF pair replaced by LF.
/// Inputs hash: SHA-256 of the UTF-8 text "path TAB fileHash LF" per input, inputs sorted (ordinal);
/// a missing input contributes the hash "missing".
/// </remarks>
[<RequireQualifiedAccess>]
module Fingerprints =

    /// One fingerprint line.
    type Line = {
        Target: string
        TargetHash: string
        InputsHash: string
        Inputs: string list
    }

    /// Verification outcome of one line.
    type Status =
        | Fresh
        /// An input changed since the line was recorded; the target was not regenerated.
        | Stale
        /// The target itself differs from the recorded hash (edited by hand).
        | HandEdited
        /// Both: the inputs changed and the target was edited.
        | StaleAndHandEdited
        /// The target file is absent.
        | MissingTarget

    let private toHex (bytes: byte[]) =
        let sb = StringBuilder(bytes.Length * 2)
        for b in bytes do sb.Append(b.ToString("x2")) |> ignore
        sb.ToString()

    let private sha256 (bytes: byte[]) =
        use h = SHA256.Create()
        h.ComputeHash bytes |> toHex

    /// Replaces every CRLF pair by LF, leaving other bytes untouched.
    let normalizeCrLf (bytes: byte[]) =
        let out = ResizeArray<byte>(bytes.Length)
        for i in 0 .. bytes.Length - 1 do
            if not (bytes.[i] = 13uy && i + 1 < bytes.Length && bytes.[i + 1] = 10uy) then
                out.Add bytes.[i]
        out.ToArray()

    /// SHA-256 (hex) of the content with CRLF normalised to LF.
    let hashBytes (bytes: byte[]) = bytes |> normalizeCrLf |> sha256

    /// SHA-256 (hex) of a file with CRLF normalised to LF.
    let hashFile (path: string) = File.ReadAllBytes path |> hashBytes

    /// The hash a missing input contributes to the inputs hash.
    [<Literal>]
    let MissingHash = "missing"

    let private fullPath root (path: string) =
        Path.GetFullPath(if Path.IsPathRooted path then path else Path.Combine(root, path))

    /// Path relative to <paramref name="root"/> with '/' separators; a path outside the root
    /// stays absolute (with '/' separators).
    let relativePath (root: string) (path: string) =
        let full = fullPath root path
        let rootFull = (Path.GetFullPath root).TrimEnd('\\', '/') + string Path.DirectorySeparatorChar
        let cmp = if Env.isUnix then StringComparison.Ordinal else StringComparison.OrdinalIgnoreCase
        let rel = if full.StartsWith(rootFull, cmp) then full.Substring rootFull.Length else full
        rel.Replace('\\', '/')

    let private hashOrMissing root path =
        let p = fullPath root path
        if File.Exists p then hashFile p else MissingHash

    /// Normalised, de-duplicated, sorted (ordinal) input list.
    let normalizeInputs root (inputs: string seq) =
        inputs |> Seq.map (relativePath root) |> Seq.distinct |> Seq.sortWith (fun a b -> String.CompareOrdinal(a, b)) |> List.ofSeq

    /// Hash over the (already normalised) inputs: their paths and contents.
    let inputsHash root (inputs: string list) =
        let sb = StringBuilder()
        for i in inputs do sb.Append(i).Append('\t').Append(hashOrMissing root i).Append('\n') |> ignore
        Encoding.UTF8.GetBytes(sb.ToString()) |> sha256

    /// Computes the line of <paramref name="target"/> from the current files.
    let compute root (target: string) (inputs: string seq) =
        let inputs = normalizeInputs root inputs
        let target = relativePath root target
        { Target = target; TargetHash = hashOrMissing root target; InputsHash = inputsHash root inputs; Inputs = inputs }

    /// Parses the file content; blank lines are ignored.
    let parse (text: string) =
        text.Split([| '\n' |])
        |> Seq.map (fun l -> l.TrimEnd '\r')
        |> Seq.filter (String.IsNullOrWhiteSpace >> not)
        |> Seq.map (fun l ->
            match l.Split '\t' |> List.ofArray with
            | t :: th :: ih :: inputs -> { Target = t; TargetHash = th; InputsHash = ih; Inputs = inputs }
            | _ -> failwithf "Fingerprints: malformed line '%s'" l)
        |> List.ofSeq

    /// Formats lines: sorted by target (ordinal), LF endings, final newline.
    let format (lines: Line seq) =
        lines
        |> Seq.sortWith (fun a b -> String.CompareOrdinal(a.Target, b.Target))
        |> Seq.map (fun l -> String.Join("\t", l.Target :: l.TargetHash :: l.InputsHash :: l.Inputs) + "\n")
        |> String.concat ""

    /// Loads the fingerprint file; a missing file gives no lines.
    let load root file =
        let p = fullPath root file
        if File.Exists p then File.ReadAllText p |> parse else []

    /// Writes the lines; returns true if the file content changed (an unchanged file is not touched).
    let save root file (lines: Line seq) =
        let p = fullPath root file
        let text = format lines
        if File.Exists p && File.ReadAllText p = text then false
        else
            let dir = Path.GetDirectoryName p
            if not (String.IsNullOrEmpty dir) then Directory.CreateDirectory dir |> ignore
            File.WriteAllBytes(p, Encoding.UTF8.GetBytes text)
            true

    /// Sets the line of <paramref name="line"/>.Target, replacing a previous one.
    let upsert (line: Line) (lines: Line list) =
        line :: (lines |> List.filter (fun l -> l.Target <> line.Target))

    /// Records the current state of <paramref name="target"/> into <paramref name="file"/>;
    /// returns true if the file changed.
    let recordIn root file target inputs =
        let line = compute root target inputs
        load root file |> upsert line |> save root file

    /// Status of one line against the current files.
    let status root (line: Line) =
        if not (File.Exists(fullPath root line.Target)) then MissingTarget else
        let stale = inputsHash root line.Inputs <> line.InputsHash
        let edited = hashFile (fullPath root line.Target) <> line.TargetHash
        match stale, edited with
        | false, false -> Fresh
        | true, false -> Stale
        | false, true -> HandEdited
        | true, true -> StaleAndHandEdited

    /// Lines that are not fresh, with their status.
    let verify root file =
        load root file
        |> List.map (fun l -> l, status root l)
        |> List.filter (snd >> (<>) Fresh)

    /// Human-readable message for one problem line.
    let describe (line: Line, st: Status) =
        match st with
        | Fresh -> sprintf "%s is up to date" line.Target
        | Stale -> sprintf "%s is stale: its inputs changed since it was generated" line.Target
        | HandEdited -> sprintf "%s was edited by hand: it differs from the generated content" line.Target
        | StaleAndHandEdited -> sprintf "%s is stale and was edited by hand" line.Target
        | MissingTarget -> sprintf "%s is missing" line.Target

    /// Recipe: records the line of <paramref name="target"/> (paths relative to the project root).
    let record (file: string) (target: string) (inputs: string list) : Recipe<ExecContext, unit> =
        recipe {
            let! ctx = getCtx()
            let changed = recordIn ctx.Options.ProjectRoot file target inputs
            if changed then do! trace Info "Fingerprints: recorded %s in %s" target file
        }

    /// Recipe: logs one warning per stale, hand-edited or missing target and continues.
    let warn (file: string) : Recipe<ExecContext, unit> =
        recipe {
            let! ctx = getCtx()
            for problem in verify ctx.Options.ProjectRoot file do
                do ctx.Logger.Log Warning "%s" (describe problem)
        }

    /// Recipe: fails with a message listing every stale, hand-edited or missing target.
    let check (file: string) : Recipe<ExecContext, unit> =
        recipe {
            let! ctx = getCtx()
            match verify ctx.Options.ProjectRoot file with
            | [] -> do! trace Info "Fingerprints: %s is up to date" file
            | problems ->
                let lines = problems |> List.map describe
                failwithf "Fingerprints check failed (%s):\n  %s" file (String.Join("\n  ", lines))
        }
