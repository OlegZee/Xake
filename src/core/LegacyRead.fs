/// Reads databases 0.4-0.6 (Xake 3.x/4.x) for the upgrade to 0.7 (decision B3). Self-contained:
/// removing this module leaves `Database.fs` with one call to drop ("no longer read, recreating").
module internal Xake.LegacyRead

open Xake
open Xake.Pickler

/// A dependency as databases 0.4-0.6 recorded it.
type OldDep =
    | Plain of Dependency
    /// FileDep (file, stamp)
    | OldFile of File * Timestamp
    /// ArtifactDep (0.4, no stamp)
    | OldArtifact of Target
    /// ArtifactDep2 (target, stamp)
    | OldArtifact2 of Target * Timestamp

type OldResult = { OTargets: Target list; OBuilt: Timestamp; ODepends: OldDep list; OSteps: StepInfo list }

let private step =
    wrap ((fun (n, s, o, w) -> { StepInfo.Name = n; Start = s; OwnTime = o * 1<ms>; WaitTime = w * 1<ms> }),
          fun s -> (s.Name, s.Start, s.OwnTime / 1<ms>, s.WaitTime / 1<ms>)) (quad str date int int)

let private bytes =
    { pickle = (fun (b: byte[]) (st: OutState) -> st.Write(b.Length); st.Write(b))
      unpickle = fun (st: InState) -> st.ReadBytes(st.ReadInt32()) }

let private secretDigest =
    alt (function SecretAbsent -> 0 | SecretPresent -> 1 | SecretHmac _ -> 2)
        [| wrap0 SecretAbsent
           wrap0 SecretPresent
           wrap (SecretHmac, fun (SecretHmac (salt, h) | OtherwiseFail (salt, h)) -> salt, h) (pair bytes bytes) |]

let private fileset =
    let options = wrap ((fun (foe, bdir) -> { FailOnEmpty = foe; BaseDir = bdir; CaseSensitive = false }), fun o -> o.FailOnEmpty, o.BaseDir) (pair bool (option str))
    let element =
        alt (function Includes _ -> 0 | Excludes _ -> 1)
            [| wrap (Includes, fun (Includes p | OtherwiseFail p) -> p) Path.pickler
               wrap (Excludes, fun (Excludes p | OtherwiseFail p) -> p) Path.pickler |]
    wrap (Fileset, fun (Fileset (o, l)) -> o, l) (pair options (list element))

/// The 0.4-0.6 result pickler: paths are strings, joined with the root (an absolute one stays as
/// is); a pickled file is its absolute path (the tests' fixture writes with it).
let result (root: string) =
    let file = wrap ((fun s -> File.make (root </> s)), File.getSysFileName) str
    let target =
        alt (function FileTarget _ -> 0 | PhonyAction _ -> 1)
            [| wrap (FileTarget, fun (FileTarget f | OtherwiseFail f) -> f) file
               wrap (PhonyAction, fun (PhonyAction a | OtherwiseFail a) -> a) str |]
    let plain (pu: PU<Dependency>) = wrap (Plain, fun (Plain d | OtherwiseFail d) -> d) pu
    let nameValue case get = plain (wrap (case, get) (pair str (option str)))
    let dependency =
        alt (function
            | OldArtifact _ -> 0
            | OldFile _ -> 1
            | Plain (EnvVar _) -> 2
            | Plain (Var _) -> 3
            | Plain AlwaysRerun -> 4
            | Plain (GetFiles _) -> 5
            | OldArtifact2 _ -> 6
            | Plain (SecretDep _) -> 7
            | d -> failwithf "not a 0.6 dependency: %A" d)
            [| wrap (OldArtifact, fun (OldArtifact t | OtherwiseFail t) -> t) target
               wrap (OldFile, fun (OldFile (f, ts) | OtherwiseFail (f, ts)) -> (f, ts)) (pair file date)
               nameValue EnvVar (fun (EnvVar (n, v) | OtherwiseFail (n, v)) -> n, v)
               nameValue Var (fun (Var (n, v) | OtherwiseFail (n, v)) -> n, v)
               plain (wrap0 AlwaysRerun)
               plain (wrap (GetFiles, fun (GetFiles (fs, fl) | OtherwiseFail (fs, fl)) -> fs, fl)
                           (pair fileset (wrap (Filelist, fun (Filelist l) -> l) (list file))))
               wrap (OldArtifact2, fun (OldArtifact2 (t, ts) | OtherwiseFail (t, ts)) -> (t, ts)) (pair target date)
               plain (wrap (SecretDep, fun (SecretDep (n, s, d) | OtherwiseFail (n, s, d)) -> (n, s, d))
                           (triple str (alt (function SecretEnv -> 0 | SecretArg -> 1) [| wrap0 SecretEnv; wrap0 SecretArg |]) secretDigest)) |]
    wrap ((fun (t, b, d, s) -> { OTargets = t; OBuilt = b; ODepends = d; OSteps = s }),
          fun r -> (r.OTargets, r.OBuilt, r.ODepends, r.OSteps))
         (quad (list target) date (list dependency) (list step))

