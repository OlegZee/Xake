[<AutoOpen>]
module Xake.Fileset

open System.IO
open Xake

/// <summary>
/// Defines interface to a file system
/// </summary>
type FileSystemType = {
    GetDisk: string -> string
    GetDirRoot: string -> string
    GetParent: string -> string
    AllDirs: string -> string seq
    ScanDirs: string -> string -> string seq  // mask -> dir -> dirs
    ScanFiles: string -> string -> string seq // mask -> dir -> files
}

/// Type alias for a file pattern string.
type FilePattern = string

/// Filesystem pattern
type FilesetElement = | Includes of Path.PathMask | Excludes of Path.PathMask

/// Options controlling fileset evaluation: base directory and empty-result behavior.
type FilesetOptions = {FailOnEmpty:bool; BaseDir:string option}

/// A fileset definition: options plus a list of include/exclude elements.
type Fileset = Fileset of FilesetOptions * FilesetElement list
/// A materialized list of files produced from evaluating a Fileset.
type Filelist = Filelist of File list

/// Default fileset options
let DefaultOptions = {FilesetOptions.BaseDir = None; FailOnEmpty = false}

/// The empty fileset with default options.
let Empty = Fileset (DefaultOptions,[])
/// The empty file list.
let EmptyList = Filelist []

/// Implementation module
module private Impl =

    open Path

    let fullname (f:DirectoryInfo) = f.FullName

    let FileSystem = {
        GetDisk = fun d -> d + Path.DirectorySeparatorChar.ToString()
        GetDirRoot = fun x -> Directory.GetDirectoryRoot x
        GetParent = Directory.GetParent >> fullname
        AllDirs = fun dir -> Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
        ScanDirs = fun mask dir -> Directory.EnumerateDirectories(dir, mask, SearchOption.TopDirectoryOnly)
        ScanFiles = fun mask dir -> Directory.EnumerateFiles(dir, mask)
    }

    let ifNone = Option.fold (fun _ -> id)

    /// The mask of an element as the pattern module reads it, with its role (spec 6.1, 8.3).
    let element = function
        | Includes pat -> PatternAst.MaskRole.Include, Path.maskText pat
        | Excludes pat -> PatternAst.MaskRole.Exclude, Path.maskText pat

    /// Evaluates the ordered elements from `baseDir` with the pattern module's walker (spec 6.3)
    /// and reports its warnings and notes to the build log (7.3, 6.5, 6.6). Raises
    /// `PatternAst.PatternException` for an invalid mask.
    let walk (baseDir: string) (elements: (PatternAst.MaskRole * string) list) =
        let result = PatternScan.scan (not Env.isUnix) baseDir elements
        PatternScan.reportWarnings result
        result.Files

    /// Implementation of fileset execute
    /// "Materializes" fileset to a filelist: the base directory (or `root`), the elements in
    /// declaration order (an exclude removes from the includes before it), files in ordinal
    /// order per directory, duplicates removed (spec 6.3, 6.6).
    let scan root (Fileset (options,filesetItems)) =
        let baseDir = options.BaseDir |> ifNone root
        filesetItems |> List.map element |> walk baseDir |> List.map File.make |> Filelist

    /// An include or exclude element of a mask, checked when it is built (spec 7.1, 8.3): raises
    /// `PatternAst.PatternException` for an invalid mask, reports its parse-time warnings once.
    let includes (mask: FilePattern) =
        PatternAst.Mask.check PatternAst.MaskRole.Include mask
        Includes (Path.parse mask)

    let excludes (mask: FilePattern) =
        PatternAst.Mask.check PatternAst.MaskRole.Exclude mask
        Excludes (Path.parse mask)

    // combines two fileset options
    let combineOptions (o1:FilesetOptions) (o2:FilesetOptions) =
        {DefaultOptions with
            BaseDir =
                match o1.BaseDir,o2.BaseDir with
                | Some _, Some _ -> failwith "Cannot combine filesets with basedirs defined in both (not implemented)"
                | Some _, None -> o1.BaseDir
                | _ -> o2.BaseDir
            FailOnEmpty = o1.FailOnEmpty || o2.FailOnEmpty}

    // combines two filesets
    let combineWith (Fileset (o2, set2)) (Fileset (o1,set1)) = Fileset(combineOptions o1 o2, set1 @ set2)

    // Combines result of reading file to a fileset
    let combineWithFile map (file:FileInfo) (Fileset (opts,fs)) =
        // blank lines are skipped (an empty mask is an error, XP018)
        let elements = File.ReadAllLines file.FullName |> Array.toList |> List.filter (System.String.IsNullOrWhiteSpace >> not) |> List.map map in
        Fileset (opts, fs @ elements)

    let changeBasedir dir (Fileset (opts,ps)) =   Fileset ({opts with BaseDir = Some dir}, ps)
    let changeFailonEmpty f (Fileset (opts,ps)) = Fileset ({opts with FailOnEmpty = f}, ps)

