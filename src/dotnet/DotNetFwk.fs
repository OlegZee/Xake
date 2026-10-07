namespace Xake.Dotnet

open Xake
open Xake.ProcessExec
open Xake.Tasks
open System.IO

module internal PkgConfig =

    /// Runs pkg-config and returns its first line of output, or "" on any failure.
    let private pkgConfig args =
        // a ref cell rather than `let mutable`: the handler is a closure, and F# does not
        // allow a mutable local to be captured
        let outp = ref option<string>.None
        // stdout only -- a diagnostic on stderr must not become the value
        let takeFirst s = if Option.isNone outp.Value then outp.Value <- Some s
        try
            pexecSync takeFirst ignore "pkg-config" (args |> String.concat " ") [] None |> ignore
            outp.Value |> Option.defaultValue ""
        with _ ->
            ""

    /// Runs pkg-config and returns true when it succeeds.
    let private pkgConfigBool args =
        try
            0 = pexecSync ignore ignore "pkg-config" (args |> String.concat " ") [] None
        with _ ->
            false

    /// Gets true if specified package exists
    let exists package = pkgConfigBool ["--exists"; package]

    /// Get the version of a package
    let modVersion package = pkgConfig ["--modversion"; package]

    /// Get the value of a package variable
    let variable package var = pkgConfig ["--variable=\"" + var + "\""; package]

    /// Gets true if the package version is at least the one specified
    let isAtLeastVersion package version = pkgConfigBool ["--atleast-version=\"" + version + "\""; package]

    /// Gets true if the package version is exactly the one specified
    let isExactVersion package version = pkgConfigBool ["--exact-version=\"" + version + "\""; package]

