// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.Survey.Sav

open System
open System.IO
open System.Security.Cryptography
open System.Text

// ============================================================================
//  Cases: one streaming pass over the data, and the open-file handle.
// ============================================================================

/// One variable's value in one case.
type RawValue =
    | RNum of float option // None = system-missing
    | RStr of string

/// The data stopped part-way through a case.
exception TruncatedCase

/// How a scan of the case data ended.
type ScanEnd =
    | Complete
    | ShortOfHeader // fewer cases than the header promised
    | Truncated // data stops part-way through a case

module Cases =

    /// Reads one case; None at a clean end of data, TruncatedCase if data stops part-way through.
    let readCase
        (next: unit -> DataUnit)
        (toDouble: byte[] -> float)
        (sysmis: float)
        (enc: Encoding)
        (vars: Variable[])
        =
        let mutable started = false

        let take () =
            match next () with
            | UEnd ->
                if started then
                    raise TruncatedCase
                else
                    raise (EndOfStreamException())
            | u ->
                started <- true
                u

        /// Copies one segment's first `w` bytes into `dst` at `at`, consuming its 8-byte units.
        let readSegment (w: int) (dst: byte[]) (at: int) =
            let n = (w + 7) / 8

            for k in 0 .. n - 1 do
                let ofs = at + k * 8
                let take' = min 8 (w - k * 8)

                match take () with
                | UBytes b -> Array.blit b 0 dst ofs take'
                | _ -> Array.fill dst ofs take' 32uy

        try
            let values = Array.zeroCreate<RawValue> vars.Length

            for i in 0 .. vars.Length - 1 do
                let v = vars[i]

                if v.Width = 0 then
                    values[i] <-
                        match take () with
                        | UBytes b ->
                            let x = toDouble b
                            RNum(if x = sysmis then None else Some x)
                        | UNum x -> RNum(Some x)
                        | _ -> RNum None
                else
                    let total = v.Segments |> List.sumBy id
                    let buf = Array.create total 32uy
                    let mutable at = 0

                    for w in v.Segments do
                        readSegment w buf at
                        at <- at + w

                    let len = min total v.Width
                    values[i] <- RStr(enc.GetString(buf, 0, len).TrimEnd(' ', '\000'))

            Some values
        with :? EndOfStreamException ->
            None

/// An open system file, positioned at the start of the case data. Dispose closes the file.
type SavFile
    private
    (
        path: string,
        encodingOverride: string option,
        fs: FileStream,
        header: Header,
        raw: RawDictionary,
        dict: Dictionary,
        enc: Encoding,
        sysmis: float,
        next: unit -> DataUnit
    ) =

    /// The path the file was opened from.
    member _.Path = path
    /// The encoding override the file was opened with, if any.
    member _.EncodingOverride = encodingOverride
    member _.Header = header
    member _.Raw = raw
    member _.Dictionary = dict
    member _.Encoding = enc
    /// The system-missing value the file declares (subtype 4), or the IEEE default.
    member _.SysMis = sysmis
    /// The next 8-byte unit of case data.
    member _.Next = next

    interface IDisposable with
        member _.Dispose() = fs.Dispose()

    /// Opens a .sav or .zsav file, reading its header and dictionary. `encodingOverride` names
    /// a .NET encoding to decode text with instead of the one the file declares.
    static member openFile (path: string) (encodingOverride: string option) =
        let fs =
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 <<< 16)

        try
            let hdr = Records.readHeader fs
            let src = Src(fs, hdr.Swap)
            let raw = Records.readDictionary src

            let enc =
                Dictionary.resolveEncoding
                    encodingOverride
                    (Dictionary.declaredEncoding raw)
                    (Dictionary.characterCode hdr.Swap raw)

            // The fingerprint is the SHA-256 of the dictionary bytes, [0, DictionaryEnd).
            let dictBytes = Array.zeroCreate<byte> (int raw.DictionaryEnd)
            fs.Position <- 0L
            fs.ReadExactly(dictBytes, 0, dictBytes.Length)
            let fingerprint = Convert.ToHexStringLower(SHA256.HashData dictBytes)

            let dict = Dictionary.build hdr raw enc fingerprint
            let sysmis = Dictionary.sysmis hdr.Swap raw

            let next =
                match hdr.Compression with
                | 0 -> Binary.uncompressedUnits fs
                | 1 -> Binary.bytecodeUnits fs hdr.Bias
                | 2 -> Binary.bytecodeUnits (Binary.zsavStream src) hdr.Bias
                | c -> failwithf "Unknown compression code %d." c

            new SavFile(path, encodingOverride, fs, hdr, raw, dict, enc, sysmis, next)
        with _ ->
            fs.Dispose()
            reraise ()

    /// Reads every case, handing its raw values to `onRow`; returns the case count and how the
    /// data ended. Cases are read once: a second scan of the same SavFile sees no data.
    static member scan (file: SavFile) (onRow: int -> RawValue[] -> unit) : int * ScanEnd =
        let toDouble = Binary.toDoubleWith file.Header.Swap
        let vars = file.Dictionary.Variables

        let expected =
            match file.Dictionary.Cases with
            | Some n -> n
            | None -> -1L

        let mutable cases = 0
        let mutable go = true
        let mutable ending = Complete

        try
            while go && (expected < 0L || int64 cases < expected) do
                match Cases.readCase file.Next toDouble file.SysMis file.Encoding vars with
                | Some values ->
                    onRow cases values
                    cases <- cases + 1
                | None -> go <- false

            if expected >= 0L && int64 cases < expected then
                ending <- ShortOfHeader
        with TruncatedCase ->
            ending <- Truncated

        cases, ending
