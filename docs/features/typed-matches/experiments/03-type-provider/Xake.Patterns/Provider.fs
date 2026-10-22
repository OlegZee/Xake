namespace Xake.Patterns.DesignTime

open System
open System.Reflection
open System.Text.RegularExpressions
open FSharp.Core.CompilerServices
open FSharp.Quotations
open ProviderImplementation.ProvidedTypes
open Xake
open Xake.Patterns

/// Experiment: `Xake.Pattern<"out/(fwk:*)/(lib:*).dll">` (several patterns separated by ';').
/// Erased to Map<string,string>. Members:
///   static  Text / Texts           the pattern string(s), for `..>` / `*..>`
///   static  Rule(fun m -> recipe)  registers the rule (FileRule or MultiFileRule)
///   static  Get                    Recipe<ExecContext, T>: the current rule's groups, typed
///   static  Make(g1, g2, ...)      T from group values (inverse of matching), checked at run time
///   static  Path(g1, g2, ...)      the first pattern's path for those values
///   instance <group>               one string property per named group
///   instance <ext> / Out<i>        one path property per pattern (multi-file outputs)
[<TypeProvider>]
type PatternProvider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(config, assemblyReplacementMap = [ ("Xake.Patterns.DesignTime", "Xake.Patterns") ], addDefaultProbingLocation = true)

    let ns = "Xake"
    let asm = Assembly.GetExecutingAssembly()

    let tagRegex = Regex(@"\((?'tag'\w+?)\:")
    let bareWildcard = Regex(@"\((\w+?)\:[^)]*\)|(\*\*|\*|\?)")

    let groupsOf (p: string) = [ for m in tagRegex.Matches p |> Seq.cast<Match> -> m.Groups.["tag"].Value ] |> List.distinct
    let hasBareWildcard (p: string) = bareWildcard.Matches p |> Seq.cast<Match> |> Seq.exists (fun m -> m.Groups.[2].Success)

    let outputName i (p: string) =
        let m = Regex.Match(p, @"\.(\w+)$")
        if m.Success then m.Groups.[1].Value else sprintf "Out%d" i

    let mapTy = typeof<Map<string, string>>

    let root = ProvidedTypeDefinition(asm, ns, "Pattern", Some typeof<obj>, isErased = true)

    do root.DefineStaticParameters(
        [ ProvidedStaticParameter("pattern", typeof<string>) ],
        fun typeName args ->
            let text = args.[0] :?> string
            let patterns = text.Split(';') |> Array.map (fun s -> s.Trim()) |> Array.filter ((<>) "") |> List.ofArray
            if patterns.IsEmpty then failwith "Pattern: no pattern given"

            // groups: the order of the first pattern; every group must be in every pattern,
            // otherwise a multi-file target gets an empty segment (see experiment 01, case 2)
            let names = groupsOf patterns.Head
            for p in patterns.Tail do
                let gs = groupsOf p
                let missing = names |> List.filter (fun n -> not (List.contains n gs))
                let extra = gs |> List.filter (fun n -> not (List.contains n names))
                if not missing.IsEmpty || not extra.IsEmpty then
                    failwithf "Pattern '%s': groups %A differ from the first pattern's %A (missing %A, extra %A)" p gs names missing extra
            if patterns.Length > 1 && patterns |> List.exists hasBareWildcard then
                failwith "Pattern: a multi-file pattern may not use unnamed wildcards (*, **, ?); name them, e.g. (dir:*)"

            let t = ProvidedTypeDefinition(asm, ns, typeName, Some mapTy, isErased = true)
            t.AddXmlDoc(sprintf "Typed matches of %A. Groups: %A" patterns names)

            // pattern text
            let p0 = patterns.Head
            t.AddMember(ProvidedProperty("Text", typeof<string>, isStatic = true, getterCode = fun _ -> <@@ p0 @@>))
            t.AddMember(ProvidedProperty("Texts", typeof<string list>, isStatic = true, getterCode = fun _ -> <@@ patterns @@>))

            // group properties
            for n in names do
                let n' = n
                t.AddMember(ProvidedProperty(n, typeof<string>, getterCode = fun a -> <@@ Runtime.group (%%a.[0] : Map<string, string>) n' @@>))

            // output paths (relative, as written in the pattern)
            let used = Collections.Generic.HashSet<string>(names)
            patterns |> List.iteri (fun i p ->
                let nm = outputName i p
                let nm = if used.Add nm then nm else sprintf "Out%d" i
                let p' = p
                t.AddMember(ProvidedProperty(nm, typeof<string>, getterCode = fun a -> <@@ Runtime.subst p' (%%a.[0] : Map<string, string>) @@>)))

            // Get : Recipe<ExecContext, T>
            let recipeT = ProvidedTypeBuilder.MakeGenericType(typedefof<Recipe<_, _>>, [ typeof<ExecContext>; t :> Type ])
            t.AddMember(ProvidedProperty("Get", recipeT, isStatic = true, getterCode = fun _ -> <@@ Runtime.get () @@>))

            // Rule : (T -> Recipe<ExecContext, unit>) -> Rule<ExecContext>
            let bodyT = ProvidedTypeBuilder.MakeGenericType(typedefof<_ -> _>, [ t :> Type; typeof<Recipe<ExecContext, unit>> ])
            t.AddMember(
                ProvidedMethod("Rule", [ ProvidedParameter("body", bodyT) ], typeof<ExecContext Rule>, isStatic = true,
                    invokeCode = fun a -> <@@ Runtime.rule patterns (%%a.[0] : Map<string, string> -> Recipe<ExecContext, unit>) @@>))

            // Make / Path: inverse of matching. Only when every variable part is a named group.
            if not (patterns |> List.exists hasBareWildcard) && not names.IsEmpty then
                let ps () = [ for n in names -> ProvidedParameter(n, typeof<string>) ]
                let valuesExpr (a: Expr list) = Expr.NewArray(typeof<string>, a) // string[]
                t.AddMember(
                    ProvidedMethod("Make", ps (), t, isStatic = true,
                        invokeCode = fun a -> <@@ Runtime.make patterns names (List.ofArray (%%(valuesExpr a) : string[])) @@>))
                t.AddMember(
                    ProvidedMethod("Path", ps (), typeof<string>, isStatic = true,
                        invokeCode = fun a -> <@@ Runtime.subst p0 (Runtime.make patterns names (List.ofArray (%%(valuesExpr a) : string[]))) @@>))
                // All(g1 = [..], g2 = [..]) : string list -- cross product, all outputs
                let listParams = [ for n in names -> ProvidedParameter(n, typeof<string list>) ]
                t.AddMember(
                    ProvidedMethod("All", listParams, typeof<string list>, isStatic = true,
                        invokeCode = fun a -> <@@ Runtime.all patterns names (List.ofArray (%%(Expr.NewArray(typeof<string list>, a)) : string list[])) @@>))

            // Glob(?g1, ?g2) : string list -- unbound groups become their sub-masks (a fileset mask)
            if not names.IsEmpty then
                let optParams = [ for n in names -> ProvidedParameter(n, typeof<string>, optionalValue = null) ]
                t.AddMember(
                    ProvidedMethod("Glob", optParams, typeof<string list>, isStatic = true,
                        invokeCode = fun a -> <@@ Runtime.glob patterns names (List.ofArray (%%(Expr.NewArray(typeof<string>, a)) : string[])) @@>))
            t)

    do this.AddNamespace(ns, [ root ])

[<assembly: TypeProviderAssembly>]
do ()
