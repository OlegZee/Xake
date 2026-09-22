# samples/hermetic

- `toolset/` -- a sample project pinning the compiler via `Microsoft.Net.Compilers.Toolset`, for
  humans to read; `src/tests/ToolsetTests.fs` writes its own copy of the same project and source
  into its test sandbox rather than importing this one, so the tests do not depend on `samples/`.
- `dataengine/` -- generated inspection output from running the import against a local
  dataengine checkout; untracked, not part of the repository.