module DotNetFwk =

    type FrameworkInfo = {
        Version: string
        InstallPath: string
        AssemblyDirs: string list
        ToolDir: string
        CscTool: string
        FscTool: string option -> string option
        MsbuildTool: string
        EnvVars: (string* string) list
        }

    let private (~%) = System.Environment.GetEnvironmentVariable

    module internal registry =

        open Microsoft.Win32
        let HKLM () = Registry.LocalMachine

        let open_subkey (hive:RegistryKey) key  = match hive.OpenSubKey(key) with | null -> None | k -> Some k
        let get_value key (hive:RegistryKey)    = match hive.GetValue(key) with |null -> None | v -> Some v
        let get_value_str h k                   = get_value h k |> Option.bind (string >> Some)


    // a set of functions and structures to detect Mono framework location
    module internal monoFwkImpl =

        open registry

        let tryLocateFwk fwk : option<FrameworkInfo> * string =

            let (sdkroot,libdir,err) =
                if PkgConfig.exists "mono" then
                    let prefix = PkgConfig.variable "mono" "prefix" in

                    let winpath (str:string) = str.Replace('/', System.IO.Path.DirectorySeparatorChar)
                    (
                        prefix |> winpath,
                        PkgConfig.variable "mono" "libdir" |> winpath,
                        null
                    )
                else if Env.isWindows then
                    let MonoProbeKeys = [@"SOFTWARE\Wow6432Node\Novell\Mono"; @"SOFTWARE\Novell\Mono"]
                    let Mono48ProbeKeys = [@"SOFTWARE\Wow6432Node\Mono"; @"SOFTWARE\Mono"]

                    MonoProbeKeys |> List.tryPick (open_subkey <| HKLM ())
                    |>
                    function
                    | Some key ->
                        let monover = key |> registry.get_value_str "DefaultCLR"
                        monover |> Option.bind (open_subkey key)
                    | _ ->
                        Mono48ProbeKeys |> List.tryPick (open_subkey <| HKLM ())
                    |>
                    function
                    | Some monokey ->
                        let gets key = monokey |> registry.get_value_str key |> Option.get in
                        (
                            gets "SdkInstallRoot",
                            gets "FrameworkAssemblyDirectory",
                            null
                        )
                    | _ ->
                        ("", "", "Failed to locate default mono version")
                else
                    ("", "", "Failed to obtain mono framework (check if mono and pkg-config are installed)")
            match err with
            | null ->
                let cscTool = if PkgConfig.isAtLeastVersion "mono" "3.0" then "mcs" else "dmcs"

                let fwkinfo libpath ver =
                    let libPath = libdir </> "mono" </> libpath
                    let r = Some {
                        InstallPath = sdkroot
                        AssemblyDirs = [libPath]
                        ToolDir = libPath
                        Version = ver
                        CscTool = cscTool
                        FscTool = fun _ -> Some "fsharpc"
                        MsbuildTool = "xbuild"
                        // `+` binds tighter than `</>`, so the parentheses matter here; and the
                        // separator is platform-dependent (';' on Windows, ':' elsewhere)
                        EnvVars = ["PATH", (sdkroot </> "bin") + string Path.PathSeparator + (%"PATH")]
                    }

                    r
                // ^^^^^^^ TODO proper tool (xbuild) lookup, this lib/mono/xxx contains only specific tools

                match fwk with
                | "mono-20" | "mono-2.0" | "2.0" -> fwkinfo "2.0" "2.0.50727", null
                | "mono-35" | "mono-3.5" | "3.5" -> fwkinfo "3.5" "2.0.50727", null
                | "mono-40" | "mono-4.0" | "4.0" -> fwkinfo "4.0" "4.0.30319", null
                | "mono-45" | "mono-4.5" | "4.5" -> fwkinfo "4.5" "4.5.50709", null
                | _ ->
                    None, sprintf "Unknown or unsupported profile '%s'" fwk
            | _ ->
                None, err

    module internal msImpl =
        open registry

        let ifNone f arg = function
            | None -> f arg
            | x -> x
        let tryUntil (f: 't -> 'r option) (data: 't seq) : 'r option =
            data |> Seq.fold (fun state value -> state |> ifNone f value) None

        let tryLocateFwk fwk =

            // TODO drop Wow node lookup, lookup depending on fwk
            let fscTool ver =
                match ver with
                    | None -> ["4.1"; "4.0"; "3.1"; "3.0"]
                    | Some v -> [v]
                |> tryUntil (
                    sprintf @"SOFTWARE\Wow6432Node\Microsoft\FSharp\%s\Runtime\v4.0" >> registry.open_subkey (registry.HKLM())
                )
                |> Option.bind (registry.get_value_str "")
                |> Option.map (fun p -> p </> "fsc.exe")
 
            let fwkKey = open_subkey (HKLM()) @"SOFTWARE\Microsoft\.NETFramework"
            let installRoot_ = fwkKey |> Option.bind (get_value_str "InstallRoot")
            let installRoot = installRoot_ |> Option.get    // TODO gracefully fail

            let (version,fwkdir,asmpaths,vars,err) =
                match fwk with
                | "net-20" | "net-2.0" | "2.0" ->
                    ("2.0.50727", "v2.0.50727",
                        [
                            installRoot </> "v2.0.50727"
                        ], [], null)
                | "net-35" | "net-3.5" | "3.5" ->
                    ("3.5", "v3.5",
                        [
                            installRoot </> "v2.0.50727"
                            %"ProgramFiles" </> @"Reference Assemblies\Microsoft\Framework\v3.0"
                            %"ProgramFiles" </> @"Reference Assemblies\Microsoft\Framework\v3.5"
                        ],
                        [("COMPLUS_VERSION", "v2.0.50727")], null)
                | "net-40" | "net-4.0" | "4.0" | "4.0-full"
                | "net-45" | "net-4.5" | "4.5"| "4.5-full" ->
                    ("4.0", "v4.0.30319",
                        [
                            installRoot </> "v4.0.30319"
                            installRoot </> "v4.0.30319" </> "WPF"
                            %"ProgramFiles" </> @"\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.0"
                        ], [("COMPLUS_VERSION", "v4.0.30319")],null)
                | _ ->
                    ("", "", [], [], "framework is not available on this PC")

            match err with
            | null ->
                let fwkdir = installRoot </> fwkdir in
                Some {
                    InstallPath = fwkdir; ToolDir = fwkdir
                    Version = version
                    AssemblyDirs = asmpaths
                    CscTool = fwkdir </> "csc.exe"
                    FscTool = fscTool
                    MsbuildTool = fwkdir </> "msbuild.exe"
                    EnvVars = vars
                }, null
            | _ ->
                None, err

    /// The synthesized restore project. `PackageDownload` rather than `PackageReference`: it
    /// fetches exactly the listed version into the folder and nothing else -- no dependency
    /// walk and, crucially, no framework compatibility check, so a package that targets only
    /// `net472` downloads from this `netstandard2.0` project just as well.
    /// `DisableImplicitFrameworkReferences` keeps the SDK from adding `NETStandard.Library` to
    /// the folder as a side effect of the TFM.
    let restoreProjectText (packages: (string * string) list) =
        [ yield "<Project Sdk=\"Microsoft.NET.Sdk\">"
          yield "  <PropertyGroup>"
          yield "    <TargetFramework>netstandard2.0</TargetFramework>"
          yield "    <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>"
          yield "  </PropertyGroup>"
          yield "  <ItemGroup>"
          for (id, version) in packages do
              yield sprintf "    <PackageDownload Include=\"%s\" Version=\"[%s]\" />" id version
          yield "  </ItemGroup>"
          yield "</Project>"
          yield "" ]
        |> String.concat "\n"

    /// The `dotnet restore` arguments for the synthesized project: quiet, no audit, and the
    /// repository's own msbuild customizations switched off (see `downloadPackages`).
    let internal restoreArgs (project: string) =
        [ "restore"; project
          "-v:quiet"; "-nologo"
          "-p:NuGetAudit=false"
          "-p:ImportDirectoryBuildProps=false"
          "-p:ImportDirectoryBuildTargets=false"
          "-p:ImportDirectoryPackagesProps=false"
          "-p:ManagePackageVersionsCentrally=false" ]

    let private downloadAttempts = ref 0

    /// A fresh `obj/xake/restore/<n>/` under the project root, for one synthesized project.
    let internal nextRestoreDir (projectRoot: string) =
        let attempt = System.Threading.Interlocked.Increment downloadAttempts
        projectRoot </> "obj" </> "xake" </> "restore" </> string attempt

    let internal restoreFailure (root: string) (exitCode: int) (project: string) (packages: (string * string) list) =
        sprintf "restoring %d package(s) into '%s' failed with exit code %d (see '%s'): %s"
            (List.length packages) root exitCode project
            (packages |> List.map (fun (id, v) -> id + " " + v) |> String.concat ", ")

    /// The versions of the NuGet packages that carry reference assemblies for the SDK
    /// provider: `NETStandard.Library` (netstandard2.0) and
    /// `Microsoft.NETFramework.ReferenceAssemblies.<moniker>` (.NET Framework). Exact versions,
    /// never "the newest in the cache": the reference assemblies are compiler inputs.
    type ReferencePackVersions = {
        /// `NETStandard.Library`, script variable `NETSTANDARD_LIBRARY_VERSION`
        NetStandardLibrary: string
        /// `Microsoft.NETFramework.ReferenceAssemblies.*`, script variable
        /// `NETFX_REFERENCE_ASSEMBLIES_VERSION`
        ReferenceAssemblies: string
    }

    /// Locates the compilers shipped with the .NET SDK and the .NET Framework reference
    /// assemblies distributed as a NuGet package. This is what makes building binaries for
    /// full framework possible on any OS: neither a Framework installation nor the registry
    /// is involved, only the SDK and a restorable package.
    module internal sdkImpl =

        let referenceAssembliesVersion = "1.0.3"

        /// The package carrying the netstandard2.0 reference assemblies. 2.1 ships with the
        /// SDK instead, see netstandardRefDir.
        let netstandardLibraryVersion = "2.0.3"

        let private knownMonikers =
            [ "net20"; "net35"; "net40"; "net45"; "net451"; "net452"; "net46"
              "net461"; "net462"; "net47"; "net471"; "net472"; "net48" ]

        /// "net-4.6.2" | "4.6.2" | "sdk-net462" | "4.5-full" -> Some "net462"
        let moniker (fwk: string) =
            let digits =
                fwk.ToLowerInvariant().Replace("-full", "").Replace("sdk-", "").Replace("net-", "")
                   .Replace("net", "").Replace(".", "").Replace("-", "")
            let m = "net" + digits
            if knownMonikers |> List.contains m then Some m else None

        /// "netstandard2.0" | "sdk-netstandard2.0" -> Some "netstandard2.0". Kept apart from
        /// `moniker`: netstandard is a profile, not a Framework version, and it is resolved
        /// against a different set of reference assemblies.
        let netstandardMoniker (fwk: string) =
            let m = fwk.ToLowerInvariant().Replace("sdk-", "")
            if ["netstandard2.0"; "netstandard2.1"] |> List.contains m then Some m else None

        /// "net10.0" | "sdk-net10.0" | "NET8.0" -> Some "net10.0" / "net8.0": the .NET (Core)
        /// target frameworks, `net5.0` and later, whose reference assemblies are the SDK's
        /// `Microsoft.NETCore.App.Ref` targeting pack. Kept apart from `moniker`, which knows
        /// only the .NET Framework ones (`net472`, no dot).
        let netcoreMoniker (fwk: string) =
            let m = fwk.ToLowerInvariant().Replace("sdk-", "")
            let rx = System.Text.RegularExpressions.Regex.Match(m, @"^net(\d+)\.(\d+)$")
            if rx.Success && int rx.Groups.[1].Value >= 5 then Some m else None

        /// Numeric-aware pick of the latest subdirectory: "10.0.400" beats "8.0.424",
        /// and a released version beats a preview one.
        let private latestDir path =
            let versionKey (dir: string) =
                (Path.GetFileName dir).Split([| '.'; '-' |])
                |> Array.map (fun part -> match System.Int32.TryParse part with | true, v -> v | _ -> -1)
                |> List.ofArray
            if not <| Directory.Exists path then None
            else
                let dirs = Directory.GetDirectories path
                let released = dirs |> Array.filter (fun d -> not <| (Path.GetFileName d).Contains "-")
                let candidates = if Array.isEmpty released then dirs else released
                candidates |> Array.sortBy versionKey |> Array.tryLast

        let internal nugetRoot () =
            match %"NUGET_PACKAGES" with
            | null | "" ->
                System.Environment.GetFolderPath System.Environment.SpecialFolder.UserProfile
                    </> ".nuget" </> "packages"
            | dir -> dir

        let defaultVersions =
            { NetStandardLibrary = netstandardLibraryVersion; ReferenceAssemblies = referenceAssembliesVersion }

        /// The package (id, exact version) the SDK provider reads `fwk`'s reference assemblies
        /// from; `None` when they come with the SDK (netstandard2.1) or `fwk` is not a known
        /// profile.
        let referencePackage (versions: ReferencePackVersions) (fwk: string) =
            match netstandardMoniker fwk with
            | Some "netstandard2.0" -> Some ("NETStandard.Library", versions.NetStandardLibrary)
            | Some _ -> None
            | None ->
                moniker fwk
                |> Option.map (fun m -> "Microsoft.NETFramework.ReferenceAssemblies." + m, versions.ReferenceAssemblies)

        /// `<packageRoot>/<id lowercase>/<version>`. Every function below that reads or restores
        /// a package takes the package folder as its first argument: `nugetRoot ()` for a caller
        /// with no build, the build's own folder (`DotNetFwk.packageRoot`) from a recipe.
        let internal packageDir (packageRoot: string) (packageId: string) (version: string) =
            packageRoot </> packageId.ToLowerInvariant() </> version

        /// The synchronous twin of `downloadPackages`, for a caller with no build context
        /// (`locateFramework`): the same synthesized project under `projectRoot`, the same
        /// arguments, and a failure that names the package and version. A build goes through
        /// `resolveFramework`, which downloads with the recipe before this is reached.
        let internal restoreSync (packageRoot: string) (projectRoot: string) (packageId: string) (version: string) =
            let root = packageRoot.Replace('\\', '/').TrimEnd '/'
            let dir = nextRestoreDir projectRoot
            Directory.CreateDirectory dir |> ignore
            let project = dir </> "restore.csproj"
            File.WriteAllText (project, restoreProjectText [ packageId, version ])
            let args = restoreArgs project |> List.map (fun a -> "\"" + a + "\"") |> String.concat " "
            let exitCode =
                try pexecSync ignore ignore "dotnet" args [ "NUGET_PACKAGES", root ] (Some dir)
                with _ -> -1
            if exitCode <> 0 then
                failwith (restoreFailure root exitCode project [ packageId, version ])
            try Directory.Delete (dir, true) with _ -> ()

        /// `locate ()`, restoring `packageId version` first when it finds nothing.
        let private locateOrRestore packageRoot projectRoot packageId version (locate: unit -> string option) =
            match locate () with
            | Some dir -> dir
            | None ->
                restoreSync packageRoot projectRoot packageId version
                match locate () with
                | Some dir -> dir
                | None ->
                    failwithf "package %s %s was restored into '%s' but has no reference assemblies where expected"
                        packageId version (packageDir packageRoot packageId version)

        let private refAssembliesDir packageRoot projectRoot versions moniker =
            let packageId = "Microsoft.NETFramework.ReferenceAssemblies." + moniker
            let version = versions.ReferenceAssemblies
            locateOrRestore packageRoot projectRoot packageId version (fun () ->
                // one `v4.x` directory under `build/.NETFramework` in the package
                latestDir (packageDir packageRoot packageId version </> "build" </> ".NETFramework"))

        let private dotnetHost () =
            match %"DOTNET_HOST_PATH" with
            | null | "" -> try System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName with _ -> null
            | path -> path

        let internal dotnetRoot () =
            let hostDir =
                match dotnetHost () with
                | null | "" -> None
                | path when Path.GetFileNameWithoutExtension path = "dotnet" -> Some (Path.GetDirectoryName path)
                | _ -> None
            [ hostDir
              (match %"DOTNET_ROOT" with | null | "" -> None | dir -> Some dir)
              Some "/usr/local/share/dotnet"
              Some "/usr/share/dotnet"
              (match %"ProgramFiles" with | null | "" -> None | dir -> Some (dir </> "dotnet")) ]
            |> List.tryPick (Option.filter (fun root -> Directory.Exists (root </> "sdk")))

        /// The SDK directory the `dotnet` host picks when run in `projectRoot`, plus a warning
        /// when that could not be honoured. The host resolves `global.json` (searched upwards
        /// from its working directory, `rollForward` included), so this runs `dotnet --version`
        /// there and takes `<dotnetRoot>/sdk/<printed version>`. With no `global.json` the host
        /// picks the newest SDK, which is what `latestDir` does, so nothing changes then. When
        /// the host fails (the pinned SDK is not installed) or prints a version with no
        /// directory under this `dotnetRoot`, the newest SDK is taken and the warning names the
        /// version asked for.
        let private probeSdk (projectRoot: string) : string option * string option =
            match dotnetRoot () with
            | None -> None, None
            | Some dotnetDir ->
                let sdkRoot = dotnetDir </> "sdk"
                let newest = latestDir sdkRoot
                let hostExe = dotnetDir </> (if Env.isWindows then "dotnet.exe" else "dotnet")
                let host = if File.Exists hostExe then hostExe else "dotnet"
                let stdout = ResizeArray<string>()
                let allOutput = ResizeArray<string>()
                let onOut (line: string) = lock allOutput (fun () -> stdout.Add line; allOutput.Add line)
                let onErr (line: string) = lock allOutput (fun () -> allOutput.Add line)
                let workDir = if Directory.Exists projectRoot then Some projectRoot else None
                let exitCode =
                    try pexecSync onOut onErr host "--version" [] workDir
                    with _ -> -1
                let newestName = newest |> Option.map Path.GetFileName |> Option.defaultValue "none"
                let printed =
                    if exitCode <> 0 then None
                    else stdout |> Seq.map (fun l -> l.Trim()) |> Seq.tryFind ((<>) "")
                match printed with
                | Some version when Directory.Exists (sdkRoot </> version) ->
                    Some (sdkRoot </> version), None
                | Some version ->
                    newest,
                    Some (sprintf "'dotnet --version' in '%s' selects SDK %s, which is not under %s; using the newest SDK there (%s)"
                            projectRoot version sdkRoot newestName)
                | None ->
                    let field (prefix: string) =
                        allOutput |> Seq.map (fun l -> l.Trim())
                        |> Seq.tryFind (fun l -> l.StartsWith prefix)
                        |> Option.map (fun l -> l.Substring(prefix.Length).Trim())
                    let requested =
                        match field "Requested SDK version:", field "global.json file:" with
                        | Some v, Some file -> sprintf "global.json (%s) asks for SDK %s, which is not installed" file v
                        | Some v, None -> sprintf "global.json asks for SDK %s, which is not installed" v
                        | None, _ -> sprintf "'dotnet --version' failed (exit code %d)" exitCode
                    newest,
                    Some (sprintf "%s in '%s'; using the newest SDK (%s)" requested projectRoot newestName)

        /// `probeSdk`, once per project root for the life of the process (a `global.json` edited
        /// mid-run is not seen again). A cache of its own rather than `CommonLib.memoize` so
        /// `probedWarning` can ask about a root without running the probe.
        let private sdkProbes =
            System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<string option * string option>>()

        let private sdkProbe (projectRoot: string) =
            sdkProbes.GetOrAdd(projectRoot, fun root -> lazy (probeSdk root)).Value

        /// The probe's warning for `projectRoot`, if the SDK was probed there at all.
        let internal probedWarning (projectRoot: string) =
            match sdkProbes.TryGetValue projectRoot with
            | true, probe when probe.IsValueCreated -> snd probe.Value
            | _ -> None

        let private sdkDir projectRoot = sdkProbe projectRoot |> fst

        /// The .NET target framework the SDK in `sdkDir` is for and the version of the
        /// targeting pack it bundles, from its `Microsoft.NETCoreSdk.BundledVersions.props`
        /// (`BundledNETCoreAppTargetFrameworkVersion`, `BundledNETCoreAppPackageVersion`):
        /// ("net10.0", "10.0.12") for SDK 10.0.401.
        let internal bundledFramework (sdkDir: string) =
            let props = sdkDir </> "Microsoft.NETCoreSdk.BundledVersions.props"
            if not (File.Exists props) then None else
            let text = File.ReadAllText props
            let element name =
                let m = System.Text.RegularExpressions.Regex.Match(text, sprintf "<%s>\\s*([^<\\s]+)\\s*</%s>" name name)
                if m.Success then Some m.Groups.[1].Value else None
            match element "BundledNETCoreAppTargetFrameworkVersion", element "BundledNETCoreAppPackageVersion" with
            | Some tfv, Some packVersion -> Some ("net" + tfv, packVersion)
            | _ -> None

        /// The target framework of the SDK the `dotnet` host selects in `projectRoot`
        /// ("net10.0"), when it can be determined.
        let sdkFramework (projectRoot: string) =
            sdkDir projectRoot |> Option.bind bundledFramework |> Option.map fst

        /// The reference assemblies of a .NET target framework (`net8.0`, `net10.0`), from the
        /// targeting pack that ships with the SDK -- `<dotnet>/packs/Microsoft.NETCore.App.Ref/
        /// <version>/ref/<moniker>`, no download. For the SDK's own framework that is exactly
        /// the version the SDK bundles; for another one the newest `<major>.<minor>.*` pack
        /// present (one an installed SDK of that major brought along). A missing pack fails,
        /// naming the folder looked in.
        let private netcoreRefDir projectRoot moniker =
            match sdkDir projectRoot with
            | None -> failwithf "reference assemblies for '%s': the .NET SDK is not found" moniker
            | Some sdk ->
                let packRoot = Path.GetDirectoryName (Path.GetDirectoryName sdk) </> "packs" </> "Microsoft.NETCore.App.Ref"
                let refDir version = packRoot </> version </> "ref" </> moniker
                match bundledFramework sdk with
                | Some (tfm, packVersion) when tfm = moniker ->
                    let dir = refDir packVersion
                    if Directory.Exists dir then dir
                    else failwithf "reference assemblies for '%s' are not available: the SDK %s bundles targeting pack %s, but '%s' does not exist"
                            moniker (Path.GetFileName sdk) packVersion dir
                | _ ->
                    let prefix = moniker.Substring 3 + "."
                    let candidates =
                        if Directory.Exists packRoot then
                            Directory.GetDirectories packRoot
                            |> Array.filter (fun d -> (Path.GetFileName d).StartsWith prefix && Directory.Exists (d </> "ref" </> moniker))
                        else [||]
                    let versionKey (dir: string) =
                        (Path.GetFileName dir).Split([| '.'; '-' |])
                        |> Array.map (fun part -> match System.Int32.TryParse part with | true, v -> v | _ -> -1)
                        |> List.ofArray
                    match candidates |> Array.sortBy versionKey |> Array.tryLast with
                    | Some dir -> dir </> "ref" </> moniker
                    | None ->
                        failwithf "reference assemblies for '%s' are not available: no targeting pack under '%s' has ref/%s (install a %s SDK, or target the SDK's own framework)"
                            moniker packRoot moniker (moniker.Substring 3)

        /// netstandard reference assemblies: 2.1 ships with the SDK as a pack, 2.0 only
        /// exists in the NETStandard.Library package, taken at the exact version asked for.
        let private netstandardRefDir packageRoot projectRoot versions moniker =
            match moniker with
            | "netstandard2.0" ->
                let version = versions.NetStandardLibrary
                locateOrRestore packageRoot projectRoot "NETStandard.Library" version (fun () ->
                    let dir = packageDir packageRoot "NETStandard.Library" version </> "build" </> moniker </> "ref"
                    if Directory.Exists dir then Some dir else None)
            | _ ->
                dotnetRoot ()
                |> Option.bind (fun root -> latestDir (root </> "packs" </> "NETStandard.Library.Ref"))
                |> Option.map (fun pack -> pack </> "ref" </> moniker)
                |> Option.filter Directory.Exists
                |> Option.defaultWith (fun () ->
                    failwithf "reference assemblies for '%s' are not available: the SDK has no packs/NETStandard.Library.Ref/*/ref/%s" moniker moniker)

        /// fsc and msbuild ship as managed dlls, so they are launched through a tiny script.
        let private launcher name (args: string) =
            let host = dotnetHost ()
            let ext, text =
                if Env.isWindows
                then ".cmd", sprintf "@\"%s\" %s %%*" host args
                else "", sprintf "#!/bin/sh\nexec \"%s\" %s \"$@\"\n" host args
            let path = Path.GetTempPath() </> (sprintf "xake-%s-%x%s" name (hash (host + args) &&& 0xffffff) ext)
            File.WriteAllText (path, text)
            if not Env.isWindows then
                pexecSync ignore ignore "chmod" (sprintf "+x \"%s\"" path) [] None |> ignore
            path

        /// The compilers always come from the SDK; only the reference assemblies differ
        /// between the profiles.
        let private sdkFwkInfo projectRoot refDirs version =
            match sdkDir projectRoot with
            | None -> None, "the .NET SDK is not found, cannot locate the compilers"
            | Some sdk ->
                let exe name = if Env.isWindows then name + ".exe" else name
                Some {
                    Version = version
                    InstallPath = sdk
                    ToolDir = sdk </> "Roslyn" </> "bincore"
                    AssemblyDirs = refDirs
                    CscTool = sdk </> "Roslyn" </> "bincore" </> exe "csc"
                    FscTool = fun _ -> Some (launcher "fsc" (sprintf "\"%s\"" (sdk </> "FSharp" </> "fsc.dll")))
                    MsbuildTool = launcher "msbuild" "msbuild"
                    EnvVars = []
                }, null

        let tryLocateFwk packageRoot projectRoot versions fwk =
            match netcoreMoniker fwk with
            | Some moniker ->
                sdkFwkInfo projectRoot [netcoreRefDir projectRoot moniker] moniker
            | None ->

            match netstandardMoniker fwk with
            | Some moniker ->
                sdkFwkInfo projectRoot [netstandardRefDir packageRoot projectRoot versions moniker] moniker
            | None ->

            match moniker fwk with
            | None -> None, sprintf "'%s' is not a known .NET Framework profile" fwk
            | Some moniker ->
                let refDir = refAssembliesDir packageRoot projectRoot versions moniker
                sdkFwkInfo projectRoot [refDir; refDir </> "Facades"] moniker

    /// The machine's NuGet package cache: the environment variable `NUGET_PACKAGES`, else
    /// `~/.nuget/packages`. This is the package folder of a caller with no build; a recipe asks
    /// `packageRoot ()`, which honours the script variable `NUGET_PACKAGES` first.
    let nugetRoot () = sdkImpl.nugetRoot ()

    /// <summary>
    /// The package folder of the build: the script variable `NUGET_PACKAGES` (relative to the
    /// project root, or absolute; read, and so a dependency, here), else `nugetRoot ()` (the
    /// environment variable, else `~/.nuget/packages`). Everything Xake restores or looks up in
    /// the package folder from a recipe goes here: `restorePackage None`, `downloadPackages None`,
    /// the reference packs `resolveFramework` reads, the `toolset` compiler, fsc's default
    /// `FSharp.Core`.
    /// </summary>
    let packageRoot () : Recipe<ExecContext, string> =
        recipe {
            let! options = getCtxOptions ()
            let! var = getVar "NUGET_PACKAGES"
            match var |> Option.map (fun v -> v.Trim()) |> Option.filter ((<>) "") with
            | Some dir -> return Path.GetFullPath (Path.Combine (Path.GetFullPath options.ProjectRoot, dir))
            | None -> return nugetRoot ()
        }

    /// The .NET SDK installation root, when one can be located: compilers, analyzers and
    /// reference packs live under it.
    let dotnetRoot () = sdkImpl.dotnetRoot ()

    /// <summary>
    /// Whether Xake may download packages into the package folder: the script variable `NUGET_FETCH`
    /// (read, and so a dependency, here) -- `off`, `false`, `no` or `0` turn fetching off,
    /// anything else, or unset, leaves it on. `restorePackage` and `downloadPackages` honour it:
    /// with fetching off a package missing from the folder fails the build, naming it. This is
    /// what `Restore.Options.Enabled` of Xake.Hermetic.Dotnet is to default to.
    /// </summary>
    let fetchEnabled () : Recipe<ExecContext, bool> =
        recipe {
            let! var = getVar "NUGET_FETCH"
            match var |> Option.map (fun v -> v.Trim().ToLowerInvariant()) with
            | Some ("off" | "false" | "no" | "0") -> return false
            | _ -> return true
        }

    /// The failure of a package that is missing while fetching is off.
    let internal fetchOffFailure (root: string) (packages: (string * string) list) =
        sprintf "package(s) %s not found in '%s' and fetching packages is off (NUGET_FETCH=off): put them in the folder, or turn fetching on"
            (packages |> List.map (fun (id, v) -> id + " " + v) |> String.concat ", ") root

    /// `packageRoot ()`, under a name the `packageRoot` parameters below do not shadow.
    let private buildPackageRoot () = packageRoot ()

    /// The package folder in effect, normalized to forward slashes without a trailing one:
    /// `None` is the machine's own cache (`nugetRoot ()`, the environment only: this function
    /// has no build to read the script variable from -- a recipe passes `packageRoot ()`),
    /// `Some dir` a folder of the build's own.
    let normalizedPackageRoot (packageRoot: string option) =
        (packageRoot |> Option.defaultWith nugetRoot).Replace('\\', '/').TrimEnd '/'

    /// Fetches the named `(id, version)` packages into the package folder with **one**
    /// `dotnet restore` of a synthesized project.
    ///
    /// The project lives under the build's own project root (`obj/xake/restore/<n>/`), not in a
    /// temp directory, so that NuGet's settings discovery finds the repository's `nuget.config`
    /// and its private feeds. The repository's own msbuild customizations are switched off on
    /// the command line instead (`Directory.Build.props`/`.targets`, central package
    /// management), because they are written for real projects and this one only downloads.
    ///
    /// Each call gets a numbered subdirectory of its own, removed once the restore succeeds, so
    /// concurrent calls do not overwrite each other's project file. Concurrent restores into
    /// one package folder are NuGet's own business, and it handles them. A failed attempt is
    /// left on disk, named by the message, so it can be re-run by hand.
    ///
    /// `packageRoot` `None` is the build's package folder (`packageRoot ()`: the script variable
    /// `NUGET_PACKAGES`, else the environment's), `Some dir` (absolute) a folder of the caller's
    /// choosing. The child `dotnet restore` gets it as the environment variable `NUGET_PACKAGES`.
    /// With fetching off (`fetchEnabled`, the script variable `NUGET_FETCH`) nothing is
    /// started: packages already in the folder pass, a missing one fails naming it.
    let downloadPackages (packageRoot: string option) (packages: (string * string) list) : Recipe<ExecContext, unit> =
        recipe {
            if not (List.isEmpty packages) then
                let! buildRoot =
                    match packageRoot with
                    | Some dir -> recipe { return dir }
                    | None -> buildPackageRoot ()
                let root = normalizedPackageRoot (Some buildRoot)
                let! enabled = fetchEnabled ()
                if not enabled then
                    let missing =
                        packages |> List.filter (fun (id, v) -> not (Directory.Exists (root </> id.ToLowerInvariant() </> v)))
                    if not (List.isEmpty missing) then
                        failwith (fetchOffFailure root missing)
                else
                    let! ctxOptions = getCtxOptions ()
                    let dir = nextRestoreDir ctxOptions.ProjectRoot
                    Directory.CreateDirectory dir |> ignore
                    let project = dir </> "restore.csproj"
                    File.WriteAllText (project, restoreProjectText packages)

                    let! exitCode =
                        shell {
                            cmd "dotnet"
                            args (restoreArgs project)
                            env ("NUGET_PACKAGES", root)
                            workdir dir
                            logprefix "[restore]"
                            stdoutlevel (Tool.diagnosticLevel Level.Verbose)
                            erroutlevel (Tool.diagnosticLevel Level.Verbose)
                        }

                    if exitCode <> 0 then
                        failwith (restoreFailure root exitCode project packages)
                    try Directory.Delete (dir, true) with _ -> ()
        }

    /// The folder of one NuGet package (`<packageRoot>/<id lowercase>/<version>`), fetching it
    /// into the package folder first when it is not there yet. `packageRoot` is `None` for the
    /// build's package folder (`packageRoot ()`: the script variable `NUGET_PACKAGES`, else the
    /// environment's), `Some dir` (absolute) for a folder of the caller's choosing. Restoring
    /// cannot change *what* a version is, so this is safe to call from any recipe; it starts no
    /// process when the folder already exists, and fails naming the package when it does not
    /// and fetching is off (`fetchEnabled`).
    let restorePackage (packageRoot: string option) (packageId: string) (version: string) : Recipe<ExecContext, string> =
        recipe {
            let! root =
                match packageRoot with
                | Some dir -> recipe { return dir }
                | None -> buildPackageRoot ()
            let dir = normalizedPackageRoot (Some root) </> packageId.ToLowerInvariant() </> version
            if not (Directory.Exists dir) then
                // `downloadPackages` reads `NUGET_FETCH`, only when something is missing
                do! downloadPackages (Some root) [ packageId, version ]
            return dir
        }

    module internal impl =

        let locateFramework (packageRoot: string) (projectRoot: string) (versions: ReferencePackVersions) (fwk) : FrameworkInfo =
            let startsWith fragment (s: string option) =
                match s with
                | None | Some null -> false
                | Some str -> str.StartsWith fragment

            // msImpl throws when the framework is not installed, so any kind of failure
            // has to fall through to the next provider
            let orElse fallback primary name =
                let attempt locate = try locate name with e -> None, e.Message
                match attempt primary with
                | Some _, _ as found -> found
                | None, err ->
                    match attempt fallback with
                    | Some _, _ as found -> found
                    | None, err2 -> None, err + "; " + err2

            let tryLocate =
                if fwk |> startsWith "mono-" then monoFwkImpl.tryLocateFwk
                elif fwk |> startsWith "sdk-" || (fwk |> Option.bind sdkImpl.netcoreMoniker |> Option.isSome) then
                    // .NET (net5.0 and later) has no provider but the SDK's targeting pack
                    sdkImpl.tryLocateFwk packageRoot projectRoot versions
                elif Env.isUnix then
                    // SDK compilers over reference assemblies from NuGet build for full
                    // framework on any OS; mono is just a fallback these days
                    sdkImpl.tryLocateFwk packageRoot projectRoot versions |> orElse monoFwkImpl.tryLocateFwk
                elif Env.isRunningOnMono then
                    monoFwkImpl.tryLocateFwk |> orElse (sdkImpl.tryLocateFwk packageRoot projectRoot versions)
                else
                    // a real Framework installation found through the registry wins on Windows
                    msImpl.tryLocateFwk |> orElse (sdkImpl.tryLocateFwk packageRoot projectRoot versions)

            match fwk with
            | None ->
                match ["2.0"; "3.0"; "3.5"; "4.0"] |> List.rev |> List.tryPick (tryLocate >> fst) with
                | (Some i) -> i
                | _ -> failwith "No framework found"
            | Some name ->
                match name |> tryLocate with
                | None, err -> failwith err
                | Some f,_ -> f

    let private locateFrameworkMemo =
        CommonLib.memoize (fun (packageRoot: string, projectRoot: string, versions: ReferencePackVersions, fwk: string option) ->
            impl.locateFramework packageRoot projectRoot versions fwk)

    /// <summary>
    /// Attempts to locate either .NET or Mono framework, for a build rooted at `projectRoot`:
    /// the SDK provider takes the SDK the `dotnet` host selects there, so a `global.json` in
    /// (or above) the project root is honoured. Memoized per (package folder, root, framework).
    ///
    /// For callers with no build: reference packages are read from (and restored into) the
    /// machine's cache, `nugetRoot ()` -- the environment variable `NUGET_PACKAGES`, never the
    /// script variable. A recipe calls `resolveFramework`, which honours the script variable.
    /// </summary>
    let locateFrameworkIn (projectRoot: string) (fwk: string option) : FrameworkInfo =
        locateFrameworkMemo (nugetRoot (), Path.GetFullPath projectRoot, sdkImpl.defaultVersions, fwk)

    /// The default reference-pack versions: `NETStandard.Library` 2.0.3 and
    /// `Microsoft.NETFramework.ReferenceAssemblies.*` 1.0.3.
    let defaultReferencePackVersions = sdkImpl.defaultVersions

    /// `locateFrameworkIn` with the reference-pack versions given (the machine's package cache,
    /// as there). Memoized per (package folder, root, versions, framework).
    let locateFrameworkWith (projectRoot: string) (versions: ReferencePackVersions) (fwk: string option) : FrameworkInfo =
        locateFrameworkMemo (nugetRoot (), Path.GetFullPath projectRoot, versions, fwk)

    /// The reference package (id, exact version) the SDK provider reads `fwk`'s reference
    /// assemblies from: `NETStandard.Library` for netstandard2.0,
    /// `Microsoft.NETFramework.ReferenceAssemblies.<moniker>` for .NET Framework; `None` for
    /// netstandard2.1 (an SDK pack) and anything unknown.
    let referencePackage (versions: ReferencePackVersions) (fwk: string) : (string * string) option =
        sdkImpl.referencePackage versions fwk

    /// The reference-pack versions in effect for the build: the defaults, overridden by the
    /// script variables `NETSTANDARD_LIBRARY_VERSION` and `NETFX_REFERENCE_ASSEMBLIES_VERSION`
    /// (each read, and so a dependency, of every compile that resolves a framework).
    let referencePackVersions () : Recipe<ExecContext, ReferencePackVersions> =
        recipe {
            let pick (v: string option) dflt =
                v |> Option.map (fun s -> s.Trim()) |> Option.filter ((<>) "") |> Option.defaultValue dflt
            let! ns = getVar "NETSTANDARD_LIBRARY_VERSION"
            let! refasm = getVar "NETFX_REFERENCE_ASSEMBLIES_VERSION"
            return { NetStandardLibrary = pick ns sdkImpl.defaultVersions.NetStandardLibrary
                     ReferenceAssemblies = pick refasm sdkImpl.defaultVersions.ReferenceAssemblies }
        }

    /// <summary>
    /// `locateFrameworkWith` for a build: the reference-pack versions from
    /// `referencePackVersions`, and the reference package fetched first through
    /// `restorePackage` (the synthesized project under the project root, so the repository's
    /// `nuget.config` applies; an exact version; a failure fails the build naming the package
    /// and version) whenever the SDK provider is the one that reads it -- an `sdk-` name, any
    /// netstandard, and everything but `mono-` on Unix. On Windows a .NET Framework found
    /// through the registry still wins, and the package is fetched only if it is consulted.
    /// The package folder is the build's, `packageRoot ()` (the script variable
    /// `NUGET_PACKAGES`, else the environment's), and part of the memo key.
    /// </summary>
    let resolveFramework (fwk: string option) : Recipe<ExecContext, FrameworkInfo> =
        recipe {
            let! options = getCtxOptions ()
            let! root = packageRoot ()
            let! versions = referencePackVersions ()
            match fwk with
            | Some name when not (name.StartsWith "mono-")
                             && (name.StartsWith "sdk-" || Env.isUnix || Option.isSome (sdkImpl.netstandardMoniker name)) ->
                match referencePackage versions name with
                | Some (packageId, version) ->
                    let! _ = restorePackage (Some root) packageId version
                    ()
                | None -> ()
            | _ -> ()
            return locateFrameworkMemo (root, Path.GetFullPath options.ProjectRoot, versions, fwk)
        }

    /// <summary>
    /// Attempts to locate either .NET or Mono framework. `locateFrameworkIn` with the current
    /// directory as the project root -- for callers with no build context; the tasks pass
    /// their `ProjectRoot`.
    /// </summary>
    let locateFramework (fwk: string option) : FrameworkInfo =
        locateFrameworkIn (Directory.GetCurrentDirectory()) fwk

    /// The warning the SDK probe left for `projectRoot` (a `global.json` pin that could not be
    /// honoured), when the SDK has been probed there. The tasks trace it.
    let sdkProbeWarning (projectRoot: string) : string option =
        sdkImpl.probedWarning (Path.GetFullPath projectRoot)

    /// The .NET target framework of the SDK the `dotnet` host selects in `projectRoot`
    /// (`global.json` honoured, as for `locateFrameworkIn`): "net10.0" for SDK 10.0.x, read from
    /// the SDK's `Microsoft.NETCoreSdk.BundledVersions.props`. `None` when there is no SDK or
    /// the file says nothing. The default target framework of the composed csc and fsc.
    let sdkFramework (projectRoot: string) : string option =
        sdkImpl.sdkFramework (Path.GetFullPath projectRoot)

    /// "net8.0" | "net10.0" | "sdk-net10.0" -> Some "net8.0" / "net10.0": a .NET (net5.0 and
    /// later) target framework, normalized; `None` for .NET Framework, netstandard, anything else.
    let netcoreMoniker (fwk: string) : string option =
        if System.String.IsNullOrEmpty fwk then None else sdkImpl.netcoreMoniker fwk

    /// Every reference assembly of a .NET (net5.0 and later) framework located by the SDK
    /// provider: the `*.dll` files of its targeting pack's `ref/<moniker>` directory, in
    /// ordinal order -- what the SDK itself passes the compiler. Empty for other frameworks,
    /// whose references stay opt-in (`grefs`).
    let frameworkReferences (fwkInfo: FrameworkInfo) : string list =
        match netcoreMoniker fwkInfo.Version with
        | None -> []
        | Some _ ->
            fwkInfo.AssemblyDirs
            |> List.filter Directory.Exists
            |> List.collect (fun dir -> Directory.GetFiles (dir, "*.dll") |> List.ofArray)
            |> List.sortWith (fun a b -> System.String.CompareOrdinal (Path.GetFileName a, Path.GetFileName b))

    /// The `FSharp.Core.dll` shipped next to the SDK's F# compiler (`<sdk>/FSharp/`), when the
    /// framework comes from the SDK provider and the file exists.
    let sdkFSharpCore (fwkInfo: FrameworkInfo) : string option =
        if System.String.IsNullOrEmpty fwkInfo.InstallPath then None else
        let dll = fwkInfo.InstallPath </> "FSharp" </> "FSharp.Core.dll"
        if File.Exists dll then Some dll else None

    /// The `FSharp.Core` package version an fsc compilation for netstandard or .NET Framework
    /// references when it names none: the one `src/core/Xake.fsproj` pins. The script variable
    /// `FSHARP_CORE_VERSION` overrides it (`fsharpCoreReference`).
    let fsharpCoreVersion = "8.0.100"

    /// <summary>
    /// The default `FSharp.Core.dll` of an fsc compilation for netstandard or .NET Framework:
    /// the NuGet package `FSharp.Core` at `FSHARP_CORE_VERSION` (read, and so a dependency,
    /// only here), else `fsharpCoreVersion`, fetched through `restorePackage` into the build's
    /// package folder (`packageRoot ()`); its `lib/netstandard2.1` build for netstandard2.1 when the package has one, else
    /// `lib/netstandard2.0`. Fails naming the package when neither exists.
    /// </summary>
    let fsharpCoreReference (targetFramework: string) : Recipe<ExecContext, string> =
        recipe {
            let! v = getVar "FSHARP_CORE_VERSION"
            let version =
                v |> Option.map (fun s -> s.Trim()) |> Option.filter ((<>) "") |> Option.defaultValue fsharpCoreVersion
            let! dir = restorePackage None "FSharp.Core" version
            let libs =
                match sdkImpl.netstandardMoniker (if isNull targetFramework then "" else targetFramework) with
                | Some "netstandard2.1" -> [ "netstandard2.1"; "netstandard2.0" ]
                | _ -> [ "netstandard2.0" ]
            match libs |> List.map (fun lib -> dir </> "lib" </> lib </> "FSharp.Core.dll") |> List.tryFind File.Exists with
            | Some dll -> return dll
            | None ->
                return failwithf "package FSharp.Core %s in '%s' has no lib/netstandard2.0/FSharp.Core.dll (set FSHARP_CORE_VERSION to a version that has, or reference an FSharp.Core.dll)" version dir
        }

    /// <summary>
    /// The `netstandard.dll` a .NET Framework compilation references so that the types of a
    /// netstandard2.0 assembly it uses (FSharp.Core's netstandard build) resolve to the
    /// framework's own: a type-forwarding facade, never `NETStandard.Library`'s
    /// `build/netstandard2.0/ref/netstandard.dll`, which *defines* the types and so clashes with
    /// `mscorlib` (fsc reports FS3242 on every attribute and FS0193 on `lazy`). The framework's
    /// own `Facades/netstandard.dll` when its reference assemblies carry one (4.7.1 and later),
    /// else the .NET SDK's facade for .NET Framework 4.6.1+,
    /// `<sdk>/Microsoft/Microsoft.NET.Build.Extensions/net461/lib/netstandard.dll` -- the one
    /// msbuild uses -- from the first of `sdkDirs` that has it. `None` when neither exists.
    /// </summary>
    let netstandardFacade (fwkInfo: FrameworkInfo) (sdkDirs: string list) : string option =
        let own =
            fwkInfo.AssemblyDirs |> List.tryPick (fun dir ->
                let dll = dir </> "netstandard.dll"
                if File.Exists dll then Some dll else None)
        match own with
        | Some _ -> own
        | None ->
            sdkDirs
            |> List.filter (System.String.IsNullOrEmpty >> not)
            |> List.tryPick (fun sdk ->
                let dll = sdk </> "Microsoft" </> "Microsoft.NET.Build.Extensions" </> "net461" </> "lib" </> "netstandard.dll"
                if File.Exists dll then Some dll else None)

    /// The managed F# compiler of an SDK framework, `<InstallPath>/FSharp/fsc.dll` (the SDK
    /// provider's `InstallPath` is the SDK directory), when it exists; `None` for the other
    /// providers (the registry's `fsc.exe`, mono's `fsharpc`), whose compiler `FscTool` finds.
    /// This is the file a resolved `Fsc` records and hashes -- `FscTool` on the SDK path is a
    /// temporary launcher script around it.
    let fscCompiler (fwkInfo: FrameworkInfo) : string option =
        if System.String.IsNullOrEmpty fwkInfo.InstallPath then None else
        let dll = fwkInfo.InstallPath </> "FSharp" </> "fsc.dll"
        if File.Exists dll then Some dll else None

    /// <summary>
    /// Locates "global" assembly for specific framework
    /// </summary>
    /// <param name="fwk"></param>
    let locateAssembly fwkInfo =
        let lookupFile file =
            fwkInfo.AssemblyDirs
            //["/Library/Frameworks/Mono.framework/Versions/Current/lib/pkgconfig/../../lib/mono/4.0"]
            |> List.tryPick (fun dir ->
                let fullName = dir </> file
                if File.Exists(fullName) then
                    Some fullName
                else
                    let fullName = fullName + ".dll"
                    if File.Exists(fullName) then
                        Some fullName
                    else
                        None
            )
            |> function | Some x -> x | None -> file
            
        CommonLib.memoize lookupFile