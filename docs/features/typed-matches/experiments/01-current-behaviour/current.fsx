// Experiment 01: what the current API does with a misspelled group and with a multi-file
// rule whose patterns do not share all groups. Run from this folder:
//   dotnet fsi current.fsx
// Needs `dotnet build src/core -c Release` first.
#r "../../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
open Xake

let root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xake-typed-matches-01")
System.IO.Directory.CreateDirectory root |> ignore
let opts targets =
    { ExecOptions.Default with
        ProjectRoot = root; Targets = targets; Threads = 1
        ConLogLevel = Silent; FileLogLevel = Silent; NoPersist = true; ResetDb = true }
let rel (f: File) = (File.getFullName f).Substring(root.Length)

// (1) typo: the pattern says "fwk", the recipe asks for "fkw"
do xake (opts ["out/net8.0/Lib.dll"]) {
    rules [
        "out/(fwk:*)/(lib:*).dll" ..> recipe {
            let! fwk = getRuleMatch "fkw"      // typo
            let! lib = getRuleMatch "lib"
            printfn "(1) typo: fwk=%A lib=%A -> would run: dotnet build --framework %s" fwk lib fwk
            let! all = getRuleMatches ()
            printfn "(1) the whole map: %A" (Map.toList all)
        }
    ]
}

// (2) multi-file rule: the second pattern names its group differently
do xake (opts ["out/a/Lib.dll"]) {
    rules [
        ["out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(name:*).xml"] *..> recipe {
            let! files = getTargetFiles ()
            printfn "(2) mismatched groups -> targets: %A" (files |> List.map rel)
        }
    ]
}

// (3) multi-file rule: ask for the second output; the groups come from the pattern that matched
do xake (opts ["out/a/Lib.xml"]) {
    rules [
        ["out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml"] *..> recipe {
            let! files = getTargetFiles ()
            let! m = getRuleMatches ()
            printfn "(3) asked for the .xml -> targets: %A, groups: %A" (files |> List.map rel) (Map.toList m)
        }
    ]
}

// (4) positional wildcards in a multi-file rule: "1" is the first * of the matched pattern
do xake (opts ["out/x/y.pdb"]) {
    rules [
        ["out/*/(name:*).dll"; "out/*/(name:*).pdb"] *..> recipe {
            let! files = getTargetFiles ()
            let! m = getRuleMatches ()
            printfn "(4) wildcards -> targets: %A, groups: %A" (files |> List.map rel) (Map.toList m)
        }
    ]
}
