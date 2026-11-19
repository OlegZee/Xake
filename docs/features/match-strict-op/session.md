# Session: match-strict-op

**Where things stand (2026-11-19).** Branch `feature/match-strict-op` from `dev` at `f366c9a`
(after #144). `m?n` on `RuleMatch`: the same strict lookup as `m["n"]`, asked by the owner after
#144 merged. Design and history: [../match-operator/brief.md](../match-operator/brief.md).

- A static member `(?)` on `RuleMatch`: F# resolves `m?n` through the operand's type, so no global
  `?` is claimed and a script's own `(?)` still wins.
- A generic SRTP `(?)` over any string indexer (to cover `Map` too) was tried and dropped: every
  form of the `Item`/`get_Item` constraint gives warning FS0077 at its definition.
- Tests: `MatchedTests.fs` (value, inside `$"..."`, unknown name).
