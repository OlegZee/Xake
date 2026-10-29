# vars-precedence: brief

The decisions in force (owner, 2026-10-28..29). Do not re-litigate. The spec ([spec.md](spec.md))
formalizes them; rejected designs and their reasons are in [archive/](archive/README.md).

## Principle

Every variable value is computed at launch, before the first target runs. A recipe only reads.
A read records one dependency on the final value, `Var (name, value)`, never on its source.
`ExecOptions.Vars` (input values) and the declarations (`var [...]` rules, `Var.*` in `varschema`)
are separate concepts.

## The chain (first defined wins)

engine per-demand `?vars` > `-d` (the last `-d` of a name wins) > the script's rule or constant >
the rule's own sources in list order. Already on dev: `-d` over `var`/`ExecOptions.Vars`, CLI options
over ops (#103). To do: per-demand `?vars` on top (today below the script's values).

## Declarations

- **`var "N" [sources]`**, an overload beside `var "N" "v"`. Sources: `Env "X"`, `Default "v"`;
  secrecy is a separate list element, `Secret` (presence) or `SecretHashed` (HMAC).
  Example: `var "NUGET_PACKAGES" [Env "XX_NUGET_PACKAGES"; Default ".packages"]`.
- **`varschema` imports a `Var.*` declaration completely** and evaluates it eagerly at declaration,
  the same way as a source list: `Var.string(envVar = "X") |> withDefault "d"` ≡
  `var "N" [Env "X"; Default "d"]`. Its scope, required flag and secret mode are kept with the
  value; type conversion and a missing required value fail the launch.
- **A second definition of a name is an error at launch**: two rules, a constant and a rule, a
  `var` and an `ExecOptions.Vars` entry, a `varschema` field and a rule.
- **Secrets**: `-d` of a secret is an error at launch. `getVar`/`getEnv` of a secret return the real
  value; masking applies to output only; the dependency records presence or HMAC per mode.

## Reading

- **`Var.*` read in a recipe**: if a declaration with the same key is stored, and it equals the
  read's declaration field by field, the value is taken from the set, converted and returned; the
  read's own rules (env, default) are ignored. One `Var (name, value)` dependency, no `getEnv`.
- **Same key, different declaration**: error.
- **No stored declaration**: error ("declare it in `varschema`, or use `getVar`/`getEnv`"). It fires
  at read time; an inline read is invisible at launch.
- **`getVar`** reads the set as is. **`getEnv`** is the one explicit way to read the environment
  independently, recording `EnvVar`.
- `Var` thus narrows to a typed view of the variable set plus `--help` text.

## Libraries (Xake.Dotnet, Xake.Hermetic.Dotnet)

- Read only the variable set; no implicit environment fallback; an unset name takes the library's
  fixed default. Breaking: a CI that set a library variable only in the environment adds a `var`
  rule or `-d`.
- An invalid switch value is an error from any source (`CSC_SERVER=bogus` included); an empty
  switch value means its default (`CI=` is off).
- `NUGET_PACKAGES` defaults to `<project root>/.packages`. `HERMETIC=on` accepts it from any source.

## Output

- The banner lists each name once with its real source: `cli`, `request`, `script`, `env:<NAME>`,
  `default`; secrets as `***`. `--help` lists `varschema` fields and `var [...]` rules.
- The debug dump of options shows the variables with secrets masked.

## Constraints

- `ExecContext` and six records the hermetic package builds are frozen until 4.0 (release-process
  decision B): no new fields. Declarations and sources live in `ExecOptions` / `EngineOptions`
  (reachable from a recipe as `ctx.Engine.Options`).
- No database format change.
- Out of scope: `Var.int` canonical form, the process-wide secrets registry by name.
