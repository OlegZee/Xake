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
    }

    /// A required typed variable — resolves to 't in recipes (throws or returns default when absent).
    type RequiredVar<'t> = {
        mutable FieldName : string
        Name              : string option
        CliArgName        : string option
        EnvName           : string option
        Description       : string option
        DefaultValue      : 't option
        Scope             : LookupScope
    }

    let private wrapConversionError<'t> (name: string) (s: string) (ex: exn) : 't =
        let targetType = typeof<'t>.FullName
        let message =
            sprintf "Failed to convert value '%s' for variable '%s' to type '%s'." s name targetType
        raise (System.FormatException(message, ex))

    let private convertTo<'t> (name: string) (s: string) : 't =
        try
            System.Convert.ChangeType(s, typeof<'t>) :?> 't
        with
        | :? System.FormatException as ex -> wrapConversionError<'t> name s ex
        | :? System.InvalidCastException as ex -> wrapConversionError<'t> name s ex
        | :? System.OverflowException as ex -> wrapConversionError<'t> name s ex

    /// Converts a camelCase or PascalCase field name to UPPER_SNAKE_CASE env var name.
    let private toEnvName (fieldName: string) : string =
        System.Text.RegularExpressions.Regex.Replace(fieldName, "(?<=[a-z])(?=[A-Z])", "_").ToUpperInvariant()

    /// Factory with optional named parameters.
    [<AbstractClass; Sealed>]
    type Var private () =
        /// Resolves from CLI arg first, then env var (default).
        static member create<'t>(?name: string, ?cliArg: string, ?envVar: string, ?description: string) : OptionalVar<'t> =
            { FieldName = ""; Name = name; CliArgName = cliArg; EnvName = envVar; Description = description; Scope = ArgAndEnv }

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

    /// Adds a description shown in --help output.
    let describe (text: string) (v: OptionalVar<'t>) : OptionalVar<'t> =
        { v with Description = Some text }

    /// Marks a variable as required — Bind throws when value is absent.
    let required (v: OptionalVar<'t>) : RequiredVar<'t> =
        { FieldName = v.FieldName; Name = v.Name; CliArgName = v.CliArgName; EnvName = v.EnvName; Description = v.Description; DefaultValue = None; Scope = v.Scope }

    /// Attaches a default value — Bind returns it when CLI and env are absent.
    let withDefault (value: 't) (v: OptionalVar<'t>) : RequiredVar<'t> =
        { required v with DefaultValue = Some value }

    let private resolveVar<'t> (fieldName: string) (name: string option) (cliArgName: string option) (envName: string option) (scope: LookupScope) : Recipe<ExecContext, string * 't option> =
        let resolvedName = defaultArg name fieldName
        let resolvedCliName = defaultArg cliArgName resolvedName
        let resolvedEnvName = envName |> Option.defaultWith (fun () -> toEnvName resolvedName)
        recipe {
            let! cliVal = if scope = EnvOnly then recipe { return None } else getVar resolvedCliName
            let! envVal =
                match scope, cliVal with
                | ArgOnly, _ | _, Some _ -> recipe { return None }
                | _ -> getEnv resolvedEnvName
            let reportName = if scope = EnvOnly then resolvedEnvName else resolvedCliName
            return reportName, cliVal |> Option.orElse envVal |> Option.map (convertTo<'t> reportName)
        }

    type RecipeBuilder with

        member _.Bind(v: OptionalVar<'t>, f: 't option -> Recipe<ExecContext,'r>) : Recipe<ExecContext,'r> =
            A.bindF (recipe { let! _, value = resolveVar<'t> v.FieldName v.Name v.CliArgName v.EnvName v.Scope in return value }) f

        member _.Bind(rv: RequiredVar<'t>, f: 't -> Recipe<ExecContext,'r>) : Recipe<ExecContext,'r> =
            A.bindF (recipe {
                let! reportName, value = resolveVar<'t> rv.FieldName rv.Name rv.CliArgName rv.EnvName rv.Scope
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
                            Some (effectiveCliName, {
                                TypeName     = innerTy.Name.ToLowerInvariant()
                                EnvVarName   = envVarName
                                DefaultStr   = defaultStr
                                IsRequired   = isRequired
                                Description  = getStringOption "Description"
                                Scope        = scope
                            })
                        if isGenericOf typedefof<OptionalVar<_>> ty then
                            makeEntry false None
                        elif isGenericOf typedefof<RequiredVar<_>> ty then
                            let defaultStr = ty.GetProperty("DefaultValue").GetValue(value) |> getOptionValue |> Option.map (sprintf "%A")
                            makeEntry defaultStr.IsNone defaultStr
                        else None)
                |> Array.toList
            XakeScript ({ options with VarSchema = options.VarSchema @ schemaEntries }, rules)
