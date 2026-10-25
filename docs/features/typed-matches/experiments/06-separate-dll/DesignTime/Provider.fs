namespace Xake.Patterns.DesignTime

open System
open System.Reflection
open System.Text.RegularExpressions
open FSharp.Core.CompilerServices
open FSharp.Quotations
open ProviderImplementation.ProvidedTypes
open Xake
open Xake.Patterns

/// Experiment 05: experiment 03's provider with its own regex parsing replaced by the engine's
/// `PatternAst` (sliced from src/core/Path.fs) plus the proposed generation functions
/// (`PatternAstGen`). Design-time only: the provided members are erased to calls into
/// `Xake.Patterns.PatternRuntime` (in the real design: a module of Xake.dll).
///
/// `Pattern<"p1", "p2", ...>` (multi-file outputs as separate static parameters, brief decision 2),
/// erased to Map<string,string>. Bare wildcards are positional (decision 6): properties and
/// parameters `_1`, `_2`, ...; at run time they are read under their regex group numbers.
[<TypeProvider>]
type PatternProvider(config: TypeProviderConfig) as this =
    // the provided types are declared in this dll; the map makes the compiler see them as the
    // runtime dll's (without it: FS1108 "add a reference to assembly '...DesignTime'")
    inherit TypeProviderForNamespaces(config, assemblyReplacementMap = [ ("Xake.Patterns.DesignTime", "Xake.Patterns") ], addDefaultProbingLocation = true)

    let ns = "Xake"
    let asm = Assembly.GetExecutingAssembly()
    let mapTy = typeof<Map<string, string>>

    let outputName i (p: string) =
        let m = Regex.Match(p, @"\.(\w+)$")
        if m.Success then m.Groups.[1].Value else sprintf "Out%d" i

    let root = ProvidedTypeDefinition(asm, ns, "Pattern", Some typeof<obj>, isErased = true)

    do root.DefineStaticParameters(
        [ ProvidedStaticParameter("pattern", typeof<string>)
          ProvidedStaticParameter("out2", typeof<string>, parameterDefaultValue = "")
          ProvidedStaticParameter("out3", typeof<string>, parameterDefaultValue = "")
          ProvidedStaticParameter("out4", typeof<string>, parameterDefaultValue = "") ],
        fun typeName args ->
            let patterns = args |> Array.map (fun a -> a :?> string) |> Array.filter (String.IsNullOrWhiteSpace >> not)
            // the one parser: everything below is read off the engine's token list
            let asts = patterns |> Array.map PatternAst.parse
            for p, toks in Array.zip patterns asts do
                // the same regex the engine will build: a malformed pattern fails here, not at run time
                try Regex("^" + PatternAst.toRegex toks + "$") |> ignore
                with e -> failwithf "Pattern '%s' does not compile to a regex: %s" p e.Message

            let names = PatternAstGen.groupNames asts.[0]
            for p, toks in Array.zip patterns asts |> Array.skip 1 do
                let gs = PatternAstGen.groupNames toks
                let missing = names |> List.except gs
                let extra = gs |> List.except names
                if not missing.IsEmpty || not extra.IsEmpty then
                    failwithf "Pattern '%s': groups %A differ from the first pattern's %A (missing %A, extra %A)" p gs names missing extra
            let wilds = PatternAstGen.wildcards asts.[0]
            if patterns.Length > 1 && asts |> Array.exists (PatternAstGen.wildcards >> List.isEmpty >> not) then
                failwith "Pattern: a multi-file pattern may not use unnamed wildcards (*, **, ?); name them, e.g. (dir:*)"

            // slot = (member/parameter name, run-time key); wildcards by position
            let slots =
                [ for n in names -> n, n ]
                @ [ for i, (_, num) in List.indexed wilds -> sprintf "_%d" (i + 1), string num ]
            let keys = slots |> List.map snd |> Array.ofList

            let t = ProvidedTypeDefinition(asm, ns, typeName, Some mapTy, isErased = true)
            t.AddXmlDoc(sprintf "Typed matches of %A. Slots: %A" patterns (List.map fst slots))

            let p0 = patterns.[0]
            t.AddMember(ProvidedProperty("Text", typeof<string>, isStatic = true, getterCode = fun _ -> <@@ p0 @@>))
            t.AddMember(ProvidedProperty("Texts", typeof<string[]>, isStatic = true, getterCode = fun _ -> <@@ patterns @@>))

            for name, key in slots do
                t.AddMember(ProvidedProperty(name, typeof<string>, getterCode = fun a -> <@@ PatternRuntime.group (%%a.[0] : Map<string, string>) key @@>))

            let used = Collections.Generic.HashSet<string>(List.map fst slots)
            patterns |> Array.iteri (fun i p ->
                let nm = outputName i p
                let nm = if used.Add nm then nm else sprintf "Out%d" i
                t.AddMember(ProvidedProperty(nm, typeof<string>, getterCode = fun a -> <@@ PatternRuntime.format p (%%a.[0] : Map<string, string>) @@>)))

            let recipeT = ProvidedTypeBuilder.MakeGenericType(typedefof<Recipe<_, _>>, [ typeof<ExecContext>; t :> Type ])
            t.AddMember(ProvidedProperty("Get", recipeT, isStatic = true, getterCode = fun _ -> <@@ PatternRuntime.get () @@>))

            let bodyT = ProvidedTypeBuilder.MakeGenericType(typedefof<_ -> _>, [ t :> Type; typeof<Recipe<ExecContext, unit>> ])
            t.AddMember(
                ProvidedMethod("Rule", [ ProvidedParameter("body", bodyT) ], typeof<ExecContext Rule>, isStatic = true,
                    invokeCode = fun a -> <@@ PatternRuntime.rule patterns (%%a.[0] : Map<string, string> -> Recipe<ExecContext, unit>) @@>))

            if not slots.IsEmpty then
                let ps () = [ for n, _ in slots -> ProvidedParameter(n, typeof<string>) ]
                let arr (a: Expr list) = Expr.NewArray(typeof<string>, a)
                t.AddMember(
                    ProvidedMethod("Make", ps (), t, isStatic = true,
                        invokeCode = fun a -> <@@ PatternRuntime.make patterns keys (%%(arr a) : string[]) @@>))
                t.AddMember(
                    ProvidedMethod("Format", ps (), typeof<string>, isStatic = true,
                        invokeCode = fun a -> <@@ PatternRuntime.format p0 (PatternRuntime.make patterns keys (%%(arr a) : string[])) @@>))
                t.AddMember(
                    ProvidedMethod("All", [ for n, _ in slots -> ProvidedParameter(n, typeof<string list>) ], typeof<string list>, isStatic = true,
                        invokeCode = fun a -> <@@ PatternRuntime.all patterns keys (%%(Expr.NewArray(typeof<string list>, a)) : string list[]) @@>))
                let opt () = [ for n, _ in slots -> ProvidedParameter(n, typeof<string>, optionalValue = null) ]
                t.AddMember(
                    ProvidedMethod("Glob", opt (), typeof<Fileset.Fileset>, isStatic = true,
                        invokeCode = fun a -> <@@ PatternRuntime.glob patterns keys (%%(arr a) : string[]) @@>))
                t.AddMember(
                    ProvidedMethod("GlobMasks", opt (), typeof<string list>, isStatic = true,
                        invokeCode = fun a -> <@@ PatternRuntime.globMasks patterns keys (%%(arr a) : string[]) @@>))
            t)

    do this.AddNamespace(ns, [ root ])

[<assembly: TypeProviderAssembly>]
do ()