/// Fileset persistance implementation
module private PicklerImpl =

    open Pickler

    let filesetoptions =
        wrap(
            (fun(foe,bdir) -> {FilesetOptions.FailOnEmpty = foe; BaseDir = bdir}),
            fun o -> (o.FailOnEmpty, o.BaseDir))
            (pair bool (option str))

    let filesetElement =
      alt
        (function | Includes _ -> 0 | Excludes _ -> 1)
        [|
          wrap (Includes, fun (Includes p | OtherwiseFail p) -> p) Path.pickler
          wrap (Excludes, fun (Excludes p | OtherwiseFail p) -> p) Path.pickler
        |]

    let fileinfo = wrap(File.make, File.getFullName) str

    let fileset  = wrap(Fileset, fun (Fileset (o,l)) -> o,l) (pair filesetoptions (list filesetElement))
    let filelist = wrap(Filelist, fun (Filelist l) -> l) (list fileinfo)

open Impl

/// Gets the pickler for fileset type
let filesetPickler = PicklerImpl.fileset
/// Gets the pickler for filelist type
let filelistPickler = PicklerImpl.filelist

/// <summary>
/// Creates a new fileset with default options.
/// </summary>
/// <param name="filePattern"></param>
let ls (filePattern:FilePattern) =
    Fileset (DefaultOptions, [Impl.includes filePattern])

/// <summary>
/// Create a file set for specific file mask. The same as "ls"
/// </summary>
let (!!) = ls

/// <summary>
/// Defines the empty fileset with a specified base dir.
/// </summary>
/// <param name="dir"></param>
let (~+) dir =
    Fileset ({DefaultOptions with BaseDir = Some dir}, [])

[<System.Obsolete("Use Path.parse instead")>]
let parseFileMask = Path.parse

[<System.Obsolete("Use Path.parseDir instead")>]
let parseDirMask = Path.parseDir

// let matches filePattern projectRoot
[<System.Obsolete("Use Path.matches instead")>]
let matches = Path.matches

/// The real file system adapter. Unused since 4.0: fileset scanning reads the file system
/// through the pattern module's walker.
[<System.Obsolete("Unused since Xake 4.0: fileset scanning reads the file system directly")>]
let FileSystem = Impl.FileSystem

/// <summary>
/// "Materializes" fileset to a filelist
/// </summary>
let toFileList = Impl.scan

/// <summary>
/// "Materializes" file mask to a list of files/paths
/// </summary>
let listByMask (root:string) (mask: Path.PathMask) : string seq =
    Impl.walk root [ PatternAst.MaskRole.Include, Path.maskText mask ] |> Seq.ofList

/// <summary>
/// The same as toFileList. The file system adapter is ignored since Xake 4.0 (the pattern
/// module's walker reads the file system).
/// </summary>
[<System.Obsolete("The file system adapter is ignored since Xake 4.0; use toFileList")>]
let toFileList1 (_: FileSystemType) root fileset = Impl.scan root fileset

