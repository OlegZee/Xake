# Critic: plan for database 0.7 (compact, relocatable paths)

Fresh-eyes review of [plan.md](plan.md) against [brief.md](brief.md) (Decisions and Acceptance), the dep-cutoff
brief (Part 3, "Database (0.7)", Step 3) and the sources at `fe19b82`. Decisions Q1-Q6 are taken as settled; where a
finding touches one, it is a consequence or a gap, not a re-argument. Nothing was run; every claim about the sources
was read from the file and line given.

The plan's source facts 1-6 hold (`ExecCore.fs:383` and `:79`, `ScriptRunner.fs:34-37`, `DependencyAnalysis.fs:171`,
`File.fs:5-13` vs `Types.fs:6`, `Fileset.fs:14`). The size estimate is credible (m1). The problems are in what
happens to the path table when something goes wrong, and in what "relocatable" silently does not cover.

## Critical

### C1. A failed read leaves a half-loaded table and a deleted file: the database corrupts itself on the next run

`Database.fs:164-181`: when the read loop throws, the file is deleted but `db` keeps every record read so far, and
the run continues with a fresh, header-only file (`:206-208`). Today that is merely lossy (records live in memory
for one run). With 0.7 the agent would also keep the path table read so far. Its `Store` then skips `DirRec`/`FileRec`
for "known" paths, so the new file holds `ResultRec`s naming ids that no record in it defines. The next open fails on
an unknown id, deletes the file again, keeps a partial table again. The database never recovers while builds touch
old paths, and a torn tail record (plan: "a failed `Write` may still leave a partial tail record, as today") is
enough to start it. The writer is also never flushed before `Close` (`:263`, `:275`), so a killed process tears the
tail routinely.

Proposal, one of:
- (a) on any read failure discard both the records and the table (true "recreate"), or
- (b) recommended: salvage. Frame each record with a length (varint, about 40 KB on the measured file), remember the
  offset of the last whole record, and on a failure rewrite the survivors through the compaction path (fresh table).
  A torn tail then costs one record, not an 11 MB database of a build that takes hours.
Either way add the test: a file truncated mid-record, then open, store, close, reopen: no "Failed to read database"
on the second open.

### C2. Tentative ids are reused after a failed `Write`

Plan, "The database agent": "A failure in pickling or writing commits nothing". Right for pickling (nothing reached
the file). Wrong for `dbwriter.Write` (`Database.fs:263`): a write that throws may have put some or all of the buffer
on disk. The next `Store` hands the same ids to possibly different paths, so the file can hold `FileRec 50 = A` and
`FileRec 50 = B`. If load takes the last one, an earlier record that meant A now reads B: a wrong dependency, silently.

Proposal: never reuse an id. `nextId` advances when ids are handed out, whether or not the store succeeds; only the
path -> id entries wait for the successful write. On load a duplicate id is a corrupt file (C1 path), never "last
wins". After a failed `Write` the agent should also stop appending (the file's tail is unknown) and count further
stores as unsaved. Test with an injected failing stream; the plan tests only the pickling failure.

## Major

### M1. Two ids for one path is a normal state, and the plan assumes it cannot happen

The table is "`Map<string, int>` ... keyed by full path", ids "unique", interning "the same object". But resolution
on load makes aliases:
- run 1 declares `$(EXT)`, a file is `$(EXT)/a.dll` (id 10); run 2 has the variable unset, the same file is interned
  under `/` (id 20); run 3 declares it again: 10 and 20 are the same path. Q3 + Q4 make this routine.
- relocation onto a path that the file already names absolutely; two named roots with the same value; a root moved
  under another one.

Proposal: specify it. Load tolerates aliases (no throw from the map); the path -> id map prefers the id under the
longest root of this run; equal paths share one `File` object. Launch check: two roots with the same value is an
error, or a defined tie-break (a root equal to `ProjectRoot` loses to it).

### M2. A path's root is frozen when it is first interned

