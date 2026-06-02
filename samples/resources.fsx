// Resources — throttling concurrent access to shared resources.
//
// USAGE:
//   dotnet fsi build.fsx -- -- build
//   dotnet fsi samples/resources.fsx

#r "../out/netstandard2.0/Xake.dll"

open Xake
open System.Threading

let downloads = Resource.newResource "net:download" 3
let restore   = Resource.newResource "dotnet:restore" 1
let dbWrite   = Resource.newResource "db:write" 1

let active = ref 0
let peak = ref 0
let trackPeak () =
    let n = Interlocked.Increment active
    let mutable cur = peak.Value
    while n > cur && Interlocked.CompareExchange(peak, n, cur) <> cur do
        cur <- peak.Value

let simulateWork label (ms: int) = async {
    printfn "  [start]  %s" label
    do! Async.Sleep ms
    printfn "  [done]   %s" label
}

let packages = ["pkg-a"; "pkg-b"; "pkg-c"; "pkg-d"; "pkg-e"; "pkg-f"]
let projects = ["project-a"; "project-b"]

do xakeScript {
    consolelog Verbosity.Quiet
    noPersist
    want ["main"]

    rules [
        // downloads: at most 3 at a time
        "download:(pkg:*)" => recipe {
            let! pkg = getRuleMatch "pkg"
            do! withResource downloads 1 (recipe {
                trackPeak()
                do! simulateWork (sprintf "download %s" pkg) 300
                Interlocked.Decrement active |> ignore
            })
        }

        "downloads" <== [ for pkg in packages -> sprintf "download:%s" pkg ]
        "restores" <== [ for proj in projects -> sprintf "restore:%s" proj ]

        // dotnet restore: mutual exclusion
        "restore:(project:*)" => recipe {
            let! proj = getRuleMatch "project"
            do! simulateWork (sprintf "dotnet restore %s" proj) 300
        } |> requiring restore

        // DB: writes serialized, reads are free
        target "db:seed-(table:*)" {
            let! table = getRuleMatch "table"
            do! simulateWork (sprintf "INSERT INTO %s" table) 200
        } |> requiring dbWrite

        "db:read-config" => recipe {
            do! simulateWork "SELECT config" 100
        }

        command "db:import-dump" {
            do! simulateWork "IMPORT dump (heavy)" 400
        } |> requiring dbWrite

        "database" <== ["db:seed-users"; "db:seed-products"; "db:read-config"; "db:import-dump"]

        // orchestration
        "main" => recipe {
            printfn "\n=== Downloads (%d packages, max 3 concurrent) ===" packages.Length
            do! need ["downloads"]
            printfn "  peak concurrent downloads: %d\n" peak.Value

            printfn "=== Restores (%d projects, max 1 concurrent) ===" projects.Length
            do! need ["restores"]

            printfn "\n=== Database (writes serialized, reads free) ==="
            do! need ["database"]

            printfn "\n=== Done ==="
        }
    ]
}
