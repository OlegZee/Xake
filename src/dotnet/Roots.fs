namespace Xake.Dotnet

open System.IO

open Xake

/// The roots a kept evaluation and a lock file are written against: what a build links
/// against sits under a package cache, a checkout and the SDK, and none of the three is in the
/// same place on the next machine. A path under a root is written as `$(Token)/...` and
/// expanded back on read, which is what makes those files byte-identical everywhere and their
/// diffs mean something.
///
/// The project root is the engine's (`ExecOptions.ProjectRoot`, what `need`, `getFiles` and
/// rule matching already resolve against), not the process's current directory: inside a
/// recipe take the list from `current` / `currentWith`, outside one pass the root explicitly to
/// `builtin` / `withExtra`.
module Roots =

    /// The NuGet package cache (`NUGET_PACKAGES`, else `~/.nuget/packages`).
    let nugetRoot () = DotNetFwk.sdkImpl.nugetRoot ()

    /// The .NET SDK installation root, when one can be located: compilers, analyzers and
    /// reference packs live under it.
    let dotnetRoot () = DotNetFwk.sdkImpl.dotnetRoot ()

    /// The token the package folder is written against.
    let nugetPackageRootToken = "$(NuGetPackageRoot)"

    /// The tokens `builtin` always provides. An extra root of the same name replaces the
    /// built-in one (see `withExtra`).
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
    /// `withExtra root (Roots.packageRootOverride dir)` reads and writes
    /// `$(NuGetPackageRoot)/...` against `dir` instead of the machine's cache, so a lock can
    /// be resolved against the same folder `Restore` fills.
    ///
    /// A relative path is taken against `projectRoot`, like every other path a script writes,
    /// and not against the process's current directory -- `Path.GetFullPath` in a script would
    /// silently reintroduce exactly the cwd dependency the project root exists to avoid. The
    /// function stays pure in its two arguments either way.
    let withExtra (projectRoot: string) (extra: (string * string) list) =
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

    /// Replaces a root anywhere in the string, not only as a prefix: `/pathmap:<root>=/_/`
    /// carries one in the middle.
    let internal tokenizeAll roots (text: string) =
        let text = text.Replace ('\\', '/')
        roots |> List.fold (fun (text: string) (token: string, root: string) ->
            // the root followed by a separator or the end, not a longer name with that prefix
            System.Text.RegularExpressions.Regex.Replace (
                text, System.Text.RegularExpressions.Regex.Escape root + "(?=[/=,;]|$)", token.Replace ("$", "$$"))) text

    let internal expand roots (path: string) =
        roots |> List.fold (fun (path: string) (token: string, root: string) -> path.Replace(token, root)) path

    /// The built-in roots for the project root this build runs with -- the engine's
    /// `ExecOptions.ProjectRoot`, not the process's current directory.
    let current : Recipe<ExecContext, (string * string) list> =
        recipe {
            let! options = getCtxOptions()
            return builtin options.ProjectRoot
        }

    /// `current` plus the extra roots a script declares (see `withExtra`).
    let currentWith (extra: (string * string) list) : Recipe<ExecContext, (string * string) list> =
        recipe {
            let! options = getCtxOptions()
            return withExtra options.ProjectRoot extra
        }
