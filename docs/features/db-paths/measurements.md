# Measurements: db-paths (2026-11-17)

Synthetic databases from `DatabasePathsTests.fs` (`Database paths: size`.Generate): paths of 114 bytes (the measured
mean), each result half `FileDep`s (with an 8-byte stamp) and half one `GetFiles` list. macOS, Apple silicon,
net8.0, Release.

| database | 0.6 | 0.7 | ratio |
|---|---:|---:|---:|
| test scale: 60 results x 1 200 refs, 3 600 files in 350 dirs (`the 0.7 file is at least 5x smaller`) | 12.1 MB | 0.67 MB | 18.0x |
| full scale: 600 results x 1 200 refs, 36 000 files in 3 500 dirs (explicit `measure a real database`, no env) | 122.8 MB | 7.37 MB | 16.7x |

Load (open + close, read-only), full scale: 0.6 cold 814 ms, warm 781 ms; 0.7 warm 334 ms.

Caveat: the consumer's file has about 10 bytes per reference besides the path (list byte, tag, 8-byte stamp: critic
m1), more `FileDep`s than this mix; the critic's estimate for it is about 10.4 MB (8.5x). The real number comes from
the consumer running `measure a real database` with `XAKE_DB_MEASURE=<copy of .xake>` and
`XAKE_DB_MEASURE_ROOT=<its project root>` (Q-I).
