#!/bin/sh
# Experiment 09: gathers the per-folder outputs into output.txt, each under the command that made it.
# Full rerun (about 10 min):
#   sh make-engine.sh; (cd designtime && dotnet build -c Release); (cd generative/provider && dotnet build -c Release)
#   (cd plain && sh ../run.sh cases.fsx; FSIARGS=--langversion:preview OUT=output-preview.txt sh ../run.sh cases.fsx; sh run-ce.sh)
#   (cd generative && sh ../run.sh cases.fsx); (cd consumer && sh ../run.sh cases.fsx); sh probes/run.sh
#   sh collect.sh
here=$(cd "$(dirname "$0")" && pwd)
out="$here/output.txt"
section() { printf '\n######## %s\n######## %s\n' "$1" "$2" >> "$out"; grep -v '^   at ' "$here/$3" >> "$out"; }
echo "Experiment 09 outputs. SDK 10 = dotnet fsi (10.0.402), SDK 8 = dotnet sdk/8.0.426/FSharp/fsi.dll." > "$out"
section "build" "sh make-engine.sh; cd designtime; dotnet build -c Release; cd ../generative/provider; dotnet build -c Release" build-output.txt
section "1. plain F#: record punning, partial records, anonymous records, .NET classes, Deconstruct, static active patterns, union fields, tuples" "cd plain; sh ../run.sh cases.fsx" plain/output.txt
section "1. the same with --langversion:preview" "cd plain; FSIARGS=--langversion:preview OUT=output-preview.txt sh ../run.sh cases.fsx" plain/output-preview.txt
section "1. a generative provided type in record patterns" "cd generative; sh ../run.sh cases.fsx" generative/output.txt
section "2d. computation expressions: a custom operation with \`into\`" "cd plain; sh run-ce.sh" plain/output-ce.txt
section "2. the forms on real rules (Target<>/Command<> of 08 + the 09 additions)" "cd consumer; sh ../run.sh cases.fsx" consumer/output.txt
section "2e. probe: the tuple lambda as a second \`Rule\` overload" "sh probes/run.sh  (XAKE09_RULE_OVERLOAD=1)" probes/output-overload.txt
section "2c. probe: a provided static method named \`|Parts|\`" "sh probes/run.sh  (XAKE09_AP=1)" probes/output-ap.txt
echo "output.txt: $(wc -l < "$out") lines"
