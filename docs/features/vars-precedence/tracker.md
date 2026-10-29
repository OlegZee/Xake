# vars-precedence: tracker

Done before 2026-10-29 (see [archive](archive/README.md)): analysis; `setup` op and run
banner (#101); CLI over the script's ops (#103); spec rev. 1-5 (archived).

- [x] Variant fixed by the owner; `brief.md`; rejected designs archived (2026-10-29)
- [x] Spec rev. 6 from the brief (43 rules, 10 diagnostics, 60 conformance tests)
- [x] Owner approval of spec rev. 6 (2026-10-29; amended: evaluation at launch, in createScriptContext)
- [x] Owner approval of the PR split (PR 1 core, PR 2 libraries), 2026-10-29
- [x] Archive shortened to one README (idea, key details, good, why rejected)
- [ ] PR 1 (in progress, `feature/vars-core`): core — names (key = internal name, `cliArg`/`Cli` = -d name), `var` source lists, `varschema` evaluated at launch, `Var` read by stored declaration, errors, `?vars` on top, banner sources, debug dump
- [ ] PR 2: optional import of library declarations (schema records, read by key and type); library readers without environment fallback (Xake.Dotnet, Xake.Hermetic.Dotnet), switch values, `.packages`
- [ ] Release notes (breaking: undeclared `Var` read, libraries no longer read the environment), docs
