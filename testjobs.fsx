// The test jobs of `test`, loaded by build.fsx and build.fsc.fsx so both run the same slices.
// Plain data and string functions only: the two scripts bootstrap on different Xake versions.
//
// A job is a test project and a slice of its fixtures (`tests/jobs`, `hermetic/run`, ...); the
// slices of a project are made of whole fixture classes, since each fixture owns its working
// folder (src/tests/~testout~/<folder>, XakeTestBase), so two jobs never share one. The last
// slice of a project (`Rest`) is every fixture not named by the project's other slices: each
// test is in exactly one job. Slices are cut to roughly even durations (seconds measured on
// 18 logical CPUs, 2026-10); a new fixture lands in `rest` until it is worth a slice of its own.

type Slice =
    /// these fixture classes (full names, namespace included)
    | Fixtures of string list
    /// every fixture of the project not named by its other slices
    | Rest

type TestJob = { Project: string; Name: string; Slice: Slice }

/// Test project key (`test::<key>/<slice>`) -> project folder.
let testProjects = [ "tests", "src/tests"; "hermetic", "src/hermetic.tests" ]

let testJobs = [
    { Project = "tests"; Name = "jobs"                      // ~38 s
      Slice = Fixtures [ "Tests.Compiler jobs"; "Tests.Delegated and resource tests" ] }
    { Project = "tests"; Name = "rest"; Slice = Rest }      // ~31 s
    { Project = "hermetic"; Name = "build"                  // ~34 s
      Slice = Fixtures [ "Tests.Building from Xake-generated locks"; "Tests.Fsc lock"; "Tests.Csc fromlock"; "Tests.Csc lock" ] }
    { Project = "hermetic"; Name = "record"                 // ~38 s
      Slice = Fixtures [ "Tests.Import record"; "Tests.Import record project chain"; "Tests.Import record configurations"
                         "Tests.Import record refusals and descriptions"; "Tests.Import record switch files"
                         "Tests.Import record concurrent brands"; "Tests.Compile under the compiler server" ] }
    { Project = "hermetic"; Name = "run"                    // ~36 s
      Slice = Fixtures [ "Tests.Run folders"; "Tests.Import record applications"; "Tests.Description import" ] }
    { Project = "hermetic"; Name = "lock"                   // ~26 s
      Slice = Fixtures [ "Tests.Lock generation"; "Tests.Described compilation" ] }
    { Project = "hermetic"; Name = "rest"; Slice = Rest }   // ~18 s
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
