module Xake.Patterns.AssemblyInfo

open FSharp.Core.CompilerServices

// In the real design both attributes go on Xake.dll (src/core): the compiler finds the design-time
// dll by this name, next to the runtime dll or under typeproviders/fsharp41/<tfm> of its package.
[<assembly: TypeProviderAssembly("Xake.Patterns.DesignTime")>]
#if AST_VIA_IVT
[<assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Xake.Patterns.DesignTime")>]
#endif
do ()
