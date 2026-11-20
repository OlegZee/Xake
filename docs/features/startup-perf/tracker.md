# Startup performance: tracker

- [x] Copy the consumer's databases outside the repository (5.0 12.5 MB, 4.x 88 MB)
- [x] Q2: split the load (harness): the path table is quadratic (`Map.Count`), 1.2-2.0 s of 1.3-1.8 s
- [x] Q2: prototype fix A (counters) and C (id array): toy 1.47 s -> 0.66-0.71 s (A), 0.50-0.53 s (A + C)
- [x] Q2: compare 4.0.0 on the 88 MB database: 0.37-0.42 s to the first recipe
- [x] Q3: contents by kind and epoch; compaction rule (results > 5 x live) not due, history only
- [x] Q1: Stopwatch marks on the bench: banner -> first recipe ~20 ms; the toy reproduces +1.07 s, all in `openDb`
- [x] Q1: the consumer's measurement points: `XAKE build tool` -> first flush of `info`; the load is inside on both
- [x] Q1: the consumer's run with the marks build: `database open` 2.72 s at 17.3 MB, the rest ms; Q1 = Q2
- [ ] Owner: choose among A, C, B1-B3
