# Topic 5: simplify `FileRule.decide`

State at ab66ea6. The owner finds `decide` error-prone: one `match mode, known` of eight rows
whose result depends on the order of rows from different modes (an equal stat answers `Keep`
for `Verdict`/`Recording` only because the `Output`/`PreHash` rows come first).

## 1. What each mode needs (today's behaviour, unchanged)

| Mode | Called from | Rule |
|---|---|---|
| `Verdict` | a no-build verdict (once per run per file) | equal stat: keep (size filled in); moved: hash (cut-off on) or changed now |
| `Recording` | a body records a file (`need` of a plain file) | as `Verdict`, and A1: recorded by another body in this run: keep |
| `Output cutoff` | a rule's outputs right after it | cut-off and hashing: hash; cut-off, hashing off: as `Verdict`; no cut-off: changed now |
| `PreHash` | a rule's outputs before it runs (cut-off) | a record without a hash and an equal stat: hash; else keep |

Missing: always `Keep None`. `SeenByVerdict` is dead: `observe` always passes `false` (the
`RunFiles` claim answers later verdicts before `decide` is called).

After hashing (`decideHashed`), per mode:

| Mode | Unreadable | Hashed |
|---|---|---|
| `Verdict`, `Recording` | changed now, no hash | same hash: keep `changed` (cut-off); else changed now |
| `Output` | as above | as above, and a record never hashed with an equal stat: fill the hash, keep `changed` |
| `PreHash` | keep | equal stat: fill the hash only; else keep |

## 2. Proposal

```fsharp
type RuleContext = { Epoch: int; Hashing: bool; RecordedBefore: bool }   // SeenByVerdict gone

// one function per kind of observation; each is a match over `known` only
let private input  known stamp size rc = ...   // Verdict, Recording (A1 via rc.RecordedBefore)
let private output cutoff known stamp size rc = ...
let private preHash known stamp size rc = ...

let decide known stat mode rc =
    match stat with
    | Missing -> Keep None
    | Present (stamp, size) ->
        match mode with
        | Verdict | Recording -> input known stamp size rc
        | Output cutoff       -> output cutoff known stamp size rc
        | PreHash             -> preHash known stamp size rc
```

`decideHashed` is split the same way, each part next to its `decide` part (`inputHashed`,
`outputHashed`, `preHashed`). No row depends on another mode's row. `FileRuleTests` (15 tests,
every mode x every case) is the safety net: they must pass unchanged, except the two lines that
test `SeenByVerdict = true`, which go with the field.

**Option (3'): the continuation in the decision.** `NeedHash` could carry the mode's own
continuation, `NeedHash of (HashResult -> Decision)`, so `observe` no longer passes the mode and
the context twice and cannot pair a decision with another mode's `decideHashed`. The cost: a
`Decision` is no longer comparable, so the tests match on the case instead of `AreEqual`.
Recommendation: not now; the split alone removes the cross-mode order.

## 3. Optional behaviour change: drop `PreHash`

`PreHash` hashes an output before its rule reruns, when its record has no hash and its stat is
unchanged, so that the rerun can be cut off. With cut-off on, an output gets its hash at `Output`
time; a record without a hash comes from a transition: a database upgraded from 0.4-0.6, a run
with `--content-cutoff=off`, a rule that had `noContentCutoff`, or a file first recorded by a
verdict before its rule ran.

Dropping it: one mode, one function and one observation per rebuilt task fewer (a stat of each
output). The cost: after a transition, the first rebuild of such an output counts as a change
even when the content is the same, so its dependents rebuild once (one extra cascade), and from
then on the output has a hash. Recommendation: keep `PreHash` for now. The saving is small, and
the extra cascade lands on the upgrade from 4.x, the moment users compare times. Revisit when
`LegacyRead` goes.

## 4. Next step after this: how `observe` uses the decision

`observe` maps `Keep` to the state, `Write` to the state and a write, `NeedHash` to the hasher
and then `decideHashed`; it sends every write in one `Db.apply` and decides a conflict again on a
fresh stat, at most 3 rounds (candidate 9). That is a separate topic, to take after this one.