/// Describes how a file list changed: an item was added or removed.
type ListDiffType<'a> = | Added of 'a | Removed of 'a

/// <summary>
/// Compares two file lists and returns differences list.
/// </summary>
/// <param name="list1"></param>
/// <param name="list2"></param>
let compareFileList (Filelist list1) (Filelist list2) =

    let setOfNames = List.map File.getFullName >> Set.ofList

    let set1, set2 = setOfNames list1, setOfNames list2

    let removed = Set.difference set1 set2 |> List.ofSeq |> List.map (Removed)
    let added = Set.difference set2 set1 |> List.ofSeq |> List.map (Added)

    removed @ added

/// <summary>
/// Defines various operations on Fieset type.
/// </summary>
type Fileset with
    static member (+) (fs1, fs2: Fileset) :Fileset = fs1 |> combineWith fs2
    static member (+) (fs1: Fileset, pat) = fs1 ++ pat
    static member (-) (fs1: Fileset, pat) = fs1 -- pat
    static member (@@) (fs1, basedir) = fs1 |> Impl.changeBasedir basedir
    static member (@@) (Fileset (_,lst), options) = Fileset (options,lst)

    /// Conditional include/exclude operator
    static member (+?) (fs1: Fileset, (condition:bool,pat: FilePattern)) = if condition then fs1 ++ pat else fs1
    static member (+?) (fs1: Fileset, (condition:bool,fs2: Fileset)) :Fileset = if condition then fs1 |> combineWith fs2 else fs1
    static member (-?) (fs1: Fileset, (condition:bool,pat: FilePattern)) = if condition then fs1 -- pat else fs1

    /// Adds includes pattern to a fileset.
    static member (++) ((Fileset (opts,pts)), includes) :Fileset =
        Fileset (opts, pts @ [Impl.includes includes])

    /// Adds excludes pattern to a fileset.
    static member (--) (Fileset (opts,pts), excludes): Fileset =
        Fileset (opts, pts @ [Impl.excludes excludes])
end

/// Computation expression builder for constructing Fileset values.
type FilesetBuilder() =

    [<CustomOperation("failonempty")>]
    member __.FailOnEmpty(fs,f) = fs |> changeFailonEmpty f

    [<CustomOperation("basedir")>]
    member __.Basedir(fs,dir) = fs |> changeBasedir dir

    [<CustomOperation("includes")>]
    member __.Includes(fs:Fileset,pattern) = fs ++ pattern

    [<CustomOperation("includesif")>]
    member __.IncludesIf(fs:Fileset,condition, pattern:FilePattern) =    fs +? (condition,pattern)

    [<CustomOperation("join")>]
    member __.JoinFileset(fs1, fs2) = fs1 |> Impl.combineWith fs2

    [<CustomOperation("excludes")>]
    member __.Excludes(fs:Fileset, pattern) = fs -- pattern

    [<CustomOperation("excludesif")>]
    member __.ExcludesIf(fs:Fileset, pattern) = fs -? pattern

    [<CustomOperation("includefile")>]
    member __.IncludeFile(fs, file)  = (file, fs) ||> combineWithFile Impl.includes

    [<CustomOperation("excludefile")>]
    member __.ExcludeFile(fs,file)    = (file, fs) ||> combineWithFile Impl.excludes

    member __.Yield(())    = Empty
    member __.Return(pattern:FilePattern) = Empty ++ pattern

    member __.Combine(fs1, fs2) = fs1 |> Impl.combineWith fs2
    member __.Delay(f) = f()
    member this.Zero() = this.Yield ( () )

    member __.Bind(fs1:Fileset, f) = let fs2 = f() in fs1 |> Impl.combineWith fs2
    member x.For(fs, f) = x.Bind(fs, f)
    member x.Return(a) = x.Yield(a)

/// The fileset computation expression builder instance.
let fileset = FilesetBuilder()
