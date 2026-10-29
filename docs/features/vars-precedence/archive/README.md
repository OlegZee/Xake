# vars-precedence: archive

Superseded designs and finished analyses. The decisions in force are in [../brief.md](../brief.md);
nothing here is to be revived without the owner.

## Rejected designs

- **Spec rev. 1-3: a `VarStore` evaluated lazily at the point of use** ([spec-rev3.md](spec-rev3.md);
  rev. 1 and 2 are in git: e10408b, 11f1dc4). One evaluator per run with the chain
  Request > Cli > Script > Env > Default > LibraryDefault (decisions P1-P11), library declarations
  registered at task construction (D-10), a `defaulted` flag on `Var` dependencies and database 0.7.
  Rejected 2026-10-28: evaluation at the point of use meant the dependency check could meet a name
  with no declaration (Q-5), and every fix was a patch. Replaced by the hard constraint "every value
  is computed at launch".
- **Concept variants 1, 2a, 2b, 2c** ([concept.md](concept.md) §1-7). (1) the database stores each
  variable's declaration and a changed declaration stops the run; (2a) the script explicitly
  imports library declarations; (2b) libraries take explicit parameters instead of variables;
  (2c) libraries read the environment only. Superseded 2026-10-28 by variant 3 (concept.md §8): the
  script states the rule for a name, libraries only read names.
- **Spec rev. 4** ([spec-rev4.md](spec-rev4.md)): variant 3 with `varschema` evaluating `Var.*` at
  launch, and a `Var` read outside `varschema` falling back to the variable set and its constant
  default (K-3). Its `varschema` rules return in rev. 6; K-3 became an error.
- **Spec rev. 5: `Var.*` untouched** ([spec-rev5.md](spec-rev5.md)). `Var` kept its 3.13 behaviour
  (reads the input value, then its own environment fallback at the point of use, per-source
  dependencies); `varschema` only fed `--help`. Rejected 2026-10-29: it left the environment read
  at the point of use as a loophole beside launch-time evaluation. It also added a field to
  `ExecContext`, which is frozen until 4.0.

## Finished analyses

- [analysis.md](analysis.md): the 3.13 problems (`var` deletes `-d`, two "default" semantics,
  libraries reading the environment, per-source dependencies).
- [after-103.md](after-103.md): what #103/#101 solved; `ExecOptions.Vars` (input values) and `Var.*`
  (declarations) are separate concepts.
- [spec4-trace.md](spec4-trace.md): 48 defects traced against rev. 4, and the impact of leaving
  `Var` untouched.
- [session-2026-10-28.md](session-2026-10-28.md), [tracker-2026-10-28.md](tracker-2026-10-28.md): the session log and tracker up to spec rev. 5.
