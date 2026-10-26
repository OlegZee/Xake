# Interview: decisions on the Windows remarks (2026-10-26)

Numbers refer to brief.md §4. Answers are the owner's; "default" means the owner accepted the
suggested default without discussion. This file is authoritative over brief.md.

## Round 1: lock portability (PR1)

1. Lock format: **no bump**. Record normalized hashes, accept a match on the normalized or the raw hash; format-2 locks recorded on macOS/Linux work on Windows without a re-import.
2. `App.Files` and the project's own `App.config`: **included in the same change**.
3. `<lock>.files/.gitattributes` written by the import: no (default); mention `.gitattributes` in the docs.
4. Packs: **NuGet by default** (`RecordOptions.PacksFrom = NuGet | Installed`).
5. `Installed` stays as an opt-in for offline imports: yes.
6. Separate pack version pin: no (default); the exact `global.json` pin is the mechanism.
29. Packaging: **PR1 (a+b) ships now**, one re-import at the consumer; satellites later with a second re-import.

## Round 2: import diagnostics (PR2)

7. Classifier: **in the core**, `Tool.diagnosticLevel`, Xake 3.12 (all runners); hermetic adopts it when it moves to `[3.12, 3.13)`.
8. Import warnings: **Info**, deduplicated.
9. Failure message: first 5 distinct errors with their project, then "+N more, see the log" (default).
10. `dotnet restore` runners (`Restore`/`Described`, and the core's): same classifier and summary, **same PR**.
11. `runMsbuild` runs in `ProjectRoot`: **yes**.
12. Floor check: `dotnet msbuild -version` in hermetic **now**; the probe + `MSBuild.deps.json` with the next core release.
13. Confirm the 17.10 / SDK 8.0.300 floor with one run on an 8.0.2xx SDK before release: yes (default).

## Round 3: secrets (PR3) and the build log (PR4)

14. Digest in `.xake`: **presence only** by default (a rotated token rebuilds nothing); the salted hash is the opt-in.
15. API: **flag** on the existing var records (`|> secret`, `Var.secret`), ships as Xake 3.12; hermetic moves to `[3.12, 3.13)`.
16. `-d NAME=value`: **refused** for `Var.secret`; a warning for `|> secret` on an argument var.
17. Log masking engine-wide: **now** (change messages, `--dryrun`, `--dump`, the `shell { envs }` Debug dump, conversion errors, `--help` defaults).
18. Minimum length for masking: 4 characters (default).
19. A plain `getEnv` of a name declared secret: treated as secret too, the name decides (default).
20-24. Build log (f): **not touched now**. The roadmap claim was wrong (the file already starts fresh per run); the consumer's growth is to be traced in its own script or CI first. The nearby defects (lost tail, name clash, hard-coded "See build.log") stay in the brief as a backlog item. PR4 dropped.
