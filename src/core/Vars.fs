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

    let private wrapConversionError<'t> (isSecret: bool) (name: string) (s: string) (ex: exn) : 't =
        let targetType = typeof<'t>.FullName
        let message =
            if isSecret then
                sprintf "Failed to convert the value of secret variable '%s' to type '%s'." name targetType
            else
                sprintf "Failed to convert value '%s' for variable '%s' to type '%s'." s name targetType
        // the inner exception's message may quote the value too
        raise (System.FormatException(message, if isSecret then null else ex))

    let private convertTo<'t> (isSecret: bool) (name: string) (s: string) : 't =
        try
            System.Convert.ChangeType(s, typeof<'t>) :?> 't
        with
        | :? System.FormatException as ex -> wrapConversionError<'t> isSecret name s ex
        | :? System.InvalidCastException as ex -> wrapConversionError<'t> isSecret name s ex
        | :? System.OverflowException as ex -> wrapConversionError<'t> isSecret name s ex

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

    let private resolveVar<'t> (fieldName: string) (name: string option) (cliArgName: string option) (envName: string option) (scope: LookupScope) (secret: SecretMode) : Recipe<ExecContext, string * 't option> =
        let resolvedName = defaultArg name fieldName
        let resolvedCliName = defaultArg cliArgName resolvedName
        let resolvedEnvName = envName |> Option.defaultWith (fun () -> toEnvName resolvedName)
        recipe {
            if secret <> NotSecret then
                if scope <> EnvOnly then Secrets.declare resolvedCliName secret
                if scope <> ArgOnly then Secrets.declare resolvedEnvName secret
                if scope = EnvOnly then
                    let! ctx = getCtx()
                    match [resolvedCliName; resolvedEnvName] |> List.tryFind (Util.getVar ctx.Vars >> Option.isSome) with
                    | Some given ->
                        Util.getVar ctx.Vars given |> Option.iter SecretMask.register
                        failwith (Secrets.fromEnvOnly given resolvedEnvName)
                    | None -> ()
            let! cliVal = if scope = EnvOnly then recipe { return None } else getVar resolvedCliName
            let! envVal =
                match scope, cliVal with
                | ArgOnly, _ | _, Some _ -> recipe { return None }
                | _ -> getEnv resolvedEnvName
            let reportName = if scope = EnvOnly then resolvedEnvName else resolvedCliName
            return reportName, cliVal |> Option.orElse envVal |> Option.map (convertTo<'t> (secret <> NotSecret) reportName)
        }

    type RecipeBuilder with

        member _.Bind(v: OptionalVar<'t>, f: 't option -> Recipe<ExecContext,'r>) : Recipe<ExecContext,'r> =
            A.bindF (recipe { let! _, value = resolveVar<'t> v.FieldName v.Name v.CliArgName v.EnvName v.Scope v.Secret in return value }) f

        member _.Bind(rv: RequiredVar<'t>, f: 't -> Recipe<ExecContext,'r>) : Recipe<ExecContext,'r> =
            A.bindF (recipe {
                let! reportName, value = resolveVar<'t> rv.FieldName rv.Name rv.CliArgName rv.EnvName rv.Scope rv.Secret
                return
                    match value with
                    | Some v -> v
                    | None ->
                        match rv.DefaultValue with
                        | Some d -> d
                        | None -> failwithf "Required variable '%s' is not provided" reportName
            }) f

    open System.Reflection

    type RulesBuilder with

        [<CustomOperation("varschema")>]
        member _.Vars(XakeScript (options, rules, wo), schema: 'a) : XakeScript =
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
                        let makeEntry isRequired defaultStr =
                            ty.GetProperty("FieldName").SetValue(value, p.Name)
                            let effectiveName = getStringOption "Name" |> Option.defaultValue p.Name
                            let effectiveCliName = getStringOption "CliArgName" |> Option.defaultValue effectiveName
                            let scope = ty.GetProperty("Scope").GetValue(value) :?> LookupScope
                            let envVarName =
                                match scope with
                                | ArgOnly -> None
                                | EnvOnly | ArgAndEnv ->
                                    // Always show the env var name (explicit or auto-derived)
                                    Some (getStringOption "EnvName" |> Option.defaultWith (fun () -> toEnvName effectiveName))
                            let innerTy = ty.GetGenericArguments().[0]
                            let secret = ty.GetProperty("Secret").GetValue(value) :?> SecretMode
                            if scope <> EnvOnly then Secrets.declare effectiveCliName secret
                            envVarName |> Option.iter (fun env -> Secrets.declare env secret)
                            Some (effectiveCliName, {
                                TypeName     = innerTy.Name.ToLowerInvariant()
                                EnvVarName   = envVarName
                                DefaultStr   = defaultStr
                                IsRequired   = isRequired
                                Description  = getStringOption "Description"
                                Scope        = scope
                                IsSecret     = (secret <> NotSecret)
                            })
                        if isGenericOf typedefof<OptionalVar<_>> ty then
                            makeEntry false None
                        elif isGenericOf typedefof<RequiredVar<_>> ty then
                            let defaultStr = ty.GetProperty("DefaultValue").GetValue(value) |> getOptionValue |> Option.map (sprintf "%A")
                            makeEntry defaultStr.IsNone defaultStr
                        else None)
                |> Array.toList
            XakeScript ({ options with VarSchema = options.VarSchema @ schemaEntries }, rules, wo)
