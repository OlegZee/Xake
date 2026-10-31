namespace Xake.Hermetic.Dotnet

open System.IO

open Xake
open Xake.Dotnet

/// <summary>
/// The build variables Xake.Hermetic.Dotnet reads, as a schema a script MAY import:
/// <c>varschema HermeticVars.Default</c>, or with fields changed by copy-and-update, e.g.
/// <c>varschema { HermeticVars.Default with CI = Var.string(envVar = "CI") |> withDefault "off" }</c> to take
/// the environment variable `CI` too (what a CI service sets; without `envVar`, `XAKE__CI`). Each field is named by its
/// variable, is a string, takes `-d NAME=value` and no environment variable (the package reads
/// none), and carries the package's fixed default where it has one. Without the import the
/// package reads the same names with the same defaults. Xake.Dotnet's are `DotNetVars`.
/// </summary>
type HermeticVars = {
    /// Whether the build runs under CI (a missing lock fails, `LOCK_STALE=warn` does not apply): on or off.
    CI: RequiredVar<string>
    /// The package ids a lock re-resolves (`*` every one), comma, semicolon or space separated.
    LOCK_REFRESH: OptionalVar<string>
    /// A stale lock: fail or warn.
    LOCK_STALE: RequiredVar<string>
    /// The timestamp of pack entries and SBOM annotations, unix seconds; unset, 1980-01-01.
    SOURCE_DATE_EPOCH: OptionalVar<string>
} with
    static member Default : HermeticVars = {
        CI = Var.arg<string>(description = "Whether the build runs under CI: on or off") |> withDefault "off"
        LOCK_REFRESH = Var.arg<string>(description = "The package ids a lock re-resolves, * for every one")
        LOCK_STALE = Var.arg<string>(description = "A stale lock: fail or warn") |> withDefault "fail"
        SOURCE_DATE_EPOCH = Var.arg<string>(description = "The timestamp of pack entries and SBOM annotations, unix seconds; unset, 1980-01-01")
    }

/// Reading the variables of `HermeticVars` (the values parsed here; `Var.read` records them).
module internal HermeticVarValues =

    /// The DOS epoch, 1980-01-01: the oldest date a zip entry holds, and the timestamp when
    /// `SOURCE_DATE_EPOCH` is unset, empty or not a number.
    let epoch = System.DateTime (1980, 1, 1, 0, 0, 0, System.DateTimeKind.Utc)

    /// A `SOURCE_DATE_EPOCH` value (unix seconds), clamped to `epoch`.
    let sourceDateEpoch (value: string option) =
        match value |> Option.map (fun v -> v.Trim ()) with
        | None | Some "" -> epoch
        | Some v ->
            match System.Int64.TryParse v with
            | true, seconds -> max epoch (System.DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime)
            | false, _ -> epoch

    /// `SOURCE_DATE_EPOCH` of the variable set (read, and so a dependency, here).
    let readSourceDateEpoch : Recipe<ExecContext, System.DateTime> =
        recipe {
            let! value = Var.read<string> "SOURCE_DATE_EPOCH"
            return sourceDateEpoch value
        }

