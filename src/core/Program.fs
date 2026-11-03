namespace Xake

[<AutoOpen>]
module Main =

    open ParseArgs

    /// <summary>
    /// Creates a script with script parameters passed as list of strings. The arguments are
    /// applied over the script's options when it runs, so the command line wins over the script.
    /// </summary>
    let xakeArgs args initialOptions = RulesBuilder (initialOptions, Some args)

    /// <summary>
    /// Create xake build script using command-line arguments to define script options
    /// </summary>
    /// <param name="options">Initial options set. Could be overridden by a command line arguments.
    /// Define option IgnoreCommandLine=true to ignore command line arguments
    /// </param>
    let xake options =
        let args = getArgs() |> getScriptArgs in
        xakeArgs args options

    let xakeScript =
        xake ExecOptions.Default

    /// The engine builder: no command line, no database, silent. `IgnoreEnvironment` stays false:
    /// declarations read the environment at `Start` unless the options given say otherwise.
    let xakeEngine =
        RulesBuilder { ExecOptions.Default with IgnoreCommandLine = true; NoPersist = true; ConLogLevel = Silent; FileLogLevel = Silent; Progress = false }
