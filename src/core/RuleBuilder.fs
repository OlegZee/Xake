namespace Xake.Experimental

open Xake

[<AutoOpen>]
module RuleBuilder =

    /// <summary>
    /// A computation expression builder for creating Xake rules with a fluent syntax.
    /// Inherits from RecipeBuilder to provide recipe computation expression capabilities.
    /// </summary>
    /// <param name="ruleType">The type of rule to create ("phony", "file", or "files")</param>
    /// <param name="name">The name or pattern for the rule</param>
    /// <param name="patterns">Optional list of patterns for multi-file rules</param>
    type RuleBuilder(ruleType: string, name: string, ?patterns: string list) =
        inherit RecipeBuilder()
        
        /// <summary>
        /// Executes the rule builder and creates the appropriate rule type based on the configuration.
        /// </summary>
        member __.Run(expr: Recipe<ExecContext, unit>) = 
            match ruleType with
            | "phony" -> PhonyRule (name, expr)
            | "target" -> FileRule (name, expr)
            | "targets" -> MultiFileRule (patterns.Value, expr)
            | _ -> failwith "Invalid rule type"

    /// <summary>
    /// Creates a phony rule builder for targets that don't correspond to actual files.
    /// Phony rules are useful for grouping dependencies or executing actions without file outputs.
    /// </summary>
    /// <param name="name">The name of the phony target</param>
    /// <returns>A RuleBuilder configured for creating phony rules</returns>
    /// <example>
    /// <code>
    /// phony "clean" {
    ///     do! rm {dir "bin"}
    ///     do! rm {dir "obj"}
    /// }
    /// </code>
    /// </example>
    let phony name = RuleBuilder("phony", name)
    
    /// <summary>
    /// Creates a file rule builder for targets that correspond to actual files.
    /// File rules define how to build a specific file from its dependencies.
    /// </summary>
    /// <param name="pattern">The file pattern or path that this rule will build (supports wildcards)</param>
    /// <returns>A RuleBuilder configured for creating file rules</returns>
    /// <example>
    /// <code>
    /// target "bin/app.exe" {
    ///     do! csc {src (!!"src/*.cs")}
    /// }
    /// </code>
    /// </example>
    let target pattern = RuleBuilder ("target", pattern)
    
    /// <summary>
    /// Creates a multi-file rule builder for targets that produce multiple files from a single build action.
    /// Multi-file rules are useful when one build step produces several output files.
    /// </summary>
    /// <param name="patterns">A list of file patterns that this rule will build</param>
    /// <returns>A RuleBuilder configured for creating multi-file rules</returns>
    /// <example>
    /// <code>
    /// targets ["app.exe"; "app.xml"] {
    ///     do! fsc {
    ///         src (!!"src/*.fs")
    ///         out "app.exe"
    ///         doc "app.xml"
    ///     }
    /// }
    /// </code>
    /// </example>
    let targets patterns = RuleBuilder("targets", "", patterns)