/// The roots a kept evaluation and a lock file are written against: what a build links
/// against sits under a package cache, a checkout and the SDK, and none of the three is in the
/// same place on the next machine. A path under a root is written as `$(Token)/...` and
/// expanded back on read, which is what makes those files byte-identical everywhere and their
/// diffs mean something.
///
/// The project root is the engine's (`ExecOptions.ProjectRoot`, what `need`, `getFiles` and
/// rule matching already resolve against), not the process's current directory: inside a
/// recipe take the list from `current` / `currentWith`, outside one pass the root explicitly to
/// `builtin` / `make`.
module Roots =

    /// The machine's NuGet package cache, `~/.nuget/packages` (`DotNetFwk.nugetRoot`; the
    /// environment variable `NUGET_PACKAGES` is not read). This is the package folder of the
    /// context-free functions here (`builtin`, `make`), which have no build to read the build
    /// variable from. A recipe takes the build's folder instead -- `current`/`currentWith`,
    /// which follow `DotNetFwk.packageRoot ()`: the build variable `NUGET_PACKAGES`, else `.packages` --
    /// so a lock recorded in a build with its own package folder tokenizes that folder as
    /// `$(NuGetPackageRoot)`.
    let nugetRoot () = DotNetFwk.nugetRoot ()

    /// The .NET SDK installation root, when one can be located: compilers, analyzers and
    /// reference packs live under it.
    let dotnetRoot () = DotNetFwk.dotnetRoot ()

    /// The token the package folder is written against.
    let nugetPackageRootToken = "$(NuGetPackageRoot)"

    /// The tokens `builtin` always provides. An extra root of the same name replaces the
    /// built-in one (see `make`).
    let builtinTokens = [ nugetPackageRootToken; "$(ProjectRoot)"; "$(DotnetRoot)" ]

    /// The extra-root list that points `$(NuGetPackageRoot)` at a folder of the build's own,
    /// for `Lock.loadWith` / `saveWith`: a lock is read against the same folder the build
    /// restores into (`Restore.Options.PackageRoot`), or the two disagree about where the
    /// packages are. `Lock.loadWith (Roots.packageRootOverride "./.packages") "locks/app.json"`.
    let packageRootOverride (dir: string) = [ nugetPackageRootToken, dir ]

    let private normalize (path: string) = path.Replace('\\', '/').TrimEnd '/'

    /// The three built-in roots for a given project root, longest root first so that the more
    /// specific one wins.
    let builtin (projectRoot: string) =
        let projectRoot =
            match projectRoot with
            | null | "" -> Directory.GetCurrentDirectory()
            | dir -> Path.GetFullPath dir
        [ yield "$(NuGetPackageRoot)", nugetRoot ()
          yield "$(ProjectRoot)", projectRoot
          match dotnetRoot () with
          | Some root -> yield "$(DotnetRoot)", root
          | None -> () ]
        |> List.map (fun (token, path) -> token, normalize path)
        |> List.sortByDescending (snd >> String.length)

    /// Combines the built-in roots with extra ones a script declares explicitly -- one token
    /// per sibling repository, no shared parent root, so that importing a project from a second
    /// checkout (e.g. a cross-repo `ProjectReference`) still tokenizes. Each extra token must
    /// look like `$(Name)`: fails early rather than tokenizing nothing, or the wrong thing,
    /// silently. Longest root first is kept, same as `builtin` alone.
    ///
    /// An extra root *replaces* the built-in of the same name rather than being refused (it
    /// used to be refused): that is how a build declares a package folder of its own --
    /// `make root (Roots.packageRootOverride dir)` reads and writes
    /// `$(NuGetPackageRoot)/...` against `dir` instead of the machine's cache, so a lock can
    /// be resolved against the same folder `Restore` fills.
    ///
    /// A relative path is taken against `projectRoot`, like every other path a script writes,
    /// and not against the process's current directory -- `Path.GetFullPath` in a script would
    /// silently reintroduce exactly the cwd dependency the project root exists to avoid. The
    /// function stays pure in its two arguments either way.
    let make (projectRoot: string) (extra: (string * string) list) =
        let baseDir =
            match projectRoot with
            | null | "" -> Directory.GetCurrentDirectory()
            | dir -> Path.GetFullPath dir
        let extra =
            extra |> List.map (fun (token, path) ->
                if not (System.Text.RegularExpressions.Regex.IsMatch (token, @"^\$\([A-Za-z_][A-Za-z0-9_]*\)$")) then
                    failwithf "'%s' is not a valid root token: expected the form $(Name)" token
                let full = if Path.IsPathRooted path then Path.GetFullPath path else Path.GetFullPath (baseDir </> path)
                token, normalize full)
        let overridden = extra |> List.map fst |> Set.ofList
        ((builtin projectRoot |> List.filter (fst >> overridden.Contains >> not)) @ extra)
        |> List.sortByDescending (snd >> String.length)

    /// Paths are written with '/' whatever the platform: these files are read and diffed by
    /// people, and the compilers take forward slashes everywhere.
    let internal tokenize roots (path: string) =
        let path = path.Replace('\\', '/')
        roots
        |> List.tryPick (fun (token: string, root: string) ->
            if path.StartsWith (root + "/") then Some (token + path.Substring root.Length) else None)
        |> Option.defaultValue path

    /// Replaces a root anywhere in `text`, not only as a prefix, and nothing else: the
    /// content of a generated source, whose backslashes are the language's (an escape in a
    /// string literal), not path separators.
    let internal tokenizeText roots (text: string) =
        roots |> List.fold (fun (text: string) (token: string, root: string) ->
            // the root followed by a separator or the end, not a longer name with that prefix
            System.Text.RegularExpressions.Regex.Replace (
                text, System.Text.RegularExpressions.Regex.Escape root + "(?=[/=,;]|$)", token.Replace ("$", "$$"))) text

    /// Replaces a root anywhere in the string, not only as a prefix: `/pathmap:<root>=/_/`
    /// carries one in the middle. A path field: `\` becomes `/` first.
    let internal tokenizeAll roots (text: string) =
        tokenizeText roots (text.Replace ('\\', '/'))

    let internal expand roots (path: string) =
        roots |> List.fold (fun (path: string) (token: string, root: string) -> path.Replace(token, root)) path

    /// `current` plus the extra roots a script declares (see `make`). An extra
    /// `$(NuGetPackageRoot)` (`packageRootOverride`) wins over the build's package folder.
    let currentWith (extra: (string * string) list) : Recipe<ExecContext, (string * string) list> =
        recipe {
            let! options = getCtxOptions()
            // the build's package folder (the build variable `NUGET_PACKAGES`, else
            // `.packages`), read -- and so a dependency -- here; an explicit override wins
            let! packageRoot = DotNetFwk.packageRoot ()
            let extra =
                if extra |> List.exists (fst >> (=) nugetPackageRootToken) then extra
                else (nugetPackageRootToken, packageRoot) :: extra
            return make options.ProjectRoot extra
        }

    /// The built-in roots for the project root this build runs with -- the engine's
    /// `ExecOptions.ProjectRoot`, not the process's current directory -- with
    /// `$(NuGetPackageRoot)` at the build's package folder (`DotNetFwk.packageRoot ()`: the
    /// build variable `NUGET_PACKAGES`, else `.packages` under the project root).
    let current : Recipe<ExecContext, (string * string) list> = currentWith []
