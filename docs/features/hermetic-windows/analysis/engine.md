# Engine items: secret script variables, a build log per run

Analysis of two roadmap items (`docs/roadmap.md`, "Secret script variables" and "A build log per run"), checked
against the code on `feature/hermetic-windows` (after #89). Paths are relative to the repository root.

## 1. Secret script variables

### Root cause: confirmed, and wider than the roadmap says

How a var resolves: `Vars.resolveVar` (`src/core/Vars.fs:81-93`) calls `getVar` (CLI `-d`) and, if absent,
`getEnv`. Both record the value in clear: `getEnv` records `EnvVar (name, value)` (`src/core/ScriptFuncs.fs:98-103`),
`getVar` records `Var (name, value)` (`ScriptFuncs.fs:112-118`). For an `ArgAndEnv` var both records exist: `Var (name, None)`
and `EnvVar (name, Some token)` (covered by `src/tests/VarsTests.fs:469,488`). The DU cases are `src/core/Types.fs:54-55`.

Every place where the value is stored or printed:

| # | Where | What leaks | When |
|---|-------|-----------|------|
| 1 | `src/core/Database.fs:76-77` (pickler `pair str (option str)`) | the value in clear in `.xake` | every run that resolves the var |
| 2 | `src/core/DependencyAnalysis.fs:80-84` | old and new value in the change reason ("Environment variable X was changed from '..' to '..'", "Global script variable X was changed '..'->'..'") | value rotates |
| 2a | logged by `src/core/ExecCore.fs:373` at `Info` | reaches `build.log`: the default `ExecOptions` has `FileLog = "build.log"`, `FileLogLevel = Chatty` (`src/core/XakeScript.fs:74-75`), and Chatty keeps Info (`src/core/Logging.fs:46`) | value rotates, any script with default options |
| 2b | `src/core/ScriptRunner.fs:56-58` (dry run, `Reason: %s`) | the same text | `--dryrun` |
| 3 | `src/core/DependencyAnalysis.fs:187-193` (`--dump`) | the recorded value, and the new one if changed, via `printfn` to stdout | `--dump` |
| 4 | `src/core/Vars.fs:30` | "Failed to convert value '<value>' for variable ..." | a typed (`int`/`bool`) secret that does not parse |
| 5 | `src/core/Vars.fs:159` -> `XakeScriptBuilder.fs:25` | a `withDefault` value printed by `--help` | a secret with a default (unlikely, but possible) |
| 6 | `src/core/Tasks/Shell.fs:66` (`trace Debug "[shell] settings: '%A'" opts`) | the whole `ShellOptions`, incl. `EnvVars` and `Args` | Diag log; this repo's own `build.fsx:97` uses `filelog "build.log" Diag` |
| 7 | tool command lines (the `Xake.Dotnet` tools trace the full command line, `docs/dotnet-build.md:430`) | a secret passed as an argument or msbuild `-p:` property (`RecordOptions.Properties`) | Debug/Verbose |

Not a leak today: `-d` is never echoed (`src/core/Program.fs:50-51`), and the options dump explicitly drops vars
(`ScriptRunner.fs:124`, `{ options with Vars = [] }`). But a `-d` value is visible in the process list and shell history.

Corrections to the roadmap text:
- Leak 2 is not just "the build log when it rotates": with default options it is `build.log` at Chatty on every rotation,
  and leak 6 is the shell dump, which the roadmap does not mention (it is the easiest one to hit with `shell { envs }`).
- "Today it passes only `NUGET_PACKAGES`" (`src/hermetic/Import.fs:1643`, `src/hermetic/Project.fs:409`) is right for
  the explicit list, but `pexec` only *adds* to the inherited environment (`src/core/ProcessExec.fs:14-21`;
  `ProcessStartInfo.EnvironmentVariables` starts as a copy of the parent's). So an env-sourced secret already reaches msbuild;
  what is missing is (a) a `-d`-sourced secret reaching it, and (b) the dependency: msbuild reading an env var is not tracked.
  The "explicit, not inherited" principle is a convention, nothing enforces it (no clear-env option in `pexec`).
- `Project.import` has its own options (`Project.ImportOptions`, `src/hermetic/Project.fs:23`) and its own msbuild env
  (`Project.fs:409`); an `Environment` field is needed there too, not only on `Import.RecordOptions` (`Import.fs:742`).
- `DependencyAnalysis` sees only the recorded `Dependency`, not the var schema, so a secret must be recognisable from the
  record itself: that rules out "hash into the existing `EnvVar` value" without a marker.

### Design options

**A. New dependency case + engine-wide masking (recommended).**
- API: a flag on the var record (`IsSecret: bool` on `OptionalVar`/`RequiredVar`), set by `|> secret` (works on any
  var, composes with `required`/`withDefault`/`describe`) plus sugar `Var.secret<'t>(?envVar, ?description)` = env-only
  + secret. Low-level: `getSecretEnv name` / `getSecretVar name` next to `getEnv`/`getVar`.
- Record: `SecretDep of name: string * source: SecretSource (Env|Arg) * digest: SecretDigest` with
  `SecretDigest = Absent | Present | Hashed of salt: byte[] * hash: byte[]`. HMAC-SHA256(key = salt, value).
  Presence-only (`Present`/`Absent`) is a per-var opt-in (`secret` vs `secretPresence`, or a parameter).
- Salt: per record, 16 random bytes stored with the digest. No header change, no per-database salt file to lose or
  copy; two machines never produce comparable digests. (Per-database salt in the header is the alternative; it needs a
  header pickler change and makes the db the unit of secrecy for no gain.)
- Messages: `getDepState` for `SecretDep` says "Secret variable X was changed" / "was set" / "was unset", never a value;
  `--dump` prints `***`. `--help` prints name, type, env and description, and `default: ***` if any.
- Masking registry: each resolved secret value (length >= 4) is added to a per-run set in the context; the loggers
  (console and file) replace occurrences with `***` before writing (as CI systems mask). This covers leaks 4, 6, 7 and any
  tool that echoes its environment or arguments, which the typed record alone cannot.
- Database: append case tag 7 to the `dependency` pickler (`Database.fs:65-82`), bump `XakeDbVersion` to `"0.6"`;
  0.4/0.5 files stay readable (their pickles are a subset), are upgraded on open as today (`Database.fs:152-170`).
  An older Xake meeting a 0.6 file fails to unpickle tag 7 and recreates the db (warning + full rebuild): acceptable.
- `-d` for a secret: refused by default ("secret X is read from the environment only"); `Var.secret` is `EnvOnly`.
  An `|> secret` on an `ArgAndEnv` var keeps CLI lookup (the author chose it) but its `Var` record becomes a `SecretDep`.
- Propagation: `shell { envs }` unchanged (the value is explicit already), the masking registry covers its dump;
  add `Environment: (string * string) list` to `Import.RecordOptions` and `Project.ImportOptions`, appended after
  `NUGET_PACKAGES` into `runMsbuild`. Recommend documenting "pass secrets by environment, never as `-p:`/`Properties`".

**B. Hash inside the existing cases.** Store `EnvVar (name, Some "\u0001hmac:<salt>:<hash>")` and teach `getDepState`
the prefix. No format change, but it is an in-band marker in a string, an old Xake would report "changed" on every
run (it compares the marker to the clear value) and print the marker; the format bump in A is cheap, so B is not worth it.

**C. Masking only.** Keep recording values, mask in loggers. Fixes the log, not the database (leak 1), which is the
main concern for a checked-in CI cache or a shared workspace. Not enough alone; it is the second half of A.

### Effort, risks, tests

Effort: **M, about 3-4 days** (core 2-2.5: DU case, pickler + 0.6, `getDepState`/`dumpDeps`, `secret`, help, masking
in loggers and the shell dump; hermetic 0.5: the two `Environment` fields; docs 0.5: `docs/tasks.md` vars section,
`docs/cheatsheet.md:76`, `docs/hermetic/description.md:132` table).

Risks:
- Adding a field to the public `OptionalVar`/`RequiredVar` records is a binary break for code compiled against 3.11 that
  builds them by record expression; Xake.Hermetic.Dotnet pins `[3.11, 3.12)`, so this is a core minor (3.12) and a
  hermetic range bump. Alternative with no record change: keep secrets in a separate type `SecretVar<'t>` with its own
  `Bind` overloads (more code, no break). Decide with the owner.
- Masking by substring: short or common values would mask unrelated text (hence the minimum length); a value that the
  tool transforms (base64, URL-encoded) is not masked; masking costs a scan per line per secret (negligible for a few).
- Hash rebuilds on rotation by design; presence-only never does: a wrong token cached as "built" stays until `-B`.
- The masking registry must be filled before the first message that could quote the value: resolve order in recipes is
  lazy, so a value logged by another recipe before the secret var's recipe resolves it is not masked (rare: the value
  has to come from somewhere other than the var).

Tests to add (`src/tests/VarsTests.fs`, `StorageTests.fs`):
- a secret env var records `SecretDep`, not `EnvVar`; the `.xake` bytes do not contain the value;
- rotating the value rebuilds (hash) / does not rebuild (presence); unset->set rebuilds in both;
- the change reason and `--dump` output contain the name and no value;
- a 0.5 database with `EnvVar` records loads, upgrades to 0.6, and the next run is not a spurious rebuild of unrelated targets;
- `shell { envs ["ACME_API_TOKEN", v] }` at Diag: the log file contains `***`, not the value;
- `-d ACME_API_TOKEN=...` on a `Var.secret` fails with a clear message; `--help` lists the secret without a value;
- `Import.RecordOptions.Environment` reaches msbuild (an msbuild property reading `$(LICENSE_KEY)` in a fixture project).

### Open questions (suggested default in brackets)

1. Default digest: salted hash (rebuild on rotation) or presence-only? [hash; presence-only is opt-in]
2. API shape: `|> secret` flag on the existing records (3.12, binary break) or a separate `SecretVar<'t>`? [flag, ship as 3.12]
3. `-d` for a secret: refuse, warn, or allow? [refuse for `Var.secret`; allow with a warning for `|> secret` on an arg var]
4. Engine-wide log masking of resolved secret values: in scope now or later? [now, it is the part that covers tools]
5. Minimum length for masking? [4 characters]
6. Should a non-secret `getEnv` of a name already declared secret be masked/hashed too? [yes: the name decides]

## 2. A build log per run

### Root cause: not confirmed

The roadmap says `filelog` appends across runs. It does not: `FileLogger` deletes the file when it is created
(`src/core/Logging.fs:86`, `System.IO.File.Delete name`, unchanged since 2018) and only then appends line by line
(`Logging.fs:55-61`, `File.AppendAllLines` per message from a `MailboxProcessor`). `FileLogger` is created once per
`xake {}` run in `ScriptRunner.toEngineOptions` (`src/core/ScriptRunner.fs:7-12`). So one `dotnet fsi build.fsx` = one
fresh `build.log`; deleting it in a CI step first is redundant. Nothing in the repo relies on append: no test reads a log
across runs (`src/tests/XakeScriptTests.fs:575`, `FlatScriptTests.fs:120`, `DotnetTasksTests.fs:31` only set a name),
CI uploads `build.log` from one script per job (`.github/workflows/build.yml:31-37`, `:57-63`), no doc mentions append.
If the consumer saw a growing file, the likely source is something else writing it (a `captureOutput (ToFileAppend ...)`
sink, `Shell.fs:85-87`, or a CI wrapper), worth one look at the consumer script before any change.

Real defects found next to it:
- **Lost tail on failure.** Messages are posted to the mailbox asynchronously, nothing flushes it, and a failed build
  ends with `exit exitCode` (`ScriptRunner.fs:189`): the last lines ("Error ...", "Build failed after ...") can be
  missing from `build.log` exactly when it is needed. Same for Ctrl+C.
- **Two runs, one file.** Two `xake {}` runs in one process (tests do this; a script may run `xake` twice) or two
  scripts in one folder at once: the second `File.Delete` removes the first's log mid-run, and both mailboxes append to
  one file interleaved. On Windows a concurrent open throws `IOException` inside the mailbox, which kills it silently:
  logging just stops.
- **Relative to the process directory**, not `ProjectRoot` (`-r`/`rootdir`): `File.Delete name` and `AppendAllLines`
  use the CWD.
- The failure message hard-codes "See build.log for more details" (`ScriptRunner.fs:161`) whatever `FileLog` is, also
  when file logging is off.
- Open/append/close per line is slow on Windows with antivirus at Diag (thousands of lines from msbuild).

### Design options

**A. Keep truncate-per-run, fix the writer (recommended).** One `StreamWriter` opened with `FileMode.Create`,
`FileShare.Read` at logger creation (the truncate point stays "start of `xake {}`"); a mailbox (or a lock) writes lines
and flushes per message or per batch; `runScript` disposes the file logger in its `finally` before `exit`, so the tail is
written. A second run in the same process or folder that cannot open the file falls back to `build.<pid>.log` with a
console warning instead of corrupting the first. Resolve the name against `ProjectRoot`? (see questions). Use the actual
`FileLog` in the failure message. Not breaking.

**B. A: plus rotation.** `filelog "build.log" Chatty` keeps `build.1.log`..`build.N.log` (rename at start, N default 0
= off, `-fln N` / `ExecOptions.FileLogKeep`). Useful for local "what did the previous run say"; cheap (~0.5 day).

**C. Opt-in `append`.** `filelog ... append` (or `FileLogAppend = true`) for anyone who wants the history in one file.
Only meaningful if someone asks; adds a record field.

The roadmap's "start anew by default, `append` for the old behaviour" is already the current default minus the option,
so there is no breaking change to make.

### Effort, risks, tests

Effort: **S, about 1 day** for A (+0.5 for B rotation). Risks: changing the writer changes when lines hit the disk
(flush per line keeps `tail -f` working); the fallback name must not surprise CI artifact paths (only used on conflict).
Tests: a failed build whose last line ("Build failed") is in the file; two `xake` runs in one process with the same
`filelog` name leave two complete logs (or one complete + fallback), never interleaved; a second run truncates the first's
content; rotation keeps N files; `rootdir` + relative `filelog` lands where the docs say.

### Open questions (suggested default in brackets)

1. Can the consumer's growing log be traced to its own script (a `ToFileAppend` capture or the CI step) before we change
   the engine? [yes, check first; the engine already truncates]
2. Is a relative `filelog` path resolved against the process directory or `ProjectRoot`? [keep the process directory;
   document it; changing it breaks CI artifact paths]
3. Rotation (`build.1.log`) wanted now? [no, add when asked]
4. `append` option wanted at all? [no]
5. On a name conflict between concurrent runs: fallback file or fail? [fallback `build.<pid>.log` with a warning]
