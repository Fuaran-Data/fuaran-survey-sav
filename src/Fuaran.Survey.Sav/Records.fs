// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.Survey.Sav

open System
open System.IO
open System.Text

// ============================================================================
//  The file header and the raw dictionary.
//
//  This layer reads the dictionary exactly as the file holds it and keeps all
//  of it: every variable record, every value-label set, the document lines and
//  every type-7 extension record as (subtype, size, count, bytes). Nothing is
//  decoded here because the text encoding is itself one of the extension
//  records; Dictionary.fs does the interpretation. Keeping the raw records
//  means an extension subtype this library does not understand is still
//  carried, and the dictionary's byte range is known so it can be hashed.
// ============================================================================

/// The 176-byte file header record.
type Header =
    { Zsav: bool // "$FL3": zlib-compressed case data
      Swap: bool // the file's byte order differs from the host's
      Compression: int // 0 none, 1 bytecode, 2 zlib
      NCases: int // -1 when the writer did not know
      WeightIndex: int // dictionary index (1-based unit index) of the weight variable; 0 = unweighted
      Bias: float // compression bias, ordinarily 100
      ProductName: string // "@(#) SPSS DATA FILE ..." (60 bytes, trimmed)
      CreationDate: string // "dd mmm yy"
      CreationTime: string // "hh:mm:ss"
      FileLabel: string // 64 bytes, trimmed; decoded as Latin-1 here, see Dictionary.FileLabel
      Raw: byte[] } // the header bytes themselves

/// One type-2 variable record. Continuation records (type -1) for strings wider than 8 bytes
/// are folded away; `Index` is the 1-based dictionary index value-label records refer to.
type RawVar =
    { ShortName: byte[]
      Width: int // 0 = numeric, else string width in bytes
      Label: byte[] option
      MissingCode: int // n_missing_values: >0 discrete, -2 range, -3 range plus a value
      MissingValues: byte[] list // 8 bytes each
      Print: int
      Write: int
      Index: int }

/// A type-7 extension record, kept whole.
type Extension =
    { Subtype: int
      Size: int
      Count: int
      Data: byte[] }

/// Everything the dictionary holds, undecoded.
type RawDictionary =
    {
        Variables: RawVar list
        /// Type 3/4 pairs: the dictionary indexes the labels apply to, and (value, label) bytes.
        LabelSets: (int[] * (byte[] * byte[])[]) list
        /// Type 6: each 80-byte document line, in order.
        Documents: byte[] list
        /// Every type-7 record in file order, unknown subtypes included.
        Extensions: Extension list
        /// Byte offset just past the type-999 record: the dictionary occupies [0, DictionaryEnd).
        DictionaryEnd: int64
    }

module Records =

    let private latin1 = Encoding.Latin1

    let private text (h: byte[]) (off: int) (len: int) =
        latin1.GetString(h, off, len).TrimEnd(' ', '\000')

    /// Reads the header from the start of the stream.
    let readHeader (s: Stream) =
        let h = Array.zeroCreate<byte> 176

        if Binary.readUpTo s h < 176 then
            failwith "File is too short to be an SPSS system file."

        let magic = Encoding.ASCII.GetString(h, 0, 4)

        if magic <> "$FL2" && magic <> "$FL3" then
            failwithf "Not an SPSS system file (starts %A, expected $FL2 or $FL3)." magic

        // The layout code is 2 or 3; read in host order, anything else means the file is the
        // other order.
        let layout = BitConverter.ToInt32(h, 64)
        let swap = not (layout = 2 || layout = 3)

        let fix (b: byte[]) =
            if swap then
                Array.Reverse b

            b

        let i32 off =
            BitConverter.ToInt32(fix h[off .. off + 3], 0)

        let f64 off =
            BitConverter.ToDouble(fix h[off .. off + 7], 0)

        if swap && (let l = i32 64 in l <> 2 && l <> 3) then
            failwith "Unrecognised layout code in header: the file looks corrupt."

        { Zsav = (magic = "$FL3")
          Swap = swap
          Compression = i32 72
          NCases = i32 80
          WeightIndex = i32 76
          Bias = f64 84
          ProductName = text h 4 60
          CreationDate = text h 92 9
          CreationTime = text h 101 8
          FileLabel = text h 109 64
          Raw = h }

    /// Reads every dictionary record up to and including the type-999 terminator. The stream
    /// is left positioned at the first byte of case data.
    let readDictionary (src: Src) =
        let vars = ResizeArray<RawVar>()
        let labelSets = ResizeArray<int[] * (byte[] * byte[])[]>()
        let documents = ResizeArray<byte[]>()
        let extensions = ResizeArray<Extension>()
        let mutable unitIndex = 0
        let mutable fin = false

        while not fin do
            match src.Int32() with
            | 2 ->
                let typ = src.Int32()
                let hasLabel = src.Int32()
                let nMissing = src.Int32()
                let print = src.Int32()
                let write = src.Int32()
                let name = src.Bytes 8

                let label =
                    if hasLabel = 1 then
                        let len = src.Int32()
                        let b = src.Bytes len
                        src.Bytes((4 - len % 4) % 4) |> ignore
                        Some b
                    else
                        None

                let missing = [ for _ in 1 .. abs nMissing -> src.Bytes 8 ]
                unitIndex <- unitIndex + 1

                if typ <> -1 then
                    vars.Add
                        { ShortName = name
                          Width = typ
                          Label = label
                          MissingCode = nMissing
                          MissingValues = missing
                          Print = print
                          Write = write
                          Index = unitIndex }
            | 3 ->
                let n = src.Int32()

                let labels =
                    [| for _ in 1..n do
                           let value = src.Bytes 8
                           let len = int ((src.Bytes 1)[0])
                           let label = src.Bytes len
                           src.Bytes((8 - (len + 1) % 8) % 8) |> ignore
                           value, label |]

                let rt = src.Int32()

                if rt <> 4 then
                    failwithf "Value-label record not followed by its variable list (got record type %d)." rt

                let nv = src.Int32()
                labelSets.Add([| for _ in 1..nv -> src.Int32() |], labels)
            | 6 ->
                let lines = src.Int32()

                for _ in 1..lines do
                    documents.Add(src.Bytes 80)
            | 7 ->
                let subtype = src.Int32()
                let size = src.Int32()
                let count = src.Int32()

                extensions.Add
                    { Subtype = subtype
                      Size = size
                      Count = count
                      Data = src.Bytes(size * count) }
            | 999 ->
                src.Int32() |> ignore
                fin <- true
            | t -> failwithf "Unexpected dictionary record type %d at byte %d." t (src.Stream.Position - 4L)

        { Variables = List.ofSeq vars
          LabelSets = List.ofSeq labelSets
          Documents = List.ofSeq documents
          Extensions = List.ofSeq extensions
          DictionaryEnd = src.Stream.Position }
