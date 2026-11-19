// No-change benchmark (docs/features/idle-perf): 6 groups x 198 file targets, 150 file
// dependencies each over 10,000 sources. Prepend the reference, e.g. #r "nuget: Xake, 4.0.0".
// Run: `mksrc`, then `-t 16 build` twice; the second is the no-change run.
open Xake
open Xake.Tasks
let NSRC = 10000
let NG = 6
let PER = 198
let NDEP = 150
let srcOf i j = sprintf "src/s%d.txt" ((i * 7 + j * 13) % NSRC)
do xakeScript {
    for g in 1 .. NG do
        (sprintf "g%d" g) <== [ for i in 1 .. PER -> sprintf "out/g%d/t%d.o" g i ]
    "build" <== [ for g in 1 .. NG -> sprintf "g%d" g ]
    "out/(g:*)/t(i:*).o" ..> recipe {
        let! i = getRuleMatch "i"
        let i = int i
        do! need [ for j in 0 .. NDEP - 1 -> srcOf i j ]
        do! writeText "x"
    }
    "mksrc" => recipe {
        do System.IO.Directory.CreateDirectory "src" |> ignore
        for i in 0 .. NSRC - 1 do System.IO.File.WriteAllText(sprintf "src/s%d.txt" i, string i)
    }
}
