# Brief: `let! m = matched` in every recipe

Decided by the owner on 2026-11-19, after the research in [research.md](research.md) (prefix
operators, an ambient context, `m?n`, names from lambda parameters: all dropped).

## API

```fsharp
"out/(cfg:*)/(n:*).dll" ..> recipe {
    needs "obj/(n:*).rsp"              // a header still comes first
    let! m = matched
    do! need [ "src/" + m["cfg"] + "/" + m["n"] + ".cs" ]
}
```

- `matched` (the marker `Matched`) moves from `Xake.Typed` to `Xake`. `let! m = matched` works in
  `recipe { }` and in every builder derived from it (`target`, `targets`, `command`), in file and
  phony rules alike.
- In a plain recipe `m` is a `RuleMatch`; in `X.rule { }` it stays the typed record `'G`
  (the typed builders' own `Bind` wins over the general one).
- `RuleMatch`:
  - `m["n"]` / `m.["n"]`: the value of the group; **strict**: an unknown name fails the task with
    `no group 'x' in the rule match (groups: cfg, n)` (numbered groups are not listed).
  - `m.TryFind "n" : string option`.
  - `m.Names : string list`: the named groups.
- `m?n`: the same strict lookup as `m["n"]` (added later, owner 2026-11-19, branch
  `feature/match-strict-op`); a static member of `RuleMatch`, so no global `?` is claimed.
- `getRuleMatches ()` (the map) is unchanged; `getRuleMatch "n"` (`""` when absent) is obsolete.
- Outside a rule (a recipe run with no match) `matched` gives an empty `RuleMatch`.

## Breaking

- `Xake.Typed.Matched` is now `Xake.Matched`; `matched` in a plain recipe compiles (it was an error,
  FS0041, on purpose: the typed-neg case `matched-outside-a-rule` goes).
