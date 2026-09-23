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

    /// The tokens `builtin` always provides; an extra root may not redeclare one of them.
    let builtinTokens = [ "$(NuGetPackageRoot)"; "$(ProjectRoot)"; "$(DotnetRoot)" ]

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
    /// look like `$(Name)`, must not be one of the built-in three, and its path must be
    /// absolute: fails early rather than tokenizing nothing, or the wrong thing, silently.
    /// Longest root first is kept, same as `builtin` alone.
    let withExtra (projectRoot: string) (extra: (string * string) list) =
        for (token, path) in extra do
            if not (System.Text.RegularExpressions.Regex.IsMatch (token, @"^\$\([A-Za-z_][A-Za-z0-9_]*\)$")) then
                failwithf "'%s' is not a valid root token: expected the form $(Name)" token
            if List.contains token builtinTokens then
                failwithf "'%s' is a built-in root token and cannot be redeclared" token
            if not (Path.IsPathRooted path) then
                failwithf "root '%s' must be an absolute path, got '%s'" token path
        (builtin projectRoot @ (extra |> List.map (fun (token, path) -> token, normalize path)))
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
