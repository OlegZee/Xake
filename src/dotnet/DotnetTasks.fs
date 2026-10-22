namespace Xake.Dotnet

open System.IO
open System.Resources

open Xake
open Xake.Tasks

[<AutoOpen>]
module DotNetTaskTypes =

    // CSC task and related types
    type TargetType = |Auto |AppContainerExe |Exe |Library |Module |WinExe |WinmdObj
    type TargetPlatform = |AnyCpu |AnyCpu32Preferred |ARM | X64 | X86 |Itanium
    // see http://msdn.microsoft.com/en-us/library/78f4aasd.aspx
    // defines, optimize, warn, debug, platform

    type MsbVerbosity = | Quiet | Minimal | Normal | Detailed | Diag


module internal Impl =
    /// Escapes argument according to CSC.exe rules (see http://msdn.microsoft.com/en-us/library/78f4aasd.aspx)
    let escapeArgument (str:string) = Tool.quoteArgument str

    let isEmpty str = System.String.IsNullOrWhiteSpace(str)

    /// Gets the path relative to specified root path
    let getRelative (root:string) (path:string) =

        // TODO reimplement and test
        match true with
        | _ when isEmpty root ->
            path
        | _ when path.ToLowerInvariant().StartsWith (root.ToLowerInvariant()) ->
            path.Substring(root.Length).TrimStart('/', '\\')
        | _ -> path

    let endsWith e (str:string) = str.EndsWith (e, System.StringComparison.OrdinalIgnoreCase)
    let (|EndsWith|_|) e str = if endsWith e str then Some () else None

    let resolveTarget  =
        function
        | EndsWith ".dll" -> Library
        | EndsWith ".exe" -> Exe
        | _ -> Library

    let rec targetStr fileName = function
        |AppContainerExe -> "appcontainerexe" |Exe -> "exe" |Library -> "library" |Module -> "module" |WinExe -> "winexe" |WinmdObj -> "winmdobj"
        |Auto -> fileName |> resolveTarget |> targetStr fileName

    /// The content of the `<output>.runtimeconfig.json` that `dotnet <output>` needs, for a
    /// `netN.0` framework (N >= 5); `None` for anything else (netstandard, .NET Framework, "").
    /// It is what `dotnet build` writes by default (no roll-forward settings): same keys, same
    /// order, no trailing newline.
    let runtimeConfigContent (framework: string) : string option =
        Option.ofObj framework
        |> Option.bind DotNetFwk.sdkImpl.netcoreMoniker
        |> Option.map (fun moniker ->
            let major = moniker.Substring(3).Split('.').[0]
            String.concat "\n"
                [ "{"
                  "  \"runtimeOptions\": {"
                  sprintf "    \"tfm\": \"%s\"," moniker
                  "    \"framework\": {"
                  "      \"name\": \"Microsoft.NETCore.App\","
                  sprintf "      \"version\": \"%s.0.0\"" major
                  "    },"
                  "    \"configProperties\": {"
                  "      \"System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization\": false"
                  "    }"
                  "  }"
                  "}" ])

    let isRuntimeConfig (file: File) = file |> File.getFileName |> endsWith ".runtimeconfig.json"

    /// The rule's file targets (none for a phony action or outside a rule).
    let fileTargets (targets: Target list) =
        targets |> List.choose (function FileTarget f -> Some f | _ -> None)

    /// The `.runtimeconfig.json` the rule declares among its targets, checked against the
    /// output and the framework, with the compilation's target type: `None` and the target as
    /// given when the rule declares none (a single-file rule, record syntax with `Out` outside
    /// a rule). A declared one makes an `Auto` target an executable (the runtimeconfig is what
    /// `dotnet <output>` starts it with); it fails for a library target, a framework that is not
    /// `netN.0`, a name that is not the output's (`app.dll` -> `app.runtimeconfig.json`), and
    /// more than one declared.
    let declaredRuntimeConfig (targets: File list) (outFile: File) (framework: string) (target: TargetType) : string option * TargetType =
        match targets |> List.filter isRuntimeConfig with
        | [] -> None, target
        | [ rc ] ->
            let rcName = File.getFileName rc
            let outPath = File.getFullName outFile
            let expected = System.IO.Path.ChangeExtension (outPath, ".runtimeconfig.json")
            if File.make expected <> rc then
                failwithf "'%s' is declared as a target, but the output is '%s', whose runtimeconfig is '%s'"
                    rcName (File.getFileName outFile) (System.IO.Path.GetFileName expected)
            let effective =
                match target with
                | Auto -> Exe
                | Exe | WinExe | AppContainerExe -> target
                | other -> failwithf "'%s' is declared as a target, but '%s' is compiled as a %A, not an application" rcName (File.getFileName outFile) other
            match runtimeConfigContent framework with
            | None ->
                failwithf "'%s' is declared as a target, but %s applications do not use one"
                    rcName (match framework with null | "" -> "this framework's" | f -> f)
            | Some _ -> Some (File.getFullName rc), effective
        | many ->
            failwithf "more than one runtimeconfig is declared as a target: %s" (many |> List.map File.getFileName |> String.concat ", ")

    let platformStr = function
        |AnyCpu -> "anycpu" |AnyCpu32Preferred -> "anycpu32preferred" |ARM -> "arm" | X64 -> "x64" | X86 -> "x86" |Itanium -> "itanium"

    /// Forwarder to `Tool.diagnosticLevel`.
    let levelFromString defaultLevel (text:string) :Level = Tool.diagnosticLevel defaultLevel text
    let inline coalesce ls = //: 'a option list -> 'a option =
        ls |> List.fold (fun r a -> if Option.isSome r then r else a) None

    /// Makes resource name given the file name
    let makeResourceName (options:ResourceSetOptions) baseDir resxfile =

        let baseName = Path.GetFileName(resxfile)

        let baseName =
            match options.DynamicPrefix,baseDir with
            | true, Some dir ->
                let path = Path.GetDirectoryName(resxfile) |> getRelative (Path.GetFullPath(dir))
                if not <| isEmpty path then
                    path.Replace(Path.DirectorySeparatorChar, '.').Replace(':', '.') + "." + baseName
                else
                    baseName
            | _ ->
                baseName

        match options.Prefix with
            | Some prefix -> prefix + "." + baseName
            | _ -> baseName

    let collectResInfo pathRoot = function
        |ResourceFileset (o,Fileset (fo,fs)) ->
            let mapFile file =
                let resname = makeResourceName o fo.BaseDir (File.getFullName file) in
                (resname,file)

            let (Filelist l) = Fileset (fo,fs) |> (toFileList pathRoot) in
            l |> List.map mapFile

    /// Compiles a `.resx` to a `.resources` file: one body for both targets, `Resx.compile`
    /// (`ResxFormat`), which follows the .NET SDK's `GenerateResource` byte for byte -- strings,
    /// typed values, `ResXNullRef`, `ResXFileRef` (text in the named encoding, code pages
    /// included; `byte[]`; images and other types as activator streams), `bytearray.base64`
    /// and `binary.base64` payloads. `ResxTests` compare it against msbuild.
    /// A resx of strings (and primitives) compiles to the classic format any runtime reads; any
    /// other entry makes the output preserialized (`System.Resources.Extensions`), which the
    /// assembly carrying it needs at run time: in .NET 5+'s shared framework, a package
    /// reference on .NET Framework (`ResxFormat.needsExtensions` tells which format a file is).
    /// Before, net462 used `ResXResourceReader` and wrote the classic BinaryFormatter format;
    /// both targets now write the same bytes.
    let compileResx (resxfile:File) (rcfile:File) =
        Xake.Dotnet.Resx.compile resxfile.FullName rcfile.FullName

    let compileResxFiles = function
        | (res,(file:File)) when file |> File.getFileName |> endsWith ".resx" ->
            let tempfile = Path.GetTempFileName() |> File.make
            do compileResx file tempfile
            (Path.ChangeExtension(res,".resources"),tempfile,true)
        | (res,file) ->
            (res,file,false)

    /// Forwarder to `Tool.failOnExitCode`.
    let failOnExitCode failOnError (name: string) exitCode = Tool.failOnExitCode failOnError name exitCode
