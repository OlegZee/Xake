// The test jobs of `test`, loaded by build.fsx.
// Plain data and string functions only.
//
// A job is a test project and a slice of its fixtures (`tests/jobs`, `tests/rest`); the slices
// of a project are made of whole fixture classes, since each fixture owns its working folder
// (src/tests/~testout~/<folder>, XakeTestBase), so two jobs never share one. The last slice of a
// project (`Rest`) is every fixture not named by the project's other slices: each test is in
// exactly one job. Slices are cut to roughly even durations; a new fixture lands in `rest` until
// it is worth a slice of its own. (The compiler and hermetic slices, with the serial compiler
// server jobs, moved with Xake.Dotnet to its own repository, OlegZee/Xake.Dotnet.)

type Slice =
    /// these fixture classes (full names, namespace included)
    | Fixtures of string list
    /// every fixture of the project not named by its other slices
    | Rest

type TestJob = { Project: string; Name: string; Slice: Slice }

/// Test project key (`test::<key>/<slice>`) -> project folder.
let testProjects = [ "tests", "src/tests" ]

let private job project name slice = { Project = project; Name = name; Slice = slice }

let testJobs = [
    job "tests" "jobs" (Fixtures [ "Tests.Delegated and resource tests" ])
    job "tests" "rest" Rest
]

let testJobName job = $"{job.Project}/{job.Name}"
let testJob project name = testJobs |> List.find (fun j -> j.Project = project && j.Name = name)
let testProjectDir project = testProjects |> List.find (fst >> (=) project) |> snd

/// Where a job writes its trx, console log and blame files: a folder of its own.
let testJobDir job = $"out/test/{job.Project}/{job.Name}"

/// Empties a job's folder before its run: only the last run's files are kept.
let resetTestJobDir job =
    let dir = testJobDir job
    if System.IO.Directory.Exists dir then System.IO.Directory.Delete (dir, true)
    System.IO.Directory.CreateDirectory dir |> ignore

/// The NUnit selection of a job (`NUnit.Where`, exact class names), `&& name =~ '<filter>'`
/// added when FILTER is set. Not vstest's `--filter`: the NUnit adapter (4.2) converts it to
/// this syntax and fails on a class name with a space in it.
let testWhereOf job (filter: string option) =
    let mine = function Fixtures fs -> fs | Rest -> []
    let anyOf fs = [ for f in fs -> $"class == '{f}'" ] |> String.concat " || "
    let slice =
        match job.Slice with
        | Fixtures fs -> anyOf fs
        | Rest ->
            match [ for j in testJobs do if j.Project = job.Project then yield! mine j.Slice ] with
            | [] -> ""
            | others -> $"!({anyOf others})"
    match slice, filter with
    | "", None -> None
    | "", Some f -> Some $"name =~ '{f}'"
    | s, None -> Some s
    | s, Some f -> Some $"({s}) && name =~ '{f}'"

/// `dotnet test` arguments of a job (after the project and `-c Release --no-build`); they end
/// with `-- NUnit.Where=...`, so other options go before them.
let testArgs job filter =
    [ yield! [ "--results-directory"; testJobDir job; "--logger"; "\"trx;LogFileName=test.trx\"" ]
      for w in Option.toList (testWhereOf job filter) do yield! [ "--"; $"\"NUnit.Where={w}\"" ] ]

/// The lines of `dotnet test` output that go to the console: the summary and the failed tests
/// (the tests' own output, expected compiler errors among it, stays in console.log).
let isTestSummary (line: string) =
    let l = line.TrimStart()
    l.StartsWith "Passed!" || l.StartsWith "Failed!" || l.StartsWith "Skipped!" || l.StartsWith "Failed "