`Store` reuses the id of any path already in the table. A file interned under `/` (root not declared yet, variable
unset that day, or the 0.6 upgrade ran in such a run) stays absolute after the root is declared: its records are not
relocatable, and nothing says so. Compaction would fix it, but `recordCount > Count * 5` (`Database.fs:193`) may
never fire (the brief notes it does not on the measured file).

Proposal: at load, count ids whose root is not the longest matching root of this run; if any, rewrite (same branch
as compaction). It is one pass over the table and makes "declare a root, next run is relocatable" true.

### M3. Relocation acceptance has holes the plan does not close or state

"Copy the working copy ... nothing is rebuilt" fails, by the sources, in these cases:
- **Sources above `ProjectRoot`.** A script that lives in a subfolder (`ProjectRoot` = `repo/build`, sources in
  `../src`) has every source under `/`: moving the repository rebuilds everything. The cure exists (Q2: a relative
  root `".."`), but the plan neither documents it as the standard recipe nor tests a relative root with `..`.
- **`GetFiles` with an absolute `BaseDir`** (Q5, accepted): `DependencyAnalysis.fs:106-108` re-evaluates the recorded
  fileset; an absolute `BaseDir` still points at the old place, the loaded list resolves to the new one, the lists
  differ, the rule reruns. Also a *relative* `BaseDir` is used as given (`Fileset.fs:54`), i.e. against the process
  directory, not `ProjectRoot`. The docs must say: relocation holds for filesets without `BaseDir` or with one
  relative to the directory the build is started from.
- **Variables holding paths** (plan fact 6): `Var`/`EnvVar` compare by value (`DependencyAnalysis.fs:97-101`). Any
  rule reading an output directory or a tool path variable reruns. State it in the docs and keep the acceptance
  test's script free of such variables on purpose, with a second test that shows the rerun.
- **Paths spelled through a symlink or another case.** No symlink resolution and an ordinal prefix on Unix:
  on macOS a path given as `/tmp/x/..` or `/var/..` against a `ProjectRoot` of `/private/..`, or with other letter
  case, falls under `/`. Correct, not relocatable, invisible. This will also bite the relocation test itself
  (`Path.GetTempPath()` vs the current directory on macOS).

Proposal: add to the Info line `<n> files outside all roots` (and list a few at Verbose/`--dump`), so "why did the
move rebuild everything" has an answer; document the four cases; add the `..` root test.

### M4. Root errors are reported after the database was opened and possibly rewritten

`ScriptRunner.createScriptContext` (`:28-39`) opens the database before the caller looks at `launchErrors`
(`:195`, `XakeScriptBuilder.fs:360`). If root checks are "a launch error like the variable errors", an invalid root
set still reaches `openDb`, which may upgrade or compact, and by Q4 a rewrite drops every record under a root that
is "undeclared" only because its declaration was wrong. The same for `--dump` and `--dryrun`: they open, upgrade and
compact too.

Proposal: validate roots before `openDb`; with launch errors open no database (`noopDb`). Decide whether a
read-only mode (`--dump`, `--dryrun`) may drop stale records at all (see Q-B).

### M5. Q4 + compaction turns a one-off unset variable into permanent loss

Stale records are skipped on load (fine, they come back when the root does), but upgrade, compaction and the
secret scrub rewrite only loaded records. One run with the variable unset that happens to compact deletes all
records under that root; the next proper run rebuilds them. With `fromVar` roots (Q3) this is a developer forgetting
an environment variable once.

Proposal: say it in the docs and log it at Warning, not Info (`<k> records under undeclared root $(X) dropped`); or
make the rewrite carry unresolved records over untouched (raw bytes are available if records are length-framed, C1).
Owner's call (Q-A).

### M6. One version string, two record bodies: tag 2 changes meaning between the two merges

Plan: "`ResultRec` ... body changes in dep-cutoff step 3", "whichever feature merges second rebases", "no skipping
... is needed". A 0.7 file written by a build with only the first feature is then misparsed by a build with both:
same header, same tag, different body. Best case the read throws and the database is recreated (with C1 on top);
worst case it parses. Acceptance of this feature is a measurement on a real database, so such interim files will
exist.

