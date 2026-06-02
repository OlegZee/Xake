# Ideas

## Rule-level combinators via `mapRecipe`

`mapRecipe` transforms the recipe inside any `Rule` variant, enabling composable
`Rule -> Rule` decorators piped with `|>`. Works uniformly with `=>`, `target`, `command`.

### Shipped

- `requiring resource` — wraps recipe in a resource lock (1 unit, CPU-slot yielding).

### Planned

- **`onError`** — rule-level error handler. Wraps recipe in `tryWithF`. Possible signatures:
  - `onError (exn -> unit)` — log/swallow, rule returns unit
  - `onError (exn -> Recipe<ExecContext, unit>)` — recovery recipe (retry, fallback)
  - `retryOn n` — retry the recipe up to N times on failure

### Possible future combinators

- `withTimeout duration` — cancel recipe after a deadline
- `logged label` — trace start/end/duration around the recipe
- Combinators compose naturally: `"deploy:*" => recipe { ... } |> requiring lock |> retryOn 3`
