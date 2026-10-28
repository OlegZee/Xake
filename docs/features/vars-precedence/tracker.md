# vars-precedence: tracker

- [x] Analysis of var / -d precedence, setup, banner (`analysis.md`)
- [x] B: `setup` op (accumulating), CLI and engine mode, tests, docs: #101
- [x] C: run banner at Info, secrets masked, tests, docs: #101
- [x] A: owner's design decisions P1-P11 (2026-10-28)
- [x] A: spec `spec.md` rev. 1 (numbered rules, diagnostics, 33 conformance tests), PR #102
- [x] A: spec rev. 2 after the owner's review (M-3, D-10, db stays 0.6, `SecretDep` without kind)
- [x] A: spec rev. 3 after the second review (`defaulted` flag, db 0.7, secrets via declarations, `.packages`, `ArgAndEnv`, `CSC_SERVER` pair, §8.1)
- [ ] A: owner approval of `spec.md` (gaps in §10, open question Q-5)
- [ ] PR 1: core `VarStore` + `Var (name, value, defaulted)` + db 0.7 + secrets via declarations + banner sources + diagnostics
- [ ] PR 2: Xake.Dotnet `DotNetVars` (`ArgAndEnv`, `CSC_SERVER` pair, `.packages`), readers through declarations, `HERMETIC=on` by source
- [ ] PR 3: options precedence (CLI over `rootdir`/`consolelog`/`filelog`)
- [ ] PR 4: Xake.Hermetic.Dotnet `HermeticVars`, Xake `[3.14, 3.15)`
- [ ] Release note 3.14 (spec V-3), docs (`var` vs `-d`, library variables)
