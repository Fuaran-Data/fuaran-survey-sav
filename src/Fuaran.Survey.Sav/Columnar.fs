// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.Survey.Sav

open System
open System.Buffers.Binary
open System.Collections.Generic
open System.IO
open System.IO.MemoryMappedFiles
open System.Text
open System.Text.Json

// ============================================================================
//  The columnar store.
//
//  A dependency-free format of this package's own (DECISIONS.md D2): under one
//  directory, `manifest.json` plus, per variable, a typed little-endian column
//  file and a validity bitmap (one bit per case, bit set = the value is
//  present). Numeric columns are stored as the narrowest integer type that
//  holds every present value exactly, else as doubles; string columns as an
//  Int32 index into a sorted UTF-8 string table. The writer is deterministic:
//  two writes of one file are byte-identical.
//
//  Layout of the files:
//    col-NNNNN.bin    Int8 | Int16 | Int32 | Float64 values, or Int32 string-table indexes
//    col-NNNNN.valid  ceil(cases / 8) bytes; case i is bit (i % 8) of byte (i / 8)
//    col-NNNNN.dict   int32 count, int32 offsets[count + 1], then the UTF-8 bytes
//  Invalid (system-missing) cases hold a zero in the column file.
// ============================================================================

/// How a column's values are stored on disk.
type Storage =
    | Int8
    | Int16
    | Int32
    | Float64
    | Utf8Dictionary

type ColumnInfo =
    { Name: string
      Position: int
      Kind: Kind
      Storage: Storage
      DataFile: string
      ValidityFile: string
      DictionaryFile: string option
      DictionarySize: int }

type Manifest =
    { Format: string
      Version: int
      SourceFingerprint: string
      Cases: int
      Columns: ColumnInfo list }

/// One memory-mapped column. Reads are bounds-checked; out-of-range cases read as invalid.
type Column
    internal
    (
        info: ColumnInfo,
        cases: int,
        data: (MemoryMappedFile * MemoryMappedViewAccessor) option,
        valid: (MemoryMappedFile * MemoryMappedViewAccessor) option,
        dictionary: string[]
    ) =

    let dataView = data |> Option.map snd
    let validView = valid |> Option.map snd

    let scratch = Array.zeroCreate<byte> 8

    let readInt16 (i: int) =
        match dataView with
        | Some v ->
            if BitConverter.IsLittleEndian then
                v.ReadInt16(int64 i * 2L)
            else
                v.ReadArray(int64 i * 2L, scratch, 0, 2) |> ignore
                BinaryPrimitives.ReadInt16LittleEndian(ReadOnlySpan(scratch, 0, 2))
        | None -> 0s

    let readInt32 (i: int) =
        match dataView with
        | Some v ->
            if BitConverter.IsLittleEndian then
                v.ReadInt32(int64 i * 4L)
            else
                v.ReadArray(int64 i * 4L, scratch, 0, 4) |> ignore
                BinaryPrimitives.ReadInt32LittleEndian(ReadOnlySpan(scratch, 0, 4))
        | None -> 0

    let readDouble (i: int) =
        match dataView with
        | Some v ->
            if BitConverter.IsLittleEndian then
                v.ReadDouble(int64 i * 8L)
            else
                v.ReadArray(int64 i * 8L, scratch, 0, 8) |> ignore
                BinaryPrimitives.ReadDoubleLittleEndian(ReadOnlySpan(scratch, 0, 8))
        | None -> 0.0

    member _.Info = info
    member _.Name = info.Name
    member _.Kind = info.Kind
    member _.Storage = info.Storage
    member _.Count = cases
    /// The string table of a Utf8Dictionary column (empty for numeric columns).
    member _.Dictionary = dictionary

    /// Whether case `i` holds a value.
    member _.IsValid(i: int) =
        if i < 0 || i >= cases then
            false
        else
            match validView with
            | Some v -> (v.ReadByte(int64 (i >>> 3)) >>> (i &&& 7)) &&& 1uy = 1uy
            | None -> false

    /// The string-table index of case `i` (Utf8Dictionary columns; 0 when invalid).
    member this.Index(i: int) =
        if info.Storage = Utf8Dictionary && this.IsValid i then
            readInt32 i
        else
            0

    /// The numeric value of case `i`; NaN when invalid or not a numeric column.
    member this.Float(i: int) : float =
        if not (this.IsValid i) then
            nan
        else
            match info.Storage with
            | Int8 -> float (dataView.Value.ReadSByte(int64 i))
            | Int16 -> float (readInt16 i)
            | Int32 -> float (readInt32 i)
            | Float64 -> readDouble i
            | Utf8Dictionary -> nan

    member this.TryFloat(i: int) : float option =
        if info.Storage <> Utf8Dictionary && this.IsValid i then
            Some(this.Float i)
        else
            None

    /// The string value of case `i`; None when invalid or not a string column.
    member this.String(i: int) : string option =
        if info.Storage = Utf8Dictionary && this.IsValid i then
            Some dictionary[readInt32 i]
        else
            None

    interface IDisposable with
        member _.Dispose() =
            for f, v in Option.toList data @ Option.toList valid do
                v.Dispose()
                f.Dispose()

