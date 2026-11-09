// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.Survey.Sav

open System
open System.IO
open System.IO.Compression

// ============================================================================
//  Binary input: fixed-size reads in the file's byte order, and the case data
//  as a stream of 8-byte units under each of the three compression modes.
// ============================================================================

/// Fixed-size reads in the file's byte order (`swap` = the file's order differs from the host's).
type Src(s: Stream, swap: bool) =
    member _.Stream = s
    member _.Swap = swap

    member _.Bytes(n: int) =
        let b = Array.zeroCreate<byte> n
        s.ReadExactly(b, 0, n)
        b

    member private _.Fix(b: byte[]) =
        if swap then
            Array.Reverse b

        b

    member this.Int32() =
        BitConverter.ToInt32(this.Fix(this.Bytes 4), 0)

    member this.Int64() =
        BitConverter.ToInt64(this.Fix(this.Bytes 8), 0)

    member this.Double() =
        BitConverter.ToDouble(this.Fix(this.Bytes 8), 0)

    member _.AtEnd = s.Position >= s.Length

/// A read-only stream over a sequence of streams opened one after another (the .zsav blocks).
type ConcatStream(parts: seq<unit -> Stream>) =
    inherit Stream()
    let e = parts.GetEnumerator()
    let mutable cur: Stream = null
    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override _.Flush() = ()
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())

    override this.Read(buffer: byte[], offset: int, count: int) =
        if isNull cur then
            if e.MoveNext() then
                cur <- e.Current()
                this.Read(buffer, offset, count)
            else
                0
        else
            let n = cur.Read(buffer, offset, count)

            if n > 0 then
                n
            else
                cur.Dispose()
                cur <- null
                this.Read(buffer, offset, count)

    override _.Dispose(disposing: bool) =
        if disposing && not (isNull cur) then
            cur.Dispose()
            cur <- null

        base.Dispose disposing

/// One 8-byte unit of case data.
[<Struct>]
type DataUnit =
    | UBytes of bytes: byte[] // 8 raw bytes: a double, or 8 characters of a string
    | UNum of num: float // bytecode-compressed small integer
    | USpaces // bytecode 254: eight spaces
    | USysMis // bytecode 255: system-missing
    | UEnd

module Binary =

    /// Reads until `buf` is full or the stream ends; returns the number of bytes read.
    let readUpTo (s: Stream) (buf: byte[]) =
        let mutable total = 0
        let mutable go = true

        while go && total < buf.Length do
            let n = s.Read(buf, total, buf.Length - total)

            if n = 0 then go <- false else total <- total + n

        total

    /// An 8-byte unit as a double in the file's byte order.
    let toDoubleWith (swap: bool) (b: byte[]) =
        if swap then
            let c = Array.copy b
            Array.Reverse c
            BitConverter.ToDouble(c, 0)
        else
            BitConverter.ToDouble(b, 0)

    /// Compression 0: every unit is 8 raw bytes.
    let uncompressedUnits (s: Stream) =
        fun () ->
            let b = Array.zeroCreate<byte> 8
            if readUpTo s b = 8 then UBytes b else UEnd

    /// Compression 1 and 2: 8 one-byte command codes, then any raw 8-byte values they call for.
    let bytecodeUnits (s: Stream) (bias: float) =
        let cmd = Array.zeroCreate<byte> 8
        let mutable pos = 8
        let mutable finished = false

        let rec next () =
            if finished then
                UEnd
            elif pos >= 8 then
                if readUpTo s cmd = 8 then
                    pos <- 0
                    next ()
                else
                    finished <- true
                    UEnd
            else
                let code = cmd[pos]
                pos <- pos + 1

                match code with
                | 0uy -> next () // padding
                | 252uy ->
                    finished <- true
                    UEnd
                | 253uy ->
                    let b = Array.zeroCreate<byte> 8

                    if readUpTo s b = 8 then
                        UBytes b
                    else
                        finished <- true
                        UEnd
                | 254uy -> USpaces
                | 255uy -> USysMis
                | c -> UNum(float c - bias)

        next

    /// .zsav: the bytecode stream is cut into zlib blocks, located by a trailer at the end of
    /// the file. `src` must be positioned at the zlib header that follows the dictionary.
    let zsavStream (src: Src) : Stream =
        let s = src.Stream
        let _zheaderOfs = src.Int64()
        let ztrailerOfs = src.Int64()
        let _ztrailerLen = src.Int64()
        s.Position <- ztrailerOfs
        let _bias = src.Int64()
        let _zero = src.Int64()
        let _blockSize = src.Int32()
        let nBlocks = src.Int32()

        let blocks =
            [ for _ in 1..nBlocks do
                  let _uncompressedOfs = src.Int64()
                  let compressedOfs = src.Int64()
                  let _uncompressedSize = src.Int32()
                  let compressedSize = src.Int32()
                  compressedOfs, compressedSize ]

        let openBlock (ofs: int64, len: int) () =
            s.Position <- ofs
            let buf = Array.zeroCreate<byte> len
            s.ReadExactly(buf, 0, len)
            new ZLibStream(new MemoryStream(buf), CompressionMode.Decompress) :> Stream

        new ConcatStream(blocks |> Seq.map openBlock) :> Stream
