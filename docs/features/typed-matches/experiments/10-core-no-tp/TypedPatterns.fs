namespace Xake.Typed
// NOTE: its own namespace, opened by the script AFTER `open Xake` (probes p01-p04, p15).
// Logging's union case `Level.Command` comes into scope with `open Xake`, and in an expression a
// union case wins over a type of the same name declared in `Xake` itself (namespace or a later
// AutoOpen module): `Command<string> "..."` -> FS0717 Unexpected type arguments; so does
// `Command<string>.Of "..."`. Only a later explicit `open` of another namespace shadows the case.

open Xake
//
// Experiment 10: typed patterns in Core, no type provider. Would be src/core/TypedPatterns.fs,
// compiled after ScriptFuncs.fs. Builds on the public PatternAst of 08's engine.patch.
//
//   Pattern          a rule pattern parsed and checked once (outputs, labels, groups); stringly
//                    generation/access, but a wrong name fails loudly instead of giving ""
//   Target<'G>       a file pattern whose groups are the fields of 'G (a record, a tuple of
//                    strings, string or unit) -- typed access, typed generation
//   Target<'G, 'O>   the same plus a record/tuple 'O of the outputs (by label)
//   Command<'G>      a phony (command) pattern, today's `command` semantics (AlwaysRerun)
//   matched          form B marker: `X.rule { let! m = matched ... }`
//
// Every check between the pattern string and 'G/'O runs when the value is created, i.e. at script
// start for a top-level `let Dll = Target<DllG> "..."`.

open System.Text.RegularExpressions
open Microsoft.FSharp.Reflection

/// One output of a pattern: its label ("label=pattern", else Out1, Out2, ...), text and tokens.
type PatternOutput = { Label: string; Text: string; Tokens: PatternAst.Token list }

/// The groups of a rule match, read through a Pattern: an unknown name fails with the group list.
[<Sealed>]
type PatternMatch internal (describe: string, slots: string list, read: string -> string, outputs: (string * string) list) =
    /// A group by name (`_1`, `_2`, ... for bare wildcards). An unknown name raises XakeException.
    member _.Item
        with get (name: string) =
            if List.contains name slots then read name
            else raise (XakeException (sprintf "Pattern %s has no group '%s' (it has %A)" describe name slots))
    /// This match's outputs (relative paths, in pattern order).
    member _.Outputs = outputs |> List.map snd
    /// One output by label. An unknown label raises XakeException.
    member _.Output(label: string) =
        match outputs |> List.tryFind (fst >> (=) label) with
        | Some (_, p) -> p
        | None -> raise (XakeException (sprintf "Pattern %s has no output '%s' (it has %A)" describe label (List.map fst outputs)))

