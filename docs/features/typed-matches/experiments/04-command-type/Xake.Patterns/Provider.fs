namespace Xake.Patterns.DesignTime

open System
open System.Reflection
open System.Text.RegularExpressions
open FSharp.Core.CompilerServices
open FSharp.Quotations
open ProviderImplementation.ProvidedTypes
open Xake
open Xake.Patterns

/// Experiment 04: experiment 03's `Xake.Pattern<"...">` plus a second provided type,
/// `Xake.Command<"lock::(name:*)::(cfg:*)">`, for phony rules. Both erase to Map<string,string>
/// and share the parser and the group members; only the registration and generation members
/// differ. The Pattern part is unchanged from 03 (member names Make/Path/All/Glob kept).
[<TypeProvider>]
type PatternProvider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(config, assemblyReplacementMap = [ ("Xake.Patterns.DesignTime", "Xake.Patterns") ], addDefaultProbingLocation = true)

    let ns = "Xake"
    let asm = Assembly.GetExecutingAssembly()

    // ---------------------------------------------------------------- shared (both types)
    let tagRegex = Regex(@"\((?'tag'\w+?)\:")
    let bareWildcard = Regex(@"\((\w+?)\:[^)]*\)|(\*\*|\*|\?)")

    let groupsOf (p: string) = [ for m in tagRegex.Matches p |> Seq.cast<Match> -> m.Groups.["tag"].Value ] |> List.distinct
    let hasBareWildcard (p: string) = bareWildcard.Matches p |> Seq.cast<Match> |> Seq.exists (fun m -> m.Groups.[2].Success)

    let mapTy = typeof<Map<string, string>>

    /// The erased type, its group properties, Text and Get: identical for files and phony names.
    let newType typeName (patterns: string list) (names: string list) kind =
        let t = ProvidedTypeDefinition(asm, ns, typeName, Some mapTy, isErased = true)
        t.AddXmlDoc(sprintf "Typed matches of %s %A. Groups: %A" kind patterns names)
        let p0 = patterns.Head
        t.AddMember(ProvidedProperty("Text", typeof<string>, isStatic = true, getterCode = fun _ -> <@@ p0 @@>))
        for n in names do
            let n' = n
            t.AddMember(ProvidedProperty(n, typeof<string>, getterCode = fun a -> <@@ Runtime.group (%%a.[0] : Map<string, string>) n' @@>))
        let recipeT = ProvidedTypeBuilder.MakeGenericType(typedefof<Recipe<_, _>>, [ typeof<ExecContext>; t :> Type ])
        t.AddMember(ProvidedProperty("Get", recipeT, isStatic = true, getterCode = fun _ -> <@@ Runtime.get () @@>))
        t

    let bodyType (t: ProvidedTypeDefinition) =
        ProvidedTypeBuilder.MakeGenericType(typedefof<_ -> _>, [ t :> Type; typeof<Recipe<ExecContext, unit>> ])

    let valueParams names = [ for n in names -> ProvidedParameter(n, typeof<string>) ]
    let listParams names = [ for n in names -> ProvidedParameter(n, typeof<string list>) ]
    let valuesExpr (a: Expr list) = Expr.NewArray(typeof<string>, a)
    let listsExpr (a: Expr list) = Expr.NewArray(typeof<string list>, a)

    /// Make(g1, ..) : T  and  <all>(g1 = [..], ..) : string list -- the same code for both types.
    let addMakeAndCross (t: ProvidedTypeDefinition) (patterns: string list) (names: string list) crossName =
        t.AddMember(
            ProvidedMethod("Make", valueParams names, t, isStatic = true,
                invokeCode = fun a -> <@@ Runtime.make patterns names (List.ofArray (%%(valuesExpr a) : string[])) @@>))
        t.AddMember(
            ProvidedMethod(crossName, listParams names, typeof<string list>, isStatic = true,
                invokeCode = fun a -> <@@ Runtime.all patterns names (List.ofArray (%%(listsExpr a) : string list[])) @@>))

    // ---------------------------------------------------------------- Pattern<> (as in 03)
    let outputName i (p: string) =
        let m = Regex.Match(p, @"\.(\w+)$")
        if m.Success then m.Groups.[1].Value else sprintf "Out%d" i

    let pattern = ProvidedTypeDefinition(asm, ns, "Pattern", Some typeof<obj>, isErased = true)
    do pattern.DefineStaticParameters(
        [ ProvidedStaticParameter("pattern", typeof<string>) ],
        fun typeName args ->
            let text = args.[0] :?> string
            let patterns = text.Split(';') |> Array.map (fun s -> s.Trim()) |> Array.filter ((<>) "") |> List.ofArray
            if patterns.IsEmpty then failwith "Pattern: no pattern given"
            let names = groupsOf patterns.Head
            for p in patterns.Tail do
                let gs = groupsOf p
                let missing = names |> List.filter (fun n -> not (List.contains n gs))
                let extra = gs |> List.filter (fun n -> not (List.contains n names))
                if not missing.IsEmpty || not extra.IsEmpty then
                    failwithf "Pattern '%s': groups %A differ from the first pattern's %A (missing %A, extra %A)" p gs names missing extra
            if patterns.Length > 1 && patterns |> List.exists hasBareWildcard then
                failwith "Pattern: a multi-file pattern may not use unnamed wildcards (*, **, ?); name them, e.g. (dir:*)"

            let t = newType typeName patterns names "file pattern"
            t.AddMember(ProvidedProperty("Texts", typeof<string list>, isStatic = true, getterCode = fun _ -> <@@ patterns @@>))

            // output paths (file-specific)
            let used = Collections.Generic.HashSet<string>(names)
            patterns |> List.iteri (fun i p ->
                let nm = outputName i p
                let nm = if used.Add nm then nm else sprintf "Out%d" i
                let p' = p
                t.AddMember(ProvidedProperty(nm, typeof<string>, getterCode = fun a -> <@@ Runtime.subst p' (%%a.[0] : Map<string, string>) @@>)))

            t.AddMember(
                ProvidedMethod("Rule", [ ProvidedParameter("body", bodyType t) ], typeof<ExecContext Rule>, isStatic = true,
                    invokeCode = fun a -> <@@ Runtime.rule patterns (%%a.[0] : Map<string, string> -> Recipe<ExecContext, unit>) @@>))

            if not (patterns |> List.exists hasBareWildcard) && not names.IsEmpty then
                addMakeAndCross t patterns names "All"
                let p0 = patterns.Head
                t.AddMember(
                    ProvidedMethod("Path", valueParams names, typeof<string>, isStatic = true,
                        invokeCode = fun a -> <@@ Runtime.subst p0 (Runtime.make patterns names (List.ofArray (%%(valuesExpr a) : string[]))) @@>))

            // Glob (file-specific: a fileset mask)
            if not names.IsEmpty then
                let optParams = [ for n in names -> ProvidedParameter(n, typeof<string>, optionalValue = null) ]
                t.AddMember(
                    ProvidedMethod("Glob", optParams, typeof<string list>, isStatic = true,
                        invokeCode = fun a -> <@@ Runtime.glob patterns names (List.ofArray (%%(valuesExpr a) : string[])) @@>))
            t)

    // ---------------------------------------------------------------- Command<> (phony names)
    // One name per rule (the engine has no multi-name phony rule): no Texts, no output paths,
    // no Glob (nothing on disk to glob). Rule registers a PhonyRule; Target/For give names
    // for `need` and `<==`; Name is the matched action's own name; Need demands one.
    let command = ProvidedTypeDefinition(asm, ns, "Command", Some typeof<obj>, isErased = true)
    do command.DefineStaticParameters(
        [ ProvidedStaticParameter("name", typeof<string>) ],
        fun typeName args ->
            let text = (args.[0] :?> string).Trim()
            if text = "" then failwith "Command: no name given"
            let names = groupsOf text
            let t = newType typeName [ text ] names "phony name"

            t.AddMember(ProvidedProperty("Name", typeof<string>, getterCode = fun a -> <@@ Runtime.name text (%%a.[0] : Map<string, string>) @@>))
            t.AddMember(
                ProvidedMethod("Rule", [ ProvidedParameter("body", bodyType t) ], typeof<ExecContext Rule>, isStatic = true,
                    invokeCode = fun a -> <@@ Runtime.phony text (%%a.[0] : Map<string, string> -> Recipe<ExecContext, unit>) @@>))

            if not (hasBareWildcard text) then
                addMakeAndCross t [ text ] names "For"
                t.AddMember(
                    ProvidedMethod("Target", valueParams names, typeof<string>, isStatic = true,
                        invokeCode = fun a -> <@@ Runtime.subst text (Runtime.make [ text ] names (List.ofArray (%%(valuesExpr a) : string[]))) @@>))
                t.AddMember(
                    ProvidedMethod("Need", valueParams names, typeof<Recipe<ExecContext, unit>>, isStatic = true,
                        invokeCode = fun a -> <@@ Runtime.needOne text names (List.ofArray (%%(valuesExpr a) : string[])) @@>))
            t)

    do this.AddNamespace(ns, [ pattern; command ])

[<assembly: TypeProviderAssembly>]
do ()
