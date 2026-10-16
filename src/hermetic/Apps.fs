namespace Xake.Hermetic.Dotnet

open System.IO

open Xake
open Xake.Dotnet

/// <summary>
/// What a build writes beside an application's assembly from its recorded `Lock.App`: the
/// copy-local files, the files msbuild generated (`runtimeconfig.json`, `deps.json`, the
/// `&lt;exe&gt;.config`) and the apphost. Nothing here is derived from the project: every list,
/// every text and the apphost template come from the lock; what the build adds is what msbuild
/// would add at build time -- a referenced project's versions read from its built file, the
/// files beside it with msbuild's related extensions, the apphost written from the template by
/// the SDK's own `HostWriter`.
/// </summary>
module Apps =

    let private normal (path: string) = path.Replace ('\\', '/')
    let private isMacHost = Env.isUnix && Directory.Exists "/System/Library/CoreServices" && File.Exists "/usr/bin/sw_vers"

    /// The SDK's `Microsoft.NETCoreSdk.BundledVersions.props` at `sdkDir`, or "".
    let private bundledVersions (sdkDir: string) =
        let props = Path.Combine (sdkDir, "Microsoft.NETCoreSdk.BundledVersions.props")
        if File.Exists props then File.ReadAllText props else ""

    /// The version of the apphost pack the SDK at `sdkDir` names for `framework`
    /// (`KnownAppHostPack` of `Microsoft.NETCore.App`, the base framework of `net8.0-windows`).
    let appHostPackVersion (sdkDir: string) (framework: string) : string option =
        let text = bundledVersions sdkDir
        let b = match framework.IndexOf '-' with -1 -> framework | i -> framework.Substring (0, i)
        System.Text.RegularExpressions.Regex.Matches (text, "<KnownAppHostPack\\s[^>]*>")
        |> Seq.cast<System.Text.RegularExpressions.Match> |> Seq.map (fun m -> m.Value)
        |> Seq.tryPick (fun item ->
            let attr name =
                let m = System.Text.RegularExpressions.Regex.Match (item, sprintf "\\b%s\\s*=\\s*\"([^\"]*)\"" name)
                if m.Success then Some m.Groups.[1].Value else None
            if attr "Include" = Some "Microsoft.NETCore.App" && attr "TargetFramework" = Some b then attr "AppHostPackVersion" else None)

    /// The runtime identifier of the SDK at `sdkDir` (`NETCoreSdkRuntimeIdentifier`): the
    /// apphost's when a project names none.
    let sdkRuntimeIdentifier (sdkDir: string) : string option =
        let m = System.Text.RegularExpressions.Regex.Match (bundledVersions sdkDir, "<NETCoreSdkRuntimeIdentifier>([^<]*)</NETCoreSdkRuntimeIdentifier>")
        if m.Success && m.Groups.[1].Value <> "" then Some m.Groups.[1].Value else None

    /// The file name of the apphost template of runtime identifier `rid`.
    let appHostFileName (rid: string) = if rid.StartsWith "win" then "apphost.exe" else "apphost"

    /// The apphost template of `rid` at pack `version` in the package folder `packageRoot`.
    let appHostInPackages (packageRoot: string) (rid: string) (version: string) : string =
        normal (Path.Combine (packageRoot, ("microsoft.netcore.app.host." + rid).ToLowerInvariant (), version.ToLowerInvariant (),
                              "runtimes", rid, "native", appHostFileName rid))

    /// The apphost template of `rid` at pack `version` in the `packs` folder of the .NET
    /// installation `dotnetRoot`.
    let appHostInstalled (dotnetRoot: string) (rid: string) (version: string) : string =
        normal (Path.Combine (dotnetRoot, "packs", "Microsoft.NETCore.App.Host." + rid, version, "runtimes", rid, "native", appHostFileName rid))

    /// <summary>
    /// The apphost template of an application whose project names no runtime identifier
    /// (`Lock.SdkHost`), and that runtime identifier: the SDK at `sdkDir` (the lock's
    /// prerequisite) names both, as it does to msbuild -- its own runtime identifier
    /// (`NETCoreSdkRuntimeIdentifier`) and its `KnownAppHostPack` version for `framework`. The
    /// template is the installation's (`dotnetRoot/packs`) when it is there, else the package
    /// of that name and version in `packageRoot` (`None` for the package to fetch: id, version).
    /// </summary>
    let sdkAppHost (dotnetRoot: string) (sdkDir: string) (framework: string) (packageRoot: string)
                   : string * string * (string * string) option =
        let rid =
            match sdkRuntimeIdentifier sdkDir with
            | Some r -> r
            | None -> failwithf "the SDK at '%s' names no runtime identifier of its own (NETCoreSdkRuntimeIdentifier); set RuntimeIdentifier in the project and import it again" sdkDir
        let version =
            match appHostPackVersion sdkDir framework with
            | Some v -> v
            | None -> failwithf "the SDK at '%s' names no apphost pack for %s (KnownAppHostPack)" sdkDir framework
        let installed = appHostInstalled dotnetRoot rid version
        if File.Exists installed then installed, rid, None
        else
            let package = appHostInPackages packageRoot rid version
            package, rid, (if File.Exists package then None else Some ("Microsoft.NETCore.App.Host." + rid, version))

    /// <summary>
    /// Removes the code signature of a Mach-O apphost the way the SDK's
    /// `MachOUtils.RemoveSignature` does: the `LC_CODE_SIGNATURE` command (the last one) zeroed,
    /// the header's command count and size reduced, `__LINKEDIT` cut to the end of the symbol
    /// table, the file truncated there. `bytes` unchanged when it is not a 64-bit Mach-O or has
    /// no signature.
    /// </summary>
    let internal removeMachOSignature (bytes: byte[]) : byte[] =
        let u32 (o: int) = System.BitConverter.ToUInt32 (bytes, o)
        let u64 (o: int) = System.BitConverter.ToUInt64 (bytes, o)
        if bytes.Length < 32 || u32 0 <> 0xfeedfacfu then bytes else
        let ncmds = int (u32 16)
        let rec scan (i: int) (off: int) (linkEdit: int option) (symtab: int option) =
            if i >= ncmds then linkEdit, symtab, None
            else
                let cmd, size = u32 off, int (u32 (off + 4))
                if cmd = 0x19u && System.Text.Encoding.ASCII.GetString(bytes, off + 8, 16).TrimEnd '\000' = "__LINKEDIT" then scan (i + 1) (off + size) (Some off) symtab
                elif cmd = 0x2u then scan (i + 1) (off + size) linkEdit (Some off)
                elif cmd = 0x1du then linkEdit, symtab, Some off
                else scan (i + 1) (off + size) linkEdit symtab
        match scan 0 32 None None with
        | Some linkEdit, Some symtab, Some signature ->
            let b = Array.copy bytes
            let put32 (o: int) (v: uint32) = System.BitConverter.GetBytes(v).CopyTo (b, o)
            let put64 (o: int) (v: uint64) = System.BitConverter.GetBytes(v).CopyTo (b, o)
            let symtabEnd = int (u32 (symtab + 16) + u32 (symtab + 20))
            let signatureSize = uint64 (bytes.Length - symtabEnd)
            let cmdSize = u32 (signature + 4)
            put32 16 (u32 16 - 1u)
            put32 20 (u32 20 - cmdSize)
            System.Array.Clear (b, signature, int cmdSize)
            let filesize = u64 (linkEdit + 48) - signatureSize
            put64 (linkEdit + 48) filesize
            put64 (linkEdit + 32) filesize
            Array.sub b 0 symtabEnd
        | _ -> bytes

    /// The apphost's placeholder for the application's file name (`HostWriter`), UTF-8.
    let private appBinaryPlaceholder = System.Text.Encoding.UTF8.GetBytes "c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2"

    let private runTool (tool: string) (args: string list) (dir: string) =
        let psi = System.Diagnostics.ProcessStartInfo (tool, args |> List.map (fun a -> "\"" + a + "\"") |> String.concat " ")
        psi.WorkingDirectory <- dir
        psi.UseShellExecute <- false
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        use p = System.Diagnostics.Process.Start psi
        let err = p.StandardError.ReadToEnd ()
        p.StandardOutput.ReadToEnd () |> ignore
        p.WaitForExit ()
        if p.ExitCode <> 0 then failwithf "'%s %s' failed (exit %d): %s" tool (String.concat " " args) p.ExitCode err

    /// <summary>
    /// The apphost as written without the SDK's `HostModel` (when it cannot be loaded here):
    /// the template with the application's file name (`appBinary`, UTF-8, zero-padded) in place
    /// of the placeholder; a Windows apphost's GUI subsystem (`gui`) only on a Windows host,
    /// without the assembly's resources; a Mach-O apphost's signature removed and, on macOS for
    /// an `osx` apphost, signed ad hoc (`codesign -s -`) as a file named `apphost`; executable
    /// on Unix.
    /// </summary>
    let private createAppHostHere (template: string) (destination: string) (appBinary: string) (gui: bool) (rid: string) : unit =
        let bytes = File.ReadAllBytes template
        let name = System.Text.Encoding.UTF8.GetBytes appBinary
        if name.Length >= 1024 then failwithf "the application's file name '%s' is too long for an apphost" appBinary
        let at =
            let mutable found = -1
            let mutable i = 0
            while found < 0 && i <= bytes.Length - appBinaryPlaceholder.Length do
                if bytes.[i] = appBinaryPlaceholder.[0] && Array.sub bytes i appBinaryPlaceholder.Length = appBinaryPlaceholder then found <- i
                i <- i + 1
            found
        if at < 0 then failwithf "'%s' has no application placeholder: not an apphost" template
        let patched = Array.copy bytes
        System.Array.Clear (patched, at, appBinaryPlaceholder.Length)
        name.CopyTo (patched, at)
        let isPE = patched.Length > 0x40 && patched.[0] = byte 'M' && patched.[1] = byte 'Z'
        if isPE && gui && Env.isWindows then
            let pe = System.BitConverter.ToInt32 (patched, 0x3C)
            let subsystem = pe + 4 + 20 + 68
            System.BitConverter.GetBytes(2us).CopyTo (patched, subsystem)
        let result = if isPE then patched else removeMachOSignature patched
        Directory.CreateDirectory (Path.GetDirectoryName (Path.GetFullPath destination)) |> ignore
        if not isPE && isMacHost && File.Exists "/usr/bin/codesign" && rid.StartsWith "osx" then
            let temp = Path.Combine (Path.GetTempPath (), "xake-apphost-" + System.Guid.NewGuid().ToString "N")
            Directory.CreateDirectory temp |> ignore
            try
                let file = Path.Combine (temp, "apphost")
                File.WriteAllBytes (file, result)
                runTool "/usr/bin/codesign" [ "-s"; "-"; file ] temp
                File.Copy (file, destination, true)
            finally
                try Directory.Delete (temp, true) with _ -> ()
        else File.WriteAllBytes (destination, result)
        if not Env.isWindows then
            try runTool "chmod" [ "755"; Path.GetFullPath destination ] (Path.GetDirectoryName (Path.GetFullPath destination)) with _ -> ()

    /// Whether the SDK's `Microsoft.NET.HostModel.dll` at `sdkDir` (`<dotnet>/sdk/<version>`)
    /// can be loaded into this process: it is built for the SDK's own runtime (SDK 8: .NET 8),
    /// which must not be newer than the one Xake runs on.
    let hostModelUsable (sdkDir: string) : bool =
        let major =
            match (Path.GetFileName (sdkDir.TrimEnd ('/', '\\'))).Split '.' with
            | parts when parts.Length > 0 -> (match System.Int32.TryParse parts.[0] with | true, v -> v | _ -> System.Int32.MaxValue)
            | _ -> System.Int32.MaxValue
        File.Exists (Path.Combine (sdkDir, "Microsoft.NET.HostModel.dll")) && major <= System.Environment.Version.Major

    /// <summary>
    /// Writes the apphost of an application as the SDK's `CreateAppHost` task does, through the
    /// SDK's own `HostWriter.CreateAppHost` (`Microsoft.NET.HostModel.dll` of `sdkDir`, loaded
    /// into this process): the template with the application's file name (`appBinary`) patched
    /// in, for a Windows apphost the GUI subsystem (`gui`) and the assembly's version resource
    /// and icon (`assembly`; on Windows only, as the SDK), for a macOS one the ad hoc signature.
    /// `true` when written so; `false` when the HostModel cannot be loaded here (an SDK newer
    /// than the runtime Xake runs on) and the apphost was written without it
    /// (`createAppHostHere`).
    /// </summary>
    let createAppHost (sdkDir: string) (template: string) (destination: string) (appBinary: string) (gui: bool) (assembly: string) (rid: string) : bool =
        if not (hostModelUsable sdkDir) then
            createAppHostHere template destination appBinary gui rid
            false
        else
        let hostModel = System.Reflection.Assembly.LoadFrom (Path.Combine (sdkDir, "Microsoft.NET.HostModel.dll"))
        let writer = hostModel.GetType ("Microsoft.NET.HostModel.AppHost.HostWriter", true)
        let macSign = isMacHost && File.Exists "/usr/bin/codesign" && rid.StartsWith "osx"
        let create =
            writer.GetMethods ()
            |> Array.filter (fun m -> m.Name = "CreateAppHost" && m.IsStatic)
            |> Array.sortByDescending (fun m -> m.GetParameters().Length)
            |> Array.head
        // msbuild writes `<obj>/apphost` and copies it: a macOS signature's identifier is the
        // file name it was signed under
        let temp = Path.Combine (Path.GetTempPath (), "xake-apphost-" + System.Guid.NewGuid().ToString "N")
        Directory.CreateDirectory temp |> ignore
        try
            let file = Path.Combine (temp, "apphost" + (if template.EndsWith ".exe" then ".exe" else ""))
            let args =
                create.GetParameters () |> Array.map (fun p ->
                    match p.Name with
                    | "appHostSourceFilePath" -> box template
                    | "appHostDestinationFilePath" -> box file
                    | "appBinaryFilePath" -> box appBinary
                    | "windowsGraphicalUserInterface" -> box gui
                    | "assemblyToCopyResourcesFrom" -> box (Path.GetFullPath assembly)
                    | "enableMacOSCodeSign" -> box macSign
                    | _ -> if p.HasDefaultValue then p.DefaultValue else null)
            (try create.Invoke (null, args) |> ignore
             with :? System.Reflection.TargetInvocationException as e when not (isNull e.InnerException) -> raise e.InnerException)
            Directory.CreateDirectory (Path.GetDirectoryName (Path.GetFullPath destination)) |> ignore
            File.Copy (file, destination, true)
            true
        finally
            try Directory.Delete (temp, true) with _ -> ()

    /// (assembly version, file version) of an assembly as the SDK's `deps.json` names them: the
    /// metadata's version and the numeric file version; `None` for what cannot be read.
    let assemblyVersions (path: string) : string option * string option =
        let assembly =
            try Some (string (System.Reflection.AssemblyName.GetAssemblyName path).Version)
            with _ -> None
        let file =
            try
                let info = System.Diagnostics.FileVersionInfo.GetVersionInfo path
                if isNull info.FileVersion then None
                else Some (sprintf "%d.%d.%d.%d" info.FileMajorPart info.FileMinorPart info.FileBuildPart info.FilePrivatePart)
            with _ -> None
        assembly, file

    /// `text` (a recorded `deps.json`) with each `$(AssemblyVersion:<file>)` and
    /// `$(FileVersion:<file>)` replaced by the versions of `fileOf <file>` (the copy in the
    /// output), as msbuild reads them from the built file.
    let depsResolved (fileOf: string -> string) (text: string) : string =
        System.Text.RegularExpressions.Regex.Replace (text, @"\$\((AssemblyVersion|FileVersion):([^)]+)\)", fun m ->
            let path = fileOf m.Groups.[2].Value
            if not (File.Exists path) then failwithf "the deps.json names '%s', which is not in the output ('%s')" m.Groups.[2].Value path
            let a, f = assemblyVersions path
            (if m.Groups.[1].Value = "AssemblyVersion" then a else f) |> Option.defaultValue "")

    /// Copies `source` to `destination` when the bytes differ (or it is missing).
    let private place (source: string) (destination: string) =
        Directory.CreateDirectory (Path.GetDirectoryName (Path.GetFullPath destination)) |> ignore
        let same =
            File.Exists destination
            && (FileInfo source).Length = (FileInfo destination).Length
            && File.ReadAllBytes source = File.ReadAllBytes destination
        if not same then File.Copy (source, destination, true)

    /// <summary>
    /// The project outputs an application copies (`Copy` items without a hash): the rule needs
    /// them before it writes the application.
    /// </summary>
    let projectCopies (app: Lock.App) : string list =
        app.Copy |> List.filter (fun c -> c.Sha256 = "") |> List.map (fun c -> c.Path)

    /// <summary>
    /// Writes what the lock records for an application (`app` of entry `name`, framework
    /// `framework`, recorded with the SDK `sdk`) beside its assembly `assembly`:
    /// <list type="number">
    /// <item>the copy-local files, each checked against its hash (a project's output unhashed,
    /// with the files beside it that have one of the `Related` extensions);</item>
    /// <item>the recorded files under their names, each checked against its hash, a project's
    /// versions in `deps.json` read from its copy;</item>
    /// <item>the apphost: the SDK's own template (`SdkHost`, by the SDK version) or the lock's
    /// (`PackHost`, checked against its hash), written by the SDK's `HostWriter`, named
    /// `&lt;assembly name&gt;` (`.exe` for a Windows template).</item>
    /// </list>
    /// `dotnetRoot` and `packageRoot` resolve the SDK's template. Returns warnings.
    /// </summary>
    let write (dotnetRoot: string) (packageRoot: string) (lockPath: string) (name: string) (framework: string) (sdk: string)
              (app: Lock.App) (assembly: string) : Recipe<ExecContext, string list> =
        recipe {
            let outDir = Path.GetDirectoryName (Path.GetFullPath assembly)
            let warnings = ResizeArray<string> ()
            let checkHash (what: string) (path: string) (sha: string) =
                if not (File.Exists path) then failwithf "'%s' (%s): the %s '%s' is missing" name framework what path
                if sha <> "" && Csc.sha256 path <> sha then
                    failwithf "'%s' (%s): the %s '%s' does not have the hash the lock '%s' records; run the import again" name framework what path lockPath
            // 1. the copy-local files
            for c in app.Copy do
                checkHash "copy-local file" c.Path c.Sha256
                let destination = Path.Combine (outDir, c.To)
                place c.Path destination
                if c.Sha256 = "" then
                    let stem = Path.GetFileNameWithoutExtension c.Path
                    let destStem = Path.Combine (Path.GetDirectoryName destination, stem)
                    for ext in app.Related do
                        let related = Path.Combine (Path.GetDirectoryName c.Path, stem + ext)
                        if File.Exists related then place related (destStem + ext)
            // 2. the files msbuild generated
            for f in app.Files do
                checkHash "recorded file" f.Path f.Sha256
                let bytes = File.ReadAllBytes f.Path
                // as it is (a project's own config keeps its BOM and line ends), but a deps.json's tokens
                let bytes =
                    if f.Name.EndsWith (".deps.json", System.StringComparison.OrdinalIgnoreCase) then
                        System.Text.Encoding.UTF8.GetBytes (depsResolved (fun file -> Path.Combine (outDir, file)) (System.Text.Encoding.UTF8.GetString bytes))
                    else bytes
                let destination = Path.Combine (outDir, f.Name)
                if not (File.Exists destination) || File.ReadAllBytes destination <> bytes then
                    File.WriteAllBytes (destination, bytes)
            // 3. the apphost
            match app.Host with
            | None -> ()
            | Some host ->
                let sdkDir = Path.Combine (dotnetRoot, "sdk", sdk)
                let! template, rid =
                    match host with
                    | Lock.PackHost (rid, t) ->
                        recipe {
                            checkHash "apphost template" t.Path t.Sha256
                            return t.Path, rid }
                    | Lock.SdkHost ->
                        recipe {
                            let template, rid, missing = sdkAppHost dotnetRoot sdkDir framework packageRoot
                            match missing with
                            | Some (id, version) ->
                                let! dir = DotNetFwk.restorePackage None id version
                                return normal (Path.Combine (dir, "runtimes", rid, "native", appHostFileName rid)), rid
                            | None -> return template, rid }
                let hostName = Path.GetFileNameWithoutExtension assembly + (if template.EndsWith ".exe" then ".exe" else "")
                let viaSdk = createAppHost sdkDir template (Path.Combine (outDir, hostName)) (Path.GetFileName assembly) (app.OutputType = "WinExe") assembly rid
                if not viaSdk then
                    warnings.Add (sprintf "'%s' (%s): the SDK %s's HostModel does not load on this runtime (%s); the apphost was written without it (no resources, no GUI subsystem)"
                                    name framework sdk (string System.Environment.Version))
            return List.ofSeq warnings
        }
