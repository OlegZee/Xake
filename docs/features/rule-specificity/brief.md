# Brief: the most specific rule wins

Roadmap item: Engine › *The most specific rule wins* (idea, 2026-10-23). Decisions below are
settled — do not re-litigate; open items are called out explicitly.

## Problem

`locateRule` (`src/core/ExecCore.fs:43`) returns the first match of the stored rule list via
`List.tryPick` (`ExecCore.fs:85`). The list is in **reverse declaration order** — `addRule`
prepends (`src/core/XakeScriptBuilder.fs:34`), `Combine` does `tail @ head`
(`XakeScriptBuilder.fs:56`) — so the **last declared** matching rule wins. A less-literal pattern
whose group/wildcard spans a segment that a more-literal rule spells out shadows the specific
rule unless it happens to be declared later.

Concrete case (xake-ar, paraphrased): a compile rule `out/(b:*)/(cfg:*)/(fwk:*)/(asm:*).dll`
also matches `out/<b>/obf/<fwk>/<asm>.dll` because `(cfg:*)` matches the literal segment `obf`;
an obfuscation rule `out/(b:*)/obf/(fwk:*)/(asm:*).dll` works only because it is declared later.
Same latent trap under `clean::(b:*)` / `clean::(b:*)::(cfg:*)`.

**Roadmap wording correction.** The roadmap says "a wildcard also matches `/`." That holds only
for `**` (→ `(.*)`). A single `*` compiles to `([^/\\]*)` and does **not** cross a separator
(`src/core/Path.fs:163-167`). The real swallow is a **group matching a literal segment**
(`(cfg:*)` vs literal `obf`), which "more literal characters" fixes; `**`-vs-`*` is additive.

## Goal

Among **all** rules that match a target, pick the **most specific**; fall back to **last-declared**
only to break a true tie. Behavior changes only for scripts that relied on declaration order
*against* specificity.

## Specificity metric (settled)

Per pattern string, cached beside the compiled regex. Tokenize with the matcher's own grammar
(`Path.fs:182`: `\((?'tag'\w+?)\:|\*\*|([*.?$^+!={}])`) — **not** the `Part` AST, which does not
isolate named groups (a `(cfg:*)` segment parses to one opaque mask). A dedicated tokenizer gives
an exact literal-char count even inside mixed segments like `v(ver:*)`.

Key = tuple, compared lexicographically, **larger = more specific**:

| Rank | Component | Why |
|---|---|---|
| 1 | literal character count (chars outside any group/wildcard) | more spelled-out text ⇒ more specific |
| 2 | − count of `**` (Recurse) | `**` crosses separators ⇒ least specific; penalized first |
| 3 | − weighted count of wildcards: `?` lighter than `*`/named-group | fewer / tighter slots ⇒ more specific |

Settled decisions:
- `?` is **more specific** than `*` (it matches exactly one non-separator char). Reflected in rank 3
  weighting (`?` < `*`/group).
- Criterion order as above — `**`-penalty at rank 2, ahead of total wildcard count — is accepted.

Worked checks:
- compile `out/(b:*)/(cfg:*)/(fwk:*)/(asm:*).dll`: literals 11, groups 4; obfuscation
  `out/(b:*)/obf/(fwk:*)/(asm:*).dll`: literals 14, groups 3 → obfuscation wins (14 > 11). ✓
- `clean::(b:*)` (lit 7, grp 1) vs `clean::(b:*)::(cfg:*)` (lit 9, grp 2) on `clean::a::b` →
  both match, two-arg wins (9 > 7); on `clean::a` only the first matches, no contest. ✓

## Tie handling (settled)

- Equal key, **different** pattern strings, both matching the target → **Warn** (severity settled)
  with a self-explaining line, then last-declared wins (today's behavior).
- **Identical** pattern strings (the `"main" => …; "main" => …` override idiom,
  `src/tests/FlatScriptTests.fs:90,101`) → silent, last-declared wins. Must stay silent & green.

Selection keeps the head-most among equal keys (list is reverse-declared ⇒ head = last declared):
fold replacing the best only on **strictly greater** specificity.

## Edge cases (settled)

- `FileConditionRule` (`ExecCore.fs:47`) has no pattern → fixed **lowest** specificity; a pattern
  rule **always** beats a condition rule when both match (settled); condition-vs-condition breaks
  by declaration order.
- `MultiFileRule`: specificity of the pattern that matched (`ExecCore.fs:61`).
- `DelegatedRule`: inner rule's specificity (`ExecCore.fs:78`).

## Explaining conflicts (settled — §10)

One source of truth, three consumers — nearly free because the new selector evaluates all matches
anyway:

- `explainMatch : Rules -> target -> Candidate list` — all matching rules with their `SpecKey`,
  sorted by specificity, winner + tie partners flagged. The selector becomes "head of
  `explainMatch`".
- The tie **Warn** prints the breakdown, e.g.
  `target X matched by A {lit=11,**=0,wc=4} and B {lit=11,**=0,wc=4} — equal specificity; last-declared A chosen`.
- Human/LLM explain mode: `-d EXPLAIN_RULE=<target>` prints the full candidate table with scores
  and the winner (no new syntax; rides the existing `-d VAR` channel).
- **Docs section (mandatory)**: the exact algorithm (the tuple, token semantics `*`=non-slash,
  `**`=any, `?`=one non-slash, `(name:…)`=named capture) with the worked examples above — what an
  LLM quotes when explaining why a rule won/lost.

Deferred to a follow-up roadmap item: a dedicated CLI verb `explain <target>` alongside
`build`/`clean`.

## Rollout (settled)

- Default = **new** (`MostSpecific`). Legacy switch — `ExecOptions` flag, e.g.
  `RuleMatch : MostSpecific | LastDeclared`, fed by `-d RULE_MATCH=last` — restores old order,
  deprecated after one minor, removed thereafter.
- Release note: behavior change + legacy switch + deprecation window.
- `docs/session.md` gets the engine fact; roadmap item flipped from "Not started".

## Hooks & cost

- Selection point: `ExecCore.locateRule` / `List.tryPick` (`ExecCore.fs:85`) — change from
  first-match to best-match. Return shape `(rule, groups, targets)` unchanged, so `execOne`
  (`ExecCore.fs:173,215`) is untouched. Phony-over-file precedence (`ExecCore.fs:245`) and
  `wantOverride` are target-kind precedence, out of scope.
- Cost: regexes are cached (`Path.fs:191`), so evaluating all rules is the already-worst-case
  O(#rules); verify against the `locateRule` benchmarks (`src/tests/RuleMatchTests.fs:37-73`).
</content>
</invoke>
