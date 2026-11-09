# Fixtures

The SPSS files and expected renderings in this folder are **committed**: the test suite reads
them as they are, and the build never runs Python (DECISIONS.md D3).

| File | What it is |
|---|---|
| `plain-le.sav`, `plain-be.sav` | uncompressed, little- and big-endian |
| `bytecode-le.sav`, `bytecode-be.sav` | bytecode-compressed |
| `bytecode-end-le.sav`, `bytecode-end-be.sav` | bytecode-compressed, ending in the 252 end-of-data code |
| `zlib-le.zsav`, `zlib-be.zsav` | zlib-compressed, in 64-byte blocks so units straddle block boundaries |
| `expected.csv` | the four cases rendered as codes |
| `expected-labels.csv` | the four cases rendered with value labels, user-missing values blank |
| `expected-dictionary.json` | the dictionary facts the extension records carry |

All eight files hold the same dictionary and cases, written independently of the reader by
`gen_sav.py` from the PSPP "System File Format" appendix. The dictionary exercises a 300-byte very
long string split across two segments, long names, UTF-8 text, long string value labels and
missing values, a document record, display parameters, a variable set, file and variable
attributes with roles, multiple response sets of all three kinds and a 64-bit case count.

## Regenerating

```
pwsh ./tests/fixtures/regenerate.ps1      # or: pwsh ./run.ps1 -Regenerate
```

Needs Python 3 (standard library only). Review the result as a diff: a change to a fixture is a
change to what the reader is held to.
