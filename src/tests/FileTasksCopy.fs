namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks

[<TestFixture>]
type ``Testing Copy task``() =
    inherit XakeTestBase("cp")

    let assertTrue: bool -> unit = Assert.True

    [<Test>]
    member x.``copies single file``() =

        if Directory.Exists "cptgt" then
            Directory.Delete ("cptgt", true)

        do xake x.TestOptions {
            rules [
                "main" => recipe {
                    do! need ["samplefile"]
                    do! Cp {CpArgs.Default with sources = [CpSource.File "samplefile"]; todir = "cptgt"}
                }

                "samplefile" ..> writeText "hello world"
            ]
        }

        assertTrue <| File.Exists ("cptgt" </> "samplefile")

    [<Test>]
    member x.``copies folder flatten``() =
        ["cptgt"; "cpin"] |> List.iter (fun d -> if Directory.Exists d then Directory.Delete (d, true))

        do xake x.TestOptions {
            rules [
                "main" => action {
                    do! need ["cpin/samplefile"]
                    do! Cp {CpArgs.Default with sources = [CpSource.Dir "cpin"]; todir = "cptgt"; flatten = true}
                }

                "cpin/samplefile" ..> writeText "hello world"
            ]
        }

        (File.Exists >> Assert.True) ("cptgt" </> "samplefile")

    [<Test>]
    member x.``copies folder no flatten``() =
        ["cptgt"; "cpin"] |> List.iter (fun d -> if Directory.Exists d then Directory.Delete (d, true))

        do xake x.TestOptions {
            rules [
                "main" => action {
                    do! need ["cpin/a/samplefile"]
                    do! Cp {CpArgs.Default with sources = [CpSource.Dir "cpin"]; todir = "cptgt"; flatten = false}
                }

                "cpin/a/samplefile" ..> writeText "hello world"
            ]
        }

        assertTrue <| File.Exists ("cptgt" </> "cpin" </> "a" </> "samplefile")

    [<Test>]
    member x.``directory contents mask is built with a forward slash``() =
        Assert.That (Xake.Tasks.CpImpl.impl.dirContentsMask "cpin", Is.EqualTo "cpin/**/*.*")
        Assert.That (Xake.Tasks.CpImpl.impl.dirContentsMask "cpin/sub/", Is.EqualTo "cpin/sub/**/*.*")
        Assert.That (Xake.Tasks.CpImpl.impl.dirContentsMask "cpin\\", Is.EqualTo "cpin/**/*.*")

    [<Test>]
    member x.``copies fileset NO flatten``() =
        ["cptgt"; "cpin"] |> List.iter (fun d -> if Directory.Exists d then Directory.Delete (d, true))

        do xake x.TestOptions {
            rules [
                "main" => action {
                    do! need ["cpin/a/samplefile"]
                    do! Cp {
                      CpArgs.Default with
                        sources = [CpSource.Files (fileset {basedir "cpin"; includes "**/*"})]
                        todir = "cptgt"
                        flatten = false
                        }
                }

                "cpin/a/samplefile" ..> writeText "hello world"
            ]
        }

        assertTrue <| File.Exists ("cptgt" </> "a" </> "samplefile")

    [<Test>]
    member x.``copies fileset flatten``() =
        ["cptgt"; "cpin"] |> List.iter (fun d -> if Directory.Exists d then Directory.Delete (d, true))

        do xake x.TestOptions {
            rules [
                "main" => action {
                    do! need ["cpin/a/samplefile"]
                    do! cp {files !!"cpin/**/*"; todir "cptgt"; flatten}
                }

                "cpin/a/samplefile" ..> writeText "hello world"
            ]
        }

        assertTrue <| File.Exists ("cptgt" </> "samplefile")

    [<Test>]
    member x.``copies every dir of repeated dir ops``() =
        ["cptgt"; "cpin"; "cpin2"] |> List.iter (fun d -> if Directory.Exists d then Directory.Delete (d, true))

        do xake {x.TestOptions with ThrowOnError = true} {
            rules [
                "main" => recipe {
                    do! need ["cpin/a/samplefile"; "cpin2/b/samplefile1"]
                    do! cp {dir "cpin"; dir "cpin2"; todir "cptgt"}
                }

                "cpin/a/samplefile" ..> writeText "hello world"
                "cpin2/b/samplefile1" ..> writeText "hello world1"
            ]
        }

        assertTrue <| File.Exists ("cptgt" </> "cpin" </> "a" </> "samplefile")
        assertTrue <| File.Exists ("cptgt" </> "cpin2" </> "b" </> "samplefile1")

    [<Test>]
    member x.``copies dir, file and files ops together``() =
        ["cptgt"; "cpin"; "cpfile"; "cpset"] |> List.iter (fun d -> if Directory.Exists d then Directory.Delete (d, true))

        do xake {x.TestOptions with ThrowOnError = true} {
            rules [
                "main" => recipe {
                    do! need ["cpin/a/samplefile"; "cpfile/x/single.txt"; "cpset/s/set.txt"]
                    do! cp {
                        dir "cpin"
                        file "cpfile/x/single.txt"
                        files (fileset {basedir "cpset"; includes "**/*"})
                        todir "cptgt"
                    }
                }

                "cpin/a/samplefile" ..> writeText "hello world"
                "cpfile/x/single.txt" ..> writeText "single"
                "cpset/s/set.txt" ..> writeText "set"
            ]
        }

        assertTrue <| File.Exists ("cptgt" </> "cpin" </> "a" </> "samplefile")
        assertTrue <| File.Exists ("cptgt" </> "single.txt")
        assertTrue <| File.Exists ("cptgt" </> "s" </> "set.txt")
