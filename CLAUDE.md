# Xake Build System - AI Coding Agent Instructions

## Project Overview

Xake is an F# build utility inspired by Shake, using the full power of F# computation expressions and monadic recipes. It provides a declarative DSL for build scripts with dependency tracking, parallel execution, and rich rule patterns.

## Architecture & Key Components

### Core Structure
- **`src/core/`** - Main library with computation expression builders and execution engine
- **`src/tests/`** - NUnit test suite with comprehensive rule and execution tests  
- **`samples/`** - Example scripts showing different patterns and use cases
- **`docs/`** - Comprehensive documentation including overview.md and tasks.md

### Critical Types & Concepts
- **`Recipe<ExecContext, 'T>`** - Core monad for build actions, supports `do!` and `let!` syntax
- **`Rule`** - Union type: `FileRule | PhonyRule | MultiFileRule | FileConditionRule`
- **`Target`** - Union type: `FileTarget of File | PhonyAction of string`
- **`ExecOptions`** - Configuration for script execution (threads, logging, root path, etc.)

### Rule Definition Operators
- **`..>`** - File rules: `"output.exe" ..> recipe { do! csc {src !!"*.cs"} }`
- **`*..>`** - Multi-file rules: `["app.exe"; "app.xml"] *..> recipe { ... }`
- **`..?>`** - Conditional rules: `(fun s -> s.EndsWith(".dll")) ..?> recipe { ... }`
- **`=>`** - Phony rules: `"clean" => recipe { do! rm {dir "bin"} }`
- **`<==`** - Parallel dependencies: `"main" <== ["build"; "test"]`
- **`<<<`** - Sequential dependencies: `"deploy" <<< ["build"; "test"; "package"]`

## Development Workflows

### Building the Project
```bash
# Build using the project's own build script
dotnet fsi build.fsx -- -- build

# Or use traditional dotnet
dotnet build src/core/Xake.fsproj -c Release -o out/netstandard2.0
```

### Running Tests
```bash
dotnet fsi build.fsx -- -- test
# Tests are in src/tests/ using NUnit framework
```

### Testing Sample Scripts
```bash
cd samples
dotnet fsi features.fsx        # Comprehensive feature demo
dotnet fsi gettingstarted.fsx  # Simple hello world
```

## Key Patterns & Conventions

### Recipe Computation Expression
All build actions use the `recipe` computation expression:
```fsharp
"target" ..> recipe {
    let! files = getFiles (!!"*.cs")  // Get files and track dependency
    do! need ["dependency"]           // Ensure dependency is built
    do! trace Info "Building..."      // Log message
    return someValue                  // Optional return value
}
```

### Dependency Management
- **`need [targets]`** - Demand targets to be built (parallel)
- **`needFiles files`** - Track file content dependencies  
- **`dependsOn fileset`** - Shorthand for file dependencies
- **`alwaysRerun()`** - Force target to always rebuild

### Pattern Matching with Named Groups
```fsharp
"(dir:*)/(file:*).(ext:cs)" ..> recipe {
    let! dir = getRuleMatch "dir"
    let! file = getRuleMatch "file" 
    let! ext = getRuleMatch "ext"
    // Use captured groups in build logic
}
```

### Environment & Variables
- **`getEnv "VAR"`** - Get environment variable (tracked dependency)
- **`getVar "name"`** - Get script variable (tracked dependency)
- **`var "name" "value"`** - Set script variable in xakeScript block

## Testing Conventions

### XakeTestBase Pattern
Test classes inherit from `XakeTestBase(testDir)` which:
- Creates isolated test directories in `~testout~/testDir/`
- Provides `TestOptions` with appropriate logging and isolation
- Cleans up after tests

### Common Test Patterns
```fsharp
let count = ref 0
do xake x.TestOptions {
    rules [
        "main" => recipe { count := !count + 1 }
    ]
}
Assert.AreEqual(1, !count)
```

## Error Handling & Debugging

### Logging Levels
- Use `filelog "build.log" Verbosity.Diag` for detailed output
- `consolelog Verbosity.Chatty` for verbose console output
- `trace Level.Info "message"` for build step logging

### Common Issues
- **Operator precedence**: Use parentheses around complex rule patterns
- **Path separators**: Use `</>` operator for cross-platform paths
- **Rule conflicts**: Only one rule can match a target - use conditional rules for overlapping patterns
- **Dependency cycles**: Xake detects and reports circular dependencies

## Integration Points

### .NET Integration
- **Xake.Dotnet** package provides `csc`, `fsc`, and other .NET build tasks
- Reference with `#r "nuget: Xake.Dotnet, version"`
- Tasks use computation expression syntax: `csc {src !!"*.cs"; out "app.exe"}`

### File System Operations  
- **Filesets**: `!!"pattern"` for glob patterns, supports exclusions with `--`
- **File operations**: `cp`, `rm`, `writeText` tasks
- **Shell commands**: `shell {cmd "dotnet"; args ["build"]; failonerror}`

## Extension Points

### Custom Tasks
Create functions returning `Recipe<ExecContext, 'T>`:
```fsharp
let myTask input = recipe {
    do! trace Info "Running custom task"
    // Your logic here
    return result
}
```

### Custom Rule Builders (Experimental)
The `Xake.Experimental` namespace provides fluent rule builders:
```fsharp
phony "clean" {
    do! rm {dir "bin"}
}
```
