namespace Xake.Patterns.DesignTime

open System
open System.Reflection
open System.Text.RegularExpressions
open FSharp.Core.CompilerServices
open FSharp.Quotations
open ProviderImplementation.ProvidedTypes
open Xake
open Xake.Patterns

/// The typed patterns provider (namespace `Xake.Patterns`):
///   Target<"p1", "p2", ...>  file rules; one static parameter per output, "label=pattern" names an
///                            output, unlabelled outputs are Out1, Out2, ...
///   Command<"name pattern">  phony rules with `command` semantics (AlwaysRerun)
/// Everything is read off the engine's own parser (Xake.PatternAst, public in Xake.dll).
[<TypeProvider>]
type PatternProvider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(config, assemblyReplacementMap = [ ("Xake.Patterns.DesignTime", "Xake.Patterns") ], addDefaultProbingLocation = true)

    let ns = "Xake.Patterns"
    let asm = Assembly.GetExecutingAssembly()
    let mapTy = typeof<Map<string, string>>
    let recipeUnit = typeof<Recipe<ExecContext, unit>>

    let labelRx = Regex(@"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$")

    /// Parses, validates, and gives the slots: (member/parameter name, run-time key). Named groups
    /// by name, top-level bare wildcards as _1, _2, ... (brief decision 6).
    let analyse (what: string) (patterns: string[]) =
        let asts = patterns |> Array.map PatternAst.parse
        for p, toks in Array.zip patterns asts do
            // the regex the engine will build: a malformed pattern fails here, not at run time
            try Regex("^" + PatternAst.toRegex toks + "$") |> ignore
            with e -> failwithf "%s '%s' does not compile to a regex: %s" what p e.Message
        let names = PatternAst.groupNames asts.[0]
        for p, toks in Array.zip patterns asts |> Array.skip 1 do
            let gs = PatternAst.groupNames toks
            let missing = names |> List.except gs
            let extra = gs |> List.except names
            if not missing.IsEmpty || not extra.IsEmpty then
                failwithf "%s '%s': groups %A differ from the first pattern's %A (missing %A, extra %A)" what p gs names missing extra
        let wilds = PatternAst.wildcards asts.[0]
        if patterns.Length > 1 && asts |> Array.exists (PatternAst.wildcards >> List.isEmpty >> not) then
            failwithf "%s: a multi-output pattern may not use unnamed wildcards (*, **, ?); name them, e.g. (dir:*)" what
        [ for n in names -> n, n ] @ [ for i, (_, num) in List.indexed wilds -> sprintf "_%d" (i + 1), string num ]

    /// A group or label may not take the name of a member of the type.
    let checkNames (what: string) (reserved: string list) (names: string list) =
        for n in names do
            if List.contains n reserved then
                failwithf "%s: the name '%s' is taken by a member of the type (%s); rename the group or label" what n (String.concat ", " reserved)
        match names |> List.countBy id |> List.filter (snd >> (<) 1) with
        | [] -> ()
        | dups -> failwithf "%s: the names %A are used twice (groups and output labels share one namespace)" what (List.map fst dups)

    let arr (a: Expr list) = Expr.NewArray(typeof<string>, a)
    let ps (slots: (string * string) list) = [ for n, _ in slots -> ProvidedParameter(n, typeof<string>) ]

    /// Members both roots share: group properties, Make, For, Rule (form D), rule (form B).
    let newType typeName (patterns: string[]) (slots: (string * string) list) =
        let keys = slots |> List.map snd |> Array.ofList
        let t = ProvidedTypeDefinition(asm, ns, typeName, Some mapTy, isErased = true)
        t.AddXmlDoc(sprintf "Typed match of %s. Groups: %s" (String.concat ", " patterns) (slots |> List.map fst |> String.concat ", "))
        for name, key in slots do
            let p = ProvidedProperty(name, typeof<string>, getterCode = fun a -> <@@ PatternRuntime.group (%%a.[0] : Map<string, string>) key @@>)
            p.AddXmlDoc(sprintf "The value of '%s' in the matched name." name)
            t.AddMember p
        t.AddMember(
            ProvidedMethod("Make", ps slots, t, isStatic = true,
                invokeCode = fun a -> <@@ PatternRuntime.make patterns keys (%%(arr a) : string[]) @@>))
        t.AddMember(
            ProvidedMethod("For", [ for n, _ in slots -> ProvidedParameter(n, typeof<string list>) ], typeof<string list>, isStatic = true,
                invokeCode = fun a -> <@@ PatternRuntime.forAll patterns keys (%%(Expr.NewArray(typeof<string list>, a)) : string list[]) @@>))
        t, keys

    let addRule (t: ProvidedTypeDefinition) (bodyT: Type) (ruleOf: Expr -> Expr) (builderT: Type) (builder: Expr) =
        t.AddMember(ProvidedMethod("Rule", [ ProvidedParameter("body", bodyT) ], typeof<ExecContext Rule>, isStatic = true, invokeCode = fun a -> ruleOf a.[0]))
        t.AddMember(ProvidedProperty("rule", builderT, isStatic = true, getterCode = fun _ -> builder))

    let typedBody (t: Type) = ProvidedTypeBuilder.MakeGenericType(typedefof<_ -> _>, [ t; recipeUnit ])
    let typedBuilder (t: Type) = ProvidedTypeBuilder.MakeGenericType(typedefof<TypedRuleBuilder<_>>, [ t ])

    // ---------------------------------------------------------------- Target<"p1", "p2", ...>
    let target = ProvidedTypeDefinition(asm, ns, "Target", Some typeof<obj>, isErased = true)
    do target.DefineStaticParameters(
        [ ProvidedStaticParameter("pattern", typeof<string>)
          ProvidedStaticParameter("out2", typeof<string>, parameterDefaultValue = "")
          ProvidedStaticParameter("out3", typeof<string>, parameterDefaultValue = "")
          ProvidedStaticParameter("out4", typeof<string>, parameterDefaultValue = "") ],
        fun typeName args ->
            let outputs =
                args |> Array.map (fun a -> a :?> string) |> Array.filter (String.IsNullOrWhiteSpace >> not)
                |> Array.mapi (fun i s ->
                    let m = labelRx.Match s
                    if m.Success then m.Groups.[1].Value, m.Groups.[2].Value.Trim() else sprintf "Out%d" (i + 1), s.Trim())
            let labels = outputs |> Array.map fst |> List.ofArray
            let patterns = outputs |> Array.map snd
            let slots = analyse "Target" patterns
            checkNames "Target" [ "Make"; "Format"; "For"; "Glob"; "Rule"; "rule" ] (List.map fst slots @ labels)
            let t, keys = newType typeName patterns slots
            for label, p in outputs do
                let prop = ProvidedProperty(label, typeof<string>, getterCode = fun a -> <@@ PatternRuntime.format p (%%a.[0] : Map<string, string>) @@>)
                prop.AddXmlDoc(sprintf "The output path for '%s'." p)
                t.AddMember prop
            let p0 = patterns.[0]
            t.AddMember(
                ProvidedMethod("Format", ps slots, typeof<string>, isStatic = true,
                    invokeCode = fun a -> <@@ PatternRuntime.format p0 (PatternRuntime.make patterns keys (%%(arr a) : string[])) @@>))
            let opt = [ for n, _ in slots -> ProvidedParameter(n, typeof<string>, optionalValue = null) ]
            t.AddMember(
                ProvidedMethod("Glob", opt, typeof<Fileset.Fileset>, isStatic = true,
                    invokeCode = fun a -> <@@ PatternRuntime.glob patterns keys (%%(arr a) : string[]) @@>))
            addRule t (typedBody t)
                (fun body -> <@@ PatternRuntime.rule patterns (%%body : Map<string, string> -> Recipe<ExecContext, unit>) @@>)
                (typedBuilder t) <@@ PatternBuilders.rule patterns @@>
            t)

    // ---------------------------------------------------------------- Command<"name pattern">
    let command = ProvidedTypeDefinition(asm, ns, "Command", Some typeof<obj>, isErased = true)
    do command.DefineStaticParameters(
        [ ProvidedStaticParameter("name", typeof<string>) ],
        fun typeName args ->
            let text = (args.[0] :?> string).Trim()
            if text = "" then failwith "Command: no name given"
            let patterns = [| text |]
            let slots = analyse "Command" patterns
            checkNames "Command" [ "Make"; "Format"; "For"; "Rule"; "rule"; "Name" ] (List.map fst slots)
            let t, keys = newType typeName patterns slots
            t.AddMember(ProvidedProperty("Name", typeof<string>, getterCode = fun a -> <@@ PatternRuntime.name text (%%a.[0] : Map<string, string>) @@>))
            t.AddMember(
                ProvidedMethod("Format", ps slots, typeof<string>, isStatic = true,
                    invokeCode = fun a -> <@@ PatternRuntime.format text (PatternRuntime.make patterns keys (%%(arr a) : string[])) @@>))
            if slots.IsEmpty then
                // no groups: the lambda takes unit, and the builder is today's `command "name" { }`
                addRule t typeof<unit -> Recipe<ExecContext, unit>>
                    (fun body -> <@@ PatternRuntime.command0 text (%%body : unit -> Recipe<ExecContext, unit>) @@>)
                    typeof<RuleBuilder.RuleBuilder> <@@ PatternBuilders.command0 text @@>
            else
                addRule t (typedBody t)
                    (fun body -> <@@ PatternRuntime.command text (%%body : Map<string, string> -> Recipe<ExecContext, unit>) @@>)
                    (typedBuilder t) <@@ PatternBuilders.command text @@>
            t)

    do this.AddNamespace(ns, [ target; command ])

[<assembly: TypeProviderAssembly>]
do ()
