module ``Progress estimator``

open NUnit.Framework
open Xake.DomainTypes
open Xake.Progress.Estimate

type TaskDeps = string list
type Task = | Task of string * int<ms> * TaskDeps

let internal estimate threadCount completed_tasks tasks goals =

    let getTaskName (Task (name,_,_)) = name

    let tasks_map = completed_tasks |> List.map (fun t -> (t, 0<ms>)) |> Map.ofList
    let machine_state = {Cpu = BusyUntil 0<ms> |> List.replicate threadCount; Tasks = tasks_map}
    let taskMap = tasks |> List.map (fun task -> getTaskName task, task) |> Map.ofList

    let taskByName name = Map.find name taskMap
    let getDurationDeps (Task (_,duration,deps)) = duration,deps |> List.map taskByName
    let _,endTime = execMany machine_state getDurationDeps (goals |> List.map taskByName)
    in
    endTime

[<TestCase(1, ExpectedResult = 8<ms>)>]
[<TestCase(6, ExpectedResult = 8<ms>)>]
let Test1(threads) =

    let tasks1 =
        [
            Task ("build", 1<ms>, ["link"])
            Task ("link", 2<ms>, ["compile"])
            Task ("compile", 5<ms>, [])
        ]

    estimate threads [] tasks1 ["build"]       

[<TestCase(1, ExpectedResult = 12<ms>)>]
[<TestCase(2, ExpectedResult = 10<ms>)>]
let TestPara(threads) =

    let tasks1 =
        [
            Task ("build", 1<ms>, ["link1"; "link2"])
            Task ("link1", 2<ms>, ["compile"])
            Task ("link2", 2<ms>, ["compile"])
            Task ("compile", 7<ms>, [])
        ]

    estimate threads [] tasks1 ["build"]       

[<TestCase(6, ExpectedResult = 11<ms>)>]
[<TestCase(1, ExpectedResult = 21<ms>)>]
let ComplexCase(threads) =
    let tasks1 =
        [
        Task ("build", 1<ms>, ["compile"])
        Task ("compile", 5<ms>,
            [
                "version.h"
                "commonheader.h"
                "resources"
                "resources-ru"
            ])
        Task ("version.h", 4<ms>, [])
        Task ("commonheader.h", 4<ms>, [])
        Task ("resources", 2<ms>, ["strings"])
        Task ("resources-ru", 3<ms>, ["strings"])
        Task ("strings", 2<ms>, [])
        ]
    estimate threads [] tasks1 ["build"]

[<TestCase(1, ExpectedResult = 9<ms>)>]
[<TestCase(2, ExpectedResult = 5<ms>)>]
let TestPara2(threads) =

    let tasks1 =
        [
            Task ("main", 0<ms>, ["t1"; "t2"])
            Task ("t1", 4<ms>, [])
            Task ("t2", 5<ms>, [])
        ]

    estimate threads [] tasks1 ["main"]       

module ``Progress dots`` =

    open Xake.Logging

    let private cap = ProgressDots.DefaultCap
    let private cells (s: string) = s.TrimEnd('+').Length

    [<TestCase(0, ExpectedResult = "")>]
    [<TestCase(1, ExpectedResult = "⠁")>]
    [<TestCase(8, ExpectedResult = "⣿")>]
    [<TestCase(9, ExpectedResult = "⣿⠁")>]
    [<TestCase(30, ExpectedResult = "⣿⣿⣿⡟")>]
    [<TestCase(31, ExpectedResult = "⣿⣿⣿⣿+")>]
    [<TestCase(100, ExpectedResult = "⣿⣿⣿⣿+")>]
    [<TestCase(-1, ExpectedResult = "")>]
    let ``encodes the running count`` running = ProgressDots.dots running cap

    [<Test>]
    let ``a partial cell fills column-wise, left column first`` () =
        let partials = [ for n in 1..8 -> ProgressDots.dots n cap ]
        Assert.That(partials, Is.EqualTo [ "⠁"; "⠃"; "⠇"; "⡇"; "⡏"; "⡟"; "⡿"; "⣿" ])

    [<Test>]
    let ``never more than four cells`` () =
        for c in [1; 8; 30; 32; 100] do
            for n in 0..200 do
                Assert.That(cells (ProgressDots.dots n c), Is.LessThanOrEqualTo ProgressDots.MaxCells)

    [<Test>]
    let ``colour grows from green to red`` () =
        Assert.That(ProgressDots.color 1 cap, Is.EqualTo System.ConsoleColor.Green)
        Assert.That(ProgressDots.color 20 cap, Is.EqualTo System.ConsoleColor.Yellow)
        Assert.That(ProgressDots.color 31 cap, Is.EqualTo System.ConsoleColor.Red)
