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
