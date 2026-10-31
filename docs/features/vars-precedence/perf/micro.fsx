// Measurement A: the cost of SecretMask.apply over a 10,000-line log, one process per case.
//
//   PERF_CASE=1 dotnet fsi micro.fsx   (0 secrets)
//   PERF_CASE=2 dotnet fsi micro.fsx   (5 secrets, no line contains one)
//   PERF_CASE=3 dotnet fsi micro.fsx   (5 secrets, 1% of lines contain one)
//   PERF_CASE=4 dotnet fsi micro.fsx   (5 secrets, 10% of lines contain one)
//
// Needs the Release build: dotnet build src/core -c Release. Prints one result line.
#r "../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
#load "corpus.fsx"

open System
open System.Diagnostics
open Xake

let case = match Environment.GetEnvironmentVariable "PERF_CASE" with null | "" -> 1 | s -> int s
let pct = match case with 3 -> 1 | 4 -> 10 | _ -> 0
let runs = 20

let lines = Corpus.baseLines () |> Corpus.withSecrets pct

// SecretMask is process-global and append-only: register only in the cases that have secrets.
if case >= 2 then
    for s in Corpus.secrets do SecretMask.register s

let expectedMasked = Corpus.lineCount * pct / 100
let timeOnce () =
    let sw = Stopwatch.StartNew()
    let mutable total = 0L
    for l in lines do
        total <- total + int64 (SecretMask.apply l).Length
    sw.Stop()
    sw.Elapsed.TotalMilliseconds, total

let warm = timeOnce ()
let masked = lines |> Array.filter (fun l -> (SecretMask.apply l).Contains "***") |> Array.length
let leaked = lines |> Array.filter (fun l -> Corpus.secrets |> Array.exists (fun s -> (SecretMask.apply l).Contains s)) |> Array.length
if masked <> expectedMasked || leaked <> 0 then failwithf "check failed: masked=%d expected=%d leaked=%d" masked expectedMasked leaked

let samples = Array.init runs (fun _ -> fst (timeOnce ())) |> Array.sort
let pick (p: float) = samples.[int (Math.Ceiling (p * float runs)) - 1]   // nearest rank
let median = pick 0.5
let p95 = pick 0.95

printfn "CASE=%d secrets=%d pct=%d lines=%d masked_lines=%d leaked=%d warmup_ms=%.2f runs=%d median_ms=%.3f p95_ms=%.3f min_ms=%.3f ns_per_line_median=%.1f"
    case (if case >= 2 then Corpus.secrets.Length else 0) pct lines.Length masked leaked (fst warm) runs
    median p95 samples.[0] (median * 1e6 / float lines.Length)
