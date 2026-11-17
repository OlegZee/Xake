#r "../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
open Xake

type Header = { Also: string list; Needs: string list; Cutoff: bool option }
    with static member Empty = { Also = []; Needs = []; Cutoff = None }
/// header + body; what the rule keeps
type Headed = Headed of Header * Recipe<ExecContext, unit>

type FileRuleBuilder(pattern: string) =
    inherit RecipeBuilder()
    member _.Yield(()) = Header.Empty
    [<CustomOperation("also")>]  member _.Also(h: Header, p: string) = { h with Also = h.Also @ [p] }
    [<CustomOperation("needs")>] member _.Needs(h: Header, p: string) = { h with Needs = h.Needs @ [p] }
    [<CustomOperation("noContentCutoff")>] member _.NoCutoff(h: Header) = { h with Cutoff = Some false }
    member _.For(h: Header, body: unit -> Recipe<ExecContext, unit>) = Headed (h, RecipeBuilder().Delay body)
    member _.Delay(f: unit -> Header) = f ()
    member _.Delay(f: unit -> Headed) = f ()
    [<CompilerMessage("The rule header (also, needs, noContentCutoff) must come before the body: move it above the first let!/do!.", 3095, IsError = true)>]
    member _.Bind(m: Recipe<'a,'b>, f: 'b -> Header) : Header = failwith "unreachable"
    [<CompilerMessage("The rule header (also, needs, noContentCutoff) must come before the body: it cannot be inside a for loop.", 3095, IsError = true)>]
    member _.For(xs: seq<'T>, f: 'T -> Header) : Header = failwith "unreachable"
    member _.Run(Headed (h, r)) : ExecContext Rule = printfn "%s header=%A" pattern h; FileRule (pattern, r)
    member _.Run(h: Header) : ExecContext Rule = printfn "%s header-only=%A" pattern h; FileRule (pattern, recipe { () })
    member _.Run(r: Recipe<ExecContext, unit>) : ExecContext Rule = printfn "%s no header" pattern; FileRule (pattern, r)

let target' p = FileRuleBuilder p
/// brief's shape: a pattern-less header builder + SRTP ..>
type RuleSpec = RuleSpec of Header * Recipe<ExecContext, unit>
type SpecBuilder() =
    inherit RecipeBuilder()
    member _.Yield(()) = Header.Empty
    [<CustomOperation("also")>]  member _.Also(h: Header, p: string) = { h with Also = h.Also @ [p] }
    member _.For(h: Header, body: unit -> Recipe<ExecContext, unit>) = RuleSpec (h, RecipeBuilder().Delay body)
    member _.Delay(f: unit -> Header) = RuleSpec (f (), recipe { () })
    member _.Delay(f: unit -> RuleSpec) = f ()
let header = SpecBuilder()
type RuleArg = RuleArg with
    static member ($) (RuleArg, r: Recipe<ExecContext, unit>) = fun p -> FileRule (p, r)
    static member ($) (RuleArg, RuleSpec (h, r)) = fun p -> printfn "spec %A" h; FileRule (p, r)
let inline ( ..> ) (pattern: string) body : ExecContext Rule = (RuleArg $ body) pattern
