# vars-precedence: tracker

- [x] Analysis of var / -d precedence, setup, banner (`analysis.md`)
- [x] B: `setup` op (accumulating), CLI and engine mode, tests, docs: #101
- [x] C: run banner at Info, secrets masked, tests, docs: #101
- [x] A: owner's design decisions P1-P11 (2026-10-28)
- [x] A: spec `spec.md` rev. 1 (numbered rules, diagnostics, 33 conformance tests), PR #102
- [x] A: spec rev. 2 after the owner's review (M-3, D-10, db stays 0.6, `SecretDep` without kind)
- [x] A: spec rev. 3 after the second review (`defaulted` flag, db 0.7, secrets via declarations, `.packages`, `ArgAndEnv`, `CSC_SERVER` pair, §8.1)
- [x] A: spec rev. 3 rejected; concept variants 1/2a/2b/2c (`concept.md`)
- [x] Merge dev (#103 CLI wins, #101); gap analysis `after-103.md`
- [x] A: owner's direction: variant 3 (`var` with a source list, libraries read vars only, banner shows real source), `concept.md` §8
- [x] A: spec rev. 4 for variant 3 (36 rules, 59 conformance tests)
- [x] A: owner answers on rev. 4 §10; Var.* untouched (final)
- [x] A: spec rev. 5 (45 rules, 58 conformance tests)
- [ ] A: owner approval of spec rev. 5 (§10: X-5 Var.arg secret via -d, L-4 blank NUGET_PACKAGES)
- [ ] PR 1: core `var` source-list overload, engine `?vars` on top, source per name, banner
- [ ] PR 2: Xake.Dotnet readers without env fallback, fixed defaults
- [ ] PR 3: Xake.Hermetic.Dotnet on Xake `[3.14, 3.15)` (readers without env fallback)
- [ ] Release note 3.14 (breaking: libraries no longer read the environment), docs (`var` vs `-d`, library variables)
