namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks

[<TestFixture>]
type ``Testing Rm``() =
    inherit XakeTestBase("rm")

    [<Test>]
    member x.``Rm deletes single file``() =

        do xake x.TestOptions {
            rules [
                "main" => action {
                    do! need ["samplefile"]
                    File.Exists "samplefile" |> Assert.True
                    do! rm {file "samplefile"; verbose}
                }

                "samplefile" ..> writeText "hello world"
            ]
        }

        (File.Exists >> Assert.False) "samplefile"

    [<Test>]
    member x.``Rm deletes files by mask``() =

        do xake x.TestOptions {
            filelog "rm-mask.log" Diag
            rules [
                "main" => action {
                    do! need ["samplefile"; "samplefile1"]
                    (File.Exists >> Assert.True) "samplefile"
                    (File.Exists >> Assert.True) "samplefile1"

                    do! rm {file "samplefile*"}
                }

                "samplefile" ..> writeText "hello world"
                "samplefile1" ..> writeText "hello world1"
            ]
        }

        (File.Exists >> Assert.False) "samplefile"
        (File.Exists >> Assert.False) "samplefile1"

    [<Test>]
    member x.``Rm deletes dir``() =
        do xake x.TestOptions {
            rules [
                "main" => recipe {
                    do! need ["a/samplefile"; "a/b/samplefile1"]
                    File.Exists ("a" </> "b" </> "samplefile1") |> Assert.True

                    do! rm {dir "a"}
                }

                "a/samplefile" ..> writeText "hello world"
                "a/b/samplefile1" ..> writeText "hello world1"
            ]
        }

        (Directory.Exists >> Assert.False) "a"


    [<Test>]
    member x.``Rm deletes fileset``() =

        do xake x.TestOptions {
            rules [
                "main" => recipe {
                    do! need ["samplefile"; "samplefile1"]
                    do! rm {
                        files (fileset {
                            includes "samplefile*"
                        })
                    }
                }

                "samplefile*" ..> writeText "hello world"
            ]
        }

        (File.Exists >> Assert.False) "samplefile"
        (File.Exists >> Assert.False) "samplefile1"

    [<Test>]
    member x.``Rm deletes every dir of repeated dir ops``() =
        do xake {x.TestOptions with ThrowOnError = true} {
            rules [
                "main" => recipe {
                    do! need ["a/samplefile"; "b/samplefile1"]
                    do! rm {dir "a"; dir "b"}
                }

                "a/samplefile" ..> writeText "hello world"
                "b/samplefile1" ..> writeText "hello world1"
            ]
        }

        (Directory.Exists >> Assert.False) "a"
        (Directory.Exists >> Assert.False) "b"

    [<Test>]
    member x.``Rm applies dir, file and files ops together``() =
        do xake {x.TestOptions with ThrowOnError = true} {
            rules [
                "main" => recipe {
                    do! need ["a/samplefile"; "sample.tmp"; "sample.cache"; "keep.txt"]
                    do! rm {
                        dir "a"
                        file "*.tmp"
                        files (fileset { includes "*.cache" })
                    }
                }

                "a/samplefile" ..> writeText "hello world"
                "sample.tmp" ..> writeText "tmp"
                "sample.cache" ..> writeText "cache"
                "keep.txt" ..> writeText "keep"
            ]
        }

        (Directory.Exists >> Assert.False) "a"
        (File.Exists >> Assert.False) "sample.tmp"
        (File.Exists >> Assert.False) "sample.cache"
        (File.Exists >> Assert.True) "keep.txt"

    [<Test>]
    member x.``Rm file mask inside a dir removed earlier does not fail``() =
        do xake {x.TestOptions with ThrowOnError = true} {
            rules [
                "main" => recipe {
                    do! need ["a/samplefile"; "a/b/samplefile1"]
                    do! rm {
                        dir "a"
                        file "a/**/samplefile*"
                        files (fileset { includes "a/**/*" })
                    }
                }

                "a/samplefile" ..> writeText "hello world"
                "a/b/samplefile1" ..> writeText "hello world1"
            ]
        }

        (Directory.Exists >> Assert.False) "a"

    [<Test>]
    member x.``Rm takes a fileset entry``() =
        do xake {x.TestOptions with ThrowOnError = true} {
            rules [
                "main" => recipe {
                    do! need ["out/a.txt"; "out/b.log"; "keep.txt"]
                    do! rm { !!"out/*.*" }
                }

                "out/a.txt" ..> writeText "a"
                "out/b.log" ..> writeText "b"
                "keep.txt" ..> writeText "keep"
            ]
        }

        (File.Exists >> Assert.False) ("out" </> "a.txt")
        (File.Exists >> Assert.False) ("out" </> "b.log")
        (File.Exists >> Assert.True) "keep.txt"

    [<Test>]
    member x.``Rm fileset entry honors excludes``() =
        do xake {x.TestOptions with ThrowOnError = true} {
            rules [
                "main" => recipe {
                    do! need ["out/a.txt"; "out/keep.txt"]
                    do! rm { !!"out/*.*" -- "out/keep.txt" }
                }

                "out/a.txt" ..> writeText "a"
                "out/keep.txt" ..> writeText "keep"
            ]
        }

        (File.Exists >> Assert.False) ("out" </> "a.txt")
        (File.Exists >> Assert.True) ("out" </> "keep.txt")

    [<Test>]
    member x.``Rm takes several entries, strings and operations mixed``() =
        do xake {x.TestOptions with ThrowOnError = true} {
            rules [
                "main" => recipe {
                    do! need ["a/samplefile"; "out/a.txt"; "obj/x.o"; "sample.tmp"; "keep.txt"]
                    do! rm {
                        verbose
                        !!"out/*.*"
                        "*.tmp"
                        dir "a"
                        !!"obj/*.*"
                    }
                }

                "a/samplefile" ..> writeText "a"
                "out/a.txt" ..> writeText "a"
                "obj/x.o" ..> writeText "o"
                "sample.tmp" ..> writeText "tmp"
                "keep.txt" ..> writeText "keep"
            ]
        }

        (Directory.Exists >> Assert.False) "a"
        (File.Exists >> Assert.False) ("out" </> "a.txt")
        (File.Exists >> Assert.False) ("obj" </> "x.o")
        (File.Exists >> Assert.False) "sample.tmp"
        (File.Exists >> Assert.True) "keep.txt"

    [<Test>]
    member x.``Rm takes a plain path string``() =
        do xake {x.TestOptions with ThrowOnError = true} {
            rules [
                "main" => recipe {
                    do! need ["samplefile"; "samplefile1"]
                    do! del { "samplefile" }
                }

                "samplefile*" ..> writeText "hello world"
            ]
        }

        (File.Exists >> Assert.False) "samplefile"
        (File.Exists >> Assert.True) "samplefile1"

    [<Test>]
    member x.``Rm fileset entry matching nothing succeeds``() =
        do xake {x.TestOptions with ThrowOnError = true} {
            rules [
                "main" => recipe {
                    do! rm { !!"nothing-here/*.*"; verbose }
                }
            ]
        }

        (Directory.Exists >> Assert.False) "nothing-here"
