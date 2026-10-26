/// Typed rule patterns declared with F# records: the groups of a pattern are the fields of a record
/// type, so a rule reads them as `m.fwk` and generates paths as `X.Format { fwk = ..; lib = .. }`,
/// both checked by the compiler. The record and the pattern are compared once, when the value is
/// created -- at script start for a top-level `let X = Target<G> "..."` -- and a mismatch fails
/// there with a message naming both (a `PatternAst.PatternException`, shown without a stack trace).
///
///   Target<'G>       a file pattern (or several outputs of one rule) with typed groups 'G
///   Target<'G, 'O>   the same, its outputs declared as the fields of the record 'O
///   Command<'G>      a command: always runs (as `command`), with typed groups 'G
///   matched          `X.rule { let! m = matched ... }`
///
/// A group carrier 'G is a record (named or anonymous) whose `string` fields are exactly the
/// pattern's named groups plus `_1`, `_2`, ... for its bare wildcards (`*`, `**`, `?`, by position);
/// `string` for a pattern with one group; `unit` for none.
///
/// Open this namespace AFTER `open Xake`: Xake brings the logging level `Command` into scope, and
/// only a namespace opened later makes `Command<_>` mean this type (otherwise FS0717 "Unexpected
/// type arguments").
namespace Xake.Typed

open Xake
open Microsoft.FSharp.Reflection

/// A checked rule pattern: the outputs' texts and tokens, the group slots, and the matchers that
/// check a generated path. Everything here fails with a PatternException prefixed by `Owner`.
type internal Spec =
    { Owner: string
      Texts: string list
      Tokens: PatternAst.Token list list
      Groups: string list
      /// Named groups, then `_1`, `_2`, ... for the top-level bare wildcards.
      Slots: string list
      /// The regex number each bare wildcard is captured under in a rule match.
      WildNums: int[]
      Matchers: (string -> (string * string) list option) list }

module internal Spec =

    let fail (message: string) : 'a = raise (PatternAst.PatternException message)

    /// Parses and checks the outputs: groups closed, one group set, no bare wildcards across outputs.
    let create (owner: string) (texts: string list) : Spec =
        if List.isEmpty texts then fail (owner + ": no pattern given")
        let parsed = texts |> List.map PatternAst.parseWithIssues
        for text, (_, issues) in List.zip texts parsed do
            for issue in issues do
                match issue with
                | PatternAst.UnclosedGroup name ->
                    fail (sprintf "%s: the group '(%s:' in \"%s\" is not closed" owner name text)
                | _ -> ()
            PatternAst.Warnings.check text
        PatternAst.checkSameGroups owner texts
        let tokens = parsed |> List.map fst
        if texts.Length > 1 && tokens |> List.exists (PatternAst.wildcards >> List.isEmpty >> not) then
            fail (owner + ": bare wildcards (*, **, ?) cannot be paired across outputs; name them, e.g. (x:*)")
        let groups = PatternAst.groupNames tokens.Head
        let wildNums = PatternAst.wildcards tokens.Head |> List.map snd |> Array.ofList
        { Owner = owner
          Texts = texts
          Tokens = tokens
          Groups = groups
          Slots = groups @ [ for i in 1 .. wildNums.Length -> sprintf "_%d" i ]
          WildNums = wildNums
          Matchers = texts |> List.map (fun t -> Path.matchGroups t "") }

    /// The key a slot's value has in a rule match: a named group by name, a wildcard by number.
    let keyOf (spec: Spec) (slot: string) =
        if slot.StartsWith "_" && not (List.contains slot spec.Groups) then string spec.WildNums.[int (slot.Substring 1) - 1]
        else slot

    /// A slot's value in a rule match (`getRuleMatches`).
    let read (spec: Spec) (matches: Map<string, string>) (slot: string) =
        match matches.TryFind (keyOf spec slot) with
        | Some v -> v
        | None -> fail (sprintf "%s: the rule match has no value for '%s'" spec.Owner slot)

    /// Values (slot, value) as a rule match holds them; null values are left out, and an error
    /// unless `partial`.
    let toMatches (spec: Spec) (partial: bool) (values: (string * string) list) =
        let missing = values |> List.filter (snd >> isNull) |> List.map fst
        if not partial && not missing.IsEmpty then
            fail (sprintf "%s: no value for %s (a null field)" spec.Owner (PatternAst.showNames missing))
        values |> List.filter (snd >> isNull >> not) |> List.map (fun (n, v) -> keyOf spec n, v) |> Map.ofList

    let private show (values: (string * string) list) =
        values |> List.map (fun (n, v) -> sprintf "%s = \"%s\"" n v) |> String.concat ", "

    /// Every output's path for these values, each matched back against its pattern: a value the
    /// pattern would not match (a '/' in a `*` group, a constraint like `(ext:c*)`) fails here.
    let formatAll (spec: Spec) (values: (string * string) list) : string list =
        let m = toMatches spec false values
        List.zip spec.Texts spec.Matchers |> List.map (fun (text, matcher) ->
            let path = PatternAst.formatMatches m text
            match matcher path |> Option.map Map.ofList with
            | Some back when m |> Map.forall (fun k v -> back.TryFind k = Some v) -> path
            | _ ->
                fail (sprintf "%s: the values %s give \"%s\", which the pattern \"%s\" does not match back (a '/' in a '*' group, or a value outside a group's constraint?)"
                        spec.Owner (show values) path text))

    /// A Fileset of every output with the given values bound; unbound (null) ones stay wildcards.
    let glob (spec: Spec) (values: (string * string) list) : Fileset.Fileset =
        let m = toMatches spec true values
        let masks =
            [ for tokens in spec.Tokens ->
                let nums = PatternAst.wildcards tokens |> List.map snd |> Array.ofList
                PatternAst.glob m.TryFind (fun i -> m.TryFind (string nums.[i - 1])) tokens ]
        Fileset.Fileset (Fileset.DefaultOptions, [ for mask in masks -> Fileset.Includes (Path.parse mask) ])

    /// The engine rule: a file rule for one output, a multi-file rule for several.
    let fileRule (spec: Spec) (body: Recipe<ExecContext, unit>) : ExecContext Rule =
        match spec.Texts with
        | [ text ] -> FileRule (text, body)
        | texts -> MultiFileRule (texts, body)

