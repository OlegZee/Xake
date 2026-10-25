open System
open System.IO
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text

let checker = FSharpChecker.Create(keepAssemblyContents = false)

let check (path: string) =
  let src = File.ReadAllText path
  let opts, diags =
    checker.GetProjectOptionsFromScript(path, SourceText.ofString src, assumeDotNetFramework = false, useSdkRefs = true, useFsiAuxLib = true)
    |> Async.RunSynchronously
  let _, res = checker.ParseAndCheckFileInProject(path, 0, SourceText.ofString src, opts) |> Async.RunSynchronously
  match res with
  | FSharpCheckFileAnswer.Aborted -> printfn "== %s: ABORTED" path
  | FSharpCheckFileAnswer.Succeeded r ->
    let errs = r.Diagnostics |> Array.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
    printfn "== %s: %d errors (+%d script-option diags)" path errs.Length diags.Length
    for d in errs |> Array.truncate 4 do printfn "   %s line %d: %s" (d.ErrorNumberText) d.StartLine (d.Message.Substring(0, min 260 d.Message.Length))

[<EntryPoint>]
let main argv =
  for p in argv do
    try check (Path.GetFullPath p) with e -> printfn "EXN %s" (e.ToString())
  0
