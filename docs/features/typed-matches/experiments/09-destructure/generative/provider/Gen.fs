namespace Exp09.Generative

open System.Reflection
open FSharp.Core.CompilerServices
open FSharp.Quotations
open ProviderImplementation.ProvidedTypes

/// Experiment 09: is a GENERATIVE provided type something an F# record pattern accepts?
/// `Gen<"cfg,fwk,name">` generates a real .NET class into the consumer's assembly: one field and one
/// get-only property per name, and a constructor taking all of them. (The erased types of 03-08 are
/// not even classes at run time.)
[<TypeProvider>]
type GenProvider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(config)

    let ns = "Exp09.Generative"
    let asm = Assembly.GetExecutingAssembly()
    let root = ProvidedTypeDefinition(asm, ns, "Gen", Some typeof<obj>, isErased = false)

    do root.DefineStaticParameters(
        [ ProvidedStaticParameter("names", typeof<string>) ],
        fun typeName args ->
            let names = (args.[0] :?> string).Split(',') |> Array.map (fun s -> s.Trim()) |> List.ofArray
            let pasm = ProvidedAssembly()
            let t = ProvidedTypeDefinition(pasm, ns, typeName, Some typeof<obj>, isErased = false)
            let fields = [ for n in names -> ProvidedField("_" + n, typeof<string>) ]
            for f in fields do t.AddMember f
            for n, f in List.zip names fields do
                t.AddMember(ProvidedProperty(n, typeof<string>, getterCode = fun a -> Expr.FieldGet(a.[0], f)))
            let ctor =
                ProvidedConstructor(
                    [ for n in names -> ProvidedParameter(n, typeof<string>) ],
                    invokeCode = fun a ->
                        List.zip fields (List.tail a)
                        |> List.fold (fun acc (f, v) -> Expr.Sequential(acc, Expr.FieldSet(a.[0], f, v))) <@@ () @@>)
            t.AddMember ctor
            pasm.AddTypes [ t ]
            t)

    do this.AddNamespace(ns, [ root ])

[<assembly: TypeProviderAssembly>]
do ()
