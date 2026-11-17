# Brief: rule header, declared outputs and content cut-off (Xake 5.0)

Audience: the agent working in the Xake repository. Xake is public: this brief names no product or company of the
consumer. Engine facts are from Xake 4.0.0 (`Xake.xml` of the package and the sources: `DependencyAnalysis.fs`,
`ExecCore.fs`, `Types.fs`, `Database.fs`, `PatternAst`). Where this brief and the 4.0 sources disagree, the sources
win: report the difference. It supersedes `content-cutoff-brief.md` (kept for history).

This is a major change (rule representation, database format), hence 5.0. Read the whole brief first; the order of
work is at the end and starts with a spike that settles the builder design.

## Decisions (owner)

- 2026-11-17, kickoff: content cut-off on by default; `--content-cutoff=off` on the command line only, no
  environment form. Out of scope: error policy, `also` shorthand, deprecation of multi-output rules. The
  Acceptance section is the spec. Where this brief and the 4.0 sources disagree, the sources win.
- 2026-11-17, spike ([probe.md](probe.md)): **syntax approved**: the header on `target "p" { }` and typed
  `X.rule { }`, operations `also`, `needs` (a pattern or a list), `noContentCutoff` (`contentCutoff`); `..>`
  unchanged. Header operations after the body do not compile (FS0001/FS0041/FS3086; FS3095 with Xake's
  message after `do!`), not "FS3095" as written below.
- 2026-11-17: **`FileRule` and `MultiFileRule` merge** into one file-rule case with a main-first outputs list;
  `also` and `*..>` both build it.
- 2026-11-17, second round (answers in [probe.md](probe.md) "Owner's questions"): **the header lives in
  `recipe`**; every file-rule form accepts a headed recipe (`..>`, `*..>`, `..?>`, `target`, `targets`,
  `X.rule`, `X.Rule`); phony forms reject it at compile time. This supersedes "header only on `target` /
  `X.rule`" above and the "Which builders get a header" section below.
- 2026-11-17: **header on `targets` / `*..>` in 5.0**; `..?>` gets a header without `also`, and its `needs` take
  no groups (it has no pattern).
- 2026-11-17: **`requires lock` / `requires (lock, n)`**, a header operation: static `needs` and the verdict
  first, without the lock; the lock only when the target runs; several locks in name order.
- 2026-11-17: **`needs` keeps the list form**; all `needs` of a rule (every line, every list element) are
  requested together before the body, order irrelevant; a failure is an ordinary error.
- 2026-11-17: rules over a list of masks: later, not 5.0.
- 2026-11-17, third round: **`ruleHeader { .. }` header values and `including h`** accepted (helpers contributing
  header items), in 5.0. Open: how group substitution in a shared header is made obvious (options in
  [probe.md](probe.md) "Third round"); `including` is implemented after that decision.
- 2026-11-17: `..?>` has no groups and no `also` (accepted). `requires` execution order accepted: static `needs`,
  then the verdict, then the lock, then the body. A headed recipe nested in a recipe is a compile error by type
  (accepted as designed).