/// A rule pattern parsed once and checked: syntax (balanced groups), one group set across all
/// outputs, no bare wildcards in a multi-output pattern, unique labels. Generation takes
/// (name, value) pairs; an unknown or missing name raises XakeException.
[<Sealed>]
type Pattern internal (outputs: string list, labels: bool) =
    static let labelRx = Regex(@"^(\w+)\s*=(.*)$")
    let fail fmt = Printf.kprintf (fun s -> raise (XakeException s)) fmt
    let describe = outputs |> List.map (sprintf "\"%s\"") |> String.concat ", "
    let outs =
        if List.isEmpty outputs then fail "Pattern: no pattern given"
        outputs |> List.mapi (fun i s ->
            let m = if labels then labelRx.Match s else Match.Empty
            let label, text = if m.Success then m.Groups.[1].Value, m.Groups.[2].Value.Trim() else sprintf "Out%d" (i + 1), s
            let tokens = PatternAst.parse text
            if PatternAst.unparse tokens <> text.Replace('\\', '/') then
                fail "Pattern \"%s\": a group '(name:...' is not closed (it parses as \"%s\")" text (PatternAst.unparse tokens)
            { Label = label; Text = text; Tokens = tokens })
    let first = outs.Head
    let groups = PatternAst.groupNames first.Tokens
    let wildNums = PatternAst.wildcards first.Tokens |> List.map snd |> Array.ofList
    let slots = groups @ [ for i in 1 .. wildNums.Length -> sprintf "_%d" i ]
    do
        for o in outs.Tail do
            let g = PatternAst.groupNames o.Tokens
            if Set.ofList g <> Set.ofList groups then
                fail "Pattern %s: output \"%s\" has groups %A, the first output %A (missing %A, extra %A)"
                    describe o.Text g groups (List.except g groups) (List.except groups g)
        if outs.Length > 1 && outs |> List.exists (fun o -> not (List.isEmpty (PatternAst.wildcards o.Tokens))) then
            fail "Pattern %s: bare wildcards (*, **, ?) cannot be paired across outputs; name them, e.g. (x:*)" describe
        let dups = outs |> List.countBy (fun o -> o.Label) |> List.filter (snd >> (<) 1) |> List.map fst
        if not dups.IsEmpty then fail "Pattern %s: output labels %A are used twice" describe dups
    // matchers for the round trip check of generation, built once
    let matchers = outs |> List.map (fun o -> Path.matchGroups o.Text "")
    let keyOf (slot: string) =
        if slot.StartsWith "_" && not (List.contains slot groups) then string wildNums.[int (slot.Substring 1) - 1] else slot

    /// A pattern; several outputs for a multi-file rule. "label=pattern" names an output.
    new (pattern: string) = Pattern([ pattern ], true)
    new (patterns: string list) = Pattern(patterns, true)
    /// The text of the (first) pattern, without its label.
    member _.Text = first.Text
    member _.Texts = outs |> List.map (fun o -> o.Text)
    member _.Labels = outs |> List.map (fun o -> o.Label)
    /// Named groups, in order of appearance.
    member _.Groups = groups
    /// Named groups then `_1`, `_2`, ... for the top-level bare wildcards: what generation takes.
    member _.Slots = slots
    member internal _.Describe = describe

    /// A slot's value in a rule match (`getRuleMatches`): named groups by name, wildcards by number.
    member _.Read(matches: Map<string, string>, slot: string) =
        match matches.TryFind (keyOf slot) with
        | Some v -> v
        | None -> fail "Pattern %s: the rule match has no value for '%s'" describe slot

    member private _.ToMatches(values: (string * string) list, partial: bool) =
        let names = values |> List.map fst
        let unknown = names |> List.filter (fun n -> not (List.contains n slots))
        if not unknown.IsEmpty then fail "Pattern %s: no group %A (it has %A)" describe unknown slots
        let missing = if partial then [] else slots |> List.filter (fun s -> values |> List.exists (fun (n, v) -> n = s && not (isNull v)) |> not)
        if not missing.IsEmpty then fail "Pattern %s: no value for %A" describe missing
        values |> List.filter (snd >> isNull >> not) |> List.map (fun (n, v) -> keyOf n, v) |> Map.ofList

    /// Every output's path for these values, each re-matched against its pattern: a value the
    /// pattern would not match back (a '/' in a `*` group, a constraint like (ext:c*)) fails here.
    member this.FormatAll(values: (string * string) list) : string list =
        let m = this.ToMatches(values, false)
        List.zip outs matchers |> List.map (fun (o, matcher) ->
            let path = PatternAst.formatMatches m o.Text
            match matcher path |> Option.map Map.ofList with
            | Some back when m |> Map.forall (fun k v -> back.TryFind k = Some v) -> path
            | _ -> fail "Pattern \"%s\": %A do not give a path the pattern matches back (got \"%s\")" o.Text values path)
    /// The (first) output's path for these values.
    member this.Format(values: (string * string) list) : string = (this.FormatAll values).Head
    /// A Fileset of every output with the given groups bound; the others stay wildcards.
    member this.Glob(values: (string * string) list) : Fileset.Fileset =
        let m = this.ToMatches(values, true)
        let masks =
            [ for o in outs ->
                let nums = PatternAst.wildcards o.Tokens |> List.map snd |> Array.ofList
                PatternAst.glob m.TryFind (fun i -> m.TryFind (string nums.[i - 1])) o.Tokens ]
        Fileset.Fileset (Fileset.DefaultOptions, [ for k in masks -> Fileset.Includes (Path.parse k) ])

    /// This match's group values and outputs, name-checked (see PatternMatch).
    member this.Match(matches: Map<string, string>) =
        PatternMatch(describe, slots, (fun s -> this.Read(matches, s)),
                     outs |> List.map (fun o -> o.Label, PatternAst.formatMatches matches o.Text))
    /// The engine rule: FileRule for one output, MultiFileRule for several.
    member _.FileRule(r: Recipe<ExecContext, unit>) : ExecContext Rule =
        match outs with
        | [ o ] -> FileRule (o.Text, r)
        | os -> MultiFileRule (os |> List.map (fun o -> o.Text), r)
    /// Untyped rule: `p.Rule <| fun m -> recipe { ... m.["fwk"] ... }`.
    member this.Rule(body: PatternMatch -> Recipe<ExecContext, unit>) : ExecContext Rule =
        this.FileRule(recipe {
            let! m = getRuleMatches ()
            do! body (this.Match m)
        })

