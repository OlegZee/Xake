# vars-precedence: archive

Rejected designs, one paragraph each: the idea, key details, what was good, why it was rejected.
The decisions in force are in [../brief.md](../brief.md). Full texts are in git (commit in brackets).

## The problem (3.13)

`var "N" v` deleted `-d N=...`, so a CI could not override a script value (`NUGET_PACKAGES`).
"Default" had two unrelated meanings (`var` vs `withDefault`). Libraries read the environment on
their own, untracked or as `EnvVar`, and dependencies were recorded per source, so moving an equal
value from the environment to `-d` rebuilt. #103 fixed the precedence (`-d` over `var`, CLI over
ops); the rest is this feature. [174d27d, 9d9cdad]

## Rejected

**Spec rev. 1-3: a lazy `VarStore`.** One evaluator per run with the chain Request > Cli > Script >
Env > Default > LibraryDefault; libraries register `Var.*` declarations, a value is evaluated and
memoized at the first read. Good: one chain for everything, libraries self-describing, values
canonical. Rejected 2026-10-28: a value evaluated at the point of use means the dependency check can
meet a name whose declaration it has not seen (Q-5); the fixes (registration at task construction,
a `defaulted` flag, database 0.7) were patches. Led to the rule "every value is computed at
launch". [965c874]

**Concept variant 1: the database stores declarations.** Each run persists every variable's
declaration; a changed declaration stops the run with "re-run". Good: launch-time evaluation with
library declarations kept. Rejected: a two-run convergence and a database change for a problem the
script can state itself. [1253115]

**Concept variants 2a/2b/2c: libraries do not read variables implicitly.** (2a) the script
explicitly imports library declarations; (2b) libraries take explicit parameters; (2c) libraries
read the environment only. Good: no hidden reads; 2a gave `--help` for library variables. Rejected:
2a/2b add ceremony to every script, 2c keeps the environment as a hidden input. Replaced by variant
3: the script states the rule for a name, libraries only read names. [1253115]

**Spec rev. 4: lenient read of an undeclared `Var`.** Variant 3 as adopted, but a `Var.*` read
without a `varschema` declaration fell back to the variable set and its constant default. Good:
inline reads stayed compatible. Rejected: it silently ignores the declared environment source;
the owner chose an error. [8b3ddc6]

**Spec rev. 5: `Var.*` untouched.** `var [...]` rules for the script; `Var` kept its 3.13 behaviour
(input value, then its own environment fallback at the point of use, per-source dependencies);
`varschema` only fed `--help`. Good: no change for `Var` users, a smaller spec. Rejected
2026-10-29: the environment read at the point of use stayed as a loophole beside launch-time
evaluation, and it needed a field on `ExecContext`, frozen until 4.0. [7d11d56]

**Evaluation when the op is applied.** Rev. 6 first evaluated `var [...]` and `varschema` while the
script expression was built. Rejected the same day: the set must be computed at launch
(`createScriptContext`), after the command line, so the dependency graph sees the final values and
`--help` reads nothing. [c871d61]
