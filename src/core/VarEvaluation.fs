/// Variable evaluation (docs/features/vars-precedence/spec.md): the declarations recorded by the
/// `var`/`varschema` ops are evaluated here, once, at launch; the per-demand checks of the engine,
/// the diagnostics, and the banner and debug-dump views of the variable set.
module internal Xake.VarEvaluation

/// How the diagnostics name a definition of a variable (spec E-2, E-7).
let constantLabel (name: string) = sprintf "var \"%s\" \"...\"" name
let ruleLabel (name: string) = sprintf "var \"%s\" [...]" name
let fieldLabel (field: string) = sprintf "varschema field %s" field

/// A key in UPPER_SNAKE: `_` between a lower-case letter and an upper-case one, then upper case
/// (`buildConfig` -> `BUILD_CONFIG`).
let toEnvName (key: string) : string =
    System.Text.RegularExpressions.Regex.Replace(key, "(?<=[a-z])(?=[A-Z])", "_").ToUpperInvariant()

/// The implicit environment name of a key (spec P-1): `XAKE__` + the key in UPPER_SNAKE, every
/// character other than `A`-`Z`, `0`-`9` and `_` replaced by `_` (`netfx-target` -> `XAKE__NETFX_TARGET`).
let implicitEnvName (key: string) : string =
    let valid (c: char) = (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c = '_'
    "XAKE__" + System.String(toEnvName key |> Seq.map (fun c -> if valid c then c else '_') |> Array.ofSeq)

/// The declaration of a `var "N" [sources]` rule: a string in both scopes, never required;
/// the secret markers set its mode (`SecretHashed` wins), `Cli` its external name; markers
/// are not kept as sources. A list without an `Env` source gets the implicit `Env "XAKE__<NAME>"`
/// first (spec P-1, P-2).
let ruleDecl (name: string) (sources: VarSource list) =
    let cliNames = sources |> List.choose (function Cli ext -> Some ext | _ -> None)
    let sources' =
        sources |> List.filter (function
            | Env _ | Default _ -> true
            | Cli _ -> cliNames.Length > 1      // kept for the launch error (two command-line names)
            | Secret | SecretHashed -> false)
    let sources' =
        if sources' |> List.exists (function Env _ -> true | _ -> false) then sources'
        else Env (implicitEnvName name) :: sources'
    { Key = name
      CliName = cliNames |> List.tryHead |> Option.defaultValue name
      Sources = sources'
      DefaultValue = sources' |> List.tryPick (function Default v -> Some (box v) | _ -> None)
      Scope = ArgAndEnv
      Required = false
      Secret =
        if List.contains SecretHashed sources then SecretHash
        elif List.contains Secret sources then SecretPresence
        else NotSecret
      ValueType = typeof<string>
      DeclaredBy = ruleLabel name }

/// The names of its `Env` sources, in list order.
let envNames (decl: VarDecl) = decl.Sources |> List.choose (function Env e -> Some e | _ -> None)

let isSecretDecl (decl: VarDecl) = decl.Secret <> NotSecret

/// Converts a value to the type of a declaration (the 3.13 conversion); the error text of a
/// secret does not show the value.
let convertVar (decl: VarDecl) (value: string) : Result<obj, string> =
    try Ok (System.Convert.ChangeType(value, decl.ValueType))
    with
    | :? System.FormatException | :? System.InvalidCastException | :? System.OverflowException ->
        let typeName = decl.ValueType.FullName
        if isSecretDecl decl then Result.Error (sprintf "Failed to convert the value of secret variable '%s' to type '%s'." decl.Key typeName)
        else Result.Error (sprintf "Failed to convert value '%s' for variable '%s' to type '%s'." value decl.Key typeName)

let private secretGiven (name: string) (where: string) =
    sprintf "Variable '%s' is secret: its value comes only from its declaration's sources, not from %s" name where

let private envOnlyGiven (name: string) (env: string) (where: string) =
    sprintf "Variable '%s' comes only from the environment variable %s, not from %s" name env where

let private isSecretEntry (name: string) (entry: VarEntry) =
    Secrets.isSecret name || entry.Decl |> Option.exists isSecretDecl

/// Builds the variable set at launch: evaluates every declaration once (the environment is
/// read here and nowhere else, and not at all with `IgnoreEnvironment`), overlays `-d` and the script's values (first defined wins:
/// `-d` > the script's value > the declaration's sources), and checks second definitions,
/// secrets, scopes, required fields and typed values. Secret names are declared and their
/// values registered for masking before anything is logged. Returns the set and the errors.
let launch (opts: ExecOptions) : Map<string, VarEntry> * string list =
    let decls = opts.VarDecls
    for d in decls do
        if isSecretDecl d then
            Secrets.declare d.Key d.Secret
            Secrets.declare d.CliName d.Secret
            envNames d |> List.iter (fun e -> Secrets.declare e d.Secret)
    for (name, value) in opts.CliVars @ opts.Vars do
        if Secrets.isSecret name then SecretMask.register value

    let errors = ResizeArray<string>()
    // the environment of the declarations; none with IgnoreEnvironment (Q-2)
    let readEnv name = if opts.IgnoreEnvironment then None else Util.getEnvVar name
    let given name = Util.getVar opts.CliVars name |> Option.isSome

    // one script definition per key: the ExecOptions.Vars entries first, then the ops in script order
    let named key = List.filter (fst >> (=) key)
    for key in (opts.Vars |> List.map fst) @ (opts.VarDefinedBy |> List.map fst) |> List.distinct do
        let byOps = opts.VarDefinedBy |> named key |> List.map snd
        let constants = byOps |> List.filter ((=) (constantLabel key)) |> List.length
        let fromOptions = (opts.Vars |> named key |> List.length) - constants
        match List.replicate (max 0 fromOptions) "ExecOptions.Vars" @ byOps with
        | a :: b :: _ -> errors.Add (sprintf "Variable '%s' is defined twice in the script: by %s and by %s; define it once" key a b)
        | _ -> ()

    let declared = decls |> List.rev |> List.map (fun d -> d.Key, d) |> Map.ofList     // the first declaration of a key

    // different keys of one implicit env name (P-5): constants and declarations that read their implicit name
    let constantOps = opts.VarDefinedBy |> List.filter (fun (k, label) -> label = constantLabel k) |> List.map fst
    let optionKeys = opts.Vars |> List.map fst |> List.filter (fun k -> not (List.contains k constantOps))
    let readsImplicit key =
        match Map.tryFind key declared with
        | Some d -> envNames d = [implicitEnvName key]
        | None -> true                          // a constant or an ExecOptions.Vars entry
    // with IgnoreEnvironment no implicit name is read: no collision check (Q-4)
    (if opts.IgnoreEnvironment then [] else optionKeys @ (opts.VarDefinedBy |> List.map fst))
    |> List.distinct
    |> List.filter readsImplicit
    |> List.groupBy implicitEnvName
    |> List.iter (fun (env, keys) ->
        match keys with
        | a :: b :: _ -> errors.Add (sprintf "Variables '%s' and '%s' both read the environment variable %s; rename one, or give one an explicit Env source" a b env)
        | _ -> ())

    for d in decls do
        // one command-line name per rule
        match d.Sources |> List.choose (function Cli ext -> Some ext | _ -> None) with
        | a :: b :: _ -> errors.Add (sprintf "Variable '%s' has more than one command-line name: %s and %s" d.Key a b)
        | _ -> ()
        // a secret takes nothing from -d, under its key, its external name or an env name of its sources
        if isSecretDecl d then
            match envNames d with
            | env :: _ as envs ->
                for name in d.Key :: d.CliName :: envs |> List.distinct do
                    if given name then errors.Add (Secrets.fromEnvOnly name env)
            | [] ->
                for name in [d.Key; d.CliName] |> List.distinct do
                    if given name then errors.Add (secretGiven name (sprintf "-d %s=..." name))
        // -d of the key is refused when the variable has another command-line name
        elif d.CliName <> d.Key && given d.Key then
            errors.Add (sprintf "-d %s: the variable is set on the command line as %s; use -d %s=..." d.Key d.CliName d.CliName)

    let ownValue (d: VarDecl) =
        d.Sources |> List.tryPick (function
            | Env e -> readEnv e |> Option.map (fun v -> v, VarOrigin.Env e)
            | Default v -> Some (v, VarOrigin.Default)
            | Cli _ | Secret | SecretHashed -> None)
    // a -d name is a key unless it is the command-line name of a declaration, or the refused key of one
    let consumed = set [ for d in decls do if d.CliName <> d.Key then yield d.CliName; yield d.Key ]
    let undeclaredCli = opts.CliVars |> List.map fst |> List.filter (consumed.Contains >> not)
    let keys = undeclaredCli @ (opts.Vars |> List.map fst) @ (decls |> List.map (fun d -> d.Key)) |> List.distinct
    let variables =
        [ for key in keys do
            let decl = Map.tryFind key declared
            let cliName = decl |> Option.map (fun d -> d.CliName) |> Option.defaultValue key
            let value =
                match decl, Util.getVar opts.CliVars cliName with
                | Some d, _ when isSecretDecl d -> ownValue d       // -d refused above
                | Some d, Some _ when d.Scope = EnvOnly ->
                    let env = envNames d |> List.tryHead |> Option.defaultValue key
                    errors.Add (envOnlyGiven key env (sprintf "-d %s=..." cliName))
                    ownValue d
                | _, Some v -> Some (v, VarOrigin.Cli cliName)
                | _, None ->
                    match Util.getVar opts.Vars key, decl with
                    | Some v, _ ->
                        // a constant reads its implicit env name first (P-3)
                        let env = implicitEnvName key
                        match readEnv env with
                        | Some e -> Some (e, VarOrigin.Env env)
                        | None -> Some (v, VarOrigin.Script)
                    | None, Some d -> ownValue d
                    | None, None -> None
            match decl, value with
            | Some d, None when d.Required -> errors.Add (sprintf "Required variable '%s' is not provided" key)
            | Some d, Some (v, origin) when d.ValueType <> typeof<string> && origin <> VarOrigin.Default ->
                match convertVar d v with
                | Result.Error e -> errors.Add e
                | Ok _ -> ()
            | _ -> ()
            let entry = { Value = Option.map fst value; Origin = Option.map snd value; Decl = decl }
            if isSecretEntry key entry then entry.Value |> Option.iter SecretMask.register
            yield key, entry ]
        |> Map.ofList
    variables, List.ofSeq errors


/// The variable set flattened to the context's `Vars`: one pair per key that has a value.
let flatVars (variables: Map<string, VarEntry>) =
    [ for KeyValue (name, entry) in variables do
        match entry.Value with
        | Some value -> yield name, value
        | None -> () ]

/// Checks the engine's per-demand `?vars` against the declarations: a secret or an
/// environment-only variable takes no request value, a typed one must convert. Returns the errors.
let checkRequestVars (variables: Map<string, VarEntry>) (pairs: (string * string) list) =
    let where = "the ?vars of Demand"
    [ for (name, value) in pairs do
        match variables |> Map.tryFind name |> Option.bind (fun e -> e.Decl) with
        | Some d when isSecretDecl d ->
            SecretMask.register value
            yield secretGiven name where
        | Some d when d.Scope = EnvOnly ->
            yield envOnlyGiven name (envNames d |> List.tryHead |> Option.defaultValue name) where
        | Some d when d.ValueType <> typeof<string> ->
            match convertVar d value with
            | Result.Error e -> yield e
            | Ok _ -> ()
        | _ -> () ]

/// The fields in which a Var read's declaration differs from the stored one (T-1).
let differences (stored: VarDecl) (read: VarDecl) =
    [ if stored.Sources <> read.Sources then yield "sources"
      elif stored.DefaultValue <> read.DefaultValue then yield "DefaultValue"
      if stored.CliName <> read.CliName then yield "command-line name"
      if stored.Scope <> read.Scope then yield "scope"
      if stored.Required <> read.Required then yield "required"
      if stored.Secret <> read.Secret then yield "secret mode"
      if stored.ValueType <> read.ValueType then yield "type" ]

/// The error of a Var read through a declaration that differs from the stored one (E-7).
let readMismatch (stored: VarDecl) (read: VarDecl) (fields: string list) =
    sprintf "Variable '%s' is read through a Var declaration that differs from its declaration by %s in %s: read it through that declaration, or use getVar \"%s\""
            read.Key stored.DeclaredBy (String.concat ", " fields) read.Key

/// The error of a Var read without a stored declaration of its key (E-8).
let readUndeclared (read: VarDecl) (envName: string) =
    let getEnvHint = if read.Scope = ArgOnly then "" else sprintf " or getEnv \"%s\"" envName
    // an inline Var.arg has no key of its own (no name, no field): it is named by its command-line name
    let shown = if read.Key = "" then read.CliName else read.Key
    sprintf "Variable '%s' is read through Var but is not declared: declare it in varschema, or use getVar \"%s\"%s" shown shown getEnvHint

/// `Var.read` (pr2-spec I-3): the error when the stored declaration of `key` has a type other
/// than the read's (E-11); `None` when there is none or it has the read's type.
let keyedTypeMismatch (key: string) (readType: System.Type) (entry: VarEntry option) =
    match entry with
    | Some { Decl = Some stored } when stored.ValueType <> readType ->
        Some (sprintf "Variable '%s' is read as '%s' but is declared by %s as '%s': declare it as '%s'"
                      key readType.FullName stored.DeclaredBy stored.ValueType.FullName readType.FullName)
    | _ -> None

/// `Var.read` (pr2-spec I-1): the typed value of `key`, given its value in the set (`getVar`) and
/// its entry. Declared: the stored declaration is trusted -- its typed default when the value is
/// its own default, else the value converted with it. Undeclared: the value converted to the
/// read's type (E-4 on failure).
let keyedValue (key: string) (readType: System.Type) (entry: VarEntry option) (value: string option) : Result<obj option, string> =
    match value, entry with
    | None, _ -> Ok None
    | Some v, Some ({ Decl = Some stored } as e) ->
        match stored.DefaultValue with
        | Some typed when value = e.Value && e.Origin = Some VarOrigin.Default -> Ok (Some typed)
        | _ -> convertVar stored v |> Result.map Some
    | Some v, _ ->
        let decl = { ruleDecl key [] with ValueType = readType; Secret = if Secrets.isSecret key then SecretPresence else NotSecret }
        convertVar decl v |> Result.map Some

let private originText (key: string) = function
    | VarOrigin.Request -> "request"
    | VarOrigin.Cli ext -> if ext = key then "cli" else "cli:" + ext
    | VarOrigin.Script -> "script"
    | VarOrigin.Env name -> "env:" + name
    | VarOrigin.Default -> "default"

/// The banner's vars section: one line per key, sorted, with its source; secrets as `***`.
let bannerLines (variables: Map<string, VarEntry>) =
    [ for KeyValue (name, entry) in variables do
        match entry.Value, entry.Origin with
        | Some value, Some origin ->
            let shown = if isSecretEntry name entry || SecretMask.apply value <> value then "***" else value
            yield sprintf "    %s=%s (%s)" name shown (originText name origin)
        | _ -> yield sprintf "    %s (not set)" name ]

let private hideDecl (d: VarDecl) =
    if isSecretDecl d then { d with Sources = d.Sources |> List.filter (function Default _ -> false | _ -> true); DefaultValue = None }
    else d

/// The options for the `Debug` dump: secret values as `***`, secret declarations without defaults.
let maskOptions (options: ExecOptions) =
    let hideValues = List.map (fun (name, value) -> name, if Secrets.isSecret name then "***" else value)
    let hideSecretDefaults = List.map (fun (name, help: VarHelp) -> name, if help.IsSecret then { help with DefaultStr = None } else help)
    { options with Vars = hideValues options.Vars; CliVars = hideValues options.CliVars
                   VarDecls = List.map hideDecl options.VarDecls
                   VarSchema = hideSecretDefaults options.VarSchema }

/// The variable set for the `Debug` dump: secret values as `***`, secret declarations without defaults.
let maskVariables (variables: Map<string, VarEntry>) =
    variables |> Map.map (fun name entry ->
        { entry with Value = if isSecretEntry name entry then Option.map (fun _ -> "***") entry.Value else entry.Value
                     Decl = Option.map hideDecl entry.Decl })
