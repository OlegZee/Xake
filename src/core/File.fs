namespace Xake

open System.IO

/// The normalised absolute path, `/` as the separator; nothing cached.
[<CustomEquality; CustomComparison>]
type File = private F of string with

    member f.Name = let (F n) = f in n
    member f.FullName =
        let (F n) = f
        if Path.DirectorySeparatorChar = '/' then n else n.Replace('/', Path.DirectorySeparatorChar)

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

    let private absolute (n: string) =
        Xake.Path.collapse (if Path.IsPathRooted n then n else Path.Combine(Directory.GetCurrentDirectory(), n))

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
    let getDirName (f:File) = f.FullName |> Path.GetDirectoryName
    /// Returns the fully qualified path of the file.
    let getFullName (f:File) = f.FullName
    /// Returns true if the file exists on disk.
    let exists (f:File) = BclFile.Exists f.FullName
    /// Returns the last write time of the file.
    let getLastWriteTime (f:File) = BclFile.GetLastWriteTime f.FullName
