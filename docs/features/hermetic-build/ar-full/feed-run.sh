#!/bin/bash
# Usage: feed-run.sh <logname> <dotnet args...>   (runs in ar-net-meta, env from .env, no secrets printed)
set -a; source /Users/olegz/Projects-work/.env; set +a
cd /Users/olegz/Projects-work/ar/ar-net-meta || exit 2
L=/Users/olegz/Projects-work/ar/xake-ar/full/logs
name=$1; shift
s=$(date +%s)
dotnet "$@" > "$L/$name.log" 2>&1
rc=$?
echo "exit $rc wall $(( $(date +%s)-s ))s" | tee -a "$L/$name.log"
