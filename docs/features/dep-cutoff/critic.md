# Fresh-eyes critic (2026-11-17)

A critic subagent (model Fable) read brief.md, probe.md, spike/, the 4.0 sources and docs/, without
the implementer's conclusions, and reported findings by severity. Read only, nothing compiled. It
saw step-1 edits half done. The owner decides; accepted findings go into brief.md "Decisions".
"Triage" is the implementer's note: the step a finding belongs to and a first take, not a
decision.

## Critical

| # | Finding | Proposal (critic) | Triage |
|---|---|---|---|
| C1 | Cut-off with "cheap checks first" skips a dependent of a stamp-file rule whose undeclared side files changed: G writes constant `gen/done.stamp` and undeclared `gen/*.cs`; D needs the stamp and then the `.cs`. The cheap checks run before G is brought up to date, G's rebuild keeps the stamp's epoch, D is skipped. 4.0 reruns D. | Check records in recorded order (as Shake does), or re-check the cheap records after speculation; a default for no-information outputs (empty/constant: no cut-off); add to Acceptance and migration notes. | Step 3. Real. Strict recorded order (needs m1's ordered record) is the clean fix. |
| C2 | Epoch ids can collide after an interrupted run: results are appended as tasks finish; if the source table and counter are saved at the end, a crash leaves a referenced id unallocated and the next run reuses it for other content, so a wrong skip. | Allocation durable before use (tagged log records), or counter = max id seen + 1 on load, or random 64-bit ids. Specify the 0.7 layout. | Step 3. Real. Max-seen + 1 on load is simple; random ids simpler still. |
| C3 | Speculation can run a side-effecting phony nobody asked for (`if deploy then need ["deploy"]` recorded last run). | Never execute phony or `AlwaysRerun` targets speculatively: they count as changed. Acceptance test. | Step 3. Agree. |
| C4 | Speculation can hang on a cycle a script edit introduced (T needed U; now U needs T and T no longer needs U); the engine has no cycle detection. | Track the wait chain; a cycle during speculation means "changed", from a body `need` an error naming the chain. | Step 3. Real; wait-chain tracking is a piece of work of its own. |

## Major

| # | Finding | Proposal (critic) | Triage |
|---|---|---|---|
| M1 | Overlapping outputs across rules: R1 `out/(n:*).dll` + `also out/(n:*).pdb`, R2 `out/special.pdb` (more specific). Lookup depends on timing (who started first); `addResult` keeps the last record. 4.0 has it for `*..>`; `also` makes it common. | At selection, every declared output must resolve back to the same rule and main target; otherwise fail naming both. | Step 2. Agree. |
| M2 | "Output stamp differs from the record → rebuild" loops: an output changed in place by another target (sign, strip) rebuilds every run; an `also` the body does not always write reruns forever. | A declared output missing after the body is an error; document in-place modification; say whether it applies with `--content-cutoff=off`. | Steps 2/3. Agree on "missing after the body is an error". |
| M3 | `--content-cutoff=off` "same decisions as 4.0" under-specified (one verdict engine or two?); "on a test suite" has no oracle. | `off` = the 5.0 verdict with "moved stamp = changed", differences listed; test against recorded 4.0 reruns on named scenarios. | Step 3. Agree. |
| M4 | No-op build cost: every reachable target becomes a pool task with a log line; #116 is open; no criterion. | Fast path when the 4.0 transitive analysis finds nothing moved; counter "tasks scheduled in a no-change run". | Step 3. Agree. |
| M5 | The verdict inside `run` holds the CPU slot: deadlock at `Threads = 1`; `execNeed` with the parent ctx breaks on an empty `Targets`/held resources. | The verdict phase yields the slot and runs under the task's own context with `HeldResources = []`. | Step 1 static needs already do this (`execNeed` with the task context, which yields). Brief to say it. |
| M6 | Delegated rules: foreign `BuildResult`s carry meaningless epochs; `runDelegated` never consults the verdict; header on the inner rule undefined; `requiring` moved into the header could drop a lock. | A "Delegated rules" section: the engine assigns output records/epochs after the executor; define header semantics or reject. | Step 1: the header (needs, locks) is applied to the delegated body too; test it. Epochs: step 3. |
| M7 | Typed form D header read by calling the lambda with `X.Any` breaks valid 4.0 lambdas (`Lib.Make m` before `recipe`). | No header in form D; form B or `X.Rule(header, body)`. | Already so: form D cannot take a header (F# rejects an overloaded or SRTP method through `<|`); probe "Step 1 findings". |
| M8 | Fileset `needs` cannot see rule outputs (expanded against the disk); the header-only example `target "out/all.stamp" { needs "out/*.dll" }` never writes its file, so it reruns forever. What a fileset `needs` records is unstated. | Define the recording; warn when a `needs` fileset overlaps a rule output pattern; redefine or forbid header-only file rules. | Step 1 records `GetFiles` + one dependency per file and builds files that have rules. Header-only file rule: owner to decide (error, or keep as "needs only, the body is empty"). |
| M9 | A header changed between runs is undefined (added `needs` has nothing to compare; added `also` not checked: 4.0 checks recorded `Targets`). | A static set different from the recorded one means out of date; the missing-output check uses the current outputs. | Step 3 (needs the static marking in 0.7); step 2 for current outputs. |
| M10 | A speculative failure "logged at Info" is impossible as written: the task already wrote Error lines; `Async.Parallel` cancels siblings. | Specify exit code and the fate of emitted lines; a resolution path for speculation that returns a result. | Step 3. Agree. |
| M11 | Upgrade from 0.6 unstated ("first seen" = full rebuild?); 4.0 reading a 0.7 file misreads it. | Acceptance: no-change run right after the upgrade reruns 0; stamp fallback for records without epoch; note the downgrade loss. | Step 3. Agree. |
| M12 | `requires`: names are not unique; duplicates (shared header + `requiring`) are rejected by the current check; ordered acquisition does not cover `withResource` in bodies; a held lock forbids dynamic `need` of rule targets. | Merge duplicates (maximum units), order by identity/creation ordinal, document the body-`need` limit next to the syntax. | Step 1. Agree with merge-to-max and the docs line; ordering by name stays unless the owner prefers identity. |
| M13 | Acceptance contradicts scope and itself (`--dryrun` is step 4; hand-edited output vs hash; Part 2 vs Part 3 on hashing an output changed outside; restored `out/` without hashes; no counters API). | Split Acceptance by step; two hand-edit cases; a counters API. | Owner. |

## Minor

| # | Finding | Triage |
|---|---|---|
| m1 | Dependency order and grouping are not recorded faithfully today (`need` appends, `recordDependency` prepends, no grouping, repeats). | Step 3 record format. |
| m2 | Inline SRTP operators may break helpers (`let mk p body = p ..> body`, `List.map ((..>) p)`). | Step 1: checked, see probe "Step 1 findings". |
| m3 | `Yield` on `RecipeBuilder` may turn a non-unit statement (warning FS0020 today) into an error. | Step 1: checked, see probe. |
| m4 | A plain statement before a header runs at registration silently. | Documented in rule-header.md. |
| m5 | `let h = recipe { needs "x" }` at top level: value restriction; `ruleHeader` lacks `requires`/cut-off/`including`; is `also` allowed in a shared header? | Step 1 checked (probe); `ruleHeader` waits for the owner's choice. |
| m6 | Group substitution must escape captured values (`(`, `[`, `*`); phony resolution via `makeTarget`. | Step 1: `formatMatches` (single path) and `glob` (escapes values); test with a `[` in a name. |
| m7 | `needs` naming an `also` of the same rule: only textual equality is decidable at registration. | Step 2. |
| m8 | Stamp tolerance for `(Stamp, Size)`; post-body hash when there is no old hash. | Step 3. |
| m9 | A path that changes kind (source ↔ output). | Step 3. |
| m10 | Pre-hash persisted when the body fails? | Step 3. |
| m11 | `ExecOptions.ContentCutoff` public, so a script can set it; `EngineOptions` needs it too. | Step 3; owner ("command line only" was about no environment form). |
| m12 | Progress and `--dump` still use the 4.0 memoized analysis until step 4. | Intended; say so in the brief. |
| m13 | Bounded history evicts old epochs silently. | Step 3: state N, log the eviction. |
