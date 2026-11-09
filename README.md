# Fuaran.Survey.Sav

A reader for agency survey deliverables in SPSS system-file format, with a columnar store for
analysis.

`Fuaran.Survey.Sav` reads `.sav` and zlib-compressed `.zsav` files in either byte order and all
three compression modes (none, bytecode, zlib). It reads the dictionary **losslessly**, extension
records included: long names, very long strings, value labels (short and long string), missing
values (discrete, range, long string), measurement level and display parameters, variable roles,
file and variable attributes, variable sets, documents, multiple response sets of all three kinds,
the declared encoding and the 64-bit case count. Unknown extension subtypes are kept raw rather
than dropped. Cases are streamed in one pass. A columnar writer turns a file into a directory of
typed, memory-mappable columns.

F# 10 / .NET 10. `FSharp.Core` is the only runtime dependency. Apache-2.0.

The reader is re-homed from the author's MIT-licensed FSharpSAV script library; see
[DECISIONS.md](DECISIONS.md) D1. The format reference is the PSPP
["System File Format"](https://www.gnu.org/software/pspp/pspp-dev/html_node/System-File-Format.html)
appendix.

## Status

0.1.0, pre-release. The public surface may still move; a change to it advances the version in the
same commit.

## Building

```
pwsh ./run.ps1
```

That is the whole gate: `dotnet tool restore`, Fantomas over `src` and `tests`, the build (with
`FS0025` incomplete matches as errors in the library) and the Expecto suite. `verify.ps1` is a
pass-through to it. The test fixtures are committed, so nothing but the .NET SDK named in
`global.json` is needed; `pwsh ./run.ps1 -Regenerate` rewrites them first, which needs Python 3
(see [tests/fixtures/README.md](tests/fixtures/README.md)).

## Using the reader

```fsharp
open Fuaran.Survey.Sav

use file = SavFile.openFile "survey.sav" None       // Some "windows-1252" overrides the encoding
let d = file.Dictionary                              // typed, lossless
d.Variables |> Array.iter (fun v -> printfn "%s %A %s" v.Name v.Kind v.Label)
d.MrSets    |> List.iter (fun s -> printfn "%s %A %A" s.Name s.Kind s.Variables)

// One streaming pass over the cases: RawValue = RNum of float option | RStr of string.
let count, ending = SavFile.scan file (fun row values -> ())
match ending with
| Complete -> ()
| ShortOfHeader -> eprintfn "fewer cases than the header promised"
| Truncated -> eprintfn "data stops part-way through a case"
```

`file.Raw` is the undecoded dictionary (every record and extension as bytes) and `file.Header`
the 176-byte header's fields. `d.Fingerprint` is the SHA-256 of the dictionary bytes, so two
deliveries with the same dictionary can be recognised without reading their cases.

Rendering a value the way SPSS would show it:

```fsharp
let opts = { Labels = true; UserMissingBlank = true }
let render = Values.renderer opts d.Variables       // RawValue[] -> Cell[]
let line = Csv.line (render values |> Array.map Values.cellText)
```

`Csv.line` renders exactly as FSharpSAV's CSV writer did (RFC 4180 quoting, CR LF), which is what
the fixture tests hold it to.

## The columnar store

```fsharp
use file = SavFile.openFile "survey.sav" None
let manifest = Columnar.write file "survey.columns"    // consumes the file's cases

use store = Columnar.openStore "survey.columns"
let age = store.Column "age"                            // case-insensitive; memory-mapped on first use
for i in 0 .. age.Count - 1 do
    if age.IsValid i then printfn "%g" (age.Float i)
let cols = store.Project [ "gender"; "region" ]         // in the order asked
```

The store is a dependency-free format of this package's own (DECISIONS.md D2): a directory holding
`manifest.json` and, per variable, a column file and a validity bitmap. Everything is little-endian
and the writer is deterministic: two writes of one file are byte-identical.

| File | Contents |
|---|---|
| `manifest.json` | format, version, the source dictionary's fingerprint, the case count, and per column: name, position, kind, storage, file names, string-table size. Fixed key order, LF line ends, no timestamps. |
| `col-NNNNN.bin` | the values: `Int8`, `Int16`, `Int32`, `Float64`, or `Int32` indexes into the string table |
| `col-NNNNN.valid` | one bit per case; case `i` is bit `i % 8` of byte `i / 8`; set = a value is present |
| `col-NNNNN.dict` | strings only: `int32 count`, `int32 offsets[count + 1]`, then the UTF-8 bytes, in ordinal order |

Storage is chosen from the data: a numeric column is `Int8` when every present value is integral
in [-128, 127], else `Int16`, else `Int32` when integral, else `Float64`; strings are always
`Utf8Dictionary`. A system-missing value is absent (validity bit 0, a zero in the column file).
User-missing values are present and left to the consumer, whose `Variable.Missing` says what they
are. An empty string is a present value.

The writer makes one typing pass over the file, then writes columns in batches of 400 with another
pass per batch, so peak memory is the per-column statistics, the string tables and one batch of
buffers, not the data.

## Licence

Apache-2.0; see [LICENSE](LICENSE) and [NOTICE](NOTICE).