/// How a type 'T carries named values: a record (by field name), a tuple of strings (by
/// position), a string (one value) or unit (none). Built once, checked against the names.
type internal Codec<'T> = { Read: (string -> string) -> 'T; Write: 'T -> (string * string) list; Any: 'T }

module internal Codec =
    /// A type's name as written in F#: AsmG, string * string, unit.
    let rec nameOf (t: System.Type) =
        if t = typeof<unit> then "unit"
        elif t = typeof<string> then "string"
        elif FSharpType.IsTuple t then FSharpType.GetTupleElements t |> Array.map nameOf |> String.concat " * "
        else t.Name
    let create<'T> (who: string) (what: string) (names: string list) : Codec<'T> =
        let t = typeof<'T>
        let fail fmt = Printf.kprintf (fun s -> raise (XakeException (who + ": " + s))) fmt
        let strings (ts: System.Type[]) = ts |> Array.forall ((=) typeof<string>)
        if t = typeof<unit> then
            if not names.IsEmpty then fail "the %s %A; unit fits none (use a record)" what names
            { Read = (fun _ -> unbox<'T> (box ())); Write = (fun _ -> []); Any = unbox<'T> (box ()) }
        elif t = typeof<string> then
            match names with
            | [ n ] -> { Read = (fun read -> unbox<'T> (box (read n))); Write = (fun v -> [ n, unbox<string> (box v) ]); Any = unbox<'T> null }
            | _ -> fail "string holds one value, the %s %A" what names
        elif FSharpType.IsRecord(t, true) then   // true: also private types and representations (probes p16, p17)
            let fields = FSharpType.GetRecordFields(t, true)
            let bad = fields |> Array.filter (fun f -> f.PropertyType <> typeof<string>) |> Array.map (fun f -> f.Name)
            if bad.Length > 0 then fail "fields %A of %s are not string" (List.ofArray bad) t.Name
            let fs = fields |> Array.map (fun f -> f.Name) |> List.ofArray
            let missing, extra = List.except fs names, List.except names fs
            if not (missing.IsEmpty && extra.IsEmpty) then
                let parts =
                    [ if not missing.IsEmpty then yield sprintf "not in the record: %A" missing
                      if not extra.IsEmpty then yield sprintf "not in the %s: %A" (what.Split(' ').[0]) extra ]
                fail "the record %s has fields %A, the %s %A (%s)" t.Name fs what names (String.concat "; " parts)
            let make = FSharpValue.PreComputeRecordConstructor(t, true)
            let get = FSharpValue.PreComputeRecordReader(t, true)
            { Read = (fun read -> make [| for f in fs -> box (read f) |] :?> 'T)
              Write = (fun v -> List.zip fs [ for o in get (box v) -> unbox<string> o ])
              Any = make (Array.create fs.Length null) :?> 'T }
        elif FSharpType.IsTuple t then
            let els = FSharpType.GetTupleElements t
            if not (strings els) then fail "a tuple of strings is expected, got %s" t.Name
            if els.Length <> names.Length then
                fail "the tuple has %d elements, the %s %d: %A (in this order)" els.Length what names.Length names
            let make = FSharpValue.PreComputeTupleConstructor t
            let get = FSharpValue.PreComputeTupleReader t
            { Read = (fun read -> make [| for n in names -> box (read n) |] :?> 'T)
              Write = (fun v -> List.zip names [ for o in get (box v) -> unbox<string> o ])
              Any = make (Array.create names.Length null) :?> 'T }
        else fail "%s is not a record, a tuple of strings, string or unit" t.FullName

/// Form B marker: `let! m = matched` inside `X.rule { ... }`.
type Matched = Matched

/// Form B: everything of `recipe { }`, plus `let! m = matched` giving the rule's typed groups 'G.
type TypedRuleBuilder<'G> internal (read: Map<string, string> -> 'G, make: Recipe<ExecContext, unit> -> ExecContext Rule) =
    inherit RecipeBuilder()
    member this.Bind(_: Matched, f: 'G -> Recipe<ExecContext, 'b>) : Recipe<ExecContext, 'b> =
        this.Bind(getRuleMatches (), fun (m: Map<string, string>) -> f (read m))
    member _.Run(body: Recipe<ExecContext, unit>) : ExecContext Rule = make body

[<AutoOpen>]
module TypedMatch =
    /// `let! m = matched` inside `X.rule { ... }`: the groups of the rule being run.
    let matched = Matched

/// A file pattern with typed groups: the fields of 'G (record by name; tuple of strings by
/// position; string for one group; unit for none) must be exactly the pattern's groups, plus
/// `_1`, `_2`, ... for bare wildcards. Checked when created.
type Target<'G> internal (pattern: Pattern) =
    let codec = Codec.create<'G> (sprintf "Target<%s> %s" (Codec.nameOf typeof<'G>) pattern.Describe) "pattern has" pattern.Slots
    let read (m: Map<string, string>) = codec.Read (fun s -> pattern.Read(m, s))
    new (pattern: string) = Target<'G>(Pattern pattern)
    new (p1: string, p2: string) = Target<'G>(Pattern [ p1; p2 ])
    new (p1: string, p2: string, p3: string) = Target<'G>(Pattern [ p1; p2; p3 ])
    /// The (first) pattern's text, as `target`/`..>` take it.
    member _.Pattern = pattern.Text
    member _.Patterns = pattern.Texts
    /// The untyped, checked pattern underneath.
    member _.Spec = pattern
    /// The (first) output's path for these groups.
    member _.Format(g: 'G) : string = pattern.Format(codec.Write g)
    /// Every output's path for these groups.
    member _.Paths(g: 'G) : string list = pattern.FormatAll(codec.Write g)
    /// Every output of every given group set: `need (X.For [ for f in fws -> { .. } ])`.
    member _.For(gs: 'G seq) : string list = [ for g in gs do yield! pattern.FormatAll(codec.Write g) ]
    /// All groups unbound (null): start for Glob, `X.Glob { X.Any with dir = "core" }`.
    member _.Any : 'G = codec.Any
    /// Existing files: null fields stay wildcards.
    member _.Glob(g: 'G) : Fileset.Fileset = pattern.Glob(codec.Write g)
    /// Form D: `X.Rule <| fun m -> recipe { ... m.fwk ... }`.
    member _.Rule(body: 'G -> Recipe<ExecContext, unit>) : ExecContext Rule =
        pattern.FileRule(recipe {
            let! m = getRuleMatches ()
            do! body (read m)
        })
    /// Form B: `X.rule { let! m = matched ... }`.
    member _.rule = TypedRuleBuilder<'G>(read, pattern.FileRule)

/// A file pattern with typed groups 'G and typed outputs 'O: a record whose fields are the
/// output labels ("dll=out/...dll"), or a tuple of strings, one per output.
[<Sealed>]
type Target<'G, 'O> internal (pattern: Pattern) =
    inherit Target<'G>(pattern)
    let outs = Codec.create<'O> (sprintf "Target<%s, %s> %s" (Codec.nameOf typeof<'G>) (Codec.nameOf typeof<'O>) pattern.Describe) "outputs are" pattern.Labels
    new (p1: string, p2: string) = Target<'G, 'O>(Pattern [ p1; p2 ])
    new (p1: string, p2: string, p3: string) = Target<'G, 'O>(Pattern [ p1; p2; p3 ])
    /// The outputs for these groups, typed: `(Lib.Make m).xml`.
    member this.Make(g: 'G) : 'O =
        let paths = List.zip pattern.Labels (this.Paths g) |> Map.ofList
        outs.Read (fun label -> paths.[label])

/// A command (phony) pattern with typed groups; `Rule`/`rule` are today's `command` (AlwaysRerun).
[<Sealed>]
type Command<'G>(pattern: string) =
    let p = Pattern([ pattern ], false)
    let codec = Codec.create<'G> (sprintf "Command<%s> \"%s\"" (Codec.nameOf typeof<'G>) pattern) "pattern has" p.Slots
    let read (m: Map<string, string>) = codec.Read (fun s -> p.Read(m, s))
    let make (r: Recipe<ExecContext, unit>) = (RuleBuilder.command pattern).Run r
    member _.Pattern = pattern
    member _.Spec = p
    /// The concrete command name for these groups (what `need` takes).
    member _.Format(g: 'G) : string = p.Format(codec.Write g)
    member _.For(gs: 'G seq) : string list = [ for g in gs -> p.Format(codec.Write g) ]
    member _.Rule(body: 'G -> Recipe<ExecContext, unit>) : ExecContext Rule =
        make (recipe {
            let! m = getRuleMatches ()
            do! body (read m)
        })
    member _.rule = TypedRuleBuilder<'G>(read, make)
