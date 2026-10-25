// Experiment 09: destructuring the typed match. Real rules of build.fsc.fsx (helpers stubbed) in
// each candidate form, plus the mistakes (misspelt / mis-ordered / wrong arity). `sh ../run.sh cases.fsx`
// splits the file at `//#case` (header + case -> cases/<name>.fsx), runs every case with the SDK 10
// and SDK 8 fsi -> output.txt. Cases a1-*/f1-* open an opt-in module that adds a second Bind on
// `matched`; the switched provider probes (Rule overload, provided `|Parts|`) are in ../probes.
#r "../../engine/src/core/bin/Release/netstandard2.0/Xake.dll"
#r "../../patterns/bin/Release/netstandard2.0/Xake.Patterns.dll"
open Xake
open Xake.Patterns

// build.fsc.fsx:64   target "out/(cfg:*)/(fwk:*)/(name:*).dll"
type Asm = Target<"out/(cfg:*)/(fwk:*)/(name:*).dll">
// build.fsc.fsx:47   "lock::(name:*)::(cfg:*)" => recipe { ... alwaysRerun ... }
type Lock = Command<"lock::(name:*)::(cfg:*)">
// build.fsc.fsx:74   "test-build::(project:*)"  -- one group
type TestBuild = Command<"test-build::(project:*)">
// build.fsx:140      targets ["out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml"]  -- labelled outputs
type Lib = Target<"dll=out/(fwk:*)/(lib:*).dll", "xml=out/(fwk:*)/(lib:*).xml">

// stubs of the script's helpers
let lockOf (name: string) (cfg: string) = $"locks/{name}.{cfg}.lock"
let descriptionOf (name: string) = $"projects/{name}.json"
let versions = "versions.props"
module Locks =
    let compileAs (lock: string) (fwk: string) (_: string) = recipe { printfn "compileAs %s %s" lock fwk }
    let generate (cfg: string) (desc: string) = recipe { printfn "generate %s %s" desc cfg }
module Description =
    let load (d: string) = recipe { return {| ProjectRefs = (if d.Contains "Dotnet" then [ "Core" ] else []) |} }
let testProjectDir (p: string) = $"src/tests/{p}"
let sh (cmd: string) = recipe { printfn "sh: %s" cmd }

let run (targets: string list) (rs: ExecContext Rule list) =
    let root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xake-09")
    if System.IO.Directory.Exists root then System.IO.Directory.Delete(root, true)
    System.IO.Directory.CreateDirectory root |> ignore
    xake { ExecOptions.Default with
             ProjectRoot = root; Targets = targets; Threads = 1
             ConLogLevel = Silent; FileLogLevel = Silent; ResetDb = true; ThrowOnError = true } {
        rules (rs @ [ "out/(cfg:*)/(fwk:*)/(name:*).txt" ..> recipe { () } ])
    }

//#case a1-tuple-same-marker
open Xake.Patterns.TupleOnMatched
// let! cfg, fwk, name = matched   (tuple overload of the `matched` Bind, an extension member)
run [ "out/Release/net8.0/Core.dll"; "lock::Xake.Dotnet::Release" ] [
    Asm.rule {
        let! cfg, fwk, name = matched
        do! Locks.compileAs (lockOf name cfg) fwk versions
    }
    Lock.rule {
        let! name, cfg = matched
        let! d = Description.load (descriptionOf name)
        for r in d.ProjectRefs do do! need [ Lock.Format(name = r, cfg = cfg) ]
        do! Locks.generate cfg (descriptionOf name)
    }
]

//#case a1-status-quo-with-tuple-overload
open Xake.Patterns.TupleOnMatched
// let! m = matched + m.name next to the tuple overload of the same marker
run [ "out/Release/net8.0/Core.dll" ] [
    Asm.rule {
        let! m = matched
        do! Locks.compileAs (lockOf m.name m.cfg) m.fwk versions
    }
]

//#case a1-one-group-unannotated
open Xake.Patterns.TupleOnMatched
// one group: `let! project = matched` -- the tuple is just a string, but which overload wins?
run [ "test-build::Core" ] [
    TestBuild.rule {
        let! project = matched
        do! sh $"dotnet build {testProjectDir project} -c Release"
    }
]

//#case a1-one-group-annotated
open Xake.Patterns.TupleOnMatched
run [ "test-build::Core" ] [
    TestBuild.rule {
        let! (project: string) = matched
        do! sh $"dotnet build {testProjectDir project} -c Release"
    }
]

