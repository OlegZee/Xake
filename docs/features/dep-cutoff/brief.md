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
- 2026-11-17, critic part 1 ([critic.md](critic.md)):
  - **M8:** a file rule with a header and no body (`recipe { needs .. }`) is a registration error (also for
    `..?>`); an explicit empty body (`recipe { needs ..; () }`) is a body.
  - **M12:** a resource named more than once on one rule (`requires` twice, `requires` + `requiring`, an included
    header bringing it) is a registration error naming the rule and the resource, not a merge. Implementer's
    proposal, applied: two *different* resources with the same name on one rule are the same error (errors name
    resources by name); across rules, locks are taken by name, then by creation ordinal, a total order, so
    same-name resources on different rules cannot deadlock. Not an error at creation: tests and libraries create
    same-name resources legitimately (one per script, per test).
  - **M13:** Acceptance is split by step and the contradictions resolved: proposal in "Acceptance by step"
    below, for the owner.
  - **m11:** `ExecOptions.ContentCutoff` may be public.
- 2026-11-17: **no speculation in 5.0 (option B,** [probe.md](probe.md) "Speculation: options A, B, C"**)**. Only
  static `needs` are built before the verdict; a dynamic dependency on a rule target is compared without building it
  (unchanged if that target is up to date, or has finished in this run, with the content the dependent saw; otherwise the
  dependent reruns). Part 3 "Decision for a dependent" and the Acceptance sections below are rewritten accordingly.
  Database 0.7 still records the order and grouping of dynamic needs, so speculation (A) can be added in a minor
  release without a format change. Critic C1, C3, C4 (speculation part) and M10 are resolved by this.
