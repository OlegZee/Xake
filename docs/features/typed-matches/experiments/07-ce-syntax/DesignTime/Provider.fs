namespace Xake.Patterns.DesignTime

open System
open System.Reflection
open System.Text.RegularExpressions
open FSharp.Core.CompilerServices
open FSharp.Quotations
open ProviderImplementation.ProvidedTypes
open Xake
open Xake.Patterns

/// Experiment 07: experiment 05's provider (PatternAst, runtime + design-time dlls) with
/// experiment 04's `Command<>` ported to it, and the members the rule-syntax forms need:
///   form A  static `Pattern : string` (single file) / `Patterns : string list` (all outputs),
///           `Get` checked at run time against the rule's matches  ->  `Dll.Pattern ..> recipe {..}`
///   form B  static `rule : TypedRuleBuilder<T>`                    ->  `Dll.rule { let! m = matched .. }`
///   form D  static `Rule : (T -> Recipe) -> Rule` (as in 05)       ->  `Dll.Rule <| fun m -> recipe {..}`
/// Generation is cut to `Format` (Pattern<>) and `Target` (Command<>): enough for the examples.
[<TypeProvider>]
type PatternProvider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(config, assemblyReplacementMap = [ ("Xake.Patterns.DesignTime", "Xake.Patterns.Runtime") ], addDefaultProbingLocation = true)

    let ns = "Xake"
    let asm = Assembly.GetExecutingAssembly()
    let mapTy = typeof<Map<string, string>>

    let outputName i (p: string) =
        let m = Regex.Match(p, @"\.(\w+)$")
        if m.Success then m.Groups.[1].Value else sprintf "Out%d" i

    /// Parses (the engine's PatternAst), validates, and gives the slots: (member name, run-time key).
    let analyse (what: string) (patterns: string[]) =
        let asts = patterns |> Array.map PatternAst.parse
        for p, toks in Array.zip patterns asts do
            try Regex("^" + PatternAst.toRegex toks + "$") |> ignore
            with e -> failwithf "%s '%s' does not compile to a regex: %s" what p e.Message
        let names = PatternAstGen.groupNames asts.[0]
        for p, toks in Array.zip patterns asts |> Array.skip 1 do
            let gs = PatternAstGen.groupNames toks
            let missing = names |> List.except gs
            let extra = gs |> List.except names
            if not missing.IsEmpty || not extra.IsEmpty then
                failwithf "%s '%s': groups %A differ from the first pattern's %A (missing %A, extra %A)" what p gs names missing extra
        let wilds = PatternAstGen.wildcards asts.[0]
        if patterns.Length > 1 && asts |> Array.exists (PatternAstGen.wildcards >> List.isEmpty >> not) then
            failwithf "%s: a multi-file pattern may not use unnamed wildcards (*, **, ?); name them, e.g. (dir:*)" what
        [ for n in names -> n, n ] @ [ for i, (_, num) in List.indexed wilds -> sprintf "_%d" (i + 1), string num ]

    /// What both roots share: the erased type, group properties, Get, Rule(fun), rule { }.
    let newType typeName (patterns: string[]) (slots: (string * string) list) (ruleOf: Expr -> Expr) (builder: Expr) =
        let keys = slots |> List.map snd |> Array.ofList
        let t = ProvidedTypeDefinition(asm, ns, typeName, Some mapTy, isErased = true)
        t.AddXmlDoc(sprintf "Typed matches of %A. Slots: %A" patterns (List.map fst slots))
        for name, key in slots do
            t.AddMember(ProvidedProperty(name, typeof<string>, getterCode = fun a -> <@@ PatternRuntime.group (%%a.[0] : Map<string, string>) key @@>))

        // form A: the accessor inside a plain recipe
        let recipeT = ProvidedTypeBuilder.MakeGenericType(typedefof<Recipe<_, _>>, [ typeof<ExecContext>; t :> Type ])
        t.AddMember(ProvidedProperty("Get", recipeT, isStatic = true, getterCode = fun _ -> <@@ PatternRuntime.getFor patterns keys @@>))

        // form D (and 05): Rule(fun m -> recipe { .. })
        let bodyT = ProvidedTypeBuilder.MakeGenericType(typedefof<_ -> _>, [ t :> Type; typeof<Recipe<ExecContext, unit>> ])
        t.AddMember(ProvidedMethod("Rule", [ ProvidedParameter("body", bodyT) ], typeof<ExecContext Rule>, isStatic = true, invokeCode = fun a -> ruleOf a.[0]))

        // form B: rule { let! m = matched .. } -- a runtime generic builder instantiated at the erased type
        let builderT = ProvidedTypeBuilder.MakeGenericType(typedefof<TypedRuleBuilder<_>>, [ t :> Type ])
        t.AddMember(ProvidedProperty("rule", builderT, isStatic = true, getterCode = fun _ -> builder))
        t, keys

    let arr (a: Expr list) = Expr.NewArray(typeof<string>, a)
    let ps (slots: (string * string) list) = [ for n, _ in slots -> ProvidedParameter(n, typeof<string>) ]

    // ---------------------------------------------------------------- Pattern<"p1", "p2", ...>
    let pattern = ProvidedTypeDefinition(asm, ns, "Pattern", Some typeof<obj>, isErased = true)
    do pattern.DefineStaticParameters(
        [ ProvidedStaticParameter("pattern", typeof<string>)
          ProvidedStaticParameter("out2", typeof<string>, parameterDefaultValue = "")
          ProvidedStaticParameter("out3", typeof<string>, parameterDefaultValue = "")
          ProvidedStaticParameter("out4", typeof<string>, parameterDefaultValue = "") ],
        fun typeName args ->
            let patterns = args |> Array.map (fun a -> a :?> string) |> Array.filter (String.IsNullOrWhiteSpace >> not)
            let slots = analyse "Pattern" patterns
            let ruleOf (body: Expr) = <@@ PatternRuntime.rule patterns (%%body : Map<string, string> -> Recipe<ExecContext, unit>) @@>
            let t, keys = newType typeName patterns slots ruleOf <@@ PatternBuilders.rule patterns @@>

            // form A operands: `Pattern` only when there is one output (so that `..>` cannot drop
            // outputs of a multi-file pattern), `Patterns` always (for `*..>`)
            if patterns.Length = 1 then
                let p0 = patterns.[0]
                t.AddMember(ProvidedProperty("Pattern", typeof<string>, isStatic = true, getterCode = fun _ -> <@@ p0 @@>))
            let pl = List.ofArray patterns
            t.AddMember(ProvidedProperty("Patterns", typeof<string list>, isStatic = true, getterCode = fun _ -> <@@ pl @@>))

            let used = Collections.Generic.HashSet<string>(List.map fst slots)
            patterns |> Array.iteri (fun i p ->
                let nm = outputName i p
                let nm = if used.Add nm then nm else sprintf "Out%d" i
                t.AddMember(ProvidedProperty(nm, typeof<string>, getterCode = fun a -> <@@ PatternRuntime.format p (%%a.[0] : Map<string, string>) @@>)))

            if not slots.IsEmpty then
                let p0 = patterns.[0]
                t.AddMember(
                    ProvidedMethod("Format", ps slots, typeof<string>, isStatic = true,
                        invokeCode = fun a -> <@@ PatternRuntime.format p0 (PatternRuntime.make patterns keys (%%(arr a) : string[])) @@>))
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
            let ruleOf (body: Expr) = <@@ PatternRuntime.phony text (%%body : Map<string, string> -> Recipe<ExecContext, unit>) @@>
            let t, keys = newType typeName patterns slots ruleOf <@@ PatternBuilders.command text @@>
            t.AddMember(ProvidedProperty("Pattern", typeof<string>, isStatic = true, getterCode = fun _ -> <@@ text @@>))
            t.AddMember(ProvidedProperty("Name", typeof<string>, getterCode = fun a -> <@@ PatternRuntime.name text (%%a.[0] : Map<string, string>) @@>))
            if not slots.IsEmpty then
                t.AddMember(
                    ProvidedMethod("Target", ps slots, typeof<string>, isStatic = true,
                        invokeCode = fun a -> <@@ PatternRuntime.format text (PatternRuntime.make patterns keys (%%(arr a) : string[])) @@>))
            t)

    do this.AddNamespace(ns, [ pattern; command ])

[<assembly: TypeProviderAssembly>]
do ()
