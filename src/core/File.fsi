namespace Xake

/// A file: its normalised absolute path (`/` as the separator on every OS), compared ordinally.
[<Sealed>]
type File =
    interface System.IComparable
    override Equals: obj -> bool
    override GetHashCode: unit -> int

    /// Xake's name of the file: the normalised absolute path with `/`, its identity (what Xake
    /// stores, compares and shows).
    member Name : string
    /// The path in the OS form (`File.getSysFileName`).
    [<System.Obsolete("Use File.getSysFileName for the OS form, or Name for Xake's form; to be removed in a later release")>]
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

    /// The path in the OS form (`\` on Windows), for command lines and tools: `Name` with the
    /// separators converted, one to one.
    val getSysFileName: File -> string

    /// A new `FileInfo` of the file at each call, for disk queries (nothing is cached in `File`).
    val getFileInfo: File -> System.IO.FileInfo

    /// The path in the OS form.
    [<System.Obsolete("Use File.getSysFileName for the OS form, or Name for Xake's form; to be removed in a later release")>]
    val getFullName: File -> string

    /// Get true if file exists
    val exists: File -> bool

    /// Get the file modification time
    val getLastWriteTime: File -> System.DateTime
