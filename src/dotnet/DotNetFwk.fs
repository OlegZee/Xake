namespace Xake.Dotnet

open Xake
open Xake.Dotnet.ProcessExec
open System.IO

module (* internal *) pkg_config =

    let private pkgcgf args =
        let outp = ref option<string>.None
        let dump s = outp := match !outp with | None -> Some s | s -> s
        try
            do pexec dump dump "pkg-config" (args |> String.concat " ") [] None |> ignore
            match !outp with | None -> "" | Some str -> str
        with _ ->
            ""

    /// Gets true if specified package exists
    let private pkgcgf_bool args =
        let dump (s : string) = ()
        try
            0 = pexec dump dump "pkg-config" (args |> String.concat " ") [] None
        with _ ->
            false
    /// Gets true if specified package exists
    let exists package = pkgcgf_bool ["--exists"; package]

    /// Get the version of a package
    let get_mod_version package = pkgcgf ["--modversion"; package]

    /// Get the version of a package
    let get_variable package var = pkgcgf ["--variable=\"" + var + "\""; package]
    let is_atleast_version package version = pkgcgf_bool ["--atleast-version=\"" + version + "\""; package]
    let is_exact_version package version = pkgcgf_bool ["--exact-version=\"" + version + "\""; package]

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
                if pkg_config.exists "mono" then
                    let prefix = pkg_config.get_variable "mono" "prefix" in

                    let winpath (str:string) = str.Replace('/', System.IO.Path.DirectorySeparatorChar)
                    (
                        prefix |> winpath,
                        pkg_config.get_variable "mono" "libdir" |> winpath,
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
                    ("", "", "Failed to obtain mono framework (check if mono and pkg_config are installed)")
            match err with
            | null ->
                let cscTool = if pkg_config.is_atleast_version "mono" "3.0" then "mcs" else "dmcs"

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
                        EnvVars =["PATH", sdkroot </> "bin" + ";" + (%"PATH")]
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

    module internal MsImpl =
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

    /// Locates the compilers shipped with the .NET SDK and the .NET Framework reference
    /// assemblies distributed as a NuGet package. This is what makes building binaries for
    /// full framework possible on any OS: neither a Framework installation nor the registry
    /// is involved, only the SDK and a restorable package.
    module internal sdkImpl =

        let referenceAssembliesVersion = "1.0.3"

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

        let private nugetRoot () =
            match %"NUGET_PACKAGES" with
            | null | "" ->
                System.Environment.GetFolderPath System.Environment.SpecialFolder.UserProfile
                    </> ".nuget" </> "packages"
            | dir -> dir

        /// Restores the reference assemblies package, so that the first build on a clean
        /// machine works without the user having to prepare anything.
        let private restorePackage moniker =
            let dir = Path.GetTempPath() </> ("xake-refasm-" + moniker)
            let project = dir </> "refasm.csproj"
            try
                Directory.CreateDirectory dir |> ignore
                File.WriteAllText (project, sprintf """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>netstandard2.0</TargetFramework></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies.%s" Version="%s" />
  </ItemGroup>
</Project>""" moniker referenceAssembliesVersion)
                pexec ignore ignore "dotnet" (sprintf "restore \"%s\"" project) [] (Some dir) |> ignore
            with _ -> ()

        let private refAssembliesDir moniker =
            let locate () =
                latestDir (nugetRoot () </> ("microsoft.netframework.referenceassemblies." + moniker))
                |> Option.bind (fun version -> latestDir (version </> "build" </> ".NETFramework"))
            match locate () with
            | Some dir -> Some dir
            | None ->
                restorePackage moniker
                locate ()

        let private dotnetHost () =
            match %"DOTNET_HOST_PATH" with
            | null | "" -> try System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName with _ -> null
            | path -> path

        let private sdkDir () =
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
            |> Option.bind (fun root -> latestDir (root </> "sdk"))

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
                pexec ignore ignore "chmod" (sprintf "+x \"%s\"" path) [] None |> ignore
            path

        let tryLocateFwk fwk =
            match moniker fwk with
            | None -> None, sprintf "'%s' is not a known .NET Framework profile" fwk
            | Some moniker ->

            match sdkDir () with
            | None -> None, "the .NET SDK is not found, cannot locate the compilers"
            | Some sdk ->

            match refAssembliesDir moniker with
            | None ->
                None, sprintf "reference assemblies for '%s' are not available: failed to restore package Microsoft.NETFramework.ReferenceAssemblies.%s" moniker moniker
            | Some refDir ->
                let exe name = if Env.isWindows then name + ".exe" else name
                Some {
                    Version = moniker
                    InstallPath = sdk
                    ToolDir = sdk </> "Roslyn" </> "bincore"
                    AssemblyDirs = [refDir; refDir </> "Facades"]
                    CscTool = sdk </> "Roslyn" </> "bincore" </> exe "csc"
                    FscTool = fun _ -> Some (launcher "fsc" (sprintf "\"%s\"" (sdk </> "FSharp" </> "fsc.dll")))
                    MsbuildTool = launcher "msbuild" "msbuild"
                    EnvVars = []
                }, null

    module internal impl =

        let locateFramework (fwk) : FrameworkInfo =
            let flip f x y = f y x
            let startsWith fragment (s: string option) =
                match s with
                | None | Some null -> false
                | Some str -> str.StartsWith fragment

            // MsImpl throws when the framework is not installed, so any kind of failure
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
                elif fwk |> startsWith "sdk-" then sdkImpl.tryLocateFwk
                elif Env.isUnix then
                    // SDK compilers over reference assemblies from NuGet build for full
                    // framework on any OS; mono is just a fallback these days
                    sdkImpl.tryLocateFwk |> orElse monoFwkImpl.tryLocateFwk
                elif Env.isRunningOnMono then
                    monoFwkImpl.tryLocateFwk |> orElse sdkImpl.tryLocateFwk
                else
                    // a real Framework installation found through the registry wins on Windows
                    MsImpl.tryLocateFwk |> orElse sdkImpl.tryLocateFwk

            match fwk with
            | None ->
                match ["2.0"; "3.0"; "3.5"; "4.0"] |> List.rev |> List.tryPick (tryLocate >> fst) with
                | (Some i) -> i
                | _ -> failwith "No framework found"
            | Some name ->
                match name |> tryLocate with
                | None, err -> failwith err
                | Some f,_ -> f

    /// <summary>
    /// Attempts to locate either .NET or Mono framework.
    /// </summary>
    /// <param name="fwk"></param>
    let locateFramework =
        CommonLib.memoize impl.locateFramework

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