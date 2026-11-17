# Content cut-off (Xake 5.0)

When a file a target depends on has a new time but the same content as before, the target does not
rerun. That is content cut-off: a source touched without an edit, a control file rewritten with the
value it had, a deterministic compiler that rebuilt a library with the same bytes after a
comment-only edit upstream.

It is on by default. `--content-cutoff=off` on the command line turns it off for one run: the run
then makes the same decisions as Xake 4.0 (a moved time is a change). It is a safety switch, to
compare a build with 4.0's decisions or to fall back if cut-off ever misbehaves.

## What is compared

- A target records the **time and size** of every file it depended on, as 4.0 recorded the time.
- Xake keeps, for each such file, a short **history** of `(time, size) -> content hash` (the last 8).
- A dependency is **unchanged** when its time and size are the ones the target saw; otherwise when
  the history has a hash for what the target saw and the file's current content has the same hash.
  So a file that went A -> B -> A does not rerun a target built from A.

## Hashes are lazy

Nothing is hashed on a clean build or on a run where nothing moved. A file is hashed:

- a **source** when its time moves (from its second move on, cut-off works for it);
- an **output** of a rule just before the rule overwrites it (if it is still what the rule wrote) and
  just after, so cut-off works from the first rebuild.

A file whose time moved during hashing is treated as changed. The run summary counts the files
hashed and the time spent (`Run summary: ... files hashed 3 (12 ms)`), and every target rebuilt
with the same content (`cut-off 1`); each one is logged at Info: `cut-off: out/A.dll rebuilt with
the same content`.

## What reruns, and what does not

- **Static inputs (`needs` in the rule header) are compared by content:** they are brought up to date
  first, then compared. For cut-off through a rule's output, name it in `needs`.
- **A rule's output needed in the body** (`do! need [..]`) is not built to decide the target: when it
  is out of date, the target reruns, as in 4.0. When it is up to date (or was already built in this
  run) it is compared by content.
- **An output changed outside the build** (edited by hand, restored from a cache): when Xake has its
  hash and the content is the same, nothing reruns; otherwise the rule reruns. A rule that rebuilds
  the bytes its dependents saw does not rerun them.
- An output whose rule has `noContentCutoff` is never hashed: every rebuild reruns its dependents.
- **Transitional:** an output that was built once and never rebuilt has no hash yet, so restoring it
  with a new time (an `out/` cache) reruns its rule once; after that its hash is known.
- An output that another target modifies in place (signing, patching) differs from what its rule
  wrote, so the rule reruns every time: give the modified file its own name.
- Phony targets have no content: they compare by their completion time, as in 4.0.

## Database

The histories and the size of each dependency are kept in the build database (`.xake`, version 0.7).
A 4.0 database loads: dependencies recorded without a size compare by time alone, so the first run
after the upgrade reruns nothing.
