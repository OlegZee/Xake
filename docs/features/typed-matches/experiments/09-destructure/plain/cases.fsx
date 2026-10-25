// Experiment 09, plain F# (no Xake): what the language accepts as a destructuring pattern.
// Every `//#case` is its own script (this header + the case), run with the SDK 10 and SDK 8 fsi:
//   sh ../run.sh cases.fsx                                  -> output.txt
//   FSIARGS=--langversion:preview OUT=output-preview.txt sh ../run.sh cases.fsx
type DllGroups = { cfg: string; fwk: string; name: string }
let r = { cfg = "Release"; fwk = "net8.0"; name = "Core" }

//#case p01-punning
// field punning, as OCaml has it: `{ fwk; name }`
let { fwk; name } = r
printfn "fwk=%s name=%s" fwk name

//#case p02-partial-record
// a partial record pattern on an ordinary F# record: only some fields, each bound to a name
let { fwk = fwk; name = name } = r
printfn "fwk=%s name=%s" fwk name

//#case p03-partial-record-in-ce
// the same in a computation expression `let!`
async {
    let! { fwk = fwk; name = n } = async.Return r
    printfn "fwk=%s name=%s" fwk n
} |> Async.RunSynchronously

//#case p04-misspelt-label
let { fkw = fwk } = r
printfn "%s" fwk

//#case p05-anonymous-record-pattern
let a = {| fwk = "net8.0"; name = "Core" |}
let {| fwk = fwk |} = a
printfn "%s" fwk

//#case p06-dotnet-class-record-pattern
// a .NET class with properties (System.Version: Major, Minor) -- what an imported or generated type is
let { Major = major } = System.Version(1, 2)
printfn "%d" major

//#case p07-qualified-label-on-class
let { System.Version.Major = major } = System.Version(1, 2)
printfn "%d" major

//#case p08-deconstruct
// C#'s Deconstruct: F# ignores it
type M(fwk: string, name: string) =
    member _.Deconstruct(f: byref<string>, n: byref<string>) = f <- fwk; n <- name
let fwk, name = M("net8.0", "Core")
printfn "%s %s" fwk name

//#case p09-static-member-active-pattern
// an active pattern as a static member of a type (what a type provider could at best provide)
type T =
    static member (|Groups|) (r: DllGroups) = r.fwk, r.name
match r with
| T.Groups (fwk, name) -> printfn "%s %s" fwk name

//#case p10-static-member-active-pattern-opened
type T =
    static member (|Groups|) (r: DllGroups) = r.fwk, r.name
open type T
match r with
| Groups (fwk, name) -> printfn "%s %s" fwk name

//#case p11-union-named-fields
// union case fields by name: the other construct where a name is checked against a declaration
type DllMatch = DllMatch of cfg: string * fwk: string * name: string
let (DllMatch (fwk = fwk; name = name)) = DllMatch("Release", "net8.0", "Core")
printfn "%s %s" fwk name

//#case p12-union-named-fields-misspelt
type DllMatch = DllMatch of cfg: string * fwk: string * name: string
let (DllMatch (fkw = fwk)) = DllMatch("Release", "net8.0", "Core")
printfn "%s" fwk

//#case p13-two-records-share-labels
// two records with a common label: the record pattern takes the LAST declared one
type LibGroups = { fwk: string; lib: string }
let { fwk = fwk; name = name } = r
printfn "%s %s" fwk name

//#case p14-two-records-annotated
type LibGroups = { fwk: string; lib: string }
let ({ fwk = fwk; name = name }: DllGroups) = r
printfn "%s %s" fwk name

//#case p15-tuple
let fwk, name = r.fwk, r.name
let (fwk2: string), (name2: string) = r.fwk, r.name
printfn "%s %s %s %s" fwk name fwk2 name2

//#case p16-tuple-arity
let cfg, fwk = r.cfg, r.fwk, r.name
printfn "%s" fwk
