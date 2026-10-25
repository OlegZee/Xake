// Experiment 08: a script that references only the engine (Xake.dll copied alone into a folder,
// no Xake.Patterns.dll, no design-time dll anywhere near): compiles and runs, no FS3031. Also
// shows the engine-side changes through public API: PatternAst (decision 13), format replacing
// applyWildcards for a multi-file rule's other targets (14), literal ( ) [ ] (15).
//   dotnet fsi engine-only.fsx     (after copying the engine's Xake.dll to $CLAUDE_JOB_DIR/tmp/stage-08/xake-only/)
#r "/Users/olegz/.claude/jobs/d57e8547/tmp/stage-08/xake-only/Xake.dll"
open Xake

let toks = PatternAst.parse "gen/*/(name:*)-?.txt"
printfn "wildcards      %A" (PatternAst.wildcards toks)
printfn "format         %s" (PatternAst.format (fun n -> "core") (fun i -> string i) toks)
printfn "literal only   %s" (PatternAst.formatMatches Map.empty "out/version(1)[x].txt")
printfn "regex          %s" (PatternAst.toRegex (PatternAst.parse "lit(x)/[y]*.txt"))

let root = System.IO.Path.Combine(System.Environment.GetEnvironmentVariable "CLAUDE_JOB_DIR", "tmp", "xake-08-engine-only")
if System.IO.Directory.Exists root then System.IO.Directory.Delete(root, true)
let run target =
    try
        do xake { ExecOptions.Default with ProjectRoot = root; Targets = [ target ]; Threads = 1; ConLogLevel = Silent; FileLogLevel = Silent; NoPersist = true; ThrowOnError = true } {
            rules [
                // a multi-file rule: the other target is named by PatternAst.formatMatches now
                [ "out/(lit)/(n:*).dll"; "out/(lit)/(n:*).xml" ] *..> recipe {
                    let! targets = getTargetFiles ()
                    for t in targets do printfn "%s -> multi-file target %s" target (System.IO.Path.GetRelativePath(root, t.FullName))
                }
            ]
        }
    with e -> printfn "%s -> %s" target (e.Message.Split('\n').[0])
run "out/(lit)/x.xml"
// "(lit)" is matched literally: "out/(a)/x.xml" and "out/lit/x.xml" do not match the pattern
run "out/lit/x.xml"
