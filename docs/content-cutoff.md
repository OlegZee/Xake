# Content cut-off

From Xake 5.0 a file whose time moved but whose content did not is not a change. A target rebuilt
to the same bytes does not wake its dependents; a touched source, a checkout that only moves
times, or a restored `out/` folder costs no rebuild. It is on by default.

## How it decides

The database keeps one record per file: its last time and size, the hash of its content (when
known) and the run in which the content last changed. A target is up to date when every file and
target it depends on last changed no later than the run that built it.

- When a file's time or size moved and its hash is known, the file is hashed again. The same
  content: nothing changed (the record takes the new time). Other content: changed in this run.
- A file seen moved without a known hash is a change; it is hashed then, so its next move can be
  cut off. A source is therefore cut off from its second move on; nothing is hashed on a clean
  build or a fresh checkout.
- **Outputs** are hashed right after every run of their rule (all outputs of a rule with `also` or
  `*..>`). Before the rule runs, an output that has no hash yet and was not touched since it was
  recorded is hashed too, so cut-off works from the first rebuild.
- An output edited by hand is a change for its dependents; its rule does not rerun (as in 4.0).

## What is compared without building

Nothing is built to decide whether a target is up to date, except its static `needs`
([rule-header.md](rule-header.md)), which are built first:

- a static `needs`, and any dependee already built in this run or being built now (the target waits
  for it), are compared by content: a dependee rebuilt to the same bytes is no change;
- a dependee recorded from the body's `need` that has not been looked at in this run is not built
  to find out: if it is out of date itself, the target reruns, and its body builds it.

So to get cut-off through a generated file, name it in `needs`.

A phony target has no content: when it ran, its dependents rerun.

## Control

- `--content-cutoff=off` on the command line: the decisions of 4.0 (a moved time is a change), no
  hashing. The next run without it continues correctly (hashes of files moved meanwhile are
  dropped).
- `noContentCutoff` in a rule's header: its outputs are never hashed and every rebuild is a change
  for their dependents. Use it for large outputs that differ on every build anyway.

## Run summary

Every run ends with one line:

```
Run summary: targets run 3, skipped 120, tasks 3, cut-off 1, files hashed 4 (12 ms)
```

`cut-off` counts targets rebuilt with the same content. A script receives the counters with
`onSummary (fun s -> ...)` in `xakeScript`; an engine has `LastSummary`. A run where nothing moved
schedules no task (`tasks 0`).

## Limits

- Two different contents with the same time and size on a file system with coarse times: an
  output is hashed after every rule, so this is caught for outputs; for a source it is not seen, as
  in 4.0.
- One hash per file: a source edited and then restored (A, B, A) is a change for a target that did
  not run while it was B; that target reruns once.
- An output restored from a cache with fresh times is free only if it was hashed before (built at
  least once with cut-off on).
