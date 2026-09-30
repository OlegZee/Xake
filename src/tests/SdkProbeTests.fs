namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet

/// The SDK probe honours `global.json`: the compiler comes from the SDK the `dotnet` host
/// selects in the project root (`dotnet --version` there), not simply the newest one installed.
/// Each test gets a project root of its own, since the probe is cached per root.
[<TestFixture>]
type ``SDK probe``() =
    inherit XakeTestBase("sdkprobe")

    /// The released SDKs installed under the probe's dotnet root, oldest first.
    let installedSdks () =
        match DotNetFwk.dotnetRoot () with
        | None -> []
        | Some root ->
            Directory.GetDirectories (root </> "sdk")
            |> Array.map Path.GetFileName
            |> Array.choose (fun name ->
                match System.Version.TryParse name with
                | true, v -> Some (v, name)
                | _ -> None)
            |> Array.sortBy fst
            |> Array.map snd
            |> List.ofArray

    /// A fresh project root holding `globalJson` (none when it is `None`).
    let projectRoot (parent: string) (name: string) (globalJson: string option) =
        let dir = Path.GetFullPath (parent </> name)
        if Directory.Exists dir then Directory.Delete (dir, true)
        Directory.CreateDirectory dir |> ignore
        globalJson |> Option.iter (fun text -> File.WriteAllText (dir </> "global.json", text))
        File.WriteAllText (dir </> "probe.cs", "public class Probe {}\n")
        dir

    let pin version =
        sprintf """{ "sdk": { "version": "%s", "rollForward": "disable" } }""" version

    /// `csc { ...; resolve }` run from `root`: the compiler path it records, slashes forward.
    let resolveCompiler (options: ExecOptions) (root: string) =
        let resolved = ref ""
        do xake {options with ProjectRoot = root; FileLog = root </> "probe.log"; FileLogLevel = Verbosity.Diag; ThrowOnError = true} {
            wantOverride (["probe"])
            rules [
                "probe" => recipe {
                    let! c = csc {
                        targetfwk "netstandard2.0"
                        out (File.make "probe.dll")
                        src !!"probe.cs"
                        resolve
                    }
                    resolved.Value <- c.Dependencies.Compiler.Path
                }
            ]
        }
        resolved.Value.Replace('\\', '/')

    [<Test; Category("Integration")>]
    member x.``takes the SDK global.json pins``() =
        match installedSdks () with
        | [] -> Assert.Ignore "no .NET SDK found"
        | oldest :: _ ->
            let root = projectRoot "." "pinned" (Some (pin oldest))
            let path = resolveCompiler x.TestOptions root
            Assert.That(path, Does.Contain (sprintf "sdk/%s/" oldest))

    [<Test; Category("Integration")>]
    member x.``a pin below the newest SDK is not the newest``() =
        match installedSdks () with
        | [] | [_] -> Assert.Ignore "only one .NET SDK is installed: nothing below the newest to pin"
        | oldest :: _ as all ->
            let newest = List.last all
            let root = projectRoot "." "pinned-older" (Some (pin oldest))
            let path = resolveCompiler x.TestOptions root
            Assert.That(path, Does.Contain (sprintf "sdk/%s/" oldest))
            Assert.That(path, Does.Not.Contain (sprintf "sdk/%s/" newest))

    [<Test; Category("Integration")>]
    member x.``a pin to an SDK that is not installed falls back to the newest with a warning``() =
        match installedSdks () with
        | [] -> Assert.Ignore "no .NET SDK found"
        | all ->
            let newest = List.last all
            let root = projectRoot "." "pinned-missing" (Some (pin "1.0.100"))
            let path = resolveCompiler x.TestOptions root
            Assert.That(path, Does.Contain (sprintf "sdk/%s/" newest))
            let log = File.ReadAllText (root </> "probe.log")
            Assert.That(log, Does.Contain "asks for SDK 1.0.100", "the warning names the pinned version")

    [<Test; Category("Integration")>]
    member x.``without a global.json the newest SDK is taken``() =
        match installedSdks () with
        | [] -> Assert.Ignore "no .NET SDK found"
        | all ->
            let parent = Path.GetTempPath() </> ("xake-sdkprobe-" + string (System.Diagnostics.Process.GetCurrentProcess().Id))
            let root = projectRoot parent "noglobaljson" None
            let rec globalJsonAbove (dir: DirectoryInfo) =
                if isNull dir then None
                elif File.Exists (dir.FullName </> "global.json") then Some (dir.FullName </> "global.json")
                else globalJsonAbove dir.Parent
            match globalJsonAbove (DirectoryInfo root) with
            | Some file -> Assert.Ignore (sprintf "'%s' applies to the temp directory" file)
            | None ->
                try
                    let path = resolveCompiler x.TestOptions root
                    Assert.That(path, Does.Contain (sprintf "sdk/%s/" (List.last all)))
                finally
                    try Directory.Delete (parent, true) with _ -> ()
