namespace Xake

[<AutoOpen>]
module Vars =

    type LookupScope = ArgAndEnv | EnvOnly | ArgOnly

    /// An optional typed variable — resolves to 't option in recipes.
    type OptionalVar<'t> = {
        mutable FieldName : string
        EnvName           : string option
        Description       : string option
        Scope             : LookupScope
    }

    /// A required typed variable — resolves to 't in recipes (throws or returns default when absent).
    type RequiredVar<'t> = {
        mutable FieldName : string
        EnvName           : string option
        Description       : string option
        DefaultValue      : 't option
        Scope             : LookupScope
    }

    let private convertTo<'t> (name: string) (s: string) : 't =
        try
            System.Convert.ChangeType(s, typeof<'t>) :?> 't
        with
        | :? System.FormatException as ex
        | :? System.InvalidCastException as ex ->
            let targetType = typeof<'t>.FullName
            let message =
                sprintf "Failed to convert value '%s' for variable '%s' to type '%s'." s name targetType
            raise (System.FormatException(message, ex))

    /// Converts a camelCase or PascalCase field name to UPPER_SNAKE_CASE env var name.
    let private toEnvName (fieldName: string) : string =
        System.Text.RegularExpressions.Regex.Replace(fieldName, "(?<=[a-z])(?=[A-Z])", "_").ToUpperInvariant()

    /// Factory with optional named parameters.
    [<AbstractClass; Sealed>]
    type Var private () =
        /// Resolves from CLI arg first, then env var (default).
        static member create<'t>(?cliArg: string, ?envVar: string, ?description: string) : OptionalVar<'t> =
            { FieldName = defaultArg cliArg ""; EnvName = envVar; Description = description; Scope = ArgAndEnv }

        static member string(?cliArg: string, ?envVar: string, ?description: string) : OptionalVar<string> =
            Var.create(?cliArg = cliArg, ?envVar = envVar, ?description = description)

        static member int(?cliArg: string, ?envVar: string, ?description: string) : OptionalVar<int> =
            Var.create(?cliArg = cliArg, ?envVar = envVar, ?description = description)

        static member bool(?cliArg: string, ?envVar: string, ?description: string) : OptionalVar<bool> =
            Var.create(?cliArg = cliArg, ?envVar = envVar, ?description = description)

        /// Resolves from environment variable only; CLI args are never checked.
        static member env<'t>(?envVar: string, ?description: string) : OptionalVar<'t> =
            { FieldName = ""; EnvName = envVar; Description = description; Scope = EnvOnly }

        /// Resolves from CLI arg only; environment variables are never checked.
        static member arg<'t>(?cliArg: string, ?description: string) : OptionalVar<'t> =
            { FieldName = defaultArg cliArg ""; EnvName = None; Description = description; Scope = ArgOnly }

    /// Adds a description shown in --help output.
    let describe (text: string) (v: OptionalVar<'t>) : OptionalVar<'t> =
        { v with Description = Some text }

    /// Marks a variable as required — Bind throws when value is absent.
    let required (v: OptionalVar<'t>) : RequiredVar<'t> =
        { FieldName = v.FieldName; EnvName = v.EnvName; Description = v.Description; DefaultValue = None; Scope = v.Scope }

    /// Attaches a default value — Bind returns it when CLI and env are absent.
    let withDefault (value: 't) (v: OptionalVar<'t>) : RequiredVar<'t> =
        { required v with DefaultValue = Some value }

    let private resolveVar<'t> (fieldName: string) (envName: string option) (scope: LookupScope) : Recipe<ExecContext, string * 't option> =
        let resolvedEnvName = envName |> Option.defaultWith (fun () -> toEnvName fieldName)
        recipe {
            let! cliVal = if scope = EnvOnly then recipe { return None } else getVar fieldName
            let! envVal = if scope = ArgOnly then recipe { return None } else getEnv resolvedEnvName
            let reportName = if scope = EnvOnly then resolvedEnvName else fieldName
            return reportName, cliVal |> Option.orElse envVal |> Option.map (convertTo<'t> reportName)
        }

    type RecipeBuilder with

        member _.Bind(v: OptionalVar<'t>, f: 't option -> Recipe<ExecContext,'r>) : Recipe<ExecContext,'r> =
            A.bindF (recipe { let! _, value = resolveVar<'t> v.FieldName v.EnvName v.Scope in return value }) f

        member _.Bind(rv: RequiredVar<'t>, f: 't -> Recipe<ExecContext,'r>) : Recipe<ExecContext,'r> =
            A.bindF (recipe {
                let! reportName, value = resolveVar<'t> rv.FieldName rv.EnvName rv.Scope
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
                            let innerTy = ty.GetGenericArguments().[0]
                            Some (p.Name, {
                                TypeName     = innerTy.Name.ToLowerInvariant()
                                EnvVarName   = getStringOption "EnvName"
                                DefaultStr   = defaultStr
                                IsRequired   = isRequired
                                Description  = getStringOption "Description"
                            })
                        if isGenericOf typedefof<OptionalVar<_>> ty then
                            makeEntry false None
                        elif isGenericOf typedefof<RequiredVar<_>> ty then
                            let defaultStr = ty.GetProperty("DefaultValue").GetValue(value) |> getOptionValue |> Option.map (sprintf "%A")
                            makeEntry defaultStr.IsNone defaultStr
                        else None)
                |> Array.toList
            XakeScript ({ options with VarSchema = options.VarSchema @ schemaEntries }, rules)
