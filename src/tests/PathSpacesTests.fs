namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet

/// Paths with a space on the compile path: the SDK under `C:\Program Files\dotnet`, a
/// repository or a temp directory under one. .NET parses `ProcessStartInfo.Arguments` by the
/// Windows rules on every OS, so a symlinked SDK directory with a space reproduces the
/// Windows failure here. The fixture's own folder (`~testout~/path spaces`) has a space too.
[<TestFixture>]
type ``Paths with spaces``() =
    inherit XakeTestBase("path spaces")

    /// `<test folder>/sdk with space`, a symlink to the SDK directory `compilerPath` is under
    /// (`<dotnet>/sdk/<version>`, two levels up from `Roslyn/bincore/csc.dll`, one from
    /// `FSharp/fsc.dll`); the test is skipped where a symlink cannot be made.
    let linkSdk (sdkDir: string) =
        let link = Path.GetFullPath "sdk with space"
        if Directory.Exists link || File.Exists link then
            try Directory.Delete link with _ -> File.Delete link
        try Directory.CreateSymbolicLink (link, sdkDir) |> ignore
        with e -> Assert.Ignore (sprintf "cannot create a symlink here: %s" e.Message)
        link

    let underLink (sdkDir: string) (link: string) (path: string) =
        link + path.Substring sdkDir.Length

    // the arguments `CompilerRunner` hands `shell`: the compiler dll and the response file are
    // one argument each, whatever they contain
    [<Test>]
    member __.``the compiler and the response file are quoted on the command line``() =
        let _, args = CompilerRunner.commandLine (CompilerRunner.Dotnet "/sdk with space/FSharp/fsc.dll") [ "@/tmp/a b/x.rsp" ]
        Assert.That(args, Is.EqualTo [ "\"/sdk with space/FSharp/fsc.dll\""; "\"@/tmp/a b/x.rsp\"" ])
        let _, args = CompilerRunner.commandLine (CompilerRunner.Native @"C:\Program Files\csc.exe") [ "/noconfig"; @"@C:\Users\A B\x.rsp" ]
        Assert.That(args, Is.EqualTo [ "/noconfig"; "\"@C:\\Users\\A B\\x.rsp\"" ])

    // csc unquotes a whole quoted line and splits an unquoted one (CS2001); fsc takes each line
    // verbatim and reads a quoted switch as a file name (FS2020)
    [<Test>]
    member __.``response file lines are quoted for csc and verbatim for fsc``() =
        let line = "-r:C:\\Program Files\\dotnet\\packs\\ref\\System.Runtime.dll"
        Assert.That(CompilerRunner.responseLine CompilerRunner.Argv line, Is.EqualTo ("\"" + line + "\""))
        Assert.That(CompilerRunner.responseLine CompilerRunner.Verbatim line, Is.EqualTo line)
        Assert.That(CompilerRunner.responseLine CompilerRunner.Argv "--optimize+", Is.EqualTo "--optimize+")
        Assert.That(CompilerRunner.responseLine CompilerRunner.Verbatim "src dir/a.fs", Is.EqualTo "src dir/a.fs")

    // the SDK's fsc.dll through a directory with a space, a library in a directory with a
    // space referenced with `-r:` (the response-file line fsc rejected quoted), and the
    // repository folder of the fixture with a space
    [<Test; Category("Integration")>]
    member x.``Fsc.run compiles with the SDK and a reference under directories with a space``() =
        Directory.CreateDirectory "lib dir" |> ignore
        File.WriteAllText ("lib dir/l.fs", "module L\nlet x = 1\n")
        File.WriteAllText ("m.fs", "module M\nlet y = L.x + 1\n")
        for p in [ "lib dir/l.dll"; "out/m.dll" ] do
            if File.Exists p then File.Delete p
        let viaLink (f: Fsc) (link: string) (sdkDir: string) =
            let compiler = f.Dependencies.Compiler
            { f with Dependencies = { f.Dependencies with Compiler = { compiler with Path = underLink sdkDir link compiler.Path } } }
        do xake {x.TestOptions with FileLog="fsc-spaces.log"; ThrowOnError = true} {
            wantOverride (["fsc-spaces"])
            rules [
                "fsc-spaces" => recipe {
                    let! lib = fsc { src !!"lib dir/l.fs"; out (File.make "lib dir/l.dll"); resolve }
                    let sdkDir = Path.GetDirectoryName (Path.GetDirectoryName lib.Dependencies.Compiler.Path)
                    let link = linkSdk sdkDir
                    let lib = viaLink lib link sdkDir
                    Assert.That(lib.Dependencies.Compiler.Path, Does.Contain "sdk with space")
                    do! Fsc.run FscRunOptions.Default lib

                    let! m = fsc { src !!"m.fs"; ref !!"lib dir/l.dll"; out (File.make "out/m.dll"); resolve }
                    Assert.That(m.Args |> List.exists (fun a -> a.StartsWith "-r:" && a.Contains "lib dir"), Is.True, "references the library by its spaced path")
                    do! Fsc.run FscRunOptions.Default (viaLink m link sdkDir)
                }
            ]
        }
        Assert.That(File.Exists "lib dir/l.dll", Is.True, "fsc did not produce lib dir/l.dll")
        Assert.That(File.Exists "out/m.dll", Is.True, "fsc did not produce out/m.dll")

    // the SDK's csc.dll through a directory with a space
    [<Test; Category("Integration")>]
    member x.``Csc.run compiles with the SDK under a directory with a space``() =
        Directory.CreateDirectory "src dir" |> ignore
        File.WriteAllText ("src dir/c.cs", "public static class C { public static int X => 1; }")
        if File.Exists "out/c.dll" then File.Delete "out/c.dll"
        do xake {x.TestOptions with FileLog="csc-spaces.log"; ThrowOnError = true} {
            wantOverride (["csc-spaces"])
            rules [
                "csc-spaces" => recipe {
                    let! c = csc { src !!"src dir/c.cs"; out (File.make "out/c.dll"); resolve }
                    let compiler = c.Dependencies.Compiler
                    // <dotnet>/sdk/<version>/Roslyn/bincore/csc.dll
                    let sdkDir = compiler.Path |> Path.GetDirectoryName |> Path.GetDirectoryName |> Path.GetDirectoryName
                    let link = linkSdk sdkDir
                    let c = { c with Dependencies = { c.Dependencies with Compiler = { compiler with Path = underLink sdkDir link compiler.Path } } }
                    Assert.That(c.Dependencies.Compiler.Path, Does.Contain "sdk with space")
                    do! Csc.run RunOptions.Default c
                }
            ]
        }
        Assert.That(File.Exists "out/c.dll", Is.True, "csc did not produce out/c.dll")