- 2026-11-17, step 1 (implementer, from the sources; [probe.md](probe.md) "Step 1 findings"): the typed function
  form `X.Rule <| fun m -> ..` takes no header (F# rejects an overloaded or SRTP method through `<|`); typed
  headers are written in `X.rule { }`. A statement with a value in `recipe { }` becomes an error. Duplicate
  locks merge (larger units). A fresh-eyes review is in [critic.md](critic.md), awaiting the owner.
- 2026-11-17: **groups in a shared header: option A, declared groups** (`ruleHeader ["n"] { .. }`): checked at
  creation (every used group declared, every declared group used) and at registration (the including rule has
  every declared group), errors naming the header, the rule and the group. `including` is implemented with
  step 1.
- 2026-11-17: **step 1 starts**: the header in `recipe` and every file-rule form, static `needs`, `requires`, the
  `FileRule`/`MultiFileRule` merge as needed.

## Problem

1. **A rule's outputs and fixed inputs are invisible until its body runs.** A recipe is an opaque function: the
   engine learns what a rule needs only as the body reaches each `need`, and never learns of files the body writes
   besides its target (a compiler's `.pdb`, `.xml` next to the `.dll`). So the plan of a first run is unknown
   (`--dryrun`, progress), fixed inputs start only when the body gets to them, and a side output has no rule: it cannot
   be requested or tracked as an output.
2. **No content cut-off.** `getDepState`, `ArtifactDep2 (t, recorded)`: a moved stamp (`stampMoved`, >= 10 ms) is a
   change, content is never compared; a stamp that did not move still counts as a change when any of `t`'s own
   recorded dependencies changed (recursion through `getChangedDeps`). `runTargets` memoizes `getChangeReasons` for
   the run, so a verdict computed before a dependee is rebuilt sticks. A rebuild producing the same bytes reruns every
   dependent: a control file derived from a variable wakes hundreds of expensive targets when the variable goes off and
   on again; a deterministic compiler after a comment-only edit upstream reruns everything downstream; `touch` or a
   checkout that only moves times rebuilds as much as an edit.

## Part 1. Rule header

A rule may carry a **header**: declarations read at registration, without running the body.

```fsharp
"out/(n:*).dll" ..> rule {        // illustrative; the real syntax is decided in the spike
    also  "out/(n:*).pdb"         // declared side output
    also  "out/(n:*).xml"
    needs "obj/(n:*).rsp"         // fixed input
    noContentCutoff               // Part 3
    // ---- body: as today, dynamic need stays
    let! ...
}
```

- `also <pattern>`: an output besides the main target. A full Xake pattern; no shorthand in this version.
- `needs <pattern>`: an input every run needs (Part 2, "Static needs").
- `contentCutoff` / `noContentCutoff`: Part 3.
- Header arguments are **patterns over the main pattern's named groups, written as literals**. They are evaluated at
  registration (a throwing argument fails registration). Group values cannot be used: they come from a match, which
  does not exist at registration, and `getRuleMatch` is a bind. Anything computed stays a dynamic `need` in the body.

### Which builders get a header

Only the **rule-level** builder(s) — the ones that produce a rule from a pattern (`..>`, `*..>`, the `rule`/`target`
forms that exist in 4.0; check the exact list in 4.0, the xml documents `=>`, `..>`, `*..>`, `..?>`). The general
`recipe` builder does not get the operations: a recipe bound with `let!`/`return!` inside a body never carries a
header, so there is nothing to merge. Not in scope: condition rules (`..?>`, no pattern), delegated rules, phony
rules (no file pattern for `also`; `needs` of plain files on a phony rule may follow later).

**Typed targets.** `Typed.Target<'T>.Rule` takes `'T -> Recipe`; the header cannot live inside that lambda (the match
value is a parameter). The header goes on the outside: a builder around the lambda or arguments of `Rule`. The spike
decides the shape.

### Header first: enforced by the compiler

The header must precede the body, and this must be a type error, not a convention. A naive builder (custom operations
on the same type as the recipe) does **not** guarantee it: after a `do!` (unit bind) an `also` still type-checks, is
either collected after an effect or silently dropped. The design that works (prototype checked with `dotnet fsi`):

- The header has its **own type**: `Yield: unit -> Header`, each operation `Header -> Header`.
- `For: Header * (unit -> Recipe<'a>) -> Recipe<'a>` joins header and body; `Run` has an overload for a header-only rule.
- Then any operation after `let!` **or `do!`** is FS3095; a header-only rule and a rule without a header still compile.
- F# restrictions to state in the docs: `if`/`match`/`try` cannot precede a custom operation (FS3086); they are fine in
  the body. `also` and `needs` become keywords inside that builder (`needs` is one letter from `need`: consider the
  names in the spike, e.g. `requires`).

The rule keeps `Header * Recipe`; reading the header is reading a field. No recipe-as-data walking is needed with
this design.

## Part 2. Declared outputs and static needs

### `also` and rule lookup

An `also` pattern is an output pattern of the rule, like the second and further outputs of a 4.0 multi-output rule
(`*..>`), with the main pattern as the first output.

- Checked as multi-output rules are (`PatternAst.checkOutputs`): same named groups as the main pattern (XP013),
  nothing outside groups that a match cannot fill (XP014).
- Indexed with the rule's patterns; resolution as in 4.0 (`RuleMatch = MostSpecific`, ties to the last declared rule,
  ambiguity warning at match time). A requested path matching an `also` resolves to that rule; the match's groups give
  the main target; the rule is built for the main target.
- Outputs of a build are the main target and every `also`; each recorded with its stamp (and content data, Part 3).
- **A rule is out of date when any of its declared outputs is missing**, not only the main target. An output whose
  stamp differs from the recorded one (changed outside the build): check what 4.0 does for its own target and do the
  same for every output; if the record has a hash, hash the file and treat equal content as unchanged (a restored
  `out/` with fresh times must not rebuild everything once hashes exist).
- `needs` naming a path that is an `also` of the same rule: registration error (a rule cannot need its own output).
- A file written by the body but not declared stays a plain file (`FileDep` in dependents), as today.

**Open: `also` vs multi-output rules.** One internal model: a rule with a list of outputs, `also` marks the first one
as main. Do not deprecate `*..>`/`targets`; implement `also` on the same representation and say in the PR what the two
syntaxes share. Whether `*..>` gets a header is decided later.

### `produces` (dynamic outputs)

`do! produces ["out/ja/A.resources.dll"]` in the body: an output known only while the body runs. Recorded with the
producer. A later run resolves a `need` of that path by the database, after rules and `also`, before "existing file".
On an empty database the producer is unknown: the dependent must need the main target first (as Shake's `produces`).
**Invalidation:** the record lives in the producer's last `BuildResult`; when the producer reruns and no longer
produces the path, the mapping disappears with the old result. Cut-off: as source files. Low priority.

### Static `needs`

- Substitution: the main pattern's groups are filled in; a group not in the main pattern is a registration error.
- After substitution: **wildcards left → fileset** (empty fileset is allowed, as an empty fileset today); **no
  wildcards → a single path**, resolved like `need` (rule, `also`, `produces`, existing file; nothing → error before
  the body runs).
- Phony targets: may be named (`needs "clean"` is a target name, no wildcards) — same resolution as `need`.
- All `needs` of a rule (every line, every element of a `needs [..]` list) are one set: order and grouping do
  not matter (decision 2026-11-17).
- Started together, before the body, through the same scheduler as `need`: dependencies between them are ordinary
  graph edges, so no ordering rule is needed. Recorded like a body `need`, marked static.

### The plan

The engine can compute, before running, the static closure of the requested targets (rules, `also`, `needs`) plus the
dynamic part recorded by the last run (body `need`s, `produces`). Use it for the progress estimate of a first run.
`--dryrun` lists the static part and the recorded dynamic part; for cut-off it reports what **may** rerun (4.0
transitive verdict), since it cannot build anything to compare.

## Part 3. Content cut-off

When a dependee's stamp moved, its content is compared with the content the dependent last saw; equal is not a
change. **On by default.**

### Content epochs (the model)

Stamps are not compared as intervals: they are not monotonic (restored caches, `tar`/`rsync -t`, checkouts, clock
steps), and an interval test would accept a dependent built from other content. Instead content is identified by an
**epoch id**.

- Each **output** record of a producer and each **source file** carries `(Stamp, Size, Epoch, History)`.
  `Epoch` is an id from a database-wide counter; `History` is a short list (last N, e.g. 4) of `(Epoch, Hash option)`
  of this file, newest first.
- Dependents record the **epoch id** they saw (plus the stamp, for diagnostics). A dependency is **unchanged** iff the
  recorded id equals the current `Epoch`, or both ids have known hashes in `History` and the hashes are equal.
- When the file's `(Stamp, Size)` moves:
  - no hash can be computed or compared → new epoch (a fresh id);
  - hash computed and equal to a hash in `History` → the current epoch becomes **that** id (A→B→A returns to A's id,
    so a dependent built from A stays valid);
  - different hash → new epoch with that hash.
- A hash computed later for the current content (the pre-hash of an output) is attached to the current epoch in
  `History`: dependents that recorded that id without a hash become comparable.
- **Skipped dependents need no rewrite**: their id stays valid as long as the content does.
- Same stamp with a different size, or different bytes behind a preserved mtime that a hash reveals: a new epoch, so
  the dependent's id no longer matches. Same stamp and size with different bytes and no hash: not detectable, as in 4.0.
- Outputs with `noContentCutoff` and phony targets: **every rebuild starts a new epoch**, no hash. Since the dependee is
  brought up to date before the comparison, this gives the 4.0 result without the transitive rule.

Sources have one table in the database (one entry per file, shared by all dependents). A moved source is hashed once:
the result is written to the table before any dependent decides, and concurrent dependents await it. The table is
saved with the run's results; entries not referenced by any record are pruned when the database is compacted.

### Hashes are lazy

A hash is computed only when a comparison needs it.

- **Empty database** (CI agent, fresh clone, new target): records get a fresh epoch without a hash. Nothing is hashed;
  a clean build costs what it costs in 4.0.
- **Outputs, before the body overwrites them**: if the output exists, its record has no hash, and its current
  stamp and size **equal the producer's recorded ones**, hash it before the body runs (the dependents saw exactly that
  content). If the file was changed outside the build (stamp differs), do not pre-hash: it is a change. After the body,
  hash the new file. So cut-off works from the first rebuild.
- **Sources** (and undeclared outputs, `produces` on an empty database): the old content cannot be hashed before it
  changes. Moved stamp with no hash → changed, and the current content is hashed and recorded; moved stamp with a hash
  → hash, compare, extend the epoch or start a new one. Cut-off works from a file's second move.
- **Why sources are hashed at a move and not at first sight.** Neither hash is needed for the decision at hand (with no
  old hash, a moved file is a change anyway); both are an investment for the next move. At first sight it would be
  every file of the tree, almost none of which will be touched (a CI agent touches none). At a move it is only the
  files being worked on, which are likely to move again, and the cost is bounded by what is rebuilt anyway. Hashing at
  a move is the minimum price at which source cut-off works at all: without it there is never an old hash to compare.
- **Why outputs are not hashed when first built.** Not needed: the pre-hash before the next overwrite yields the same
  hash at the moment it is first useful. An output that is never rebuilt is never hashed. The cost is the same, moved
  from the build that creates the output to the rebuild that replaces it.
- Hash and stamp are taken together: read stamp, hash, re-read stamp; if it moved during hashing, treat as changed and
  do not record the hash.
- `Hash` = SHA-256 of the raw bytes.

### Decision for a dependent

This **replaces** the 4.0 rule "a stamp that did not move is still a change when the dependee's own dependencies
changed": the dependee is brought up to date and the result compared.

1. **Cheap checks first**: variables, environment, filesets membership, source files. If any changed, the target runs:
   no speculation is needed.
2. **Static `needs`**: brought up to date (needed anyway), then compared.
3. **Recorded dynamic dependencies**, in recorded `need` order (the record must keep the order and the grouping of a
   `need [a; b]` call): bring each group up to date **speculatively**, compare, stop at the first changed group (Shake's
   rule). The rest is not built: the body will ask for what it still needs.
   - A speculative failure is not a build error and does not cancel the run: the target counts as changed and runs; the
     failure is logged at Info. If the new run of the body (or any other target in the run) needs that dependency, the
     failure is reported there as usual — the failed result is kept for the run, not retried.
   - A recorded dependency that now has no rule and no file: changed, not an error.
   - A failure of a static `needs` (step 2) is an ordinary error, not a speculative one.
4. **Where.** The verdict is computed lazily in `execOne.run`, right before `ctx.NeedRebuild`, after steps 2–3; the
   run-wide memo of `getChangeReasons` no longer applies to artifact dependencies. A dependee built once per run
   (existing in-flight dedup); concurrent dependents await the same result and read the same record.
5. Phony targets have no content: cut-off does not apply to them.

### Control

- Per run: `--content-cutoff=off`, an engine option (`ExecOptions.ContentCutoff`) set from the command line only. In
  4.0 the `XAKE__<NAME>` environment convention belongs to script variables, not engine options; do not add an
  environment form. `off` gives **the same rebuild decisions as 4.0** (stamps only, transitive rule, no hashing); static
  `needs` still start before the body.
- Per rule: `noContentCutoff` in the header, for outputs that are huge and always different: no hashing, every
  rebuild is a new epoch (see above).
- An `off` run hashes nothing and starts a new epoch for every moved file (its decisions are those of 4.0); epoch data
  stays consistent, so the next `on` run continues from it.
- `contentCutoff` exists for symmetry; it is the default.
- Logged: `[INF] cut-off: <target> rebuilt with the same content, <n> dependents not rerun`; the run summary gives the
  number of files hashed and the time spent hashing.

### Database (0.7)

- Output records `(Target, Stamp, Size, Epoch, History)` in `BuildResult`; per-file source table with the same fields;
  a database-wide epoch counter; dependency records hold the epoch id seen, keep the dependency order and grouping;
  `produces` with the producer; static `needs` marked.
- `produces` mappings are dropped when the producer's rule no longer exists or its pattern no longer matches the
  producer's target.
- 0.4–0.6 databases load (no content data: everything behaves as first seen). `XakeDbVersion` becomes 0.7.

## Out of scope

- Error policy (`failonerror`, keep-going, what counts as an error). Only the speculative failure of Part 3 is defined.
- Shorthand for `also` (relative paths, `{n}`/`{stem}`).
- Deprecation of multi-output rules; header on `*..>`.
- Hashing the result value of a phony target; header on condition, delegated and phony rules.

## Acceptance

Measured by counters, not by timings: the run summary exposes `files hashed`, `targets rerun`, `cut-off` count.

- **Header.** `also`/`needs` after `let!` or `do!` do not compile (FS3095); a header-only rule compiles. A rule without
  a header, with `--content-cutoff=off`, makes the same rebuild decisions as 4.0 on a test suite. Registration runs no
  body: a body whose first statement (before any bind) throws does not fail registration.
- **`also`.** `need "out/A.pdb"` builds the rule of `out/(n:*).dll` for `out/A.dll`; deleting `out/A.pdb` alone reruns
  the rule; a dependent of the `.pdb` reruns when it changes; XP013/XP014 for a bad `also`.
- **`needs`.** On an empty database, the `needs` targets have started before the body's first step (assert the order of
  step start events); `--dryrun` lists them; a single-path `needs` that resolves to nothing fails before the body runs;
  an empty fileset does not.
