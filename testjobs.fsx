// The test jobs of `test`, loaded by build.fsx and build.fsc.fsx so both run the same slices.
// Plain data and string functions only: the two scripts bootstrap on different Xake versions.
//
// A job is a test project and a slice of its fixtures (`tests/jobs`, `hermetic/run`, ...); the
// slices of a project are made of whole fixture classes, since each fixture owns its working
// folder (src/tests/~testout~/<folder>, XakeTestBase), so two jobs never share one. The last
// slice of a project (`Rest`) is every fixture not named by the project's other slices: each
// test is in exactly one job. Slices are cut to roughly even durations (seconds measured on
// 18 logical CPUs, 2026-10); a new fixture lands in `rest` until it is worth a slice of its own.
//
// Fixtures that use the compiler server are serial: the server (VBCSCompiler) is one per
// machine, and a neighbour starting it, loading it or letting its keepalive expire makes a
// client fall back to a plain csc, which fails a test that asserts the server. They sit in the
// `Server = true` jobs, run one after another in list order; every other job runs with
// XAKE_CSC_SERVER=off (csc in-process), so it never touches the server; XakeTestBase passes the
// environment variable to the test scripts as -d (Xake.Dotnet reads build variables only).

type Slice =
    /// these fixture classes (full names, namespace included)
    | Fixtures of string list
    /// every fixture of the project not named by its other slices
    | Rest

type TestJob = { Project: string; Name: string; Slice: Slice; Server: bool }

/// Test project key (`test::<key>/<slice>`) -> project folder.
let testProjects = [ "tests", "src/tests"; "hermetic", "src/hermetic.tests" ]

let private job project name slice = { Project = project; Name = name; Slice = slice; Server = false }

let testJobs = [
    // the compiler server's users, serial: tests/server, then hermetic/server
    { job "tests" "server" (Fixtures [ "Tests.Csc compiler server" ]) with Server = true }                 // ~2 s
    { job "hermetic" "server" (Fixtures [ "Tests.Building from Xake-generated locks"                       // ~32 s
                                          "Tests.Compile under the compiler server" ]) with Server = true }
    job "tests" "jobs" (Fixtures [ "Tests.Compiler jobs"; "Tests.Delegated and resource tests" ])            // ~38 s
    job "tests" "rest" Rest                                                                                   // ~29 s
    job "hermetic" "record" (Fixtures [ "Tests.Import record"; "Tests.Import record project chain"           // ~34 s
                                        "Tests.Import record configurations"; "Tests.Import record refusals and descriptions"
                                        "Tests.Import record switch files"; "Tests.Import record concurrent brands" ])
    job "hermetic" "run" (Fixtures [ "Tests.Run folders"; "Tests.Import record applications"; "Tests.Description import" ])  // ~36 s
    job "hermetic" "lock" (Fixtures [ "Tests.Lock generation"; "Tests.Described compilation"                 // ~32 s
                                      "Tests.Fsc lock"; "Tests.Csc fromlock"; "Tests.Csc lock" ])
    job "hermetic" "rest" Rest                                                                                // ~18 s
]

let testJobName job = $"{job.Project}/{job.Name}"
let testJob project name = testJobs |> List.find (fun j -> j.Project = project && j.Name = name)
let testProjectDir project = testProjects |> List.find (fst >> (=) project) |> snd

/// The jobs a job waits for: a server job, for the server job before it.
let testJobNeeds job =
    if not job.Server then [] else
    testJobs |> List.filter (fun j -> j.Server) |> List.takeWhile ((<>) job) |> List.rev |> List.truncate 1
    |> List.map (fun j -> $"test::{testJobName j}")

/// The compiler server switch of a job's `dotnet test`: on for the server jobs (their tests
/// need it), off for every other job (it never touches the server).
let testJobEnv job = "XAKE_CSC_SERVER", (if job.Server then "on" else "off")
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
