#!/bin/sh
# Experiment 09: the probes, once per provider switch (each run executes every case):
#   output-overload.txt  XAKE09_RULE_OVERLOAD=1 (the tuple lambda as a second `Rule` overload)
#   output-ap.txt        XAKE09_AP=1 (a provided static method named `|Groups|`)
here=$(cd "$(dirname "$0")" && pwd)
XAKE09_RULE_OVERLOAD=1 OUT=output-overload.txt sh "$here/../run.sh" "$here/probes.fsx"
XAKE09_AP=1 OUT=output-ap.txt sh "$here/../run.sh" "$here/probes.fsx"
