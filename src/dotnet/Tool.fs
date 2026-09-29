namespace Xake.Dotnet

open Xake
open Xake.Tasks

/// Helpers shared by every task that runs an external tool: how its output lines are
/// classified for the log, and what a non-zero exit code means. Public so that packages built
/// on `Xake.Dotnet` can run their own tools the same way the compiler tasks do.
module Tool =

    /// <summary>
    /// Classifies a line of compiler output by log level.
    /// </summary>
    /// <remarks>
    /// Diagnostics tied to a source position read "file.cs(1,1): error CS0103: ...", while
    /// whole-compilation ones read "error FS0084: ..." with no position at all. Both have to be
    /// recognized, otherwise a failing compile reports nothing above the default verbosity.
    /// </remarks>
    let diagnosticLevel defaultLevel (text:string) :Level =
        let hasDiagnostic kind =
            text.Contains ("): " + kind + " ") || text.TrimStart().StartsWith (kind + " ")
        if hasDiagnostic "warning" then Level.Warning
        else if hasDiagnostic "error" then Level.Error
        else defaultLevel

    /// <summary>
    /// The exit-code epilogue shared by the compiler tasks: reports a non-zero exit code and
    /// fails the build when the task is configured to.
    /// </summary>
    /// <param name="failOnError">Whether a non-zero exit code has to fail the build</param>
    /// <param name="name">The target being built, for the diagnostic message</param>
    /// <param name="exitCode">The tool's exit code</param>
    let failOnExitCode failOnError (name: string) exitCode = recipe {
        if exitCode <> 0 then
            do! trace Error "('%s') failed with exit code '%i'" name exitCode
            if failOnError then failwithf "Exiting due to FailOnError set on '%s'" name
    }
