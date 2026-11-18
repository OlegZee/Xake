namespace Xake

[<AutoOpen>]
module DomainTypes =

    /// A build target: either a file to produce or a phony named action.
    [<CustomEquality;CustomComparison>]
    type Target =
        | FileTarget of File
        | PhonyAction of string

        with
            /// Xake's full name: a file's `Name`, a phony's name; the identity.
            member internal this.Name =
                match this with
                | FileTarget file -> file.Name
                | PhonyAction name -> name
            /// The name the engine shows: a file relative to `root` (`File.display`), a phony as is.
            member internal this.Display (root: string) =
                match this with
                | FileTarget file -> File.display root file
                | PhonyAction name -> name
            
            override x.Equals(yobj) =
                match yobj with
                | :? Target as y -> File.comparer.Equals (x.Name, y.Name)
                | _ -> false

            override x.GetHashCode() = File.comparer.GetHashCode x.Name
            interface System.IComparable with 
                member x.CompareTo y =
                    match y with
                    | :? Target as y -> File.comparer.Compare(x.Name, y.Name)
                    | _ -> invalidArg "y" "cannot compare target to different types"

    /// Alias for System.DateTime used for file timestamps.
    type Timestamp = System.DateTime

    /// Unit of measure for milliseconds.
    [<Measure>]
    type ms

    /// How a script variable is treated as a secret.
    /// `SecretPresence` records only whether the value is set; `SecretHash` records a salted
    /// HMAC-SHA256 of the value, so a changed value rebuilds the dependents.
    type SecretMode =
        | NotSecret
        | SecretPresence
        | SecretHash

    /// Where the value of a secret variable was read from.
    type SecretSource =
        /// an environment variable (`getEnv`)
        | SecretEnv
        /// a script variable, e.g. `-d NAME=value` (`getVar`)
        | SecretArg

    /// What the build database keeps of a secret variable: never the value itself.
    type SecretDigest =
        /// the variable was not set
        | SecretAbsent
        /// the variable was set (presence only, the default)
        | SecretPresent
        /// the variable was set; HMAC-SHA256 of its UTF-8 value keyed by a random salt kept with it
        | SecretHmac of salt: byte[] * hmac: byte[]

    /// What the database knows of a file (database 0.7): the stat seen at its last observation
    /// and the run (epoch) in which its content was last seen to change. `Size` -1 is unknown;
    /// `Hash` is the first 8 bytes of the SHA-256 of the content at that stat, when computed
    /// (docs/content-cutoff.md).
    type FileState = { Stamp: Timestamp; Size: int64; Hash: uint64 option; Changed: int }

    /// A tracked dependency that determines whether a target needs rebuilding. A file or target
    /// dependency stores no stamp: the verdict compares the file's `Changed` (or a phony's
    /// `Epoch`) with the dependent's `Epoch`.
    type Dependency =
        /// a file without a rule (such as a source file)
        | FileDep of File
        /// a rule's output file or a phony target
        | TargetDep of Target
        /// depends on set of files. Triggers when resulting filelist is changed
        | GetFiles of Fileset * Filelist
        | EnvVar of string * string option // environment variable
        | Var of string * string option // any other data such as compiler version (not used yet)
        | AlwaysRerun // trigger always
        /// A secret variable (database 0.6): its name, source and a digest, never its value.
        /// Triggers when the digest no longer matches the current value.
        | SecretDep of name: string * source: SecretSource * digest: SecretDigest

    /// Timing information for one named step within a recipe execution.
    type StepInfo =
        { Name: string; Start: System.DateTime; OwnTime: int<ms>; WaitTime: int<ms> }
        with static member Empty = {Name = ""; Start = new System.DateTime(1900,1,1); OwnTime = 0<ms>; WaitTime = 0<ms>}

    /// Recorded result of building one or more targets, including dependencies and step timings.
    type BuildResult =
        { Targets : Target list
          /// the wall-clock completion time
          Built : Timestamp
          /// the run (epoch) that built it; what the verdict compares
          Epoch : int
          Depends : Dependency list
          Steps : StepInfo list
          /// the number of leading `Depends` entries that came from the rule header (`needs`)
          StaticCount : int }

    /// An async build action that threads BuildResult state through its execution.
    type Recipe<'a,'b> = Recipe of (BuildResult * 'a -> Async<BuildResult * 'b>)

    /// Caller-supplied delegated up-to-date check and execution for a delegated rule.
    /// Given the execution context, the produced targets, and a thunk that runs the inner
    /// recipe body locally and yields its BuildResult, the executor returns the BuildResult
    /// to record. The executor owns: identity-key computation, shared run-scoped result
    /// store lookup, in-flight dedup by key, publish-on-miss, and return-stored-on-hit —
    /// calling the body thunk only when it decides the work must actually run.
    /// 'ctx is instantiated to ExecContext at use sites.
    type DelegatedExecutor<'ctx> =
        'ctx -> Target list -> (unit -> Async<BuildResult>) -> Async<BuildResult>

    /// <summary>
    /// The declarations of a file rule read when the script loads, without running its body: the
    /// rule header (Xake 5.0). Written at the start of `recipe { }` with `also`, `needs`,
    /// `requires`, `noContentCutoff`; see docs/rule-header.md.
    /// </summary>
    type RuleHeader =
        { /// Outputs besides the main one (`also`), patterns over the main pattern's groups.
          Also: string list
          /// Static inputs (`needs`): one unordered set, built before the body.
          Needs: string list
          /// `Some false` for `noContentCutoff`, `Some true` for `contentCutoff`, None: the default.
          ContentCutoff: bool option
          /// Resources the engine holds while the body runs (`requires`), with their units.
          Requires: (Resource * int) list
          /// Shared headers merged in with `including`: how each is named in errors and the groups
          /// it declares, which the including rule must have.
          Included: (string * string list) list
          /// True for a header written with no body (`recipe { needs .. }`): a file rule must have
          /// a body that writes its target, so such a rule is refused when the script loads.
          HeaderOnly: bool }
        /// No declarations.
        static member Empty = { Also = []; Needs = []; ContentCutoff = None; Requires = []; Included = []; HeaderOnly = false }
        /// The resources to take, in one order for the whole process: by name, then by creation
        /// (two resources may share a name). A rule names each resource once (`Rule.validate`).
        member h.Locks : (Resource * int) list =
            h.Requires |> List.sortBy (fun (r, _) -> r.ResourceName, r.CreationOrdinal)
        /// The two headers together: lists append, a cut-off setting of the second wins.
        static member Merge (a: RuleHeader, b: RuleHeader) =
            { Also = a.Also @ b.Also
              Needs = a.Needs @ b.Needs
              ContentCutoff = (match b.ContentCutoff with Some _ -> b.ContentCutoff | None -> a.ContentCutoff)
              Requires = a.Requires @ b.Requires
              Included = a.Included @ b.Included
              HeaderOnly = a.HeaderOnly || b.HeaderOnly }

    /// <summary>
    /// A header to share between rules, made with `ruleHeader ["n"] { .. }` and merged into a rule
    /// with `including h`. `Groups` are the groups of the including rule that its patterns use.
    /// </summary>
    type SharedHeader =
        internal { Name: string; Groups: string list; Items: RuleHeader }
        /// How errors name it: `ruleHeader [n] (needs "obj/(n:*).rsp")`.
        member h.Description = h.Name
        /// The groups it declares.
        member h.DeclaredGroups = h.Groups
        /// Its header items.
        member h.Header = h.Items

    /// A rule body with its header: what `recipe { also ..; needs ..; body }` gives. Only the
    /// file-rule forms accept it; it cannot be bound inside another recipe.
    type Headed<'ctx> = Headed of RuleHeader * Recipe<'ctx, unit>

    /// A build rule that maps a target pattern to a recipe.
    type 'ctx Rule =
        /// A file rule: its output patterns (the first is the main one; `*..>` gives several, `also`
        /// in the header adds more), its header and its recipe.
        | FileRule of outputs: string list * header: RuleHeader * Recipe<'ctx,unit>
        | PhonyRule of string * Recipe<'ctx,unit>
        /// A rule for the files a condition accepts; its header has no `also` and no groups.
        | FileConditionRule of (string -> bool) * RuleHeader * Recipe<'ctx,unit>
        /// Wraps any inner rule, delegating its up-to-date check and execution to the
        /// caller-supplied executor. Opt-in: ordinary rules are unaffected.
        | DelegatedRule of 'ctx Rule * DelegatedExecutor<'ctx>
    /// A list of build rules.
    type 'ctx Rules = Rules of 'ctx Rule list

    module Rule =
        /// The output patterns of a file rule, the main one first: its outputs and its header's `also`.
        let outputs (patterns: string list) (header: RuleHeader) = patterns @ header.Also

        let private fail (message: string) : 'a = raise (PatternAst.PatternException message)
        let private showNames (names: string list) = "[" + String.concat ", " names + "]"

        /// Makes a shared header (`ruleHeader groups { .. }`), checking that every group its patterns
        /// use is declared and every declared group is used.
        let share (groups: string list) (header: RuleHeader) : SharedHeader =
            let items =
                [ for p in header.Needs -> "needs", p
                  for p in header.Also -> "also", p ]
            let name =
                let first = match items with (op, p) :: _ -> sprintf " (%s \"%s\"%s)" op p (if items.Length > 1 then ", ..." else "") | [] -> ""
                sprintf "ruleHeader %s%s" (showNames groups) first
            let used =
                [ for op, p in items do
                    for g in PatternAst.Rule.parse p |> PatternAst.groupNames -> op, p, g ]
            for op, p, g in used do
                if not (List.contains g groups) then
                    fail (sprintf "%s: %s \"%s\" uses the group %s, which is not declared; declared: %s (write ruleHeader [\"%s\"; ..])" name op p g (showNames groups) g)
            for g in groups do
                if not (used |> List.exists (fun (_, _, u) -> u = g)) then
                    fail (sprintf "%s: the group %s is declared but no pattern uses it" name g)
            for g, n in groups |> List.countBy id do
                if n > 1 then fail (sprintf "%s: the group %s is declared twice" name g)
            { Name = name; Groups = groups; Items = { header with Included = [] } }

        /// Checks a header against the groups its patterns may use (None: a condition rule, no groups).
        let private checkHeader (owner: string) (groups: string list option) (header: RuleHeader) =
            for (shared, declared) in header.Included do
                match groups with
                | None when not declared.IsEmpty ->
                    fail (sprintf "%s includes %s, which uses the group%s %s; a condition rule has no pattern and no groups" owner shared (if declared.Length > 1 then "s" else "") (String.concat ", " declared))
                | Some known ->
                    match declared |> List.filter (fun g -> not (List.contains g known)) with
                    | [] -> ()
                    | missing ->
                        fail (sprintf "%s includes %s, which uses the group%s %s; the rule has the groups %s" owner shared (if missing.Length > 1 then "s" else "") (String.concat ", " missing) (showNames known))
                | None -> ()
            for p in header.Needs do
                let names = PatternAst.Rule.parse p |> PatternAst.groupNames
                match groups with
                | None when not names.IsEmpty ->
                    fail (sprintf "%s: needs \"%s\" uses the groups %s, but a condition rule has no pattern and no groups; write a plain path or fileset" owner p (showNames names))
                | Some known ->
                    match names |> List.filter (fun n -> not (List.contains n known)) with
                    | [] -> ()
                    | unknown ->
                        fail (sprintf "%s: needs \"%s\" uses the group%s %s, which the rule's pattern does not have (its groups: %s)" owner p (if unknown.Length > 1 then "s" else "") (showNames unknown) (showNames known))
                | None -> ()
            for (r, units) in header.Requires do
                if units < 1 then fail (sprintf "%s: requires %s with %d units; it takes at least 1" owner r.ResourceName units)
            match header.Requires |> List.countBy (fun (r, _) -> r.ResourceName) |> List.filter (fun (_, n) -> n > 1) with
            | [] -> ()
            | (name, _) :: _ ->
                fail (sprintf "%s requires the resource %s more than once (requires, requiring or an included header); name it once, with the units it needs: requires (%s, n)" owner name name)

        /// Checks the patterns of a rule (spec 7.1): every pattern parses (legacy-syntax warnings are
        /// logged once), and the outputs of a multi-output rule (`*..>` or `also`) have the same named
        /// groups and nothing that cannot be filled in from a match (XP013, XP014). The header's
        /// `needs` use only the main pattern's groups; a condition rule has no `also` and no groups.
        /// Raises `PatternAst.PatternException`; it is applied where every rule enters a script
        /// (`XakeScriptBuilder`), whichever way the rule was built.
        let rec validate (rule: 'ctx Rule) : unit =
            match rule with
            | PhonyRule (pattern, _) -> PatternAst.Rule.check pattern
            | FileRule (patterns, header, _) ->
                let all = outputs patterns header
                match all with
                | [] -> fail "a file rule has no output pattern"
                | main :: _ ->
                    all |> List.iter PatternAst.Rule.check
                    if all.Length > 1 then PatternAst.checkOutputs all
                    let groups = PatternAst.Rule.parse main |> PatternAst.groupNames
                    checkHeader (sprintf "rule \"%s\"" main) (Some groups) header
                    match header.Needs |> List.filter (fun n -> List.contains n all) with
                    | [] -> ()
                    | own :: _ ->
                        fail (sprintf "rule \"%s\": needs \"%s\" names an output of the same rule; a rule cannot need what it writes" main own)
                    if header.HeaderOnly then
                        fail (sprintf "rule \"%s\" has a header and no body: a file rule must have a body that writes its target. To run inputs before something else, put them in the needs of the rule that uses them, or use a phony rule (=>) with need" main)
            | FileConditionRule (_, header, _) ->
                if not header.Also.IsEmpty then
                    fail (sprintf "a condition rule (..?>) cannot have an also (%s): it has no pattern to name a side output from" (String.concat ", " header.Also))
                checkHeader "a condition rule (..?>)" None header
                if header.HeaderOnly then
                    fail "a condition rule (..?>) has a header and no body: a file rule must have a body that writes its target"
            | DelegatedRule (inner, _) -> validate inner

    /// How to pick among the rules that match a target.
    type RuleMatchMode =
        /// The most specific matching rule wins; ties fall back to last declared (default).
        | MostSpecific
        /// Legacy order: the last declared matching rule wins, specificity ignored.
        /// Restored by `ruleMatch LastDeclared` in the script; deprecated, removed after one minor.
        | LastDeclared

    /// Defines common exception type
    exception XakeException of string

/// <summary>
/// A message to a progress reporter.
/// </summary>
type ProgressMessage =
    | Begin of System.TimeSpan
    | Progress of System.TimeSpan * int
    | End

/// Names declared secret in this process and the digests recorded for them.
/// A name is declared by a secret variable (`|> secret`, `Var.secret`, `varschema`); from then on
/// a plain `getEnv`/`getVar` of that name is recorded as a secret too (the name decides).
module internal Secrets =

    open System.Collections.Concurrent

    let private declared = ConcurrentDictionary<string, SecretMode>(System.StringComparer.Ordinal)

    /// Declares a name secret; a hashed declaration wins over a presence-only one.
    let declare (name: string) (mode: SecretMode) =
        if mode <> NotSecret && not (System.String.IsNullOrEmpty name) then
            declared.AddOrUpdate(name, mode, fun _ old -> if old = SecretHash then old else mode) |> ignore

    /// The secret mode declared for a name, `NotSecret` if none.
    let modeOf (name: string) =
        match name with
        | null -> NotSecret
        | _ ->
            match declared.TryGetValue name with
            | true, mode -> mode
            | _ -> NotSecret

    let isSecret name = modeOf name <> NotSecret

    /// The salt of the digests recorded by this process (16 random bytes); stored with each digest.
    let private processSalt =
        lazy (
            let salt = Array.zeroCreate<byte> 16
            use rng = System.Security.Cryptography.RandomNumberGenerator.Create()
            rng.GetBytes salt
            salt)

    let hmac (salt: byte[]) (value: string) =
        use h = new System.Security.Cryptography.HMACSHA256(salt)
        h.ComputeHash(System.Text.Encoding.UTF8.GetBytes value)

    /// The digest of a value for the database.
    let digest (mode: SecretMode) (value: string option) =
        match value, mode with
        | None, _ -> SecretAbsent
        | Some v, SecretHash -> let salt = processSalt.Value in SecretHmac (salt, hmac salt v)
        | Some _, _ -> SecretPresent

    /// Compares a recorded digest with the current value: None if it still matches, else the
    /// reason, which names the variable and never shows a value.
    let changeReason (name: string) (recorded: SecretDigest) (current: string option) =
        match recorded, current with
        | SecretAbsent, None -> None
        | SecretAbsent, Some _ -> Some (sprintf "Secret variable %s is now set" name)
        | (SecretPresent | SecretHmac _), None -> Some (sprintf "Secret variable %s is now unset" name)
        | SecretPresent, Some _ -> None
        | SecretHmac (salt, h), Some v ->
            if hmac salt v = h then None else Some (sprintf "Secret variable %s changed" name)

    /// The record of an env var or script var read under a name, as a secret if the name is declared one.
    let toDependency (source: SecretSource) (name: string) (value: string option) =
        match modeOf name, source with
        | NotSecret, SecretEnv -> EnvVar (name, value)
        | NotSecret, SecretArg -> Var (name, value)
        | mode, _ -> SecretDep (name, source, digest mode value)

    /// The message refusing `-d NAME=value` for an environment-only secret variable.
    let fromEnvOnly (name: string) (envName: string) =
        sprintf "Variable '%s' is secret: secret variables come from the environment, set %s instead of passing -d %s=..." name envName name

    /// Turns a clear-value record of a now-declared secret into a SecretDep (old databases).
    let scrub (dep: Dependency) =
        match dep with
        | EnvVar (name, value) when isSecret name -> toDependency SecretEnv name value
        | Var (name, value) when isSecret name -> toDependency SecretArg name value
        | d -> d
