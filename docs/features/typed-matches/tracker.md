# Tracker: typed-matches

- [x] Inventory of current rule-match API and real usages — research.md
- [x] Design options (type provider, non-TP alternatives) with before/after code — research.md
- [x] Type provider feasibility (packaging, fsi, IDE, .NET 8 floor) — research.md
- [x] Path generation from a pattern (inverse of matching): usages, typed Make/Format, edge cases — research.md
- [x] Generating path lists for `need` (map over values, cross products, partial application, link to filesets) — research.md
- [x] Prototypes — experiments/
- [x] Discussion with the user, decisions — brief.md (2026-10-25)
- [x] Merge `dev` (pattern AST #89) into the branch
- [x] Estimate the cost of separate pattern types for phony rules — phony-types.md, experiments/04
- [x] Sketch: hermetic lockOf/outOf/runOf fed from patterns, doc only — hermetic-sketch.md
- [x] How the provider reuses `PatternAst` — ast-reuse.md, experiments/05 (nupkg layout proven)
- [ ] Decide: phony type name/semantics (5 questions, phony-types.md) and AST-reuse questions (ast-reuse.md)
- [ ] Check IDE support (Ionide/Rider), a net462 consumer, a compiled project consuming the nupkg
- [ ] Separate, found by reading: `ExecCore.applyWildcards` edge bugs (empty group value, `)` in a value); literal `(` in patterns/project root
- [ ] Separate task, not this branch: explicit outputs (roadmap "Rules with arguments")
