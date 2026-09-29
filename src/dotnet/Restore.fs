namespace Xake.Dotnet

open System.IO

open Xake
open Xake.Tasks

/// One package of the restore graph, as `project.assets.json` and the package cache saw it
/// at import time (a lock records these per entry, `Lock.Entry.Packages`). Top-level in the
/// namespace so its field labels are in scope wherever `Xake.Dotnet` is open.
type Package = {
    Id: string
    Version: string
    /// base64 sha512 from the cache's `.nupkg.metadata` (`contentHash`); "" when the cache
    /// lacked it at import
    Sha512: string
    /// a direct `PackageReference` of the project (as opposed to transitive)
    Direct: bool
    /// package ids this one depends on, resolved within the same graph
    DependsOn: string list
}

/// Obtaining the packages a lock names, on a machine that does not have them yet.
///
/// A lock is a complete *description* of what a compilation reads -- every reference and
/// analyzer by absolute path, plus the whole restore graph with the nupkg's own hash
/// (`Lock.Entry.Packages`). Until this module it was only a partial *source* for
/// obtaining them: a compiler living in a package was restored, reference packages were not,
/// so an empty package folder failed the build with hundreds of `expected <sha256>, got
/// missing` lines.
///
/// Nothing here resolves a version or consults `project.assets.json`: the lock already says
/// which package, at which version, holds which file. The paths carry that -- a reference
/// under the package folder is `<root>/<id>/<version>/...` -- so a missing file names its own
/// package (`Nuget.packageOf`), and a single `dotnet restore` of a synthesized project with
/// one `PackageDownload` per missing package fetches the whole set at once.
///
/// What it deliberately does not do: decide whether the *files* are the right ones. The
/// SHA-256 check in `Csc.run` stays the authority on that, and runs after this
/// step. This module only gets the bytes onto the disk and checks the nupkg against the
/// sha512 the lock recorded.
module Restore =

    /// Where the packages a lock names live on this machine, and whether a missing one may be
    /// fetched.
    type Options = {
        /// The package folder: `None` is the machine's own cache (`Roots.nugetRoot ()`, i.e.
        /// `NUGET_PACKAGES` or `~/.nuget/packages`), `Some dir` a folder of the build's own --
        /// what a build agent points at `.packages/` so it can cache that one directory. The
        /// path must be absolute; `into` builds these options from a path written relative to
        /// the project root, the way a script writes every other path.
        ///
        /// The same folder has to be used when the lock is *read*, or the two disagree about
        /// where `$(NuGetPackageRoot)` is: pass `Roots.packageRootOverride dir` to
        /// `Lock.loadWith`.
        PackageRoot: string option
        /// Whether a package the lock names but the folder does not have may be downloaded.
        /// Default `true`: this is what the compiler restore has always done, and restoring
        /// cannot change *what* gets compiled -- the lock fixes the version, and the per-file
        /// SHA-256 check that follows fails the build if the bytes are not the recorded ones.
        /// A build that must never reach the network sets it to `false` and gets the old
        /// behaviour: the missing files are reported, in full, by that same check.
        Enabled: bool
    } with static member Default = {
            PackageRoot = None
            Enabled = true
        }

    /// The package folder in effect, normalized the way `Roots` writes paths.
    let packageRoot (options: Options) =
        DotNetFwk.normalizedPackageRoot options.PackageRoot

    /// <summary>
    /// Default options with the package folder at <c>dir</c>, taken relative to the build's
    /// project root (or absolute) -- the same rule as every other path in a script, and the
    /// reason this is a recipe: resolving it with <c>Path.GetFullPath</c> would resolve it
    /// against the process's current directory instead.
    ///
    /// The two places the folder has to be named -- reading the lock and restoring into it --
    /// are then one value:
    /// <code>
    /// let! restore = Restore.into ".packages"
    /// let! doc = Lock.loadWith (Roots.packageRootOverride (Restore.packageRoot restore)) "locks/app.json"
    /// do! Lock.restore restore doc
    /// </code>
    /// </summary>
    let into (dir: string) : Recipe<ExecContext, Options> =
        recipe {
            let! ctxOptions = getCtxOptions ()
            let root = Roots.withExtra ctxOptions.ProjectRoot (Roots.packageRootOverride dir)
            return { Options.Default with
                        PackageRoot = root |> List.tryPick (fun (token, path) -> if token = Roots.nugetPackageRootToken then Some path else None) }
        }

    /// What a restore is asked to make available: the files that have to exist, and the
    /// package graph that says which nupkg (by original id casing and sha512) each package
    /// directory comes from. `Lock.restoreRequest` projects lock entries into one; the files
    /// are every compiler, reference and analyzer path -- the compiler deliberately among
    /// them, since it is a package like any other when it comes from a
    /// `Microsoft.Net.Compilers.Toolset`-shaped one.
    type Request = {
        /// The restore graph the paths' packages are looked up in
        Packages: Package list
        /// Files expected on disk; only those under the package folder name a package
        Paths: string list
    }

    /// One package the request names whose files are not in the package folder.
    type Missing = {
        /// The id in its original NuGet casing when the lock's package graph knows it, the
        /// cache's lowercase directory name otherwise (restore is case-insensitive on ids).
        Id: string
        Version: string
        /// The nupkg's base64 sha512, from the lock's package graph; "" when the graph does
        /// not carry this package (a reference-assemblies package restored by the toolchain
        /// never appears in `project.assets.json`) -- nothing to verify against, then.
        Sha512: string
        /// The files of this package the entries name that are not on disk.
        Files: string list
    }

    /// The packages `request` names that the folder does not have, grouped so one restore
    /// covers all of them. Costs one `File.Exists` per distinct path and nothing else: on a
    /// machine that already has everything -- the normal case -- this is the whole price of
    /// the restore step, no process and no network.
    ///
    /// A path outside the package folder (the SDK's own compiler, a project reference, a
    /// framework assembly) names no package and is ignored here; whether it exists is the
    /// hash check's business.
    let missing (options: Options) (request: Request) : Missing list =
        let root = packageRoot options
        // the lock's package graph, keyed case-insensitively, for the original id casing and
        // the nupkg hash
        let graph =
            request.Packages
            |> List.fold
                (fun m (p: Package) ->
                    Map.add (p.Id.ToLowerInvariant (), p.Version.ToLowerInvariant ()) (p.Id, p.Sha512) m)
                Map.empty
        request.Paths
        |> List.distinct
        |> List.choose (fun path -> Nuget.packageOf root path |> Option.map (fun idVersion -> idVersion, path))
        |> List.filter (snd >> File.Exists >> not)
        |> List.groupBy fst
        |> List.map (fun ((id, version), items) ->
            let properId, sha512 =
                graph
                |> Map.tryFind (id.ToLowerInvariant (), version.ToLowerInvariant ())
                |> Option.defaultValue (id, "")
            ({ Id = properId; Version = version; Sha512 = sha512; Files = items |> List.map snd } : Missing))
        |> List.sortBy (fun p -> p.Id.ToLowerInvariant (), p.Version)

    /// What a restore of `packages` failed to deliver: the package directory still absent, or
    /// a nupkg whose sha512 is not the one the lock recorded. Empty means the folder now holds
    /// what the lock describes, as far as the package level can tell -- the per-file SHA-256
    /// check in the runner is what settles the files themselves.
    let verify (options: Options) (packages: Missing list) : string list =
        let root = packageRoot options
        [ for p in packages do
            let dir = root </> p.Id.ToLowerInvariant () </> p.Version.ToLowerInvariant ()
            if not (Directory.Exists dir) then
                yield sprintf "%s %s: not restored (expected it at '%s')" p.Id p.Version dir
            elif p.Sha512 <> "" then
                let actual = (Nuget.readCache root p.Id p.Version).Sha512
                if actual <> p.Sha512 then
                    yield sprintf "%s %s: expected sha512 %s, got %s"
                        p.Id p.Version p.Sha512 (if actual = "" then "none" else actual) ]

    /// The synthesized restore project (see `DotNetFwk.downloadPackages`).
    let internal projectText (packages: (string * string) list) = DotNetFwk.restoreProjectText packages

    /// Fetches the named `(id, version)` packages into the folder with **one** `dotnet
    /// restore`, whatever the policy says -- this is the primitive, `ensure` is the one that
    /// consults `Options.Enabled`. The mechanism is `DotNetFwk.downloadPackages`, shared with
    /// `csc { toolset }`; `ensure` serializes its own restores on a `Resource`.
    let download (options: Options) (packages: (string * string) list) : Recipe<ExecContext, unit> =
        DotNetFwk.downloadPackages options.PackageRoot packages

    /// One `Resource` per package folder, so that two compiles running in parallel and
    /// needing the same packages launch one restore between them rather than two competing
    /// ones over the same directory. Same mechanism, and the same reasoning, as
    /// `Project.withProjectLock`: a `Resource` is just a value, so a library recipe creates
    /// and uses one with no script-side declaration, and `withResource` yields the CPU slot
    /// while waiting.
    module private Locks =
        let private comparer = if Env.isUnix then System.StringComparer.Ordinal else System.StringComparer.OrdinalIgnoreCase
        let private locks = System.Collections.Concurrent.ConcurrentDictionary<string, Resource> (comparer)
        let forRoot (root: string) : Resource =
            locks.GetOrAdd (root, fun key -> Resource.newResource ("nuget-restore:" + key) 1)

    /// Packages this process has already restored into a given folder. Keyed
    /// case-insensitively on folder + id + version, and written only *after* a restore of that
    /// package completed: a package that was already on disk is not memoized (re-checking it
    /// is two `File.Exists`), while a package a restore could not fully provide is, so a build
    /// with a hundred entries naming it does not launch a hundred identical restores. What it
    /// failed to provide is then reported once, by the runner's hash check, per entry.
    let private restored = System.Collections.Concurrent.ConcurrentDictionary<string, unit> (System.StringComparer.OrdinalIgnoreCase)

    let private memoKey (root: string) (p: Missing) = root + "|" + p.Id + "|" + p.Version

    /// <summary>
    /// Makes every package `request` names available in the package folder, and reports what is
    /// still wrong: `[]` means nothing was missing, or everything missing was restored and its
    /// nupkg matched the sha512 the lock recorded.
    ///
    /// One restore for the whole set, once per process: the caller can hand it a single entry's
    /// request (what `Lock.compile` does) or every entry of a lock (what a script's "populate
    /// the package folder" target does, `Lock.restore`) and the cost is the same shape. With
    /// nothing missing it starts no
    /// process and touches no network -- see `missing`.
    ///
    /// Restoring is skipped, with a warning naming the count, when `Options.Enabled` is
    /// `false`; the caller's own check then reports each missing file with its expected hash,
    /// exactly as it did before this module existed.
    /// </summary>
    let ensure (options: Options) (request: Request) : Recipe<ExecContext, string list> =
        recipe {
            let root = packageRoot options
            let notMemoized = List.filter (fun p -> not (restored.ContainsKey (memoKey root p)))

            match missing options request |> notMemoized with
            | [] -> return []
            | wanted when not options.Enabled ->
                do! trace Warning
                        "%d package(s) named by the lock are not in '%s' and automatic restore is off (Restore.Options.Enabled): %s"
                        (List.length wanted) root
                        (wanted |> List.map (fun p -> p.Id + " " + p.Version) |> String.concat ", ")
                return []
            | _ ->
                let! problems =
                    withResource (Locks.forRoot root) 1 (recipe {
                        // whoever held the lock may have restored exactly what this entry
                        // needs while it waited -- recheck rather than restore again
                        match missing options request |> notMemoized with
                        | [] -> return []
                        | wanted ->
                            do! trace Info "restoring %d package(s) into '%s'" (List.length wanted) root
                            do! download options (wanted |> List.map (fun p -> p.Id, p.Version))
                            for p in wanted do restored.[memoKey root p] <- ()
                            return verify options wanted
                    })
                return problems
        }
