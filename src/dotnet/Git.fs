namespace Xake.Dotnet

open System.IO

open Xake
open Xake.Tasks

/// The current commit of a project's repository, read directly from `.git` -- no `git`
/// executable. Two uses: `Project.import` needs the files that name the current commit as
/// inputs (so a new commit re-imports), and `run` (`Dotnet.csc.fs`) needs the sha itself, to
/// resolve `$(SourceRevisionId)` at compile time.
module Git =

    /// The `.git` entry above (or at) `dir`, walking up -- stops at the filesystem root. A
    /// normal checkout has a `.git` directory; a linked worktree has a `.git` file instead
    /// (`gitdir: <path>`).
    let rec private findGitEntry (dir: string) : string option =
        let candidate = Path.Combine (dir, ".git")
        if Directory.Exists candidate || File.Exists candidate then Some candidate
        else
            match Path.GetDirectoryName (dir: string) with
            | null | "" -> None
            | parent when parent = dir -> None
            | parent -> findGitEntry parent

    /// The directory that holds `HEAD` (`gitDir`) and the one refs are resolved against
    /// (`commonDir`) -- the same directory for an ordinary repository, but for a linked
    /// worktree `gitDir` is the worktree's own private directory
    /// (`<repo>/.git/worktrees/<name>`) while `commonDir` -- named by its `commondir` file --
    /// is the main repository's `.git`, where `refs/` and `packed-refs` actually live.
    let private resolveGitDir (gitEntry: string) : (string * string) option =
        if Directory.Exists gitEntry then
            let commondirFile = Path.Combine (gitEntry, "commondir")
            let common =
                if File.Exists commondirFile then
                    let rel = (File.ReadAllText commondirFile).Trim()
                    if rel = "" then gitEntry else Path.GetFullPath (Path.Combine (gitEntry, rel))
                else gitEntry
            Some (gitEntry, common)
        elif File.Exists gitEntry then
            let content = (File.ReadAllText gitEntry).Trim()
            let prefix = "gitdir:"
            if content.StartsWith prefix then
                let target = content.Substring(prefix.Length).Trim()
                let gitDir =
                    if Path.IsPathRooted target then target
                    else Path.GetFullPath (Path.Combine (Path.GetDirectoryName (gitEntry: string), target))
                let commondirFile = Path.Combine (gitDir, "commondir")
                let common =
                    if File.Exists commondirFile then
                        let rel = (File.ReadAllText commondirFile).Trim()
                        if rel = "" then gitDir else Path.GetFullPath (Path.Combine (gitDir, rel))
                    else gitDir
                Some (gitDir, common)
            else None
        else None

    /// A ref name (`refs/heads/main`) resolved against `packed-refs`, when it is not (or no
    /// longer) a loose ref file. Lines are `<sha> <refname>`; a `#`-prefixed line is the
    /// header, and a following `^<sha>` line (a peeled annotated tag) never matches a ref name
    /// so it is skipped implicitly.
    let private fromPackedRefs (commonDir: string) (refName: string) : string option =
        let packedRefs = Path.Combine (commonDir, "packed-refs")
        if not (File.Exists packedRefs) then None
        else
            File.ReadAllLines packedRefs
            |> Array.tryPick (fun line ->
                let line = line.Trim()
                if line = "" || line.StartsWith "#" || line.StartsWith "^" then None
                else
                    match line.Split ' ' with
                    | [| sha; name |] when name = refName -> Some sha
                    | _ -> None)

    /// The files that name the repository's current commit: `HEAD`, and -- when `HEAD` is a
    /// symbolic ref -- the loose ref file it points to when that exists, or `packed-refs` when
    /// the ref is only there. Empty when `dir` is not inside a git repository. These are the
    /// files `Project.import` `needFiles`s so that a new commit re-imports the project.
    let headFiles (dir: string) : string list =
        match findGitEntry dir |> Option.bind resolveGitDir with
        | None -> []
        | Some (gitDir, commonDir) ->
            let headPath = Path.Combine (gitDir, "HEAD")
            if not (File.Exists headPath) then [] else
            let headContent = (File.ReadAllText headPath).Trim()
            let prefix = "ref:"
            if headContent.StartsWith prefix then
                let refName = headContent.Substring(prefix.Length).Trim()
                let refPath = Path.Combine (commonDir, refName.Replace ('/', Path.DirectorySeparatorChar))
                if File.Exists refPath then [ headPath; refPath ]
                else
                    let packedRefs = Path.Combine (commonDir, "packed-refs")
                    if File.Exists packedRefs then [ headPath; packedRefs ] else [ headPath ]
            else [ headPath ]

    /// The repository's current commit sha, or `None` when `dir` is not inside a git
    /// repository (or `HEAD` cannot be resolved). Pure filesystem reads, no `git` executable.
    let headSha (dir: string) : string option =
        match findGitEntry dir |> Option.bind resolveGitDir with
        | None -> None
        | Some (gitDir, commonDir) ->
            let headPath = Path.Combine (gitDir, "HEAD")
            if not (File.Exists headPath) then None else
            let headContent = (File.ReadAllText headPath).Trim()
            let prefix = "ref:"
            if headContent.StartsWith prefix then
                let refName = headContent.Substring(prefix.Length).Trim()
                let refPath = Path.Combine (commonDir, refName.Replace ('/', Path.DirectorySeparatorChar))
                if File.Exists refPath then
                    match (File.ReadAllText refPath).Trim() with
                    | "" -> None
                    | sha -> Some sha
                else fromPackedRefs commonDir refName
            else
                match headContent with
                | "" -> None
                | sha -> Some sha

    /// The token a lock carries in place of the commit sha (`tokenize`), resolved back from
    /// the repository right before a compile (`Lock.compile`).
    let revisionToken = "$(SourceRevisionId)"

    /// Replaces every occurrence of `sha` in `text` with the literal token `revisionToken`
    /// (`$(SourceRevisionId)`). `sourcelink.json` (and, in principle, any other generated
    /// text) embeds the commit that produced it -- SourceLink's own doing, not this tool's --
    /// which would otherwise make a lock's content, and so the lock file itself, change on
    /// every commit even though the compilation it describes did not. The sha itself is
    /// deliberately not recorded anywhere in the lock; `Lock.compile` resolves the token back
    /// from the project's repository right before it would be used.
    ///
    /// Text-level so that it can live here, before `Lock` in compile order (which needs
    /// `headSha`): a whole entry -- `Generated` content, `Options`, `Defines` and evaluation
    /// `Properties` -- is `entry |> Lock.mapText (Git.tokenize sha)`. Pure; a no-op when `sha`
    /// is empty.
    let tokenize (sha: string) (text: string) : string =
        if sha = "" || not (text.Contains sha) then text else text.Replace (sha, revisionToken)