/// Converts the live results (decision 1b "Upgrade", A7): a file state per file with the stamp
/// recorded by the result with the latest `Built` (size unknown, `changed = 1`); `Epoch = 1` for a
/// result whose every stamp is that one (`DateTime.MinValue` is no stamp) and whose phony dependees
/// finished no later than recorded, else 0.
let convert (results: OldResult list) : (File * FileState) list * BuildResult list =
    let latest =
        [ for r in results do
            for d in r.ODepends do
                match d with
                | OldFile (f, t) | OldArtifact2 (FileTarget f, t) when t <> System.DateTime.MinValue -> yield f, (r.OBuilt, t)
                | _ -> () ]
        |> List.groupBy fst |> List.map (fun (f, xs) -> f, xs |> List.map snd |> List.maxBy fst |> snd) |> Map.ofList
    let phonyBuilt =
        results |> List.collect (fun r -> r.OTargets |> List.choose (function PhonyAction _ as t -> Some (t, r.OBuilt) | _ -> None)) |> Map.ofList
    let current = function
        | OldFile (_, t) | OldArtifact2 (_, t) when t = System.DateTime.MinValue -> false
        | OldFile (f, t) | OldArtifact2 (FileTarget f, t) -> abs (t - latest.[f]).TotalMilliseconds < 10.0
        | OldArtifact2 (t, stamp) -> (match phonyBuilt.TryFind t with Some b -> b.Ticks <= stamp.Ticks | None -> false)
        | OldArtifact _ -> false
        | Plain _ -> true
    let toDep = function
        | Plain d -> d
        | OldFile (f, _) -> FileDep f
        | OldArtifact t | OldArtifact2 (t, _) -> TargetDep t
    [ for KeyValue (f, t) in latest -> f, { Stamp = t; Size = -1L; Hash = None; Changed = 1L } ],
    results |> List.map (fun r ->
        { Targets = r.OTargets; Built = r.OBuilt
          Epoch = (if List.forall current r.ODepends then 1L else 0L)
          Depends = r.ODepends |> List.map toDep; Steps = r.OSteps; StaticCount = 0 })

/// Reads the results after the header to the end (a torn last one ends them), keeps the last per
/// target and converts them.
let read (root: string) (reader: System.IO.BinaryReader) =
    let pu = result root
    let stream = reader.BaseStream
    let mutable live = Map.empty
    let mutable torn = false
    while not torn && stream.Position < stream.Length do
        try
            let r = pu.unpickle reader
            live <- r.OTargets |> List.fold (fun m t -> Map.add t r m) live
        with :? System.IO.EndOfStreamException -> torn <- true
    let seen = System.Collections.Generic.HashSet<obj>(HashIdentity.Reference)
    live |> Map.toList |> List.map snd |> List.filter (box >> seen.Add) |> convert
