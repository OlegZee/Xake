# Xake 5.0 phase 3: content cut-off — brief

Status: **accepted** (2026-11-18, owner's answers to Q1-Q4 relayed by the orchestrator). Base: `dev` with phases 1 and 2 (OlegZee/Xake#141,
#142). Specs as built: [db-paths-simple](../db-paths-simple/brief.md) (database 0.7, epochs),
[rule-header](../rule-header/brief.md) (`needs`, `StaticCount`, `noContentCutoff`),
`docs/implnotes.md` ("Epochs"). Anything marked **Proposal** is not decided.

## Goal

A file whose stat moved but whose content did not is not a change: dependents of an output rebuilt
to the same bytes, of a touched source, or of a restored `out/` do not rerun. On by default.

## Decisions (owner)

- **Epochs** (2026-11-18): per file one `FileRec (stamp, size, hash?, changed)`; a dependency is
  an id; a result is up to date when every dependency's `changed` <= its `Epoch`. No hash history,
  no stamp per dependency. The 0.7 format is final: phase 3 fills `hash` (first 8 bytes of
  SHA-256) and changes the verdict, nothing else on disk.
- The "`(stamp, size)` pairs per dependency + pure hash cache" model is rejected (2026-11-18).
- **No speculation (option B):** nothing is built to decide a target except its static `needs`
  (built before the verdict since phase 2).
- **`--content-cutoff=off`**: the decisions of 4.0 (a moved stat is a change, no hashing). Command
  line only, no environment form. On by default.
- **`noContentCutoff`** on a rule: its outputs are never hashed; every rebuild changes them.
- **Q1, dynamic dependee** (2026-11-18): a dependee finished in this run is compared exactly
  (`changed` or `Epoch` against `e`). A dependee **running now is waited for**, then compared (the
  body would wait for it anyway). A dependee not started in this run is unchanged iff its
  `changed <= e` **and** it is up to date by the same no-build verdict, recursively; otherwise the
  dependent is changed and its body builds it. So the 4.0 transitive rule is replaced for static
  `needs` and for dependees finished (or waited for) in this run, and stays for the rest.
- **Q2, hand-edited output** (2026-11-18): as 4.0, a change for its dependents only; the producer
  does not rerun.
- **Q3, eager output hashing** (2026-11-18): outputs are hashed right after every run of their
  rule (with the pre-hash); `noContentCutoff` is the escape for large outputs.
- **Q4** (2026-11-18): `RunSummary` and the no-change fast path are in this PR.

## The rule

On every observation of a file whose `FileRec` stat moved (stamp beyond 10 ms, or size):

- the record has a hash: hash the file; **equal: `changed` stays** (cut-off), the record takes the
  new stat; different: `changed` = this run, new stat and hash;
- the record has no hash: `changed` = this run; the file is hashed and the hash stored, so the
  next move can be cut off.

Hash and stat are taken together: stat, hash, stat; a stat moved during hashing stores no hash.

**When files are hashed:**

- **Outputs** (every target of a rule, `also` included): right after the rule, always, even with
  an unmoved stat. **Pre-hash:** before the body, an existing output with no hash whose stat
  still matches its `FileRec` is hashed, so cut-off works from the first rebuild after an upgrade
  or an `off` run.
- **Sources:** at the first moved stat (above). Never at first sight: an empty database hashes
  nothing.

## The verdict

For a stored result of epoch `e`:

- `FileDep f`: as phase 1 (`changed > e`), with `changed` now kept by cut-off.
- `TargetDep` on a static `needs`: the dependee is already up to date (phase 2), so its `changed`
  (file) or `Epoch` (phony) is compared with `e`. Full cut-off.
- `TargetDep` on a dynamic dependee (recorded from a body `need`): finished in this run, or
  running and waited for: compared exactly; not started: `changed <= e` and the dependee's own
  no-build verdict (Q1). The transitive rule stays only for this last case.

## Hazards: answers

| Hazard | Answer |
|---|---|
| Output edited by hand between runs | Its stat moved: hash differs, `changed` = run, dependents rerun; the producer does not (Q2). |
| Restored `out/` with fresh times | Hash equal: nothing reruns. An output never hashed (built before phase 3 and never rebuilt): rebuilt once. |
| Same `(stamp, size)`, different bytes (coarse stamps) | Outputs: closed, every output is hashed after its rule. Sources: not detectable, as 4.0 (documented). |
| Source A -> B -> A | One hash per file: the return to A is a change; a target that did not run while B was there reruns once (phase 1's "what epochs give up"). |
| Phony dependee | No content, no cut-off: `Epoch > e` is a change, as phase 1. |
| Delegated results | The engine observes and hashes their outputs itself after the executor returns; an executor's own records are not trusted. |
| Interrupted run | `FileRec`s are written when observed, results when stored; a hash describes the content at the stat it was taken with, so a torn run costs reruns, never a missed one. |
| `off` run | Hashes nothing; a moved stat sets `changed` = run and **clears the hash** (it no longer describes the file); the next `on` run starts from there. |
| `noContentCutoff` output | Hash stays empty; after each rebuild `changed` = run. |

## Also in this phase

- `RunSummary` ported from the first attempt (`archive/xake5-attempt1`, tracker 1h): counters
  targets run / skipped / cut-off / files hashed / hash time / tasks scheduled; one log line;
  `onSummary`; `XakeEngine.LastSummary`.
- The no-change fast path (first attempt 3c): a target whose recorded closure is up to date is
  decided without scheduling a task.
- Log: `[INF] cut-off: <target> rebuilt with the same content`.

## Acceptance (counters, not timings)

- Output rebuilt with the same bytes: dependents not rerun from its first rebuild; different bytes:
  rerun.
- Source touched without an edit: first time dependents rerun (no hash yet), second time not; a
  following no-change run hashes 0 files.
- Restored `out/` with fresh times and known hashes: nothing reruns.
- Hand-edited output: dependents rerun, the producer does not.
- Static `needs` on a deterministic output rebuilt with the same bytes: cut-off. Dynamic dependency
  on an out-of-date rule target: the dependent reruns, nothing is built to decide it.
- `--content-cutoff=off`: the same reruns as phase 1 on the existing rebuild tests; `files hashed`
  = 0; a following `on` run is correct (cleared hashes).
- `noContentCutoff`: dependents rerun on every rebuild, the output is not hashed.
- Empty database: a clean run hashes no source; every output once (Q3).
- A dependee running when its dependent is decided: the dependent waits and gets cut-off.
- 0.7 databases from phases 1-2 load as they are (no hashes): no rebuild on a no-change run.

## Out of scope

Speculation; a hash history; any 0.7 format change (if needed: stop and report); hashing phony
results; `produces`.
