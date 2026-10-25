module Xake.Patterns.AssemblyInfo

open FSharp.Core.CompilerServices

// The only place that names the design-time dll. Xake.dll has no such attribute, so a compilation
// that references Xake.dll alone never looks for the design-time dll.
[<assembly: TypeProviderAssembly("Xake.Patterns.DesignTime")>]
do ()