//#case a1-mis-ordered
open Xake.Patterns.TupleOnMatched
// fwk and cfg swapped: compiles, runs, wrong values (all groups are strings)
run [ "out/Release/net8.0/Core.dll" ] [
    Asm.rule {
        let! fwk, cfg, name = matched
        do! Locks.compileAs (lockOf name cfg) fwk versions
    }
]

//#case a1-wrong-arity
open Xake.Patterns.TupleOnMatched
run [ "out/Release/net8.0/Core.dll" ] [
    Asm.rule {
        let! cfg, fwk = matched
        do! Locks.compileAs (lockOf "x" cfg) fwk versions
    }
]

//#case a2-tuple-own-marker
// let! cfg, fwk, name = groups   (a second marker, no overloads)
run [ "out/Release/net8.0/Core.dll"; "lock::Xake.Dotnet::Release"; "test-build::Core" ] [
    Asm.rule {
        let! cfg, fwk, name = groups
        do! Locks.compileAs (lockOf name cfg) fwk versions
    }
    Lock.rule {
        let! name, cfg = groups
        let! d = Description.load (descriptionOf name)
        for r in d.ProjectRefs do do! need [ Lock.Format(name = r, cfg = cfg) ]
        do! Locks.generate cfg (descriptionOf name)
    }
    TestBuild.rule {
        let! project = groups
        do! sh $"dotnet build {testProjectDir project} -c Release"
    }
]

//#case a2-wrong-arity
run [ "out/Release/net8.0/Core.dll" ] [
    Asm.rule {
        let! cfg, fwk = groups
        do! Locks.compileAs (lockOf "x" cfg) fwk versions
    }
]

//#case a2-labels-need-m-too
// multi-output: the tuple has the groups only; the outputs are still m.dll / m.xml
run [ "out/net8.0/Core.xml" ] [
    Lib.rule {
        let! fwk, lib = groups
        let! m = matched
        printfn "compile %s for %s -> %s + %s" lib fwk m.dll m.xml
    }
]

//#case b-status-quo
run [ "out/Release/net8.0/Core.dll"; "lock::Xake.Dotnet::Release" ] [
    Asm.rule {
        let! m = matched
        do! Locks.compileAs (lockOf m.name m.cfg) m.fwk versions
    }
    Lock.rule {
        let! m = matched
        let! d = Description.load (descriptionOf m.name)
        for r in d.ProjectRefs do do! need [ Lock.Format(name = r, cfg = m.cfg) ]
        do! Locks.generate m.cfg (descriptionOf m.name)
    }
]

//#case d-for-groups
// for cfg, fwk, name in groups do   (a For overload on the marker)
run [ "out/Release/net8.0/Core.dll" ] [
    Asm.rule {
        for cfg, fwk, name in groups do
            do! Locks.compileAs (lockOf name cfg) fwk versions
    }
]

//#case e-ruleof-tuple-lambda
// form D with a tuple lambda: X.RuleOf <| fun (cfg, fwk, name) -> recipe { }
run [ "out/Release/net8.0/Core.dll"; "lock::Xake.Dotnet::Release"; "test-build::Core" ] [
    Asm.RuleOf <| fun (cfg, fwk, name) -> recipe {
        do! Locks.compileAs (lockOf name cfg) fwk versions
    }
    Lock.RuleOf <| fun (name, cfg) -> recipe {
        let! d = Description.load (descriptionOf name)
        for r in d.ProjectRefs do do! need [ Lock.Format(name = r, cfg = cfg) ]
        do! Locks.generate cfg (descriptionOf name)
    }
    TestBuild.RuleOf <| fun project -> recipe {
        do! sh $"dotnet build {testProjectDir project} -c Release"
    }
]

//#case e-ruleof-wrong-arity
run [ "out/Release/net8.0/Core.dll" ] [
    Asm.RuleOf <| fun (cfg, fwk) -> recipe { printfn "%s %s" cfg fwk }
]

//#case e-lambda-then-let
// form D, today's member, destructured on the next line through a tuple-typed RuleOf is not needed:
// with `m` you can only do `let cfg, fwk, name = m.cfg, m.fwk, m.name` (names written twice)
run [ "out/Release/net8.0/Core.dll" ] [
    Asm.Rule <| fun m -> recipe {
        let cfg, fwk, name = m.cfg, m.fwk, m.name
        do! Locks.compileAs (lockOf name cfg) fwk versions
    }
]