Proposal, inside Q6 (still one 0.7): a record body never changes under its tag. This feature's result is tag 2; the
result with content data takes its own tag from 4-15 and tag 2 is read as "no content data, first seen". That is
what the tag byte is for, and it makes the merge order irrelevant.

### M7. The `Store` flow and the compaction trigger do not fit dep-cutoff's source table

- dep-cutoff writes source entries outside any result ("written to the table before any dependent decides", "saved
  with the run's results") and updates output records without a rebuild ("the record takes the new stamp", a hash
  attached later). The plan ties path-table commits to `Store result`. Proposal: the agent step is "append a batch
  of records plus the path records they need"; `Store` is one caller, a source entry another.
- The threshold counts `ResultRec`s only. `SourceRec`s (one per move of a source) and dead `FileRec`s (renamed
  sources, generated names, package versions) grow without bound when results are few. Proposal: trigger on all
  records, e.g. total records > 5 x (live results + live files + live source entries); live files can be counted
  for free during unpickling (mark ids as they are resolved).
- "Epoch counter derived as `max + 1`" is unsafe after compaction prunes entries: a pruned id still recorded by a
  dependent can be issued again. Recommend the explicit `EpochRec` to dep-cutoff.

### M8. A separate "collect the files" pass will drift from the picklers

Plan: "collect the files of `result` not in the table ..." then pickle. Files sit in `Targets`, `FileDep`,
`ArtifactDep`, `ArtifactDep2`, `GetFiles` lists (`Database.fs:79-96`), and dep-cutoff adds more places. A collector
that misses one gives a pickler with no id for a file.

Proposal: no collector. The `file` pickler takes ids from the table, or hands out a tentative one and appends the
path to a pending list; the result goes to a second buffer; then pending `DirRec`/`FileRec` + result are written as
one `Write`. One place knows where files are. A lookup miss on unpickle (unknown id) is a corrupt file; an id under
an undeclared root is "stale"; keep the two apart in code and tests.

## Minor

- **m1. Size.** The numbers hold: 81.6 MB / 712 760 = 114 bytes per path; the remaining 7.0 MB is almost exactly
  10 bytes per reference (list byte, tag, 8-byte stamp). 0.7 is then about 7.0 + 2.1 (varint ids) + 1.3 (tables) =
  10.4 MB, 8.5x. But the synthetic test proves only what its generator assumes: pin the mean path length to the
  measured 114 bytes and the reference mix, and also run the upgrade once on the real file (an `[<Explicit>]` test
  taking the path from an environment variable). After this feature the per-dependency stamps are over half the
  file; dep-cutoff could drop them (it records an epoch id and keeps the stamp "for diagnostics" only).
- **m2. Load time.** The path -> id map is needed only by `Store`; a no-change run never stores. Build it on the
  first `Store`, and use a mutable `Dictionary(StringComparer.Ordinal)` (the agent is the only owner) rather than an
  F# `Map` of 40 000 long strings with long common prefixes. Build a directory's full path once per `DirRec`.
