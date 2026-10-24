# Tracker: typed-matches

- [x] Inventory of current rule-match API and real usages — research.md
- [x] Design options (type provider, non-TP alternatives) with before/after code — research.md
- [x] Type provider feasibility (packaging, fsi, IDE, .NET 8 floor) — research.md
- [x] Path generation from a pattern (inverse of matching): usages, typed Make/Format, edge cases — research.md
- [x] Generating path lists for `need` (map over values, cross products, partial application, link to filesets) — research.md
- [x] Prototypes — experiments/
- [x] Discussion with the user, decisions — brief.md (2026-10-25)
- [x] Merge `dev` (pattern AST #89) into the branch
- [ ] Estimate the cost of separate pattern types for phony rules (brief, decision 5)
- [ ] Sketch: hermetic lockOf/outOf/runOf fed from patterns, doc only (brief, decision 9)
- [ ] How the provider reuses `PatternAst` (design-time dll vs `src/core/Path.fs`)
- [ ] Check IDE support (Ionide/Rider) and packing the design-time dll into the Xake nupkg
- [ ] Separate task, not this branch: explicit outputs (roadmap "Rules with arguments")
