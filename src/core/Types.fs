namespace Xake

[<AutoOpen>]
module DomainTypes =

    let private stringCompare = if Env.isUnix then System.StringComparer.Ordinal else System.StringComparer.OrdinalIgnoreCase

    /// A build target: either a file to produce or a phony named action.
    [<CustomEquality;CustomComparison>]
    type Target =
        | FileTarget of File
        | PhonyAction of string

        with
            member internal this.ShortName =
                match this with
                | FileTarget file -> file.Name
                | PhonyAction name -> name
            member internal this.FullName =
                match this with
                | FileTarget file -> file.FullName
                | PhonyAction name -> name
            
            override x.Equals(yobj) =
                match yobj with
                | :? Target as y -> stringCompare.Equals (x.FullName, y.FullName)
                | _ -> false

            override x.GetHashCode() = stringCompare.GetHashCode x.FullName
            interface System.IComparable with 
                member x.CompareTo y =
                    match y with
                    | :? Target as y -> stringCompare.Compare(x.FullName, y.FullName)
                    | _ -> invalidArg "y" "cannot compare target to different types"

    /// Alias for System.DateTime used for file timestamps.
    type Timestamp = System.DateTime

    /// Unit of measure for milliseconds.
    [<Measure>]
    type ms

    /// A tracked dependency that determines whether a target needs rebuilding.
    type Dependency =
        | FileDep of File * Timestamp // regular file (such as source code file), triggers when file date/time is changed
        | ArtifactDep of Target // other target (triggers when target is rebuilt)
        | EnvVar of string * string option // environment variable
        | Var of string * string option // any other data such as compiler version (not used yet)
        | AlwaysRerun // trigger always
        | GetFiles of Fileset * Filelist // depends on set of files. Triggers when resulting filelist is changed

    /// Timing information for one named step within a recipe execution.
    type StepInfo =
        { Name: string; Start: System.DateTime; OwnTime: int<ms>; WaitTime: int<ms> }
        with static member Empty = {Name = ""; Start = new System.DateTime(1900,1,1); OwnTime = 0<ms>; WaitTime = 0<ms>}

    /// Recorded result of building one or more targets, including dependencies and step timings.
    type BuildResult =
        { Targets : Target list
          Built : Timestamp
          Depends : Dependency list
          Steps : StepInfo list }

    /// An async build action that threads BuildResult state through its execution.
    type Recipe<'a,'b> = Recipe of (BuildResult * 'a -> Async<BuildResult * 'b>)

    /// A build rule that maps a target pattern to a recipe.
    type 'ctx Rule =
        | FileRule of string * Recipe<'ctx,unit>
        | MultiFileRule of string list * Recipe<'ctx,unit>
        | PhonyRule of string * Recipe<'ctx,unit>
        | FileConditionRule of (string -> bool) * Recipe<'ctx,unit>
    /// A list of build rules.
    type 'ctx Rules = Rules of 'ctx Rule list

    /// Defines common exception type
    exception XakeException of string

/// <summary>
/// A message to a progress reporter.
/// </summary>
type ProgressMessage =
    | Begin of System.TimeSpan
    | Progress of System.TimeSpan * int
    | End
