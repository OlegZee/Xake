namespace Tests

open System.IO
open System.Text.RegularExpressions
open NUnit.Framework

/// Compile-time checks of typed targets (`Xake.Typed`): the cases of `fixtures/typed-neg/cases.fsx`
/// are type-checked by `dotnet fsi --typecheck-only` against the Xake.dll under test, one script
/// per case, and every case must give exactly its error code.
[<TestFixture; Category("Integration"); Category("TypeCheck")>]
type ``Typed targets: compile-time errors``() =

    let xakeDll = typeof<Xake.Typed.Matched>.Assembly.Location
    let workDir = Path.Combine(Path.GetTempPath(), "xake-typecheck-" + string (System.Diagnostics.Process.GetCurrentProcess().Id))
    let errorRx = Regex(@"\((\d+),\d+\): error (FS\d+)")

    /// Type-checks a script; returns (line, code) of every error and the whole output.
    let typecheck (name: string) (script: string) =
        Directory.CreateDirectory workDir |> ignore
        let file = Path.Combine(workDir, name + ".fsx")
        File.WriteAllText(file, script)
        let dotnet = match System.Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" with null | "" -> "dotnet" | p -> p
        let psi = System.Diagnostics.ProcessStartInfo(dotnet, sprintf "fsi --typecheck-only --nologo \"%s\"" file)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.WorkingDirectory <- workDir
        use p = System.Diagnostics.Process.Start psi
        let err = p.StandardError.ReadToEndAsync()
        let out = p.StandardOutput.ReadToEnd()
        if not (p.WaitForExit 180000) then
            p.Kill()
            Assert.Fail "dotnet fsi --typecheck-only timed out"
        let text = out + err.Result
        let errors = [ for m in errorRx.Matches text -> int m.Groups.[1].Value, m.Groups.[2].Value ] |> List.distinct
        errors, text

    let referenceXake (line: string) =
        if line.StartsWith "#r " then sprintf "#r @\"%s\"" xakeDll else line

    [<OneTimeTearDown>]
    member _.Cleanup() = try Directory.Delete(workDir, true) with _ -> ()

    [<Test>]
    member _.``every case gives its error``() =
        let lines = File.ReadAllLines(Path.Combine(__SOURCE_DIRECTORY__, "fixtures", "typed-neg", "cases.fsx")) |> List.ofArray
        let header = lines |> List.takeWhile (fun l -> not (l.StartsWith "//#case ")) |> List.map referenceXake
        let cases =
            lines |> List.skip header.Length
            |> List.fold (fun acc l ->
                if l.StartsWith "//#case " then
                    match l.Substring(8).Split([| ' ' |], System.StringSplitOptions.RemoveEmptyEntries) with
                    | [| name |] -> (name, None, []) :: acc
                    | [| name; code |] -> (name, Some code, []) :: acc
                    | _ -> failwithf "bad case line: %s" l
                else
                    match acc with
                    | (n, c, body) :: rest -> (n, c, l :: body) :: rest
                    | [] -> acc) []
            |> List.rev |> List.map (fun (n, c, body) -> n, c, List.rev body)
        Assert.That(cases, Is.Not.Empty)
        // fsi stops at the first error of a script, so every case is a script of its own; they run
        // in parallel, a few at a time
        let results =
            cases
            |> List.map (fun (name, expected, body) -> async {
                let errors, output = typecheck name (String.concat "\n" (header @ body))
                let codes = errors |> List.map snd |> List.distinct
                return
                    match expected, codes with
                    | None, [] -> None
                    | Some code, [ c ] when c = code -> None
                    | _ -> Some (sprintf "%s: expected %s, got %A\n%s" name (defaultArg expected "no error") codes output) })
            |> fun jobs -> Async.Parallel(jobs, max 2 (System.Environment.ProcessorCount / 2))
            |> Async.RunSynchronously
        let problems = results |> Array.choose id
        Assert.That(problems, Is.Empty, String.concat "\n" problems)

    [<Test>]
    member _.``the typed-targets sample type-checks``() =
        let sample = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "samples", "typed-targets.fsx")
        let script = File.ReadAllLines sample |> Array.map referenceXake |> String.concat "\n"
        let errors, output = typecheck "sample" script
        Assert.That(errors, Is.Empty, output)

    [<Test>]
    member _.``the snippets of docs/typed-targets.md type-check``() =
        // the blocks marked `<!-- snippet -->` form one script, in the order of the page
        let doc = File.ReadAllLines(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "docs", "typed-targets.md"))
        let snippets =
            [ for i in 0 .. doc.Length - 2 do
                if doc.[i].Trim() = "<!-- snippet -->" && doc.[i + 1].StartsWith "```fsharp" then
                    yield! doc |> Array.skip (i + 2) |> Array.takeWhile (fun l -> not (l.StartsWith "```")) ]
        Assert.That(snippets, Is.Not.Empty)
        let script = String.concat "\n" (sprintf "#r @\"%s\"" xakeDll :: snippets)
        let errors, output = typecheck "docs-typed-targets" script
        Assert.That(errors, Is.Empty, output)
