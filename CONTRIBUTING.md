# Contributing

Thanks for your interest. The surface is still moving, and the fastest way to have a change land
is to open an issue describing the problem before writing code.

## Ground rules

- **Follow the PSPP appendix.** The "System File Format" appendix of the PSPP developer manual is
  the reference for every record layout. Where a file in the wild disagrees with it, tolerate the
  file and say so in a comment; do not change the layout the reader expects without a source.
- **Nothing is dropped.** The raw dictionary keeps every record, unknown extension subtypes
  included. A new typed field is added on top of the raw record, never instead of it.
- **No new dependencies.** `FSharp.Core` at runtime, Expecto in tests. This is a hard boundary.
- **The store stays deterministic.** Two writes of one file must remain byte-identical; the
  manifest has a fixed key order and no timestamps.
- **No private names.** Nothing in this repository names a client, a survey or a product other
  than this one. Fixtures are synthetic.

## Before you open a pull request

```
pwsh ./run.ps1
```

That runs the whole gate: `dotnet tool restore`, Fantomas over `src` and `tests`, the build, and
the Expecto suite. All three must be clean.

- **Fantomas is not optional.** Run it inside the repo so it picks up `.editorconfig`.
- **Changing a fixture** means changing `tests/fixtures/gen_sav.py`, running
  `pwsh ./run.ps1 -Regenerate`, and committing the regenerated files with the change that needed
  them. Say in the pull request what the fixture now exercises.
- **New behaviour needs a test that can fail.** Where practical, confirm the test goes red against
  the unfixed code before relying on it going green.

## Licence

By contributing you agree that your contributions are licensed under Apache-2.0, matching the
project's [LICENSE](LICENSE).
