# Session: db-paths (Xake 5.0, database 0.7)

Branch `feature/db-paths` (worktree `.claude/worktrees/db-paths-ced680`), cut from local `feature/dep-cutoff` at
`4ece63b`. Merge later dep-cutoff commits only when the orchestrator says so. Brief: [brief.md](brief.md) (copied
as-is 2026-11-17; engine facts in it are from `43706d3`, the sources win). Shares the 0.7 version bump with
[dep-cutoff](../dep-cutoff/brief.md) ("Database (0.7)").

## Where things stand (2026-11-17)

Plan in [plan.md](plan.md); owner answered Q1-Q6 (brief.md "Decisions"). Fresh-eyes critic (Fable) in
[critic.md](critic.md): 2 critical, 8 major, 12 minor, owner questions Q-A..Q-I. Implementer checked C1 against
`Database.fs` (the read loop keeps results read before a failure, deletes the file and does not rewrite them;
harmless in 0.6, a dangling-id corruption in 0.7): it holds. Nothing implemented.

Implementer's leaning for the triage: C1 length-framed records with salvage of the readable prefix (Q-C); C2 ids
never reused, stop appending after a failed `Write`; M6 a record body never changes under its tag (dep-cutoff's
result takes a tag from 4-15, Q-D); M4 roots checked before `openDb`; M5 an undeclared root keeps its records
through rewrites (Q-A).

Owner answered critic Q-B..Q-I (brief.md "Decisions"); Q-A (records under an undeclared root at a rewrite) pending.
Q-G condition, implementer's proposal: the count `<n> files outside all roots` is in the Info line whenever it is
non-zero; the hint "declare a root (`pathRoot`)" is added only when the script declares no roots, matching the
owner's "if no roots are declared".

## Next step

Wait for Q-A. Then fold the decisions into plan.md (framing, read-only open, re-root, launch checks), docs, code.
