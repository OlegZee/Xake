namespace Tests

open NUnit.Framework
open Microsoft.FSharp.Reflection

open Xake

/// `ExecContext` is a public record that code outside Xake.dll builds or copies (Xake.Dotnet and
/// its hermetic part, in their own repository, use `ExecContext.withLogger` rather than a record
/// copy). A record expression compiled against one Xake calls the full constructor, so a field
/// added in a 4.x minor would break a package built against an older 4.x at run time: the record
/// stays frozen within a major (docs/features/dotnet-split/brief.md §10). Add a function instead.
/// 5.0 added `Spellings` (the two-spellings check) and `RunEpoch` (the run's epoch).
[<TestFixture>]
type ``Record shapes frozen within Xake 5``() =

    static member Records : obj[][] = [|
        [| typeof<ExecContext>; [ "Engine"; "Vars"; "Progress"; "NeedRebuild"; "ShowProgress"; "Targets"; "RuleMatches"; "Ordinal"; "Logger"; "HeldResources"; "Spellings"; "RunEpoch" ] |]
    |]

    [<TestCaseSource("Records")>]
    member _.``has the fields it had at 5.0``(t: System.Type, fields: string list) =
        let actual = FSharpType.GetRecordFields t |> Array.map (fun p -> p.Name) |> List.ofArray
        Assert.That(actual, Is.EqualTo fields,
            sprintf "%s changed: packages built against Xake 4.x construct it; no new fields within 5.x" t.FullName)

    [<Test>]
    member _.``withLogger replaces only the logger``() =
        let ctx : ExecContext =
            { Engine = Unchecked.defaultof<_>; Vars = [ "A", "1" ]; Progress = Unchecked.defaultof<_>
              NeedRebuild = (fun _ -> false); ShowProgress = false; Targets = []; RuleMatches = Map.empty
              Ordinal = 7; Logger = CustomLogger (fun _ -> false) ignore; HeldResources = []
              Spellings = System.Collections.Concurrent.ConcurrentDictionary(); RunEpoch = 0 }
        let logger = CustomLogger (fun _ -> true) ignore
        let replaced = ExecContext.withLogger logger ctx
        Assert.That(obj.ReferenceEquals(replaced.Logger, logger))
        Assert.That(replaced.Ordinal, Is.EqualTo 7)
        Assert.That(replaced.Vars, Is.EqualTo [ "A", "1" ])
