namespace Xake

[<AutoOpen>]
module ScriptFuncs =

    /// <summary>
    /// Gets the script execution context options.
    /// </summary>
    /// <returns>Recipe that returns the execution options</returns>
    let getCtxOptions () = recipe {
        let! (ctx: ExecContext) = getCtx()
        return ctx.Options
    }

    /// <summary>
    /// Executes and awaits specified artifacts (targets).
    /// This function queues the specified targets for execution and waits for their completion.
    /// </summary>
    /// <param name="targets">List of target names to execute</param>
    /// <returns>Recipe that ensures the targets are built</returns>
    let need targets =
        recipe {
            let! ctx = getCtx()
            let t' = targets |> List.map (ExecCore.makeTarget ctx)
            do! ExecCore.need t'
        }

    let private recordDependency d = recipe {
        let! result = getResult()
        do! setResult { result with Depends = d :: result.Depends }
    }

    /// <summary>
    /// Instructs Xake to rebuild the target even if dependencies are not changed.
    /// Forces the target to be considered out-of-date regardless of file timestamps.
    /// </summary>
    /// <returns>Recipe that records the always-rerun dependency</returns>
    let alwaysRerun() = AlwaysRerun |> recordDependency

    /// <summary>
    /// Gets the environment variable value and records it as a dependency.
    /// If the environment variable changes, targets depending on it will be rebuilt.
    /// </summary>
    /// <param name="variableName">Name of the environment variable to retrieve</param>
    /// <returns>Recipe that returns the environment variable value</returns>
    let getEnv variableName =
        let value = Util.getEnvVar variableName
        recipe {
            do! EnvVar (variableName,value) |> recordDependency
            return value
        }

    /// <summary>
    /// Gets the script (options) variable value and records it as a dependency. 
    /// See `Xake.Options.Vars` for available variables.
    /// If the variable changes, targets depending on it will be rebuilt.
    /// </summary>
    /// <param name="variableName">Name of the script variable to retrieve</param>
    /// <returns>Recipe that returns the variable value</returns>
    let getVar variableName = recipe {
        let! ctx = getCtx()
        let value = Util.getVar ctx.Options variableName

        do! Var (variableName,value) |> recordDependency
        return value
    }

    /// <summary>
    /// Gets the list of files matching specified fileset and records it as a dependency.
    /// If files matching the pattern change, targets depending on this fileset will be rebuilt.
    /// </summary>
    /// <param name="fileset">Fileset pattern to match files against</param>
    /// <returns>Recipe that returns the list of matching files</returns>
    let getFiles fileset = recipe {
        let! ctx = getCtx()
        let files = fileset |> toFileList ctx.Options.ProjectRoot
        do! GetFiles (fileset,files) |> recordDependency

        return files
    }

    /// <summary>
    /// Demands the files (and records dependencies).
    /// Ensures that all files in the list are built/available.
    /// </summary>
    /// <param name="files">File list to demand</param>
    /// <returns>Recipe that ensures all files are available</returns>
    let needFiles (Filelist files) =
        ExecCore.need (List.map FileTarget files)

    /// <summary>
    /// Gets the list of files matching specified fileset and demands them.
    /// This is a convenience function that combines `getFiles` and `needFiles`. 
    /// Records both dependencies on a fileset (mask) and on each found file.
    /// </summary>
    /// <param name="fileset">Fileset pattern to match and demand files</param>
    /// <returns>Recipe that gets and demands all matching files</returns>
    let dependsOn fileset = recipe {
        let! files = getFiles fileset
        do! needFiles files
    }

    /// <summary>
    /// Gets current target file.
    /// Only works for file targets, fails for phony targets.
    /// </summary>
    /// <returns>Recipe that returns the current target file</returns>
    let getTargetFile() = recipe {
        let! ctx = getCtx()
        return ctx.Targets
            |> function
            | FileTarget file::_ -> file
            | _ -> failwith "getTargetFile is not available for phony actions"
    }

    /// <summary>
    /// Gets all current target files.
    /// Only works for file targets, fails if any target is phony.
    /// </summary>
    /// <returns>Recipe that returns the list of current target files</returns>
    let getTargetFiles() : Recipe<ExecContext, File list> = recipe {
        let! ctx = getCtx()
        return ctx.Targets |> List.collect (function |FileTarget file -> [file] |_ -> failwith "Expected only a file targets")
    }

    /// <summary>
    /// Gets current target file name with full path.
    /// Convenience function that combines getTargetFile and File.getFullName.
    /// </summary>
    /// <returns>Recipe that returns the full path of the current target file</returns>
    let getTargetFullName() = recipe {
        let! file = getTargetFile()
        return File.getFullName file
    }

    /// <summary>
    /// Gets all rule matches for the current target.
    /// Returns a map of capture group names to their matched values.
    /// </summary>
    /// <returns>Recipe that returns the map of rule matches</returns>
    let getRuleMatches () = recipe {
        let! ctx = getCtx()
        return ctx.RuleMatches
    }

    /// <summary>
    /// Gets a specific rule match (capture group) by its name.
    /// Returns empty string if the group is not found.
    /// </summary>
    /// <param name="key">Name of the capture group to retrieve</param>
    /// <returns>Recipe that returns the matched value or empty string</returns>
    let getRuleMatch key = recipe {
        let! groups = getRuleMatches()
        return groups |> Map.tryFind key |> function |Some v -> v | None -> ""
    }


    /// <summary>
    /// Writes a message to the build log.
    /// Alias for ExecCore.traceLog function.
    /// </summary>
    let trace = ExecCore.traceLog

    /// <summary>
    /// Defines a phony rule that demands specified targets in parallel.
    /// All targets are started simultaneously and the rule completes when all are done.
    /// Example: "main" &lt;== ["build-release"; "build-debug"; "unit-test"]
    /// </summary>
    /// <param name="name">Name of the phony target</param>
    /// <param name="targets">List of target names to demand</param>
    /// <returns>Phony rule that demands the specified targets</returns>
    let (<==) name targets = PhonyRule (name, recipe {
        do! need targets
        do! alwaysRerun()   // always check demanded dependencies. Otherwise it wan't check any target is available
    })

    /// <summary>
    /// Alias for the &lt;== operator.
    /// Defines a phony rule that demands specified targets in parallel.
    /// </summary>
    let (<||) = (<==)
    
    /// <summary>
    /// Defines a phony rule that demands targets to be built sequentially.
    /// Unlike '&lt;==' operator, this one waits for completion of one target before starting another.
    /// Example: "deploy" &lt;&lt;&lt; ["build"; "test"; "package"]
    /// </summary>
    /// <param name="name">Name of the phony target</param>
    /// <param name="targets">List of target names to build sequentially</param>
    /// <returns>Phony rule that builds targets in sequence</returns>
    let (<<<) name targets = PhonyRule (name, recipe {
        for t in targets do
            do! need [t]
        do! alwaysRerun()
    })


