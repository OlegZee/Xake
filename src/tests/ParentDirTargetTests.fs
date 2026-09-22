namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks

[<TestFixture>]
type ``Parent-dir target patterns``() =
    inherit XakeTestBase("parentdir/proj")

    // ProjectRoot for this fixture is "~testout~/parentdir/proj". Rules below target
    // "../out/..." which should resolve to "~testout~/parentdir/out/...".

    [<Test>]
    member x.``rule pattern starting with .. matches normalized target``() =

        let mutable needExecuteCount = 0

        do xake x.TestOptions {
            rules [
                "main" => action {
                    do! need ["../out/lib.txt"]
                }

                "../out/(name:*).txt" ..> action {
                    needExecuteCount <- needExecuteCount + 1
                    let! name = getRuleMatch "name"
                    Assert.AreEqual("lib", name)
                    do! writeText "hello from parent dir rule"
                }
            ]
        }

        Assert.AreEqual(1, needExecuteCount)

        let expectedPath =
            x.TestOptions.ProjectRoot |> Path.GetDirectoryName </> "out" </> "lib.txt" |> Path.GetFullPath

        Assert.IsTrue(File.Exists expectedPath, sprintf "Expected file to exist at '%s'" expectedPath)
        Assert.AreEqual("hello from parent dir rule", File.ReadAllText expectedPath)

    [<Test>]
    member x.``.. in the middle of a pattern still resolves``() =

        let mutable needExecuteCount = 0

        do xake x.TestOptions {
            rules [
                "main" => action {
                    do! need ["a/../mid/value.txt"]
                }

                "a/../mid/(name:*).txt" ..> action {
                    needExecuteCount <- needExecuteCount + 1
                    do! writeText "mid dir content"
                }
            ]
        }

        Assert.AreEqual(1, needExecuteCount)

        let expectedPath = x.TestOptions.ProjectRoot </> "mid" </> "value.txt" |> Path.GetFullPath
        Assert.IsTrue(File.Exists expectedPath, sprintf "Expected file to exist at '%s'" expectedPath)
