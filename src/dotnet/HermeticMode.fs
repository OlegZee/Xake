namespace Xake.Dotnet

open System.IO

open Xake
open Xake.Tasks

/// <summary>
/// `HERMETIC=on`: a resolved compilation names no path outside the project root and the
/// build's own package folder, except paths covered by a prerequisite -- today exactly one
/// kind, the .NET SDK at an exact version (`dotnet-sdk <v>`), allowed only for fsc's compiler
/// and only when `global.json` pins that version with `"rollForward": "disable"`.
///
/// A pure gate: it never changes where an input resolves from. `Csc.ofSettings` and
/// `Fsc.ofSettings` call `enforce` last; `check` is the same rule over any list of paths, for a
/// caller (the hermetic lock) that has its own roots, e.g. tokenized ones.
/// </summary>
module HermeticMode =

    /// What a path is to the compilation; decides which message a path outside the roots
    /// gets, and whether the SDK prerequisite may cover it.
    type Role =
        /// the compiler, by tool name ("csc", "fsc")
        | Compiler of tool: string
        | Reference
        | Analyzer
        /// anything else the compilation reads or writes: sources, resources, generated files,
        /// `/out:`, `/doc:`
        | Other

    type Input = { Role: Role; Path: string }

    /// What the check measures paths against. Paths are compared after turning `\` into `/`
    /// and trimming a trailing `/`, by prefix (case-insensitive on Windows); nothing is made
    /// absolute, so tokenized roots (`$(ProjectRoot)`) work as well as real ones.
    type Roots = {
        /// The project root (first; the one message 8 names) and any extra roots that count as
        /// part of the checkout
        ProjectRoots: string list
        /// The package folder in effect
        PackageRoot: string
        /// Whether the package folder is the build's own (the script variable
        /// `NUGET_PACKAGES`); when not, the mode fails with message 2 and paths under the folder
        /// are not reported one by one
        PackageRootDeclared: bool
        /// The .NET installation root (`$(DotnetRoot)`), `None` when there is none
        DotnetRoot: string option
        /// The `global.json` pin that applies to the project root
        SdkPin: DotNetFwk.GlobalJsonPin
        /// Whether `<DotnetRoot>/sdk/<version>` is installed (message 7)
        SdkInstalled: string -> bool
        /// The targeting-pack version the SDK names for a `netN.0` moniker (message 4's
        /// suggestion); `fun _ -> None` leaves the suggestion out
        TargetingPackVersion: string -> string option
    }

    /// Parses a `HERMETIC` value: `on|true|yes|1` -> true, `off|false|no|0` (and unset or
    /// empty) -> false, case-insensitive; anything else fails with message 1.
    let parse (value: string option) : bool =
        match value |> Option.map (fun v -> v.Trim()) with
        | None | Some "" -> false
        | Some v ->
            match v.ToLowerInvariant() with
            | "on" | "true" | "yes" | "1" -> true
            | "off" | "false" | "no" | "0" -> false
            | _ -> failwithf "HERMETIC='%s': expected on or off" v

    /// <summary>
    /// Whether the build runs in hermetic mode: the script variable `HERMETIC` (read, and so a
    /// dependency, here; no environment fallback -- the content rule belongs to the build
    /// definition), default off. An unrecognized value fails (message 1).
    /// </summary>
    let enabled () : Recipe<ExecContext, bool> =
        recipe {
            let! v = getVar "HERMETIC"
            return parse v
        }

    let private norm (p: string) = p.Replace('\\', '/').TrimEnd '/'

    let private comparison =
        if Env.isWindows then System.StringComparison.OrdinalIgnoreCase else System.StringComparison.Ordinal

    let private isUnder (root: string) (path: string) =
        let r = norm root
        r <> "" && (let p = norm path in p.Equals (r, comparison) || p.StartsWith (r + "/", comparison))

    let private rank = function
        | Compiler _ -> 0
        | Reference -> 1
        | _ -> 2

    /// <summary>
    /// The violations of one compilation named `name`, in report order: message 2 (no package
    /// folder of the build's own) first, then one line per offending path -- the compiler,
    /// the references, everything else. Empty when the compilation satisfies the mode.
    /// Messages 3 to 7 name the variable or pin that fixes a known SDK location: csc from the
    /// SDK (3), a `packs/Microsoft.NETCore.App.Ref` targeting pack (4, once per framework), the
    /// SDK's `FSharp.Core.dll` (5), fsc from an SDK that is not pinned exactly (6) or pinned
    /// but not installed (7); anything else outside the roots gets 8. Only fsc's compiler may
    /// lie under `<DotnetRoot>/sdk/<v>/`, and only when `SdkPin` is `Exact v`.
    /// </summary>
    let check (name: string) (roots: Roots) (inputs: Input list) : string list =
        let prefix = sprintf "'%s': HERMETIC=on: " name
        let packMonikers = System.Collections.Generic.HashSet<string>()
        let sdkPathOf path =
            roots.DotnetRoot |> Option.bind (fun root -> DotNetFwk.sdkVersionOf root path |> Option.map (fun v -> root, v))
        let targetingPack path =
            // <root>/packs/Microsoft.NETCore.App.Ref/<v>/ref/<moniker>/...
            roots.DotnetRoot |> Option.bind (fun root ->
                let packs = norm root + "/packs/Microsoft.NETCore.App.Ref/"
                let p = norm path
                if not (p.StartsWith (packs, comparison)) then None else
                match p.Substring(packs.Length).Split '/' with
                | parts when parts.Length >= 3 && parts.[1] = "ref" ->
                    Some (parts.[2], packs + parts.[0] + "/ref/" + parts.[2])
                | _ -> None)
        let outside (path: string) =
            sprintf "%s%s is outside the project root '%s' and the package folder '%s', and no prerequisite covers it"
                prefix path (roots.ProjectRoots |> List.tryHead |> Option.defaultValue "") roots.PackageRoot
        let violation (input: Input) : string option =
            let path = input.Path
            if roots.ProjectRoots |> List.exists (fun r -> isUnder r path) then None
            elif isUnder roots.PackageRoot path then None   // message 2 covers an undeclared folder
            else
            match input.Role, sdkPathOf path with
            | Compiler "fsc", Some (root, v) ->
                match roots.SdkPin with
                | DotNetFwk.Exact (pinned, _) when pinned = v -> None
                | DotNetFwk.Exact (pinned, file) when not (roots.SdkInstalled pinned) ->
                    Some (sprintf "%sglobal.json (%s) pins the .NET SDK %s, but '%s' does not have it; install it (dotnet-install --version %s --install-dir %s)"
                            prefix file pinned root pinned root)
                | pin ->
                    Some (sprintf "%sfsc has no NuGet package, so the .NET SDK is a prerequisite and must be pinned exactly (found: %s); pin it in global.json: { \"sdk\": { \"version\": \"%s\", \"rollForward\": \"disable\" } }"
                            prefix (DotNetFwk.globalJsonPinText pin) v)
            | Compiler "csc", Some _ ->
                Some (sprintf "%sthe compiler %s comes from the .NET SDK; set CSC_TOOLSET (or toolset in the csc block) to take csc from the Microsoft.Net.Compilers.Toolset package" prefix path)
            | Reference, Some _ when Path.GetFileName (norm path) = "FSharp.Core.dll" ->
                Some (sprintf "%sthe default FSharp.Core %s comes from the .NET SDK; set FSHARP_CORE_VERSION to take the FSharp.Core package, or reference an FSharp.Core.dll" prefix path)
            | _ ->
            match targetingPack path with
            | Some (moniker, dir) ->
                if not (packMonikers.Add moniker) then None else
                let majorMinor = if moniker.StartsWith "net" then moniker.Substring 3 else moniker
                let bundles =
                    match roots.TargetingPackVersion moniker with
                    | Some v -> sprintf " (this SDK bundles %s)" v
                    | None -> ""
                Some (sprintf "%sthe reference assemblies for %s come from %s under the .NET SDK; add a %s version to NETCORE_REF_VERSION%s"
                        prefix moniker dir majorMinor bundles)
            | None -> Some (outside path)
        let package =
            if roots.PackageRootDeclared then []
            else
                [ sprintf "'%s': HERMETIC=on needs a package folder of the build's own: set the NUGET_PACKAGES script variable (var \"NUGET_PACKAGES\" \".nuget/packages\", or -d NUGET_PACKAGES=<dir>)" name ]
        let paths =
            inputs
            |> List.filter (fun i -> not (System.String.IsNullOrEmpty i.Path))
            |> List.distinctBy (fun i -> norm i.Path)   // the first role a path is listed in wins
            |> List.sortBy (fun i -> rank i.Role)   // stable
            |> List.choose violation
        package @ paths

    /// <summary>
    /// The roots of the build this recipe runs in: the project root, the package folder with
    /// its source (`DotNetFwk.packageRootWithSource`), the .NET root, the `global.json` pin of
    /// the project root, and the SDK's targeting-pack versions.
    /// </summary>
    let roots () : Recipe<ExecContext, Roots> =
        recipe {
            let! options = getCtxOptions ()
            let! packageRoot, source = DotNetFwk.packageRootWithSource ()
            let projectRoot = Path.GetFullPath options.ProjectRoot
            let dotnetRoot = DotNetFwk.dotnetRoot ()
            return {
                ProjectRoots = [ projectRoot ]
                PackageRoot = Path.GetFullPath packageRoot
                PackageRootDeclared = (source = DotNetFwk.ScriptVariable)
                DotnetRoot = dotnetRoot
                SdkPin = DotNetFwk.globalJsonPin projectRoot
                SdkInstalled = fun v -> dotnetRoot |> Option.exists (fun root -> Directory.Exists (root </> "sdk" </> v))
                TargetingPackVersion = DotNetFwk.sdkTargetingPackVersion projectRoot
            }
        }

    /// <summary>
    /// The gate `Csc.ofSettings` and `Fsc.ofSettings` end with: under `HERMETIC=on`, `check`
    /// the inputs against `roots ()` and fail with every violation, one per line. With the mode
    /// off it reads nothing but `HERMETIC`.
    /// </summary>
    let enforce (name: string) (inputs: Input list) : Recipe<ExecContext, unit> =
        recipe {
            let! on = enabled ()
            if on then
                let! r = roots ()
                match check name r inputs with
                | [] -> ()
                | violations -> failwith (String.concat "\n" violations)
        }
