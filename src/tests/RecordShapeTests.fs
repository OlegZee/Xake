namespace Tests

open NUnit.Framework
open Microsoft.FSharp.Reflection

open Xake

/// `ExecContext` is a public record that code outside Xake.dll builds or copies (Xake.Dotnet and
/// its hermetic part, in their own repository, use `ExecContext.withLogger` rather than a record
/// copy). A record expression compiled against one Xake calls the full constructor, so a field
/// added in a 4.x minor would break a package built against an older 4.x at run time: the record
/// stays frozen within 4.x (docs/features/dotnet-split/brief.md §10). Add a function instead.
[<TestFixture>]
type ``Record shapes frozen within Xake 4``() =

    static member Records : obj[][] = [|
        [| typeof<ExecContext>; [ "Engine"; "Vars"; "Progress"; "NeedRebuild"; "ShowProgress"; "Targets"; "RuleMatches"; "Ordinal"; "Logger"; "HeldResources" ] |]
    |]

    [<TestCaseSource("Records")>]
    member _.``has the fields it had at 4.0``(t: System.Type, fields: string list) =
        let actual = FSharpType.GetRecordFields t |> Array.map (fun p -> p.Name) |> List.ofArray
        Assert.That(actual, Is.EqualTo fields,
            sprintf "%s changed: packages built against Xake 4.x construct it; no new fields within 4.x" t.FullName)

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