/// How a type carries named string values: a record by field name, `string` for one, `unit` for none.
type internal Codec<'T> =
    { Read: (string -> string) -> 'T
      Write: 'T -> (string * string) list
      /// Every value null (unbound).
      Any: 'T }

module internal Codec =

    let private isAnonymous (t: System.Type) = t.Name.StartsWith "<>f__AnonymousType"

    let private aliases =
        dict [ typeof<unit>, "unit"; typeof<string>, "string"; typeof<int>, "int"; typeof<int64>, "int64"
               typeof<bool>, "bool"; typeof<float>, "float"; typeof<obj>, "obj"; typeof<char>, "char" ]

    /// A type's name as F# writes it: `AsmG`, `{| fwk; lib |}`, `string * int`, `Map<string,string>`.
    let rec nameOf (t: System.Type) : string =
        match aliases.TryGetValue t with
        | true, alias -> alias
        | _ when FSharpType.IsRecord(t, true) && isAnonymous t ->
            "{| " + (FSharpType.GetRecordFields(t, true) |> Array.map (fun f -> f.Name) |> String.concat "; ") + " |}"
        | _ when FSharpType.IsTuple t -> FSharpType.GetTupleElements t |> Array.map nameOf |> String.concat " * "
        | _ when t.IsGenericType ->
            let name = t.Name.Substring(0, max 0 (t.Name.IndexOf '`'))
            name + "<" + (t.GetGenericArguments() |> Array.map nameOf |> String.concat ", ") + ">"
        | _ -> t.Name

    let private stringFields (owner: string) (t: System.Type) =
        let fields = FSharpType.GetRecordFields(t, true)
        let bad = fields |> Array.filter (fun f -> f.PropertyType <> typeof<string>) |> Array.map (fun f -> f.Name)
        if bad.Length > 0 then
            Spec.fail (sprintf "%s: the field%s %s of %s %s not string" owner (if bad.Length > 1 then "s" else "")
                        (PatternAst.showNames (List.ofArray bad)) (nameOf t) (if bad.Length > 1 then "are" else "is"))
        fields

    /// A record codec over its fields, in the record's own order.
    let private ofRecord<'T> (t: System.Type) (fields: System.Reflection.PropertyInfo[]) : Codec<'T> =
        let names = fields |> Array.map (fun f -> f.Name) |> List.ofArray
        let make = FSharpValue.PreComputeRecordConstructor(t, true)
        let get = FSharpValue.PreComputeRecordReader(t, true)
        { Read = (fun read -> make [| for n in names -> box (read n) |] :?> 'T)
          Write = (fun v -> List.zip names [ for o in get (box v) -> unbox<string> o ])
          Any = make (Array.create names.Length null) :?> 'T }

    /// The group carrier 'T for a pattern with these slots (named groups, then `_1`, `_2`, ...).
    let groups<'T> (owner: string) (slots: string list) : Codec<'T> =
        let t = typeof<'T>
        let fail (s: string) : 'a = Spec.fail (owner + ": " + s)
        if t = typeof<unit> then
            if not slots.IsEmpty then
                fail (sprintf "unit carries no groups, the pattern has %s; declare a record with these fields" (PatternAst.showNames slots))
            { Read = (fun _ -> unbox<'T> (box ())); Write = (fun _ -> []); Any = unbox<'T> (box ()) }
        elif t = typeof<string> then
            match slots with
            | [ slot ] ->
                { Read = (fun read -> unbox<'T> (box (read slot)))
                  Write = (fun v -> [ slot, unbox<string> (box v) ])
                  Any = unbox<'T> null }
            | _ -> fail (sprintf "string carries one group, the pattern has %s; declare a record with these fields%s"
                            (PatternAst.showNames slots) (if slots.IsEmpty then " (or use unit)" else ""))
        elif FSharpType.IsRecord(t, true) then      // true: private types and representations too
            let fields = stringFields owner t
            let names = fields |> Array.map (fun f -> f.Name) |> List.ofArray
            let notInRecord, notInPattern = List.except names slots, List.except slots names
            if not (notInRecord.IsEmpty && notInPattern.IsEmpty) then
                let parts =
                    [ if not notInRecord.IsEmpty then yield "not in the record: " + PatternAst.showNames notInRecord
                      if not notInPattern.IsEmpty then yield "not in the pattern: " + PatternAst.showNames notInPattern ]
                fail (sprintf "the record %s has fields %s, the pattern has %s (%s)%s"
                        (nameOf t) (PatternAst.showNames names) (PatternAst.showNames slots) (String.concat "; " parts)
                        (if notInRecord |> List.exists (fun s -> s.StartsWith "_") then "; bare wildcards are fields _1, _2, ..." else ""))
            ofRecord<'T> t fields
        elif FSharpType.IsTuple t then
            fail (sprintf "%s is a tuple; a tuple's elements are positional (a swap goes unnoticed): declare a record with the fields %s"
                    (nameOf t) (PatternAst.showNames slots))
        else fail (sprintf "%s is not a record, string or unit" (nameOf t))

    /// The outputs record 'T: its field names are the labels, its values the output patterns.
    let outputs<'T> (owner: string) : Codec<'T> =
        let t = typeof<'T>
        if not (FSharpType.IsRecord(t, true)) then
            Spec.fail (sprintf "%s: the outputs %s are not a record; declare one string field per output, e.g. { dll: string; xml: string }" owner (nameOf t))
        ofRecord<'T> t (stringFields owner t)

/// Form B marker: `let! m = matched` inside `X.rule { ... }`.
type Matched = Matched

/// Form B, `X.rule { let! m = matched ... }`: everything of `recipe { }`, plus `let! m = matched`
/// giving the groups of the rule being run as 'G. Its result is the rule.
type TypedRuleBuilder<'G> internal (read: Map<string, string> -> 'G, make: Recipe<ExecContext, unit> -> ExecContext Rule) =
    inherit RecipeBuilder()
    /// `let! m = matched`: the groups of the rule match.
    member this.Bind(_: Matched, f: 'G -> Recipe<ExecContext, 'b>) : Recipe<ExecContext, 'b> =
        this.Bind(getRuleMatches (), fun (m: Map<string, string>) -> f (read m))
    /// Makes the rule from the recipe.
    member _.Run(body: Recipe<ExecContext, unit>) : ExecContext Rule = make body

/// The form B marker value.
[<AutoOpen>]
module TypedMatch =
    /// `let! m = matched` inside `X.rule { ... }`: the typed groups of the rule being run.
    let matched = Matched

/// <summary>
/// A file pattern with typed groups: the fields of 'G are exactly the pattern's named groups
/// (plus `_1`, `_2`, ... for bare wildcards), checked when the value is created.
/// </summary>
/// <example>
/// <code>
/// type AsmG = { cfg: string; fwk: string; name: string }
/// let Asm = Target&lt;AsmG&gt; "out/(cfg:*)/(fwk:*)/(name:*).dll"
/// Asm.Rule &lt;| fun m -> recipe { ... m.fwk ... }
/// need [ Asm.Format { cfg = "Release"; fwk = "net8.0"; name = "Core" } ]
/// </code>
/// </example>
type Target<'G> internal (spec: Spec, owner: string) =
    let codec = Codec.groups<'G> owner spec.Slots
    let read (m: Map<string, string>) = codec.Read (Spec.read spec m)

    static member internal OwnerOf(texts: string list, ?outs: System.Type) =
        let args = typeof<'G> :: Option.toList outs |> List.map Codec.nameOf |> String.concat ", "
        sprintf "Target<%s> %s" args (PatternAst.showPatterns texts)

    internal new (texts: string list, owner: string) = Target<'G>(Spec.create owner texts, owner)

    /// A file pattern, e.g. `Target<AsmG> "out/(cfg:*)/(fwk:*)/(name:*).dll"`.
    new (pattern: string) = Target<'G>([ pattern ], Target<'G>.OwnerOf [ pattern ])
    /// The outputs of one multi-file rule; every pattern has the same named groups and no bare wildcards.
    new (patterns: string list) = Target<'G>(patterns, Target<'G>.OwnerOf patterns)

    /// The pattern text (the first output's, for several), as `..>` takes it.
    member _.Pattern : string = spec.Texts.Head
    /// The texts of all outputs, in order.
    member _.Patterns : string list = spec.Texts
    /// The (first) output's path for these groups. A null field, or a value the pattern would not
    /// match back (a '/' in a `*` group, a constraint like `(ext:c*)` violated), fails.
    member _.Format(groups: 'G) : string = (Spec.formatAll spec (codec.Write groups)).Head
    /// Every output's path for these groups.
    member _.Paths(groups: 'G) : string list = Spec.formatAll spec (codec.Write groups)
    /// Every output for every group set, e.g. `need (X.For [ for f in fwks -> { fwk = f; lib = "Core" } ])`.
    member _.For(groups: 'G seq) : string list = [ for g in groups do yield! Spec.formatAll spec (codec.Write g) ]
    /// Every group unbound (null): the start for `Glob`, e.g. `X.Glob { X.Any with dir = "core" }`.
    member _.Any : 'G = codec.Any
    /// The existing files of every output: bound groups take their values, null ones stay wildcards.
    member _.Glob(groups: 'G) : Fileset.Fileset = Spec.glob spec (codec.Write groups)
    /// Form D, the rule: `X.Rule <| fun m -> recipe { ... m.fwk ... }`.
    member _.Rule(body: 'G -> Recipe<ExecContext, unit>) : ExecContext Rule =
        Spec.fileRule spec (recipe {
            let! m = getRuleMatches ()
            do! body (read m)
        })
    /// Form B, the rule: `X.rule { let! m = matched ... }`.
    member _.rule : TypedRuleBuilder<'G> = TypedRuleBuilder<'G>(read, Spec.fileRule spec)

    override _.ToString() = owner

/// <summary>
/// A file pattern with typed groups 'G and typed outputs 'O: 'O is a record of the rule's output
/// patterns, one string field per output; its field names label the outputs (`(X.Make m).xml`).
/// Every output has the same named groups and no bare wildcards.
/// </summary>
/// <example>
/// <code>
/// type LibG = { fwk: string; lib: string }
/// type LibOut = { dll: string; xml: string }
/// let Lib = Target&lt;LibG, LibOut&gt; { dll = "out/(fwk:*)/(lib:*).dll"; xml = "out/(fwk:*)/(lib:*).xml" }
/// Lib.Rule &lt;| fun m -> recipe { let out = Lib.Make m in ... out.dll ... out.xml ... }
/// </code>
/// </example>
[<Sealed>]
type Target<'G, 'O> private (texts: string list, labels: string list, outs: Codec<'O>) =
    inherit Target<'G>(texts, Target<'G>.OwnerOf(texts, typeof<'O>))
    static let declare (outputs: 'O) =
        let owner = sprintf "Target<%s, %s>" (Codec.nameOf typeof<'G>) (Codec.nameOf typeof<'O>)
        let outs = Codec.outputs<'O> owner
        let pairs = outs.Write outputs
        match pairs |> List.filter (snd >> isNull) with
        | [] -> ()
        | nulls -> Spec.fail (sprintf "%s: no pattern for the output%s %s (a null field)" owner (if nulls.Length > 1 then "s" else "") (PatternAst.showNames (List.map fst nulls)))
        if pairs.IsEmpty then Spec.fail (owner + ": the outputs record has no fields")
        List.map snd pairs, List.map fst pairs, outs

    /// The outputs as a record of patterns, e.g. `Target<LibG, LibOut> { dll = "...dll"; xml = "...xml" }`.
    new (outputs: 'O) = let texts, labels, outs = declare outputs in Target<'G, 'O>(texts, labels, outs)

    /// The outputs for these groups, as the record 'O of paths: `(Lib.Make m).xml`.
    member this.Make(groups: 'G) : 'O =
        let paths = List.zip labels (this.Paths groups) |> Map.ofList
        outs.Read (fun label -> paths.[label])

/// <summary>
/// A command with typed groups: a phony pattern whose rule always runs, as with `command`
/// (it records AlwaysRerun). 'G as for `Target`; `unit` for a command without groups.
/// </summary>
/// <example>
/// <code>
/// type LockG = { name: string; cfg: string }
/// let Lock = Command&lt;LockG&gt; "lock::(name:*)::(cfg:*)"
/// Lock.Rule &lt;| fun { name = name; cfg = cfg } -> recipe { ... }
/// let Clean = Command&lt;unit&gt; "clean"
/// Clean.Rule &lt;| fun () -> recipe { ... }
/// </code>
/// </example>
[<Sealed>]
type Command<'G>(pattern: string) =
    let owner = sprintf "Command<%s> %s" (Codec.nameOf typeof<'G>) (PatternAst.showPatterns [ pattern ])
    let spec = Spec.create owner [ pattern ]
    let codec = Codec.groups<'G> owner spec.Slots
    let read (m: Map<string, string>) = codec.Read (Spec.read spec m)
    let make (body: Recipe<ExecContext, unit>) = (command pattern).Run body

    /// The pattern text, as `=>` and `command` take it.
    member _.Pattern : string = pattern
    /// The command name for these groups (what `need` takes).
    member _.Format(groups: 'G) : string = (Spec.formatAll spec (codec.Write groups)).Head
    /// The command names for every group set.
    member _.For(groups: 'G seq) : string list = [ for g in groups -> (Spec.formatAll spec (codec.Write g)).Head ]
    /// Form D, the rule: `X.Rule <| fun m -> recipe { ... }` (`fun () ->` for `Command<unit>`).
    member _.Rule(body: 'G -> Recipe<ExecContext, unit>) : ExecContext Rule =
        make (recipe {
            let! m = getRuleMatches ()
            do! body (read m)
        })
    /// Form B, the rule: `X.rule { let! m = matched ... }` (no `matched` needed for `Command<unit>`).
    member _.rule : TypedRuleBuilder<'G> = TypedRuleBuilder<'G>(read, make)

    override _.ToString() = owner
