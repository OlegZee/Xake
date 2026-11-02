module ``File logger``

open System.IO
open NUnit.Framework
open Xake

let private tmpLog () = Path.Combine(Path.GetTempPath(), sprintf "xake-fl-%s.log" (System.Guid.NewGuid().ToString "N"))

[<Test>]
let ``FlushLogs writes all pending lines``() =
    let name = tmpLog ()
    try
        let log = FileLogger name Verbosity.Diag
        for i in 1..200 do log.Log Level.Message "line %d" i
        FlushLogs()
        Assert.AreEqual(200, File.ReadAllLines(name).Length)
    finally
        try File.Delete name with _ -> ()

[<Test>]
let ``second logger on the same file does not throw``() =
    let name = tmpLog ()
    try
        let first = FileLogger name Verbosity.Diag
        for i in 1..200 do first.Log Level.Message "line %d" i
        let second = FileLogger name Verbosity.Diag
        second.Log Level.Message "fresh"
        FlushLogs()
        Assert.AreEqual(1, File.ReadAllLines(name).Length)
    finally
        try File.Delete name with _ -> ()
