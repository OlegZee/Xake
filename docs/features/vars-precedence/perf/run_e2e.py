#!/usr/bin/env python3
"""Measurement B driver: runs e2e.fsx once per variant per round, in separate processes.

Variants: none (no declarations), declared (5 Secret declarations, no secret line),
pct1 (5 Secret declarations, 1% of lines carry a secret). Rounds are interleaved
(none, declared, pct1, none, ...) so drift affects all variants alike.

Writes one line per run to results-e2e.txt next to this file and prints the summary.
Each run gets a fresh work directory under /tmp (the .xake database and build.log stay out of the repo).
"""
import os
import pathlib
import shutil
import statistics
import subprocess
import sys
import time

HERE = pathlib.Path(__file__).resolve().parent
ESCRIPT = HERE / "e2e.fsx"
WORK = pathlib.Path("/tmp/perf-secrets-e2e")
ROUNDS = 7
VARIANTS = [("none", "0"), ("declared", "2"), ("pct1", "1")]

secrets = {}
for line in (HERE / "secrets.env").read_text().splitlines():
    if line.strip():
        key, value = line.split("=", 1)
        secrets[key] = value


def run(variant, mode, rnd):
    wd = WORK / variant
    if wd.exists():
        shutil.rmtree(wd)
    wd.mkdir(parents=True)
    env = dict(os.environ)
    env["PERF_PCT"] = mode
    if mode != "0":
        env.update(secrets)
    else:
        for key in secrets:
            env.pop(key, None)
    cmd = ["dotnet", "fsi", str(ESCRIPT), "--", "--", "main", "--resetdb"]
    t0 = time.perf_counter()
    proc = subprocess.run(cmd, cwd=wd, env=env, capture_output=True, text=True)
    wall = time.perf_counter() - t0
    if proc.returncode != 0:
        sys.exit(f"{variant} round {rnd}: exit {proc.returncode}\n{proc.stdout}\n{proc.stderr}")
    times = {}
    for l in proc.stdout.splitlines():
        for key in ("ELAPSED_ENQUEUE_MS", "ELAPSED_MS"):
            if l.startswith(key + "="):
                times[key] = float(l.split("=", 1)[1])
    if set(times) != {"ELAPSED_ENQUEUE_MS", "ELAPSED_MS"}:
        sys.exit(f"{variant} round {rnd}: missing timing lines\n{proc.stdout}")

    log = (wd / "build.log").read_text(encoding="utf-8").splitlines()
    # the variable banner prints secrets as *** too, so count the injected "token=***" lines only
    masked = sum(1 for l in log if "token=***" in l)
    leaked = sum(1 for l in log for v in secrets.values() if v in l)
    infos = sum(1 for l in log if "[INF]" in l)
    if infos < 10000:
        sys.exit(f"{variant} round {rnd}: only {infos} info lines on disk")
    if variant == "pct1" and (masked != 100 or leaked != 0):
        sys.exit(f"pct1 round {rnd}: masked={masked} leaked={leaked}")
    if variant != "pct1" and (masked != 0 or leaked != 0):
        sys.exit(f"{variant} round {rnd}: unexpected masked={masked} leaked={leaked}")
    return times["ELAPSED_ENQUEUE_MS"], times["ELAPSED_MS"], wall, len(log), masked, leaked


def main():
    if WORK.exists():
        shutil.rmtree(WORK)
    WORK.mkdir(parents=True)
    rows = []
    for rnd in range(1, ROUNDS + 1):
        for variant, mode in VARIANTS:
            enq, internal_ms, wall, nlines, masked, leaked = run(variant, mode, rnd)
            rows.append((variant, rnd, enq, internal_ms, wall, nlines, masked, leaked))
            print(f"{variant} round={rnd} enqueue_ms={enq:.1f} disk_ms={internal_ms:.1f} wall_s={wall:.2f} "
                  f"log_lines={nlines} masked_lines={masked} leaked={leaked}", flush=True)

    out = HERE / "results-e2e.txt"
    with out.open("w") as f:
        f.write("variant\tround\tenqueue_ms\tdisk_ms\twall_s\tlog_lines\tmasked_lines\tleaked\n")
        for r in rows:
            f.write(f"{r[0]}\t{r[1]}\t{r[2]:.3f}\t{r[3]:.3f}\t{r[4]:.3f}\t{r[5]}\t{r[6]}\t{r[7]}\n")
        f.write("\nvariant\tdisk_ms_median\tdisk_ms_min\tenqueue_ms_median\twall_s_median\twall_s_min\n")
        for variant, _ in VARIANTS:
            disk = [r[3] for r in rows if r[0] == variant]
            enq = [r[2] for r in rows if r[0] == variant]
            wall = [r[4] for r in rows if r[0] == variant]
            f.write(f"{variant}\t{statistics.median(disk):.1f}\t{min(disk):.1f}\t{statistics.median(enq):.1f}\t"
                    f"{statistics.median(wall):.2f}\t{min(wall):.2f}\n")
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