- 2026-11-17, critic part 2 ([critic.md](critic.md)):
  - **M1:** outputs of two rules overlapping for one target is an error naming both (step 2).
  - **M2:** a declared output the body did not write is an error naming the rule and the output (step 2).
  - **M3:** `--content-cutoff=off` is defined as the 5.0 verdict with "a moved stamp is a change" and checked against
    recorded 4.0 reruns on named scenarios. **Its purpose:** a safety switch: compare a build with 4.0's decisions
    and fall back to them if cut-off misbehaves.
  - **M4:** a fast path for no-change runs (no scheduling when nothing in the recorded closure moved) and the counter
    of tasks scheduled in a no-change run, as proposed.
  - **M5:** the verdict phase yields its CPU slot and runs under the task's own context with `HeldResources = []`,
    as proposed (step 1's static needs already do).
  - **M11:** a no-change run right after the upgrade from 0.6 reruns 0 targets (stamp fallback for records without
    a size); the downgrade loss is noted in the release notes.
  - **Acceptance by step** (below) is accepted and replaces "Acceptance" as the spec.
- 2026-11-17, **no epochs in 5.0** (owner; supersedes the C2 entry below and M6's "epochs"): a dependent
  records the `(Stamp, Size)` it saw, as 4.0 records the stamp; each output record and source entry keeps a short
  history of `(Stamp, Size) -> Hash option`. Unchanged iff the recorded pair equals the current one, or the history
  has a hash for the recorded pair equal to the hash of the current content. No ids, no counter, so no C2 and no
  `.xake.lock` for ids. Part 3 "Content by stamp, size and hash" and "Database (0.7)" are rewritten; content epochs
  are in docs/roadmap.md as a later idea.
- (superseded by "no epochs") 2026-11-17, **C2 (epoch collision), corrected by the owner: epoch ids are integers, allocated deterministically
  (+1)**, stored as varints (compact, as db-paths stores numbers). The earlier "random 128-bit" entry is withdrawn.
  - **The counter is derived, not stored:** on load, the next id is the largest id seen in the file + 1. A stored
    counter record would need its own write-before-use ordering; with the lock below the process that loaded is the
    only allocator, so the maximum is enough. An id handed out and never written (a crash) is referenced by nothing
    on disk, so reusing it is harmless.
  - **Gap 1, an id the load does not see:** every record kind that carries an epoch (dependency records, output
    records and their history, the source table) is read through **one** function that also reports the largest id
    it met; a test enumerates the record kinds with an epoch (by reflection over the 0.7 record types) and asserts
    each goes through it.
  - **Gap 2, two runs loading the same maximum:** a run takes an exclusive lock **before** the load and holds it until
    the database is closed. It is a separate file, `.xake.lock` next to `.xake` (opened `FileShare.None`), so the
    database file itself can still be moved for compaction, and a held lock is never mistaken for corruption: today
    an `IOException` on open, after 5 retries, takes the "failed to read database, so recreating" path, which would
    delete the other run's database. The second run waits up to a time-out (proposal: 30 s, logged once at Message
    "waiting for another Xake run on this database") and then fails with "another Xake run uses this database
    (`.xake.lock`)". Engine mode holds the lock for the engine's lifetime.
  - Implemented with step 3 (database 0.7), where epochs appear.
- 2026-11-17, **restored `out/` without hashes: option (a)**, accepted for 5.0 and documented as transitional: an
  output gets a hash at its first rebuild (the pre-hash), so a restored output that was built once and never rebuilt
  has no hash and, with a fresh time, rebuilds once; after that its hash is recorded and a restore is free.
- 2026-11-17, **M7: D2.** Typed form D takes a header as a value: `X.RuleWith h <| fun m -> recipe { .. }` with
  `h = ruleHeader [..] { .. }`; its groups are checked as for `including`. `X.Rule` stays recipe-only.
- 2026-11-17, **`RunSummary`: item 1h, before step 2.** A counters object per run (per `Demand` in engine mode) with
  `Interlocked` increments; `TargetsRun`, `TargetsSkipped`, `TasksScheduled` filled from step 1, the others in step 3;
  `OnSummary` through an interface field and a `xakeScript` operation, so `ExecOptions` keeps structural equality;
  `XakeEngine.LastSummary`.
- 2026-11-17, **M6: the engine makes the output records of delegated rules itself** (stamp, size, history), after
  the executor returns; records from an executor are never trusted. The header (`needs`, `requires`) applies to the
  delegated body.
- 2026-11-17, **M9: yes.** A header whose static set (`needs`, `also`) differs from the one recorded makes the target
  out of date (`also`: step 2, the current outputs; `needs`: step 3, recorded and marked static in 0.7).
- 2026-11-17: **go for step 2** (`also` on the multi-output representation), scope as in "Acceptance by step / Step 2".
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

### Content by stamp, size and hash (the model; decision 2026-11-17, no epochs)

Stamps are not compared as intervals: they are not monotonic (restored caches, `tar`/`rsync -t`, checkouts, clock
steps), and an interval test would accept a dependent built from other content. A dependency is compared by the
exact `(Stamp, Size)` the dependent saw, and by content when that pair is gone.

- A **dependent** records, for each file dependency (a source or a rule's output), the `(Stamp, Size)` it saw, as 4.0
  records the stamp. Stamps compare with the 4.0 tolerance (10 ms), sizes exactly.
- Each **output** record of a producer and each **source** entry keeps a `History`: the last N (proposal: 8)
  `((Stamp, Size), Hash option)` of that file, newest first; the head is the file's current pair.
- **Unchanged** iff the recorded pair equals the file's current `(Stamp, Size)`; otherwise iff the history holds a hash
  for the recorded pair and the current content's hash (from the history, or computed) equals it. No hash for the
  recorded pair: changed.
- A→B→A: A's hash stays in the history under A's pair, so a dependent built from A stays valid when A comes back
  under a new stamp.
- **Invariant:** one pair maps to one content in a file's history. If a hash reveals different bytes behind a pair
  already in the history (a preserved mtime), the old entry is dropped (so dependents that recorded it rerun).
- **A dependent decided unchanged by hash** gets its recorded pair refreshed to the current one (its record is
  appended again). Without that, a dependent that is skipped while its dependee is rebuilt N times would lose its pair
  from the bounded history and rerun once; with it, the next run compares pairs and hashes nothing.
- Same stamp with a different size: a different pair, compared by hash. Same stamp and size with different bytes
  and no hash: not detectable, as in 4.0.
- Outputs with `noContentCutoff` and phony targets: never hashed; every rebuild is a new pair without a hash, so
  dependents rerun (phony targets keep 4.0's completion stamp).

Sources have one table in the database (one entry per file, shared by all dependents). A moved source is hashed once:
the result is written to the table before any dependent decides, and concurrent dependents await it. The table is
saved with the run's results; entries not referenced by any record are pruned when the database is compacted.

### Hashes are lazy

A hash is computed only when a comparison needs it.

- **Empty database** (CI agent, fresh clone, new target): records get their pair without a hash. Nothing is hashed;
  a clean build costs what it costs in 4.0.
- **Outputs, before the body overwrites them**: if the output exists, its record has no hash, and its current
  stamp and size **equal the producer's recorded ones**, hash it before the body runs (the dependents saw exactly that
  content). If the file was changed outside the build (stamp differs), do not pre-hash: it is a change. After the body,
  hash the new file. So cut-off works from the first rebuild.
- **Sources** (and undeclared outputs, `produces` on an empty database): the old content cannot be hashed before it
  changes. Moved stamp with no hash → changed, and the current content is hashed and recorded; moved stamp with a hash
  → hash, compare with the hash of the pair the dependent recorded, add the current pair with its hash to the
  history. Cut-off works from a file's second move.
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
changed". Nothing is built to decide a target except its static `needs` (decision 2026-11-17, option B: no
speculation).

1. **Static `needs`**: brought up to date (built if needed: they are needed anyway), then compared (pair, then hash). A failure
   is an ordinary build error. Full cut-off: a static `needs` on a deterministic output rebuilt with the same bytes is
   unchanged.
2. **The rest of the record, after step 1, without building anything:**
   - sources: `(Stamp, Size)` equal: unchanged; moved: hash and compare with the history (rules above);
   - variables, environment, secrets, fileset membership: compared, as 4.0;
   - a **dynamic dependency on a rule target** D: unchanged iff D's current output matches what the dependent saw
     (the pair, or a hash in D's history, as above) **and** D is up to date by this same no-build decision, recursively. If D has
     already finished in this run (built or found up to date), its record is current and the comparison is exact; if D
     is running, the dependent waits for it. Otherwise (D out of date, not built yet) the dependent counts as changed:
     D is not built to find out. Its body will build D when it needs it, as in 4.0;
   - a dynamic phony target: out of date by this decision: changed; up to date: its completion stamp, as 4.0;
   - a recorded dependency that now has no rule and no file: changed, not an error.
3. **Where.** The verdict is computed lazily in `execOne.run`, right before `ctx.NeedRebuild`, after step 1. The
   no-build decision of step 2 is memoised for the run and refreshed for a target when it finishes (the 4.0 memo stuck
   to a verdict made before a dependee was rebuilt).
4. Phony targets have no content: cut-off does not apply to them.
5. **What is given up** (documented): cut-off through a dynamic dependency on a rule target that is out of date but
   would rebuild to the same bytes; the dependent reruns, as in 4.0. For cut-off through a rule's output, name it in
   `needs`.
6. The record keeps the order and grouping of dynamic `need` calls (database 0.7), unused by this decision, so a
   speculative mode can be added later without a format change.

### Control

- Per run: `--content-cutoff=off`, an engine option (`ExecOptions.ContentCutoff`) set from the command line only. In
  4.0 the `XAKE__<NAME>` environment convention belongs to script variables, not engine options; do not add an
  environment form. `off` gives **the same rebuild decisions as 4.0** (stamps only, transitive rule, no hashing); static
  `needs` still start before the body.
- Per rule: `noContentCutoff` in the header, for outputs that are huge and always different: no hashing, every
  rebuild is a new pair without a hash (see above).
- An `off` run hashes nothing and records every moved file's new pair without a hash (its decisions are those of 4.0); history data
  stays consistent, so the next `on` run continues from it.
- `contentCutoff` exists for symmetry; it is the default.
- Logged: `[INF] cut-off: <target> rebuilt with the same content, <n> dependents not rerun`; the run summary gives the
  number of files hashed and the time spent hashing.

### Database (0.7)

- Output records `(Target, History)` in `BuildResult`, the history's head being the output's current `(Stamp, Size,
  Hash option)`; a per-file source table `(FileId, History)` with the same history (in db-paths terms: the source record
  is `(fileId, stamp, size, history)`; there is no epoch record); `Hash` is 32 bytes (SHA-256), optional.
- Dependency records hold the `(Stamp, Size)` seen (the 4.0 `FileDep`/`ArtifactDep2` stamp plus the size); they keep the
  order and grouping of dynamic `need` calls; static `needs` are marked.
- `produces` with the producer.
- `produces` mappings are dropped when the producer's rule no longer exists or its pattern no longer matches the
  producer's target.
- 0.4–0.6 databases load: a dependency recorded without a size compares by stamp alone (the 4.0 tolerance), so a
  no-change run right after the upgrade reruns 0 targets (M11); histories start empty. `XakeDbVersion` becomes 0.7.

## Out of scope

- Error policy (`failonerror`, keep-going, what counts as an error).
- Speculative execution (bringing recorded dynamic dependencies up to date to decide a dependent): not in 5.0
  (decision 2026-11-17); the record keeps what it would need.
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
  name it in `needs` and are requested only while it is `on` (in the `off` run another route is taken). Runs: on (100 built) →
  off (the 100 not requested, the route rewritten) → on: the route returns to A's content, `targets rerun` excludes all
  100. A further run with no change: `files hashed = 0`. Control: if the 100 are requested in the `off` run, they
  rebuild in both later runs.
- **Cut-off, outputs.** A dependee rebuilt with identical bytes: dependents not rerun from its first rebuild; different
  bytes: rerun. An output edited by hand between runs: rebuilt, dependents rerun.
- **Cut-off, sources.** A source touched without an edit twice: the second time nothing reruns; a following no-change
  run hashes 0 files; with `--content-cutoff=off` the dependents rerun.
- **Dynamic vs static.** A dynamic dependency on an out-of-date rule target reruns the dependent (no speculation); a
  static `needs` on a deterministic output gets cut-off.
- **Non-monotonic stamps.** Source A at T10 → B at T20 (dependent built) → A restored with mtime T10 → `touch` to T30:
  the dependent reruns (content is A, it was built from B). Same stamp, different size: dependent reruns.
- **Empty database.** A clean run: `files hashed = 0`.
- Old databases load; `--dump` shows output records with stamp, size and history (hash prefixes).

## Acceptance by step (accepted 2026-11-17)

Replaces "Acceptance" above (critic M13). Every counter named here is in the run summary below.

### Step 1: header, static `needs`, `requires`, shared headers (done locally)

- Header operations after `let!`/`do!`/`let`/inside `for` do not compile; a recipe with a header used inside
  another recipe does not compile (compile cases in `src/tests/fixtures/header-neg`).
- Registration runs no body; a header-only file rule, a missing group, an `also` or a group on `..?>`, a resource
  named twice are registration errors naming the rule.
- On an empty database every `needs` target has started before the body's first step; a single-path `needs` that
  resolves to nothing fails before the body; an empty fileset does not.
- `requires`: the lock is held around the body only; a locked rule's static needs may be rule targets.
- A rule without a header makes the same decisions as 4.0 (the existing suite, unchanged).

### Step 2: `also`

- `need "out/A.pdb"` builds the rule of `out/(n:*).dll` for `out/A.dll`.
- Deleting `out/A.pdb` alone reruns the rule; the check uses the rule's **current** outputs (an `also` added since
  the last run counts as missing).
- A declared output the body did not write is an error naming the rule and the output (critic M2).
- Two rules whose outputs overlap for one target: an error naming both (critic M1).
- A dependent of the `.pdb` reruns when it changes (stamps, as 4.0, until step 3).
- XP013/XP014 for a bad `also`; `needs` naming an `also` of the same rule (textual): registration error.

### Step 3: content cut-off

Outputs changed outside the build (the contradictions, resolved). For an output whose `(stamp, size)` differs from
its record:
- **the record has a hash:** hash the file. Equal: the output is unchanged (no rebuild; the record takes the new
  stamp). This is the restored-`out/` case. Different: the file was edited by hand; the producer reruns, and its
  new output is compared with the **recorded** hash, which is the content the dependents saw: equal, the
  dependents do not rerun; different, they rerun.
- **the record has no hash:** nothing to compare; the producer reruns and its new output is a pair the dependents did not record, so the
  dependents rerun. The edited file is never pre-hashed (it is not what the dependents saw).
- So Part 2 ("if the record has a hash, equal content is unchanged") and Part 3 ("changed outside: no pre-hash,
  it is a change") are the two halves of this rule, not a conflict.
- **Restored `out/` without hashes rebuilds.** Outputs get a hash only at their first rebuild (lazy, the pre-hash),
  so a cache of outputs that were built once and never rebuilt has no hashes. **Decided (2026-11-17): (a)**, accepted
  for 5.0 and documented as transitional (such an output rebuilds once after a restore, then its hash is recorded).
  Not taken: (b) hash every output after its body; (c) a `--hash-outputs` command before saving a cache.

Acceptance:
- **Control file (A→B→A):** the 100 targets name the route in `needs`; as in "Acceptance" above (on: 100 built; off: not requested, route rewritten; on: the
  route returns to A's content, `targets rerun` excludes the 100; a further no-change run: `files hashed = 0`).
  Control: requested in the `off` run, they rebuild in both later runs.
- **Outputs:** a dependee rebuilt with identical bytes: dependents not rerun from its first rebuild; different
  bytes: rerun.
- **Hand-edited output:** without a hash: producer and dependents rerun; with a hash and a rebuild that reproduces
  the recorded bytes: producer reruns, dependents do not.
- **Restored output with a fresh time and the recorded hash:** nothing reruns.
- **Sources:** touched twice without an edit: the second time nothing reruns; a following no-change run hashes 0
  files; with `--content-cutoff=off` the dependents rerun.
- **Dynamic vs static:** a dynamic dependency on an out-of-date rule target reruns the dependent, and nothing is
  built to decide it; a static `needs` on a deterministic output rebuilt with the same bytes gets cut-off.
- **Nothing is built to decide a target** except its static `needs`: a recorded dynamic dependency whose body is
  expensive and is no longer needed is not built (counter `TargetsRun`).
- **Non-monotonic stamps:** A at T10 → B at T20 (dependent built) → A restored at T10 → `touch` to T30: the
  dependent reruns. Same stamp, different size: reruns.
- **Empty database:** a clean run, `files hashed = 0`.
- **`--content-cutoff=off`:** the same reruns as 4.0 on named scenarios (the StaleArtifactDep, MiscTests rebuild
  cases and the control-file control), recorded as expected target lists (critic M3).
- **Old databases:** 0.4-0.6 load; a no-change run right after the upgrade reruns 0 targets (critic M11);
  `--dump` shows output records with stamp, size and history (hash prefixes).

### Steps 4-5: plan, `--dryrun`, `produces`

- `--dryrun` lists the static closure (rules, `also`, `needs`) and the recorded dynamic part; for cut-off it
  reports what may rerun.
- The progress estimate of a first run uses the static closure.
- `produces`: a later run resolves a `need` of a produced path by the database; the mapping disappears when the
  producer no longer produces it.

### Counters: the run summary (proposal)

```fsharp
/// What a run did, for the log and for tests.
type RunSummary =
    { TargetsRun: int          // bodies executed
      TargetsSkipped: int      // up to date
      CutOff: int              // rebuilt with the same content: dependents not rerun
      FilesHashed: int
      HashTime: System.TimeSpan
      TasksScheduled: int }    // pool tasks, for the no-op cost (critic M4)
```

- Logged at the end of every run at `Message` level, one line: `targets run 3, skipped 120, cut-off 2, files hashed
  5 (12 ms)`.
- Readable by a script and by tests: `ExecOptions.OnSummary: RunSummary -> unit` (default: nothing), called once at
  the end of the run; the engine mode (`XakeEngine`) gets `LastSummary` after each `Demand`.
- Steps 1-2 fill `TargetsRun`, `TargetsSkipped`, `TasksScheduled`; step 3 the rest.

## Order of work

0. **Spike** on the real 4.0 builders: header type, `For`/`Run`, which rule builders get it, typed targets, operation
   names. Report the chosen syntax before building on it.
1. Header and static `needs`.
2. `also` on the multi-output representation.
3. Content cut-off: (stamp, size) histories with lazy hashes, decision in `execOne`, database 0.7.
4. Plan for progress and `--dryrun`.
5. `produces`.

Steps 0–3 are the release; 4–5 may follow in minor versions.
