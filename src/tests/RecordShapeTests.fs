namespace Tests

open NUnit.Framework
open Microsoft.FSharp.Reflection

open Xake
open Xake.Dotnet

/// The records Xake.Hermetic.Dotnet builds (record expressions or `with` copies). Its range
/// on Xake runs up to 4.0, and a record expression compiled against one Xake calls the full
/// constructor, so a field added to one of these in a 3.x minor breaks the published hermetic
/// package at run time. Until 4.0 these do not grow: add a function or a builder operation
/// instead (docs/devprocess.md, "The records the hermetic package builds").
[<TestFixture>]
type ``Record shapes frozen until Xake 4``() =

    static member Records : obj[][] = [|
        [| typeof<ExecContext>; [ "Engine"; "Vars"; "Progress"; "NeedRebuild"; "ShowProgress"; "Targets"; "RuleMatches"; "Ordinal"; "Logger"; "HeldResources" ] |]
        [| typeof<CscTypes.CscSettingsType>; [ "Platform"; "Target"; "Out"; "Src"; "Ref"; "RefGlobal"; "Resources"; "Define"; "Unsafe"; "TargetFramework"; "CommandArgs"; "FailOnError"; "CscPath"; "Toolset"; "Server"; "Project"; "Throttle" ] |]
        [| typeof<CscTypes.RunOptions>; [ "FailOnError"; "CscPath"; "Server"; "Environment"; "Throttle" ] |]
        [| typeof<Csc>; [ "Name"; "Framework"; "Directory"; "Options"; "Defines"; "Sources"; "Generated"; "Resources"; "RuntimeConfig"; "Dependencies" ] |]
        [| typeof<FscTypes.FscSettingsType>; [ "Platform"; "Target"; "Out"; "Src"; "Ref"; "RefGlobal"; "Resources"; "Define"; "TargetFramework"; "FscVersion"; "Doc"; "CommandArgs"; "FailOnError"; "NoFramework"; "Tailcalls"; "Project"; "Throttle" ] |]
        [| typeof<FscTypes.FscRunOptions>; [ "FailOnError"; "FscPath"; "Environment"; "Throttle" ] |]
        [| typeof<Fsc>; [ "Name"; "Framework"; "Directory"; "Options"; "Defines"; "Sources"; "Generated"; "Resources"; "RuntimeConfig"; "Dependencies" ] |]
    |]

    [<TestCaseSource("Records")>]
    member _.``has the fields it had at 3.14``(t: System.Type, fields: string list) =
        let actual = FSharpType.GetRecordFields t |> Array.map (fun p -> p.Name) |> List.ofArray
        Assert.That(actual, Is.EqualTo fields,
            sprintf "%s changed: a published Xake.Hermetic.Dotnet builds it; no new fields until 4.0" t.FullName)

    [<Test>]
    member _.``withLogger replaces only the logger``() =
        let ctx : ExecContext =
            { Engine = Unchecked.defaultof<_>; Vars = [ "A", "1" ]; Progress = Unchecked.defaultof<_>
              NeedRebuild = (fun _ -> false); ShowProgress = false; Targets = []; RuleMatches = Map.empty
              Ordinal = 7; Logger = CustomLogger (fun _ -> false) ignore; HeldResources = [] }
        let logger = CustomLogger (fun _ -> true) ignore
        let replaced = ExecContext.withLogger logger ctx
        Assert.That(obj.ReferenceEquals(replaced.Logger, logger))
        Assert.That(replaced.Ordinal, Is.EqualTo 7)
        Assert.That(replaced.Vars, Is.EqualTo [ "A", "1" ])
