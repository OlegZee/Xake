# vars-precedence: tracker

- [x] Analysis of var / -d precedence, setup, banner (`analysis.md`)
- [x] B: `setup` op (accumulating), CLI and engine mode, tests, docs: #101
- [x] C: run banner at Info, secrets masked, tests, docs: #101
- [x] A: owner's design decisions P1-P11 (2026-10-28)
- [x] A: spec `spec.md` (numbered rules, diagnostics, 33 conformance tests)
- [ ] A: owner approval of `spec.md` (gaps in section 10, open questions Q-1..Q-3)
- [ ] PR 1: core `VarStore` + dependencies + db 0.7 + banner sources + diagnostics
- [ ] PR 2: Xake.Dotnet `DotNetVars`, readers through declarations, `HERMETIC=on` by source
- [ ] PR 3: options precedence (CLI over `rootdir`/`consolelog`/`filelog`)
- [ ] PR 4: Xake.Hermetic.Dotnet `HermeticVars`, Xake `[3.14, 3.15)`
- [ ] Release note 3.14 (spec V-3), docs (`var` vs `-d`, library variables)
