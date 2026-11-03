# PR 1 report: Hermetic description model

Branch `pr/description-model` (worktree `.claude/worktrees/pr/description-model`, base `origin/dev` bb0e990),
4 commits, not pushed: 8603081 model, e3ca4df tests + fixture, 0433ef5 schema + sync test, 15eb8f0 docs.

## What landed

- `src/hermetic/Description.fs` (362 lines with doc comments; ~200 of code), compiled after `Json.fs`,
  before `Roots.fs`; Lock needs nothing from it. fsproj order comment updated.
- `src/hermetic.tests/DescriptionTests.fs` (11 tests), fixture `src/hermetic.tests/fixtures/xake.project.json`.
- `docs/schemas/xake.project.schema.json`, `docs/hermetic/description.md`, a row in `docs/hermetic/README.md`.

Public API (module `Xake.Hermetic.Dotnet.Description`):
- types `Project` (root record), `Language = FSharp | CSharp`, `Kind = Library | Exe`,
  `Package {Id; Version: string option; Private}`, `Items {Defines; References; Packages; Args}`,
  `Resource {File; Name option}`, `Options {Doc; LangVersion; WarningsAsErrors; Nullable; KeyFile; Args}` + `Options.Default`,
  `Friend {Name; Key option}`, `Repository {Type option; Url option}`, `Metadata` (package block), literal `FileName = "xake.project.json"`
- `parse: string -> Project`, `read: string -> Project` (file, error prefixed with path), `format: Project -> string`,
  `write: string -> Project -> bool` (true when written), `load: string -> Recipe<ExecContext, Project>` (needFiles the file),
  `save: string -> Project -> Recipe<ExecContext, unit>` (no input recorded, like `Lock.save`),
  `find: Fileset -> string -> Recipe<ExecContext, Project>`, `names: Project list -> string list`, `beside: string -> string`.

## Schema decisions (simplest reading of the draft)

1. Writer format: field order fixed (schema order, but `rootNamespace` after `assemblyName`, `projectRefs` before `packages`
   as in the example); absent/empty fields omitted; `kind` always written; a value inline when its line fits 100 columns,
   else one item per line; 2-space indent; LF; final newline. Arrays inline as `[a, b]`, objects as `{ "k": v }`.
2. Booleans with default false (`doc`, `warningsAsErrors`, `private`) are written only when true; `options` omitted when default.
3. `internalsVisibleTo`: plain string when no key, `{name, key}` otherwise.
4. Tightened: `additionalProperties: false` on the `internalsVisibleTo` object and `repository` (draft had none);
   `name` must be non-empty (`minLength: 1`).
5. Added validation: each `byFramework` key must be one of `frameworks`; each `package.bundle` entry one of `projectRefs`.
6. Schema restructured into `$defs` (resource, options, friend, metadata, repository) so a test can check that the
   schema's property lists equal the reader's field lists (`Description.fields`, internal) in writer order.
7. Errors: `'<path>': <message>`, e.g. `'packages[0].id': required`, `'byFramework.net462': not one of the frameworks`;
   unknown and repeated fields are errors.
8. `write` compares with CRLF normalised to LF, so a Windows autocrlf checkout is not rewritten (mtime kept).

## Deviations from the draft example

The fixture is the draft example normalised to the writer: `"packages": [{ ... }]` on one line (draft had it multi-line
though it fits), and `[ { ... } ]` -> `[{ ... }]` in `byFramework.netstandard2.0`. Also the fixture's `$schema` points
to `../../../docs/schemas/...` (correct from its folder); the draft's `../../docs/...` is right for `src/<proj>/`.
Everything else is identical. Title loses "(DRAFT rev. 3)".

## `find` semantics

Follows gaps.md (`find fileset name`, as the drafts call it): expands the fileset without a `GetFiles` dependency, reads
each file, returns the one whose `name` matches and records exactly one input on it (`needFiles`, like `Lock.load`).
Fails when none or several match. Consequence: a *new* description with that name added later is not noticed by the
recipe until something else reruns it (duplicate-name check happens only when it runs).

## Results

- `dotnet build src/hermetic --no-incremental`: `Build succeeded. 0 Warning(s) 0 Error(s)` (net462 + netstandard2.0).
- `dotnet test src/hermetic.tests`: `Passed! - Failed: 0, Passed: 171, Skipped: 0, Total: 171` (160 existing + 11 new).
- `src/tests` not run: nothing shared touched (no `src/core`/`src/dotnet` change).
- Fixture not checked by an external JSON-schema validator (python `jsonschema` not installed).

## Left out

- Path resolution helpers (description dir + `project` -> project folder), C# mask expansion: PR 2/4 territory.
- `import`, `fsc/csc { project d }`, lock changes: later PRs.
- `docs/hermetic/index.md` does not exist on `dev` (it is on `feature/hermetic-build`); the row went into
  `docs/hermetic/README.md` "The pieces". Add the index row when the feature branch's index lands.

## Questions for the user

1. `find` reads every file in the fileset but depends on one: acceptable, or should it also depend on the fileset
   (a `GetFiles` input), so an added duplicate name is caught? The task said "one input"; I kept one.
2. Writer width 100 and the inline/multi-line rule: fine as the canonical format?
3. The two added cross-field checks (`byFramework` keys, `bundle` subset): keep, or drop as not in the draft?
