#!/bin/sh
# Each case puts a header operation where it must not compile; prints the compiler's first line.
cd "$(dirname "$0")"
neg() { name=$1; body=$2
  printf '#load "Header.fsx"\nopen Xake\nopen Header\nlet r = target'"'"' "out/(n:*).dll" {\n%s\n}\n' "$body" > _neg_$name.fsx
  printf '%-18s ' "$name"; dotnet fsi --nologo _neg_$name.fsx 2>&1 | grep -m1 -E "error|header" | sed 's|.*fsx([0-9,]*): ||'; rm -f _neg_$name.fsx; }
neg after_letbang '    let! m = getRuleMatches ()
    also "out/(n:*).pdb"'
neg after_dobang '    do! need ["a"]
    also "out/(n:*).pdb"'
neg after_let '    let x = 1
    also "out/(n:*).pdb"'
neg after_if '    if true then printfn "x"
    also "out/(n:*).pdb"'
neg after_match '    match 1 with _ -> ()
    also "out/(n:*).pdb"'
neg after_for '    for f in ["x"] do do! need [f]
    also "out/(n:*).pdb"'
neg in_for '    for f in ["x"] do
        also f'
neg after_return '    return ()
    also "a"'
neg after_use '    use d = { new System.IDisposable with member _.Dispose() = () }
    also "a"'
neg need_as_header '    need ["b"]
    needs "a"'
neg statement_first '    printfn "runs at registration"
    also "out/(n:*).pdb"'