//#case f1-user-record
open Xake.Patterns.RecordOnMatched
// a record declared in the script, filled by field name when the rule runs
type AsmGroups = { cfg: string; fwk: string; name: string }
run [ "out/Release/net8.0/Core.dll" ] [
    Asm.rule {
        let! { cfg = cfg; fwk = fwk; name = name } = matched
        do! Locks.compileAs (lockOf name cfg) fwk versions
    }
]

//#case f1-user-record-partial
open Xake.Patterns.RecordOnMatched
type AsmGroups = { cfg: string; fwk: string; name: string }
type FwkOnly = { fwk: string }
run [ "out/Release/net8.0/Core.dll" ] [
    Asm.rule {
        let! ({ fwk = fwk }: FwkOnly) = matched
        printfn "fwk = %s" fwk
    }
]

//#case f1-user-record-field-misspelt
open Xake.Patterns.RecordOnMatched
// the record says `fkw`: only the run tells
type AsmGroups = { cfg: string; fkw: string; name: string }
run [ "out/Release/net8.0/Core.dll" ] [
    Asm.rule {
        let! { cfg = cfg; fkw = fwk; name = name } = matched
        do! Locks.compileAs (lockOf name cfg) fwk versions
    }
]

//#case f1-user-record-pattern-misspelt
open Xake.Patterns.RecordOnMatched
type AsmGroups = { cfg: string; fwk: string; name: string }
run [ "out/Release/net8.0/Core.dll" ] [
    Asm.rule {
        let! { cfg = cfg; fkw = fwk; name = name } = matched
        do! Locks.compileAs (lockOf name cfg) fwk versions
    }
]

//#case f1-record-for-another-pattern
open Xake.Patterns.RecordOnMatched
// the record of another rule's pattern: compiles, fails when the rule runs
type LibGroups = { fwk: string; lib: string }
run [ "out/Release/net8.0/Core.dll" ] [
    Asm.rule {
        let! { fwk = fwk; lib = lib } = matched
        printfn "%s %s" fwk lib
    }
]
//#case f1-status-quo-with-record-overload
open Xake.Patterns.RecordOnMatched
run [ "out/Release/net8.0/Core.dll" ] [
    Asm.rule {
        let! m = matched
        do! Locks.compileAs (lockOf m.name m.cfg) m.fwk versions
    }
]

//#case f2-user-record
// a record declared in the script, filled by field name when the rule runs
type AsmGroups = { cfg: string; fwk: string; name: string }
run [ "out/Release/net8.0/Core.dll" ] [
    Asm.rule {
        let! { cfg = cfg; fwk = fwk; name = name } = fields
        do! Locks.compileAs (lockOf name cfg) fwk versions
    }
]

//#case f2-user-record-partial
type AsmGroups = { cfg: string; fwk: string; name: string }
type FwkOnly = { fwk: string }
run [ "out/Release/net8.0/Core.dll" ] [
    Asm.rule {
        let! ({ fwk = fwk }: FwkOnly) = fields
        printfn "fwk = %s" fwk
    }
]

//#case f2-user-record-field-misspelt
// the record says `fkw`: only the run tells
type AsmGroups = { cfg: string; fkw: string; name: string }
run [ "out/Release/net8.0/Core.dll" ] [
    Asm.rule {
        let! { cfg = cfg; fkw = fwk; name = name } = fields
        do! Locks.compileAs (lockOf name cfg) fwk versions
    }
]

//#case f2-user-record-pattern-misspelt
type AsmGroups = { cfg: string; fwk: string; name: string }
run [ "out/Release/net8.0/Core.dll" ] [
    Asm.rule {
        let! { cfg = cfg; fkw = fwk; name = name } = fields
        do! Locks.compileAs (lockOf name cfg) fwk versions
    }
]

//#case f2-record-for-another-pattern
// the record of another rule's pattern: compiles, fails when the rule runs
type LibGroups = { fwk: string; lib: string }
run [ "out/Release/net8.0/Core.dll" ] [
    Asm.rule {
        let! { fwk = fwk; lib = lib } = fields
        printfn "%s %s" fwk lib
    }
]

//#case a2-mis-ordered
// fwk and cfg swapped: compiles, runs, wrong values
run [ "out/Release/net8.0/Core.dll" ] [
    Asm.rule {
        let! fwk, cfg, name = groups
        do! Locks.compileAs (lockOf name cfg) fwk versions
    }
]

//#case a2-one-group-annotated-wrongly
run [ "test-build::Core" ] [
    TestBuild.rule {
        let! (project: int) = groups
        printfn "%d" project
    }
]
