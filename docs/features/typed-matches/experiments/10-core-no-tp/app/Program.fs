module Program
// Experiment 10: the scenario of consumer/build.fsx, compiled.   dotnet run -c Release [D|B]

[<EntryPoint>]
let main argv =
    let forms = if argv.Length = 0 then [ "D"; "B" ] else List.ofArray argv
    for f in forms do
        Rules.scenario f (if f = "D" then Rules.rulesD else Rules.rulesB)
    0
