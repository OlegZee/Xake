# captureOutput

`captureOutput` is a CE operation on the `shell`/`sh`/`shellCmd` builders that routes shell process output to one or more explicit destinations: a file, the Xake log, or an arbitrary handler.

Multiple `captureOutput` calls accumulate — each adds a destination independently. You can also pass a list to add several at once.

## Destination types

```fsharp
type CaptureStream = Stdout | Stderr | Both

type OutputDest =
    | ToLog        of CaptureStream * (string -> Level)   // reroute to Xake log, level per line
    | ToFile       of CaptureStream * string              // write to file (overwrite)
    | ToFileAppend of CaptureStream * string              // write to file (append)
    | ToHandler    of CaptureStream * (string -> unit)    // call handler per line
```

## Examples

Write stdout to a file (overwrite on each run):

```fsharp
do! sh "dotnet build" {
    captureOutput (ToFile (Stdout, "build.log"))
}
```

Append both streams to the same file:

```fsharp
do! sh "dotnet test" {
    captureOutput (ToFileAppend (Both, "ci.log"))
}
```

Capture stderr to a file while keeping stdout in the log:

```fsharp
do! sh "dotnet publish" {
    captureOutput (ToFile (Stderr, "errors.log"))
}
```

Reroute stdout from `Info` to `Warning` level in the Xake log (suppresses the default stdout log):

```fsharp
do! sh "dotnet build" {
    captureOutput (ToLog (Stdout, fun _ -> Warning))
}
```

Detect error tags in compiler output and elevate level dynamically:

```fsharp
do! sh "dotnet build" {
    captureOutput (ToLog (Stdout, fun line -> if line.Contains("[Error]") then Error else Info))
}
```

Call a handler per line:

```fsharp
do! sh "dotnet --list-sdks" {
    captureOutput (ToHandler (Stdout, fun line -> printfn "SDK: %s" line))
}
```

Multiple destinations in a single call:

```fsharp
do! sh "dotnet build" {
    captureOutput [ToFile (Both, "build.log"); ToLog (Stderr, fun _ -> Error)]
}
```

## Default log behavior

The Xake log always receives output unless a `ToLog` destination is added for that stream. Adding `ToLog (Stdout, ...)` suppresses the default stdout log and routes it through the explicit entry instead. `ToFile` and `ToHandler` are purely additive — they do not affect default logging.

## Notes

- `ToFile` truncates the target file on open; `ToFileAppend` opens in append mode.
- File handles are closed after the process exits, even on non-zero exit codes.
- Concurrent stdout/stderr writes to the same file are serialized via a per-file lock.
- `CaptureStream.Both` routes both stdout and stderr to the same destination.
