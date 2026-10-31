// Shared log corpus for the secret-masking measurements (micro.fsx and e2e.fsx). Not run on its own:
// `#load` it. Deterministic: fixed seeds, the five secrets come from secrets.env next to this file.
module Corpus

open System
open System.IO

let seed = 20261031
let lineCount = 10000

/// The five secret values, read from secrets.env (XAKE__TOKEN1..5 are exported from the same file).
let secrets : string[] =
    File.ReadAllLines(Path.Combine(__SOURCE_DIRECTORY__, "secrets.env"))
    |> Array.filter (fun l -> l.StartsWith "XAKE__TOKEN")
    |> Array.map (fun l -> l.Substring(l.IndexOf '=' + 1))

let private alnum = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"
let private words = [| "build"; "target"; "restore"; "compile"; "copy"; "link"; "check"; "emit"; "resolve"; "cache" |]

let private files =
    [| "src/core/Logging.fs"; "src/core/ExecCore.fs"; "src/core/Database.fs"; "src/core/Fileset.fs"
       "src/core/Types.fs"; "src/core/WorkerPool.fs"; "src/tests/LoggingTests.fs"; "src/core/VarEvaluation.fs" |]

let private messages =
    [| "warning FS0064: This construct causes code to be less generic than indicated by the type annotations."
       "warning FS3370: The use of '!' and ':=' operators is deprecated"
       "error FS0001: The type 'int' does not match the type 'string'"
       "warning FS1182: The value 'x' is unused"
       "info: incremental check: target is up to date, skipping recipe execution" |]

let private shortStatus =
    [| "Restoring packages..."; "Building project Xake.Core"; "Running tests"; "Copying output files"
       "Target 'main' finished"; "Cache hit: 42 files"; "Linking Xake.dll"; "Done." |]

let private pad (rnd: Random) (target: int) (text: string) =
    let sb = Text.StringBuilder(text)
    while sb.Length < target do
        sb.Append(' ').Append(words.[rnd.Next words.Length]).Append(' ') |> ignore
        if sb.Length < target then sb.Append(alnum.[rnd.Next alnum.Length]) |> ignore
    sb.ToString(0, target)

/// Compiler-like line, 80-160 chars.
let private compilerLine (rnd: Random) =
    let head = sprintf "%s(%d,%d): " files.[rnd.Next files.Length] (rnd.Next(1, 900)) (rnd.Next(1, 120))
    let text = head + messages.[rnd.Next messages.Length]
    let target = rnd.Next(80, 161)
    if text.Length >= target then text.Substring(0, target) else pad rnd target text

/// Short status line, 20-40 chars.
let private statusLine (rnd: Random) =
    let text = shortStatus.[rnd.Next shortStatus.Length]
    let target = rnd.Next(20, 41)
    if text.Length >= target then text.Substring(0, target) else pad rnd target text

/// Long dump line, about 400 chars.
let private longLine (rnd: Random) =
    let target = rnd.Next(380, 421)
    pad rnd target "dump:"

/// The base log: 60% compiler-like, 38% status, 2% long. No line contains a secret.
let baseLines () : string[] =
    let rnd = Random(seed)
    Array.init lineCount (fun _ ->
        let r = rnd.NextDouble()
        if r < 0.60 then compilerLine rnd
        elif r < 0.98 then statusLine rnd
        else longLine rnd)

/// Gives `pct` percent of the lines one secret each (secret k % 5 on the k-th chosen line), at a
/// seeded random position in the line. pct = 0 returns the lines unchanged.
let withSecrets (pct: int) (lines: string[]) : string[] =
    let count = lines.Length * pct / 100
    let rnd = Random(seed + 7)
    let order = Array.init lines.Length id
    for i in order.Length - 1 .. -1 .. 1 do   // Fisher-Yates
        let j = rnd.Next(i + 1)
        let t = order.[i]
        order.[i] <- order.[j]
        order.[j] <- t
    let result = Array.copy lines
    for k in 0 .. count - 1 do
        let i = order.[k]
        let s = secrets.[k % secrets.Length]
        result.[i] <- result.[i] + " token=" + s
    result
