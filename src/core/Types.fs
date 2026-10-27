namespace Xake

[<AutoOpen>]
module DomainTypes =

    let private stringCompare = if Env.isUnix then System.StringComparer.Ordinal else System.StringComparer.OrdinalIgnoreCase

    /// A build target: either a file to produce or a phony named action.
    [<CustomEquality;CustomComparison>]
    type Target =
        | FileTarget of File
        | PhonyAction of string

        with
            member internal this.ShortName =
                match this with
                | FileTarget file -> file.Name
                | PhonyAction name -> name
            member internal this.FullName =
                match this with
                | FileTarget file -> file.FullName
                | PhonyAction name -> name
            
            override x.Equals(yobj) =
                match yobj with
                | :? Target as y -> stringCompare.Equals (x.FullName, y.FullName)
                | _ -> false

            override x.GetHashCode() = stringCompare.GetHashCode x.FullName
            interface System.IComparable with 
                member x.CompareTo y =
                    match y with
                    | :? Target as y -> stringCompare.Compare(x.FullName, y.FullName)
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

    /// A tracked dependency that determines whether a target needs rebuilding.
    type Dependency =
        | FileDep of File * Timestamp // regular file (such as source code file), triggers when file date/time is changed
        /// Other target, recorded by Xake databases before version 0.5 (no change stamp).
        /// Still read from old databases; judged as changed once so it is re-recorded as ArtifactDep2.
        | ArtifactDep of Target
        /// Other target (a target that has a rule) with its change stamp as observed when the
        /// dependent recorded the dependency, right after the dependee was built or found up to date:
        /// for a FileTarget the file's last write time, for a PhonyAction the completion time of
        /// its last build (BuildResult.Built). Triggers when the stamp moves or the dependee is out of date.
        | ArtifactDep2 of Target * Timestamp
        | EnvVar of string * string option // environment variable
        | Var of string * string option // any other data such as compiler version (not used yet)
        | AlwaysRerun // trigger always
        | GetFiles of Fileset * Filelist // depends on set of files. Triggers when resulting filelist is changed
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
          Built : Timestamp
          Depends : Dependency list
          Steps : StepInfo list }

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

    /// A build rule that maps a target pattern to a recipe.
    type 'ctx Rule =
        | FileRule of string * Recipe<'ctx,unit>
        | MultiFileRule of string list * Recipe<'ctx,unit>
        | PhonyRule of string * Recipe<'ctx,unit>
        | FileConditionRule of (string -> bool) * Recipe<'ctx,unit>
        /// Wraps any inner rule, delegating its up-to-date check and execution to the
        /// caller-supplied executor. Opt-in: ordinary rules are unaffected.
        | DelegatedRule of 'ctx Rule * DelegatedExecutor<'ctx>
    /// A list of build rules.
    type 'ctx Rules = Rules of 'ctx Rule list

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
