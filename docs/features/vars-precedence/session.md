# vars-precedence: session

Where things stand (2026-10-27): analysis done (`analysis.md`, written against origin/dev 92ef634).
Parts B and C of it (the `setup` op and the run banner at Info) are in review as PR #101
(`feature/setup-and-banner`). Part A (`-d` vs `var` precedence, option 1 recommended) is a spec
pending the owner's design decision; the open questions are at the end of `analysis.md`.

Next step: the owner's design discussion for part A, then implement it on this branch. The banner
in #101 labels every variable `script/cli` because the two sources share one list today; part A
(separate CLI vars) would let it say which one.
