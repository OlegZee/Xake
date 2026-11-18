namespace Xake

/// A file: its normalised absolute path (`/` as the separator on every OS), compared ordinally.
[<Sealed>]
type File =
    interface System.IComparable
    override Equals: obj -> bool
    override GetHashCode: unit -> int

    /// The absolute path with `/` separators.
    member Name : string
    /// The absolute path in the OS form (`\` on Windows).
    member FullName : string

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module File =

    /// Makes a File from a path: a rooted path as is, a relative one against the current
    /// directory at the call; normalised lexically (`Path.collapse`). Fails on an empty name.
    val make : string -> File

    /// The path relative to `root` with `/` when the file is under it, else absolute.
    val display : root: string -> (File -> string)

    /// The one comparer of paths: ordinal.
    val comparer : System.StringComparer

    /// Gets the file name.
    val getFileName: File -> string

    /// Gets the file extension.
    val getFileExt: File -> string

    /// Gets the file direactory.
    val getDirName: File -> string

    /// Gets fully qualified file name.
    val getFullName: File -> string

    /// Get true if file exists
    val exists: File -> bool

    /// Get the file modification time
    val getLastWriteTime: File -> System.DateTime
