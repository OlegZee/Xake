namespace Tests

open NUnit.Framework

open Xake

/// Rules written directly in the script body, without the `rules [ ... ]` wrapper.
[<TestFixture>]
type ``Flat script body tests``() =
    inherit XakeTestBase("flat")

    [<Test>]
    member x.``rules may be written flat in the script body``() =

        let executed = ref []

        do xake x.TestOptions {
            "main" <== ["one"; "two"]

            "one" => action { executed := "one" :: !executed }
            "two" => action { executed := "two" :: !executed }
        }

        Assert.IsTrue(!executed |> List.exists ((=) "one"))
        Assert.IsTrue(!executed |> List.exists ((=) "two"))

    [<Test>]
    member x.``flat rules mix with the rules operation``() =

        let executed = ref []

        do xake x.TestOptions {
            rules [
                "main" <== ["blocked"; "flat"]
                "blocked" => action { executed := "blocked" :: !executed }
            ]

            "flat" => action { executed := "flat" :: !executed }
        }

        Assert.IsTrue(!executed |> List.exists ((=) "blocked"))
        Assert.IsTrue(!executed |> List.exists ((=) "flat"))

    [<Test>]
    member x.``a for loop in the script body generates rules``() =

        let executed = ref []

        do xake {x.TestOptions with Targets = ["all"]} {
            "all" <== [for i in 1..3 -> sprintf "gen%i" i]

            for i in 1..3 do
                sprintf "gen%i" i => action { executed := i :: !executed }
        }

        Assert.AreEqual([1; 2; 3], !executed |> List.sort)

    [<Test>]
    member x.``yield! splices a computed rule list``() =

        let executed = ref []

        do xake {x.TestOptions with Targets = ["all"]} {
            "all" <== ["y1"; "y2"]

            yield! [
                for i in 1..2 ->
                    sprintf "y%i" i => action { executed := i :: !executed }
            ]
        }

        Assert.AreEqual([1; 2], !executed |> List.sort)

    [<Test>]
    member x.``a conditional adds a rule``() =

        let executed = ref []
        let enabled = true

        do xake x.TestOptions {
            "main" <== ["maybe"]

            if enabled then
                "maybe" => action { executed := "maybe" :: !executed }
        }

        Assert.AreEqual(["maybe"], !executed)

    [<Test>]
    member x.``the last matching flat rule wins, as with the rules operation``() =

        let winner = ref ""

        do xake x.TestOptions {
            "main" => action { winner := "first" }
            "main" => action { winner := "second" }
        }

        Assert.AreEqual("second", !winner)

    [<Test>]
    member x.``a flat rule overrides one declared in an earlier rules block``() =

        let winner = ref ""

        do xake x.TestOptions {
            rules [ "main" => action { winner := "block" } ]
            "main" => action { winner := "flat" }
        }

        Assert.AreEqual("flat", !winner)

    [<Test>]
    member x.``settings and flat rules coexist``() =

        let executed = ref false

        do xake {x.TestOptions with Targets = []} {
            want ["settled"]
            filelog "flat-settings.log" Silent

            "settled" => action { executed := true }
        }

        Assert.IsTrue(!executed)
