#!/bin/sh
# Nested use of a headed recipe, against Xake built with recipe-header.patch applied:
#   git worktree add --detach <dir> 1837856 && git -C <dir> apply recipe-header.patch
#   dotnet build <dir>/src/core -c Release
#   XAKE_DLL=<dir>/src/core/bin/Release/netstandard2.0/Xake.dll ./nested.sh
cd "$(dirname "$0")"
: "${XAKE_DLL:?set XAKE_DLL}"
hdr="#r \"$XAKE_DLL\"
open Xake
let headed : Headed<ExecContext> = recipe { needs \"obj/x.rsp\"; do! need [\"a\"] }
let helper () = recipe { needs \"obj/x.rsp\"; do! need [\"a\"] }"
t() { name=$1; shift; printf '%s\n%s\nprintfn "COMPILED"\n' "$hdr" "$*" > _n_$name.fsx
  printf '%-18s ' $name; dotnet fsi --nologo _n_$name.fsx 2>&1 | grep -E "error|COMPILED" | head -1 | sed 's|.*fsx([0-9,]*): ||' | cut -c1-100; rm -f _n_$name.fsx; }
t do_bang 'let r : Recipe<ExecContext,unit> = recipe { do! headed }'
t let_bang 'let r : Recipe<ExecContext,unit> = recipe { let! x = headed in () }'
t return_bang 'let r : Recipe<ExecContext,unit> = recipe { return! headed }'
t match_bang 'let r : Recipe<ExecContext,unit> = recipe { match! headed with () -> () }'
t in_for 'let r : Recipe<ExecContext,unit> = recipe { for x in [1] do do! headed }'
t via_helper 'let r : Recipe<ExecContext,unit> = recipe { do! helper () }'
t typed_param 'let run (body: Recipe<ExecContext,unit>) = recipe { do! body }
let r = run headed'
t inferred_param 'let run body = recipe { do! body }
let r = run headed'
t statement 'let r : Recipe<ExecContext,unit> = recipe { headed; do! need ["b"] }'
t statement_last 'let r : Recipe<ExecContext,unit> = recipe { do! need ["b"]; headed }'
t if_then 'let r : Recipe<ExecContext,unit> = recipe { if true then headed }'
t withResource 'let l = Resource.newResource "l" 1
let r : Recipe<ExecContext,unit> = recipe { do! withResource l 1 headed }'
t ignore_explicit 'let r : Recipe<ExecContext,unit> = recipe { ignore headed; do! need ["b"] }'
for f in include include_late; do sed "s|__XAKE_DLL__|$XAKE_DLL|" $f.fsx > _n_$f.fsx; printf '%-18s ' $f; dotnet fsi --nologo _n_$f.fsx 2>&1 | grep -E "error|COMPILED" | head -1 | sed 's|.*fsx([0-9,]*): ||' | cut -c1-100; rm -f _n_$f.fsx; done