- **m3. Root normalisation is underspecified.** `ProjectRoot` may be relative or carry a trailing separator
  (`XakeScriptBuilder.fs:47, 254`; `ExecTypes.fs:79`). State: `GetFullPath`, trailing separators trimmed except for
  a file-system root. The boundary test `root + separator` fails for the roots `/` and `C:\` (compare
  `Fingerprints.fs:80`, which trims first). Tests: `ProjectRoot` = `/`, a drive root, a file directly in a root, a
  root nested in another, a root equal to `ProjectRoot`, UNC with and without the trailing separator.
- **m4. `DirRec.name`**: say it is one path segment, joined with this system's separator on load. A file-system
  root that is not valid here (`C:\` read on Unix, a shared working copy) is an undeclared root (stale), not an
  exception.
- **m5. `RootRec` is inconsistent and mostly idle.** The table says `absolutePath`, "Roots" says a relative root
  records its relative text (then "moved" never fires for it; fine, but say so). It is written only on the first
  `Store`, so the relocation run itself (no stores) writes none and the message repeats every run. `$(ProjectRoot)`:
  not said whether it gets one; it is the main relocation case. A pending `RootRec` must survive a failed store.
  Note also that relocation depends only on the root's name and this run's value; "recorded as relative" changes
  the message, nothing else.
- **m6. Secret variable as a root** (`fromVar`): its value would go to `RootRec` and to the "moved" line in clear.
  Reject at launch. Also define an empty value and a relative value of the variable (resolve against `ProjectRoot`
  or an error).
- **m7. Windows.** Interning is ordinal while `Target` ignores case there (`Types.fs:6`): two spellings give two
  `FileRec`s, harmless but note it. Resolution rewrites the root part to this run's spelling, so a loaded name may
  differ in case from the one stored; `compareFileList` compares `FullName`s ordinally (`Fileset.fs:170-172`), fine
  as long as both lists come from a scan under the same `ProjectRoot` spelling; add a Windows-rule unit test.
- **m8. Downgrade.** Xake 4.0 opening a 0.7 file reads it with the 0.6 loop (`Database.fs:168-170` only checks the
  lower bound), fails or misreads, and deletes it. Worth one line in the release notes; a header check for
  "newer than I know" is a 4.0.x matter.
- **m9. Public surface.** `Storage.resultPU` is used by `SecretVarsTests.fs:185-188` and by
  `StaleArtifactDepTests.fs:18-23`, whose `OldDatabase` reads the *current* file and rewrites it as 0.4; it needs
  both the 0.7 reader and the kept 0.6 writer exposed to the tests. `Fileset.filelistPickler` is public
  (`Fileset.fs:118`) and changes shape or stays as the 0.6 one; list both under breaking changes.
- **m10. Stale rule, multi-target results.** "The targets it names that are resolvable are removed from the map":
  say what happens to a result with several targets when only some resolve (remove all resolvable ones, as
  written), and that an older live record of a co-target may remain; a test with a two-output rule.
- **m11. Tests missing from the plan:** truncated tail (C1); failed `Write` (C2); aliases (M1); root declared after
  the first build (M2); relative root with `..` (M3); unset `fromVar` then set again, with and without a compaction
  in between (M5); upgrade of a 0.6 file whose target names are not normalised (`ProjectRoot </> name` keeps `..`
  and mixed separators, `ExecCore.fs:383`); a `GetFiles` dependency in the relocation test; concurrency test with
  results that share new paths (the interesting interleaving), not only disjoint ones.
- **m12. Stamp-only validation after a root is re-pointed.** A declared root pointed at another folder is accepted
  when write times match within 10 ms (`DependencyAnalysis.fs:51`); extracted archives often carry the archive's
  times. Same weakness as 4.0 for a copied tree, closed by dep-cutoff's hashes; mention in the docs.

## Questions for the owner

- **Q-A (M5).** Records under an undeclared root at a rewrite (upgrade, compaction, scrub): drop them (Q4 as
  written, with a Warning) or carry them over unread?
- **Q-B (M4).** May `--dump` and `--dryrun` rewrite the database (upgrade, compaction, dropping stale records), or
  do they open it read-only in 5.0?
- **Q-C (C1).** Recovery from an unreadable tail: discard everything (simple) or length-framed records and salvage
  (recommended)? Framing costs 1-3 bytes per record and is a format decision, so it must be made before 0.7 ships.
- **Q-D (M6).** Agree that a record body never changes under its tag, so dep-cutoff's result is a new tag and the
  merge order does not matter?
- **Q-E (M1, m3).** Two roots with the same value, or a declared root equal to `ProjectRoot`: launch error or a
  fixed tie-break?
- **Q-F (M2).** Re-root on load when a path is not under its longest root (a rewrite at open), or leave it to the
  ordinary compaction and document it?
- **Q-G (M3).** Is the diagnostic `<n> files outside all roots` wanted in the Info line, and is the documented
  recipe for scripts below the repository root "declare a relative root `..`"?
- **Q-H (m6).** A secret variable as a root: rejected at launch?
- **Q-I (m1).** Who runs the size and load-time measurement on the real database, and is the synthetic test's
  ratio (with path length pinned to 114 bytes) accepted as the regression guard?
