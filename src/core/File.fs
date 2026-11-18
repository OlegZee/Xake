namespace Xake

open System.IO

/// The normalised absolute path, `/` as the separator; nothing cached.
[<CustomEquality; CustomComparison>]
type File = private F of string with

    member f.Name = let (F n) = f in n
    /// The OS form: `/` -> the OS separator.
    member internal f.SysName =
        let (F n) = f
        if Path.DirectorySeparatorChar = '/' then n else n.Replace('/', Path.DirectorySeparatorChar)
    [<System.Obsolete("Use File.getSysFileName for the OS form, or Name for Xake's form; to be removed in a later release")>]
    member f.FullName = f.SysName

    interface System.IComparable with
        member x.CompareTo yobj =
            match yobj with
            | :? File as y -> System.String.CompareOrdinal(x.Name, y.Name)
            | _ -> invalidArg "yobj" "cannot compare values of different types"

    override x.Equals(yobj) =
        match yobj with
        | :? File as y -> System.String.Equals(x.Name, y.Name, System.StringComparison.Ordinal)
        | _ -> false
    override x.GetHashCode() = System.StringComparer.Ordinal.GetHashCode x.Name

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module File =

    type String = System.String

    type private BclFile = System.IO.File

    /// A rooted path as is; a relative one against the current directory; on Windows `\foo` (no
    /// drive) and `C:foo` (drive-relative) through `GetFullPath` (A6).
    let private absolute (n: string) =
        let qualified (n: string) =
            n.StartsWith @"\\" || n.StartsWith "//" || (n.Length >= 3 && n.[1] = ':' && (n.[2] = '\\' || n.[2] = '/'))
        let full =
            if not (Path.IsPathRooted n) then Path.Combine(Directory.GetCurrentDirectory(), n)
            elif Env.isUnix || qualified n then n
            else Path.GetFullPath n
        Xake.Path.collapse full

    /// Creates a File from the given path string. Fails if the name is null or whitespace.
    let make n =
        if String.IsNullOrWhiteSpace n then
            failwith "File name cannot be empty"
        F (absolute n)

    let display (root: string) =
        let r = absolute root
        let prefix = if r.EndsWith "/" then r else r + "/"
        fun (f: File) ->
            let p = f.Name
            if p.Length > prefix.Length && p.StartsWith(prefix, System.StringComparison.Ordinal) then p.Substring prefix.Length
            else p

    let comparer = System.StringComparer.Ordinal

    /// Returns the file name portion of a File's path.
    let getFileName (f:File) = f.Name |> Path.GetFileName
    /// Returns the file extension including the leading dot.
    let getFileExt (f:File) = f.Name |> Path.GetExtension
    /// Returns the directory portion of a File's full path.
    let getDirName (f:File) = f.SysName |> Path.GetDirectoryName
    /// The path in the OS form.
    let getSysFileName (f:File) = f.SysName
    /// A new FileInfo of the file.
    let getFileInfo (f:File) = FileInfo f.SysName
    [<System.Obsolete("Use File.getSysFileName for the OS form, or Name for Xake's form; to be removed in a later release")>]
    let getFullName (f:File) = f.SysName
    /// Returns true if the file exists on disk.
    let exists (f:File) = BclFile.Exists f.SysName
    /// Returns the last write time of the file.
    let getLastWriteTime (f:File) = BclFile.GetLastWriteTime f.SysName
