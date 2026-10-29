namespace Xake

[<AutoOpen>]
module Vars =

    /// An optional typed variable — resolves to 't option in recipes.
    type OptionalVar<'t> = {
        mutable FieldName : string
        Name              : string option
        CliArgName        : string option
        EnvName           : string option
        Description       : string option
        Scope             : LookupScope
        /// Set by `secret`/`secretHashed`/`Var.secret`: the value is never recorded or logged.
        Secret            : SecretMode
    }
    with
        /// The same variable with another secret mode (used by `secret` and `secretHashed`).
        member v.WithSecret(mode: SecretMode) : OptionalVar<'t> = { v with Secret = mode }

    /// A required typed variable — resolves to 't in recipes (throws or returns default when absent).
    type RequiredVar<'t> = {
        mutable FieldName : string
        Name              : string option
        CliArgName        : string option
        EnvName           : string option
        Description       : string option
        DefaultValue      : 't option
        Scope             : LookupScope
        /// Set by `secret`/`secretHashed`/`Var.secret`: the value is never recorded or logged.
        Secret            : SecretMode
    }
    with
        /// The same variable with another secret mode (used by `secret` and `secretHashed`).
        member v.WithSecret(mode: SecretMode) : RequiredVar<'t> = { v with Secret = mode }

    /// Converts a camelCase or PascalCase field name to UPPER_SNAKE_CASE env var name.
    let private toEnvName (fieldName: string) : string =
        System.Text.RegularExpressions.Regex.Replace(fieldName, "(?<=[a-z])(?=[A-Z])", "_").ToUpperInvariant()

    /// Factory with optional named parameters.
    [<AbstractClass; Sealed>]
    type Var private () =
        /// Resolves from CLI arg first, then env var (default).
        static member create<'t>(?name: string, ?cliArg: string, ?envVar: string, ?description: string) : OptionalVar<'t> =
            { FieldName = ""; Name = name; CliArgName = cliArg; EnvName = envVar; Description = description; Scope = ArgAndEnv; Secret = NotSecret }

        static member string(?name: string, ?cliArg: string, ?envVar: string, ?description: string) : OptionalVar<string> =
            Var.create(?name = name, ?cliArg = cliArg, ?envVar = envVar, ?description = description)

        static member int(?name: string, ?cliArg: string, ?envVar: string, ?description: string) : OptionalVar<int> =
            Var.create(?name = name, ?cliArg = cliArg, ?envVar = envVar, ?description = description)

        static member bool(?name: string, ?cliArg: string, ?envVar: string, ?description: string) : OptionalVar<bool> =
            Var.create(?name = name, ?cliArg = cliArg, ?envVar = envVar, ?description = description)

        /// Resolves from environment variable only; CLI args are never checked.
        static member env<'t>(?envVar: string, ?description: string) : OptionalVar<'t> =
            { Var.create(?envVar = envVar, ?description = description) with Scope = EnvOnly }

        /// Resolves from CLI arg only; environment variables are never checked.
        static member arg<'t>(?cliArg: string, ?description: string) : OptionalVar<'t> =
            { Var.create(?cliArg = cliArg, ?description = description) with Scope = ArgOnly }

        /// A secret read from the environment only: the database keeps whether it is set, never
        /// the value; the value is masked in all output; `-d NAME=value` is refused.
        /// Pipe it to `secretHashed` to rebuild when the value changes.
        static member secret<'t>(?envVar: string, ?description: string) : OptionalVar<'t> =
            { Var.env<'t>(?envVar = envVar, ?description = description) with Secret = SecretPresence }

    /// Marks a variable secret: the database keeps only whether it is set (a changed value
    /// rebuilds nothing, unset/set does), the value is masked (`***`) in all output and
    /// `--help` shows no default. Works on any var, before or after `required`/`withDefault`.
    let inline secret (v: ^V) : ^V = (^V : (member WithSecret : SecretMode -> ^V) (v, SecretPresence))

    /// Like `secret`, but the database keeps a salted HMAC-SHA256 of the value, so a changed
    /// value rebuilds the targets that read it.
    let inline secretHashed (v: ^V) : ^V = (^V : (member WithSecret : SecretMode -> ^V) (v, SecretHash))

    /// Adds a description shown in --help output.
    let describe (text: string) (v: OptionalVar<'t>) : OptionalVar<'t> =
        { v with Description = Some text }

    /// Marks a variable as required — Bind throws when value is absent.
    let required (v: OptionalVar<'t>) : RequiredVar<'t> =
        { FieldName = v.FieldName; Name = v.Name; CliArgName = v.CliArgName; EnvName = v.EnvName; Description = v.Description; DefaultValue = None; Scope = v.Scope; Secret = v.Secret }

    /// Attaches a default value — Bind returns it when CLI and env are absent.
    let withDefault (value: 't) (v: OptionalVar<'t>) : RequiredVar<'t> =
        { required v with DefaultValue = Some value }

    /// The declaration a `Var.*` value stands for (its key: `name`, else the field name; its
    /// command-line name: `cliArg`, else the key), with its env name: the sources are its env
    /// name (unless argument-only) and its default, as a string.
    let internal declare (fieldName: string) (name: string option) (cliArgName: string option) (envName: string option)
                         (scope: LookupScope) (secret: SecretMode) (defaultValue: obj option) (isRequired: bool) (valueType: System.Type) =
        let resolvedName = defaultArg name fieldName
        let resolvedEnvName = envName |> Option.defaultWith (fun () -> toEnvName resolvedName)
        let decl : VarDecl =
            { Key = resolvedName
              CliName = defaultArg cliArgName resolvedName
              Sources =
                [ if scope <> ArgOnly then yield Env resolvedEnvName
                  match defaultValue with
                  | Some d -> yield Default (System.Convert.ToString(d, System.Globalization.CultureInfo.InvariantCulture))
                  | None -> () ]
              DefaultValue = defaultValue
              Scope = scope
              Required = isRequired && defaultValue.IsNone
              Secret = secret
              ValueType = valueType
              DeclaredBy = VarDecl.fieldLabel fieldName }
        decl, resolvedEnvName

    /// The fields in which a read's declaration differs from the stored one.
    let private differences (stored: VarDecl) (read: VarDecl) =
        [ if stored.Sources <> read.Sources then yield "sources"
          elif stored.DefaultValue <> read.DefaultValue then yield "DefaultValue"
          if stored.CliName <> read.CliName then yield "command-line name"
          if stored.Scope <> read.Scope then yield "scope"
          if stored.Required <> read.Required then yield "required"
          if stored.Secret <> read.Secret then yield "secret mode"
          if stored.ValueType <> read.ValueType then yield "type" ]

    /// A Var read: the value of the variable set under the read's key, through a stored
    /// declaration equal to the read's; recorded by `getVar` (one `Var`, or a `SecretDep`).
    /// The read's own sources are not consulted: the environment was read at launch.
    let private readVar<'t> (declOf: unit -> VarDecl * string) : Recipe<ExecContext, 't option> =
        recipe {
            let! ctx = getCtx()
            let decl, envName = declOf ()
            let key = decl.Key
            match ctx.Engine.Options.Variables |> Map.tryFind key with
            | Some ({ Decl = Some stored } as entry) ->
                match differences stored decl with
                | [] ->
                    let! value = getVar key
                    return
                        match value, stored.DefaultValue with
                        | None, _ -> None
                        | Some _, Some typed when value = entry.Value && entry.Origin = Some VarOrigin.Default -> Some (unbox<'t> typed)
                        | Some v, _ ->
                            match ScriptRunner.convertVar stored v with
                            | Ok converted -> Some (unbox<'t> converted)
                            | Result.Error e -> failwith e
                | fields ->
                    return failwithf "Variable '%s' is read through a Var declaration that differs from its declaration by %s in %s: read it through that declaration, or use getVar \"%s\""
                                     key stored.DeclaredBy (String.concat ", " fields) key
            | _ ->
                let getEnvHint = if decl.Scope = ArgOnly then "" else sprintf " or getEnv \"%s\"" envName
                // an inline Var.arg has no key of its own (no name, no field): it is named by its command-line name
                let shown = if key = "" then decl.CliName else key
                return failwithf "Variable '%s' is read through Var but is not declared: declare it in varschema, or use getVar \"%s\"%s" shown shown getEnvHint
        }

    type RecipeBuilder with

        member _.Bind(v: OptionalVar<'t>, f: 't option -> Recipe<ExecContext,'r>) : Recipe<ExecContext,'r> =
            let declOf () = declare v.FieldName v.Name v.CliArgName v.EnvName v.Scope v.Secret None false typeof<'t>
            A.bindF (readVar<'t> declOf) f

        member _.Bind(rv: RequiredVar<'t>, f: 't -> Recipe<ExecContext,'r>) : Recipe<ExecContext,'r> =
            let declOf () = declare rv.FieldName rv.Name rv.CliArgName rv.EnvName rv.Scope rv.Secret (Option.map box rv.DefaultValue) true typeof<'t>
            A.bindF (recipe {
                let! value = readVar<'t> declOf
                return
                    match value with
                    | Some v -> v
                    | None -> failwithf "Required variable '%s' is not provided" (fst (declOf ())).Key
            }) f

    open System.Reflection

    type RulesBuilder with

        /// Imports the `Var.*` fields of a record as declarations (key, env name, scope, secret
        /// mode, required flag, default, type) and for --help. The op sets each field's name and
        /// records the declarations only: they are evaluated at launch.
        [<CustomOperation("varschema")>]
        member _.Vars(XakeScript (options, rules), schema: 'a) : XakeScript =
            let getOptionValue (v: obj) =
                if v = null then None
                else
                    let case, fields = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(v, v.GetType())
                    if case.Name = "Some" then Some fields.[0] else None
            let isGenericOf (tdef: System.Type) (ty: System.Type) =
                ty.IsGenericType && ty.GetGenericTypeDefinition() = tdef
            let schemaEntries =
                (schema :> obj).GetType().GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
                |> Array.choose (fun p ->
                    let value = p.GetValue(schema :> obj)
                    if value = null then None
                    else
                        let ty = value.GetType()
                        let getStringOption propName =
                            ty.GetProperty(propName).GetValue(value)
                            |> getOptionValue
                            |> Option.map (fun x -> x :?> string)
                        let makeEntry isRequired (defaultValue: obj option) =
                            ty.GetProperty("FieldName").SetValue(value, p.Name)
                            let scope = ty.GetProperty("Scope").GetValue(value) :?> LookupScope
                            let innerTy = ty.GetGenericArguments().[0]
                            let secret = ty.GetProperty("Secret").GetValue(value) :?> SecretMode
                            let decl, envName =
                                declare p.Name (getStringOption "Name") (getStringOption "CliArgName") (getStringOption "EnvName")
                                        scope secret defaultValue isRequired innerTy
                            let defaultStr = defaultValue |> Option.map (sprintf "%A")
                            Some (decl, (decl.CliName, {
                                TypeName     = innerTy.Name.ToLowerInvariant()
                                // Always show the env var name (explicit or auto-derived)
                                EnvVarName   = if scope = ArgOnly then None else Some envName
                                DefaultStr   = defaultStr
                                IsRequired   = defaultStr.IsNone && isRequired
                                Description  = getStringOption "Description"
                                Scope        = scope
                                IsSecret     = (secret <> NotSecret)
                            }))
                        if isGenericOf typedefof<OptionalVar<_>> ty then
                            makeEntry false None
                        elif isGenericOf typedefof<RequiredVar<_>> ty then
                            makeEntry true (ty.GetProperty("DefaultValue").GetValue(value) |> getOptionValue)
                        else None)
                |> Array.toList
            let decls = schemaEntries |> List.map fst
            XakeScript ({ options with VarSchema = options.VarSchema @ List.map snd schemaEntries
                                       VarDecls = options.VarDecls @ decls
                                       VarDefinedBy = options.VarDefinedBy @ (decls |> List.map (fun d -> d.Key, d.DeclaredBy)) }, rules)