/// An open columnar store. Columns are mapped on first use and closed on Dispose.
type ColumnStore internal (dir: string, manifest: Manifest, openColumn: ColumnInfo -> Column) =

    let infos =
        let d = Dictionary<string, ColumnInfo>(StringComparer.OrdinalIgnoreCase)

        for c in manifest.Columns do
            d[c.Name] <- c

        d

    let opened = Dictionary<string, Column>(StringComparer.OrdinalIgnoreCase)

    member _.Directory = dir
    member _.Manifest = manifest

    /// The column of this name (case-insensitive).
    member _.Column(name: string) : Column =
        match opened.TryGetValue name with
        | true, c -> c
        | _ ->
            match infos.TryGetValue name with
            | true, info ->
                let c = openColumn info
                opened[info.Name] <- c
                c
            | _ -> raise (KeyNotFoundException(sprintf "No column named %A in the store at %s." name dir))

    /// The named columns, in the order given.
    member this.Project(names: string seq) : Column[] =
        names |> Seq.map this.Column |> Array.ofSeq

    interface IDisposable with
        member _.Dispose() =
            for c in opened.Values do
                (c :> IDisposable).Dispose()

            opened.Clear()

module Columnar =

    [<Literal>]
    let FormatName = "fuaran-survey-sav-columnar"

    [<Literal>]
    let FormatVersion = 1

    /// Columns written per pass over the source file. Bounds the number of open files.
    let private batchSize = 400

    let private storageName =
        function
        | Int8 -> "Int8"
        | Int16 -> "Int16"
        | Int32 -> "Int32"
        | Float64 -> "Float64"
        | Utf8Dictionary -> "Utf8Dictionary"

    let private storageOf =
        function
        | "Int8" -> Int8
        | "Int16" -> Int16
        | "Int32" -> Int32
        | "Float64" -> Float64
        | "Utf8Dictionary" -> Utf8Dictionary
        | s -> failwithf "Unknown column storage %A." s

    let private kindName =
        function
        | Kind.Numeric -> "Numeric"
        | Kind.String -> "String"
        | Kind.Date -> "Date"
        | Kind.DateTime -> "DateTime"
        | Kind.Duration -> "Duration"

    let private kindOf =
        function
        | "Numeric" -> Kind.Numeric
        | "String" -> Kind.String
        | "Date" -> Kind.Date
        | "DateTime" -> Kind.DateTime
        | "Duration" -> Kind.Duration
        | s -> failwithf "Unknown column kind %A." s

    let private fileStem (position: int) = sprintf "col-%05d" position

    // ------------------------------------------------------------------------
    //  Manifest
    // ------------------------------------------------------------------------

    let private writeManifest (path: string) (m: Manifest) =
        use ms = new MemoryStream()

        let options = JsonWriterOptions(Indented = true, NewLine = "\n")

        (use w = new Utf8JsonWriter(ms, options)
         w.WriteStartObject()
         w.WriteString("format", m.Format)
         w.WriteNumber("version", m.Version)
         w.WriteString("sourceFingerprint", m.SourceFingerprint)
         w.WriteNumber("cases", m.Cases)
         w.WriteStartArray "columns"

         for c in m.Columns do
             w.WriteStartObject()
             w.WriteString("name", c.Name)
             w.WriteNumber("position", c.Position)
             w.WriteString("kind", kindName c.Kind)
             w.WriteString("storage", storageName c.Storage)
             w.WriteString("data", c.DataFile)
             w.WriteString("validity", c.ValidityFile)

             match c.DictionaryFile with
             | Some f ->
                 w.WriteString("dictionary", f)
                 w.WriteNumber("dictionarySize", c.DictionarySize)
             | None -> ()

             w.WriteEndObject()

         w.WriteEndArray()
         w.WriteEndObject())

        ms.WriteByte 10uy
        File.WriteAllBytes(path, ms.ToArray())

    /// Reads a manifest.json.
    let readManifest (path: string) : Manifest =
        use doc = JsonDocument.Parse(File.ReadAllBytes path)
        let root = doc.RootElement

        let optString (e: JsonElement) (name: string) =
            match e.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
            | _ -> None

        { Format = root.GetProperty("format").GetString()
          Version = root.GetProperty("version").GetInt32()
          SourceFingerprint = root.GetProperty("sourceFingerprint").GetString()
          Cases = root.GetProperty("cases").GetInt32()
          Columns =
            [ for c in root.GetProperty("columns").EnumerateArray() do
                  { Name = c.GetProperty("name").GetString()
                    Position = c.GetProperty("position").GetInt32()
                    Kind = kindOf (c.GetProperty("kind").GetString())
                    Storage = storageOf (c.GetProperty("storage").GetString())
                    DataFile = c.GetProperty("data").GetString()
                    ValidityFile = c.GetProperty("validity").GetString()
                    DictionaryFile = optString c "dictionary"
                    DictionarySize =
                      match c.TryGetProperty "dictionarySize" with
                      | true, v -> v.GetInt32()
                      | _ -> 0 } ] }

    // ------------------------------------------------------------------------
    //  Writing
    // ------------------------------------------------------------------------

    /// Per-variable facts from the typing pass.
    type private Stats =
        { mutable AllIntegral: bool
          mutable Min: float
          mutable Max: float
          Strings: HashSet<string> }

    let private chooseStorage (v: Variable) (s: Stats) =
        if v.Width > 0 then
            Utf8Dictionary
        elif
            not s.AllIntegral
            || s.Min < float Int32.MinValue
            || s.Max > float Int32.MaxValue
        then
            Float64
        elif s.Min >= -128.0 && s.Max <= 127.0 then
            Int8
        elif s.Min >= float Int16.MinValue && s.Max <= float Int16.MaxValue then
            Int16
        else
            Int32

    let private writeDictionary (path: string) (strings: string[]) =
        use fs =
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 <<< 16)

        let b = Array.zeroCreate<byte> 4
        let encoded = strings |> Array.map Encoding.UTF8.GetBytes
        BinaryPrimitives.WriteInt32LittleEndian(Span b, strings.Length)
        fs.Write(b, 0, 4)
        let mutable ofs = 0

        for e in encoded do
            BinaryPrimitives.WriteInt32LittleEndian(Span b, ofs)
            fs.Write(b, 0, 4)
            ofs <- ofs + e.Length

        BinaryPrimitives.WriteInt32LittleEndian(Span b, ofs)
        fs.Write(b, 0, 4)

        for e in encoded do
            fs.Write(e, 0, e.Length)

    let private readDictionary (path: string) =
        let bytes = File.ReadAllBytes path

        if bytes.Length < 4 then
            [||]
        else
            let count = BinaryPrimitives.ReadInt32LittleEndian(ReadOnlySpan(bytes, 0, 4))

            let offsets =
                Array.init (count + 1) (fun k ->
                    BinaryPrimitives.ReadInt32LittleEndian(ReadOnlySpan(bytes, 4 + 4 * k, 4)))

            let blob = 4 + 4 * (count + 1)

            Array.init count (fun k -> Encoding.UTF8.GetString(bytes, blob + offsets[k], offsets[k + 1] - offsets[k]))

    /// Writes the file's cases as a columnar store under `dir` (created if absent). The file's
    /// cases are consumed by the typing pass; the writing passes reopen it by path.
    let write (file: SavFile) (dir: string) : Manifest =
        Directory.CreateDirectory dir |> ignore
        let vars = file.Dictionary.Variables

        // Pass 1: type every column.
        let stats =
            vars
            |> Array.map (fun _ ->
                { AllIntegral = true
                  Min = Double.PositiveInfinity
                  Max = Double.NegativeInfinity
                  Strings = HashSet<string>(StringComparer.Ordinal) })

        let cases, ending =
            SavFile.scan file (fun _ values ->
                for i in 0 .. values.Length - 1 do
                    match values[i] with
                    | RNum(Some x) ->
                        let s = stats[i]

                        if s.AllIntegral && not (Double.IsFinite x && Math.Floor x = x) then
                            s.AllIntegral <- false

                        if x < s.Min then
                            s.Min <- x

                        if x > s.Max then
                            s.Max <- x
                    | RNum None -> ()
                    | RStr s -> stats[i].Strings.Add s |> ignore)

        match ending with
        | Truncated -> failwithf "%s: the case data is truncated; nothing was written." file.Path
        | Complete
        | ShortOfHeader -> ()

        let storages = Array.map2 chooseStorage vars stats

        let dictionaries =
            stats
            |> Array.map (fun s ->
                let arr = Array.ofSeq s.Strings
                Array.sortInPlaceWith (fun (a: string) b -> String.CompareOrdinal(a, b)) arr
                arr)

        let indexes =
            dictionaries
            |> Array.map (fun d ->
                let m = Dictionary<string, int>(StringComparer.Ordinal)
                d |> Array.iteri (fun k s -> m[s] <- k)
                m)

        let infos =
            vars
            |> Array.mapi (fun i v ->
                let stem = fileStem i

                { Name = v.Name
                  Position = i
                  Kind = v.Kind
                  Storage = storages[i]
                  DataFile = stem + ".bin"
                  ValidityFile = stem + ".valid"
                  DictionaryFile =
                    (if storages[i] = Utf8Dictionary then
                         Some(stem + ".dict")
                     else
                         None)
                  DictionarySize =
                    (if storages[i] = Utf8Dictionary then
                         dictionaries[i].Length
                     else
                         0) })

        // Pass 2 (in batches): write the column files.
        let validityBytes = (cases + 7) / 8

        for batchStart in 0..batchSize .. max 0 (vars.Length - 1) do
            let batch = [| batchStart .. min (vars.Length - 1) (batchStart + batchSize - 1) |]
            use reopened = SavFile.openFile file.Path file.EncodingOverride

            let streams =
                batch
                |> Array.map (fun i ->
                    new BufferedStream(
                        new FileStream(
                            Path.Combine(dir, infos[i].DataFile),
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.None,
                            1 <<< 12
                        ),
                        1 <<< 16
                    ))

            let validity = batch |> Array.map (fun _ -> Array.zeroCreate<byte> validityBytes)
            let b = Array.zeroCreate<byte> 8

            let written, _ =
                SavFile.scan reopened (fun row values ->
                    for k in 0 .. batch.Length - 1 do
                        let i = batch[k]
                        let out = streams[k]

                        let present =
                            match values[i], storages[i] with
                            | RNum None, _ -> false
                            | RNum(Some x), Int8 ->
                                out.WriteByte(byte (sbyte x))
                                true
                            | RNum(Some x), Int16 ->
                                BinaryPrimitives.WriteInt16LittleEndian(Span b, int16 x)
                                out.Write(b, 0, 2)
                                true
                            | RNum(Some x), Int32 ->
                                BinaryPrimitives.WriteInt32LittleEndian(Span b, int x)
                                out.Write(b, 0, 4)
                                true
                            | RNum(Some x), Float64 ->
                                BinaryPrimitives.WriteDoubleLittleEndian(Span b, x)
                                out.Write(b, 0, 8)
                                true
                            | RNum(Some _), Utf8Dictionary -> failwith "A numeric value met a string column."
                            | RStr s, Utf8Dictionary ->
                                BinaryPrimitives.WriteInt32LittleEndian(Span b, indexes[i][s])
                                out.Write(b, 0, 4)
                                true
                            | RStr _, _ -> failwith "A string value met a numeric column."

                        if present then
                            validity[k][row >>> 3] <- validity[k][row >>> 3] ||| (1uy <<< (row &&& 7))
                        else
                            // Keep the column rectangular: a zero of the storage width.
                            let width =
                                match storages[i] with
                                | Int8 -> 1
                                | Int16 -> 2
                                | Int32
                                | Utf8Dictionary -> 4
                                | Float64 -> 8

                            Array.Clear(b, 0, width)
                            out.Write(b, 0, width))

            for s in streams do
                s.Dispose()

            if written <> cases then
                failwithf "%s: the file changed between passes (%d cases, then %d)." file.Path cases written

            for k in 0 .. batch.Length - 1 do
                let i = batch[k]
                File.WriteAllBytes(Path.Combine(dir, infos[i].ValidityFile), validity[k])

                match infos[i].DictionaryFile with
                | Some f -> writeDictionary (Path.Combine(dir, f)) dictionaries[i]
                | None -> ()

        let manifest =
            { Format = FormatName
              Version = FormatVersion
              SourceFingerprint = file.Dictionary.Fingerprint
              Cases = cases
              Columns = List.ofArray infos }

        writeManifest (Path.Combine(dir, "manifest.json")) manifest
        manifest

    // ------------------------------------------------------------------------
    //  Reading
    // ------------------------------------------------------------------------

    let private mapFile (path: string) =
        if FileInfo(path).Length = 0L then
            None
        else
            let f =
                MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0L, MemoryMappedFileAccess.Read)

            let v = f.CreateViewAccessor(0L, 0L, MemoryMappedFileAccess.Read)
            Some(f, v)

    /// Opens a store written by `write`. Columns are memory-mapped on first access.
    let openStore (dir: string) : ColumnStore =
        let manifest = readManifest (Path.Combine(dir, "manifest.json"))

        if manifest.Format <> FormatName then
            failwithf "%s is not a %s store (format %A)." dir FormatName manifest.Format

        let openColumn (info: ColumnInfo) =
            let data = mapFile (Path.Combine(dir, info.DataFile))
            let valid = mapFile (Path.Combine(dir, info.ValidityFile))

            let dictionary =
                match info.DictionaryFile with
                | Some f -> readDictionary (Path.Combine(dir, f))
                | None -> [||]

            new Column(info, manifest.Cases, data, valid, dictionary)

        new ColumnStore(dir, manifest, openColumn)