- **Cut-off, control file (A→B→A).** A rule writes `on`/`off` from a variable into `obj/layer.route`; 100 targets
  need it and are requested only while it is `on` (in the `off` run another route is taken). Runs: on (100 built) →
  off (the 100 not requested, the route rewritten) → on: the route returns to A's epoch, `targets rerun` excludes all
  100. A further run with no change: `files hashed = 0`. Control: if the 100 are requested in the `off` run, they
  rebuild in both later runs.
- **Cut-off, outputs.** A dependee rebuilt with identical bytes: dependents not rerun from its first rebuild; different
  bytes: rerun. An output edited by hand between runs: rebuilt, dependents rerun.
- **Cut-off, sources.** A source touched without an edit twice: the second time nothing reruns; a following no-change
  run hashes 0 files; with `--content-cutoff=off` the dependents rerun.
- **Speculation.** A dependent whose body stops needing a recorded dependency that now fails: it reruns, the build
  succeeds. Recorded dependencies after the first changed group are not built.
- **Non-monotonic stamps.** Source A at T10 → B at T20 (dependent built) → A restored with mtime T10 → `touch` to T30:
  the dependent reruns (content is A, it was built from B). Same stamp, different size: dependent reruns.
- **Empty database.** A clean run: `files hashed = 0`.
- Old databases load; `--dump` shows output records with stamp, size, epoch and history (hash prefixes).

## Order of work

0. **Spike** on the real 4.0 builders: header type, `For`/`Run`, which rule builders get it, typed targets, operation
   names. Report the chosen syntax before building on it.
1. Header and static `needs`.
2. `also` on the multi-output representation.
3. Content cut-off: epochs, lazy hashes, decision in `execOne`, database 0.7.
4. Plan for progress and `--dryrun`.
5. `produces`.

Steps 0–3 are the release; 4–5 may follow in minor versions.
