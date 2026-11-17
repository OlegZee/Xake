#load "Header.fsx"
open Xake
open Header
let s = xakeScript {
    dryrun
    target' "out/(n:*).dll" {
        also "out/(n:*).pdb"
        do! need ["a"]
    }
    "out/(n:*).x" ..> recipe { do! need ["a"] }
    "out/(n:*).y" ..> header { also "out/(n:*).z"; do! need ["a"] }
    rule ("out/b" ..> recipe { () })
}
