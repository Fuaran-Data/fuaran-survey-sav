// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.Survey.Sav

open System
open System.Collections.Generic
open System.IO
open System.Text

// ============================================================================
//  The typed dictionary.
//
//  Built from a RawDictionary once the text encoding is known. Every extension
//  record PSPP's "System File Format" appendix documents is interpreted here;
//  a subtype this library does not know stays available on the raw record.
//  Variable names that other records mention (multiple response sets, variable
//  sets, attributes, long string labels and missing values) are matched
//  case-insensitively against both the long and the short name, because
//  writers differ in which they use.
// ============================================================================

/// Measurement level (subtype 11).
type Measure =
    | Nominal
    | Ordinal
    | Scale
    | UnknownMeasure

/// Display alignment (subtype 11).
type Alignment =
    | AlignLeft
    | AlignRight
    | AlignCentre

/// The `$@Role` variable attribute, 0..5. PSPP's appendix calls role 1 "Output"; SPSS calls it
/// "Target", which is the name used here.
type Role =
    | Input
    | Target
    | Both
    | NoRole
    | Partition
    | Split

/// A variable's user-missing definition.
type MissingSpec =
    | NoMissing
    | Discrete of float list
    | Range of float * float
    | RangeAnd of float * float * float
    | StringMissing of string list

/// One variable, with every very-long-string segment folded into it.
type Variable =
    { Name: string
      ShortName: string
      Label: string
      Width: int // 0 = numeric, else string width in bytes
      Kind: Kind
      PrintFormat: int
      WriteFormat: int
      Measure: Measure option
      DisplayWidth: int option
      Alignment: Alignment option
      Role: Role option
      Missing: MissingSpec
      NumLabels: (float * string) list // in code order
      StrLabels: (string * string) list // in ordinal value order
      Attributes: Map<string, string list>
      Position: int // 0-based position among the folded variables
      Segments: int list // storage widths of the raw variables this one spans (>1 only for strings > 255)
      Index: int } // 1-based dictionary index of the first segment

/// Where a multiple dichotomy set's label comes from.
type LabelSource =
    | ExplicitLabel
    | FirstVariableLabel

type MrSetKind =
    | MultipleCategory
    | MultipleDichotomy of countedValue: string * labelSource: LabelSource

type MrSet =
    { Name: string
      Label: string
      Kind: MrSetKind
      Extended: bool // from subtype 19 (CATEGORYLABELS=COUNTEDVALUES) rather than subtype 7
      Variables: string list }

type Compression =
    | Uncompressed
    | Bytecode
    | Zlib

type ByteOrder =
    | LittleEndian
    | BigEndian

type Dictionary =
    { Variables: Variable[]
      WeightVariable: string option
      MrSets: MrSet list
      VariableSets: (string * string list) list
      Documents: string list
      FileLabel: string
      FileAttributes: Map<string, string list>
      Encoding: string // the .NET web name of the encoding the text was decoded with
      Cases: int64 option // the header count, or subtype 16 when present; unknown (-1) -> None
      Compression: Compression
      ByteOrder: ByteOrder
      Fingerprint: string } // lower-case hex SHA-256 over the dictionary bytes

module Dictionary =

    // ------------------------------------------------------------------------
    //  Encoding
    // ------------------------------------------------------------------------

    let private codePages =
        lazy (Encoding.RegisterProvider CodePagesEncodingProvider.Instance)

    /// Picks the text encoding: an explicit override, else the declared name (subtype 20), else
    /// the character code (subtype 3), else windows-1252, which is what old SPSS files mostly are.
    let resolveEncoding (overrideName: string option) (declared: string option) (charCode: int option) =
        codePages.Force()

        let tryGet (get: unit -> Encoding) =
            try
                Some(get ())
            with _ ->
                None

        match overrideName with
        | Some n ->
            tryGet (fun () -> Encoding.GetEncoding n)
            |> Option.defaultWith (fun () -> failwithf "Unknown encoding %A." n)
        | None ->
            declared
            |> Option.bind (fun n -> tryGet (fun () -> Encoding.GetEncoding n))
            |> Option.orElse (
                charCode
                |> Option.filter (fun c -> c > 3) // 1-3 are EBCDIC / 7-bit / 8-bit ASCII
                |> Option.bind (fun c -> tryGet (fun () -> Encoding.GetEncoding c))
            )
            |> Option.defaultWith (fun () -> Encoding.GetEncoding 1252)

    // ------------------------------------------------------------------------
    //  Extension-record readers. Each takes the record's bytes and is pure.
    // ------------------------------------------------------------------------

    let private extensions (raw: RawDictionary) subtype =
        raw.Extensions |> List.filter (fun e -> e.Subtype = subtype)

    /// Subtype 3 (machine integer info): the character code, its eighth int32.
    let characterCode (swap: bool) (raw: RawDictionary) =
        extensions raw 3
        |> List.tryPick (fun e ->
            if e.Data.Length >= 32 then
                let s = Src(new MemoryStream(e.Data), swap)
                s.Bytes 28 |> ignore
                Some(s.Int32())
            else
                None)

    /// Subtype 4 (machine floating-point info): the system-missing value, its first double.
    let sysmis (swap: bool) (raw: RawDictionary) =
        extensions raw 4
        |> List.tryPick (fun e ->
            if e.Data.Length >= 8 then
                Some(Src(new MemoryStream(e.Data), swap).Double())
            else
                None)
        |> Option.defaultValue -Double.MaxValue

    /// Subtype 20: the declared encoding name.
    let declaredEncoding (raw: RawDictionary) =
        extensions raw 20
        |> List.tryHead
        |> Option.map (fun e -> Encoding.ASCII.GetString(e.Data).Trim('\000', ' '))

    /// "KEY=value<tab>KEY=value..." (subtypes 13 and 14); keys upper-cased for matching.
    let parsePairs (text: string) =
        text.Split([| '\t'; '\000' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun p ->
            match p.IndexOf '=' with
            | i when i > 0 -> Some(p.Substring(0, i).Trim().ToUpperInvariant(), p.Substring(i + 1).Trim())
            | _ -> None)
        |> Map.ofArray

    /// Subtype 5: one set per line, "name= var var ...", long variable names.
    let parseVariableSets (text: string) =
        text.Split('\n')
        |> Array.map (fun l -> l.TrimEnd('\r', '\000'))
        |> Array.filter (fun l -> l.Length > 0)
        |> Array.choose (fun l ->
            match l.IndexOf '=' with
            | i when i > 0 ->
                let vars =
                    l.Substring(i + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    |> List.ofArray

                Some(l.Substring(0, i).Trim(), vars)
            | _ -> None)
        |> List.ofArray

    /// A multiple response set as its text line states it, before names are resolved.
    type MrSetText =
        {
            Name: string
            TypeChar: char
            /// Subtype 19 only: 1 = explicit label, 11 = LABELSOURCE=VARLABEL.
            LabelSourceFlag: int option
            CountedValue: string option
            Label: string
            Variables: string list
        }

    /// Subtypes 7 and 19. Lengths in the text are byte counts, so this walks the bytes and
    /// decodes each piece; see the PSPP appendix, "Multiple Response Sets Records".
    let parseMrSets (enc: Encoding) (data: byte[]) =
        let n = data.Length
        let mutable i = 0
        let sets = ResizeArray<MrSetText>()

        let readUntil (stop: byte) =
            let start = i

            while i < n && data[i] <> stop do
                i <- i + 1

            let s = enc.GetString(data, start, i - start)

            if i < n then
                i <- i + 1 // the delimiter

            s

        let readInt () =
            match Int32.TryParse(readUntil 32uy) with
            | true, v -> v
            | _ -> 0

        /// "<len> <bytes> ": a byte-counted string followed by one space.
        let readCounted () =
            let len = max 0 (min (readInt ()) (n - i))
            let s = enc.GetString(data, i, len)
            i <- i + len

            if i < n && data[i] = 32uy then
                i <- i + 1

            s

        let readVars () =
            (readUntil 10uy).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            |> List.ofArray

        while i < n do
            while i < n && (data[i] = 10uy || data[i] = 13uy) do
                i <- i + 1

            if i < n then
                let name = readUntil 61uy // '='

                if i < n then
                    let typ = char data[i]
                    i <- i + 1

                    match typ with
                    | 'C' ->
                        if i < n && data[i] = 32uy then
                            i <- i + 1

                        let label = readCounted ()

                        sets.Add
                            { Name = name
                              TypeChar = 'C'
                              LabelSourceFlag = None
                              CountedValue = None
                              Label = label
                              Variables = readVars () }
                    | 'D' ->
                        let counted = readCounted ()
                        let label = readCounted ()

                        sets.Add
                            { Name = name
                              TypeChar = 'D'
                              LabelSourceFlag = None
                              CountedValue = Some counted
                              Label = label
                              Variables = readVars () }
                    | 'E' ->
                        if i < n && data[i] = 32uy then
                            i <- i + 1

                        let flag = readInt ()
                        let counted = readCounted ()
                        let label = readCounted ()

                        sets.Add
                            { Name = name
                              TypeChar = 'E'
                              LabelSourceFlag = Some flag
                              CountedValue = Some counted
                              Label = label
                              Variables = readVars () }
                    | _ -> readUntil 10uy |> ignore // unknown set type: skip the line

        List.ofSeq sets

    /// Subtypes 17 and 18: "name('value'<LF>'value'<LF>)name2(...)", and for subtype 18 each
    /// attribute set is prefixed "variable:" with "/" between variables. Returns
    /// (variable name option, attributes) per set.
    let parseAttributes (withVariable: bool) (text: string) =
        let n = text.Length
        let mutable i = 0
        let sets = ResizeArray<string option * (string * string list) list>()

        let readUntil (stop: char) =
            let start = i

            while i < n && text[i] <> stop do
                i <- i + 1

            let s = text.Substring(start, i - start)

            if i < n then
                i <- i + 1

            s

        while i < n do
            let variable = if withVariable then Some(readUntil ':') else None
            let attrs = ResizeArray<string * string list>()
            let mutable inSet = true

            while inSet && i < n do
                let name = readUntil '('
                let values = ResizeArray<string>()

                while i < n && text[i] = '\'' do
                    i <- i + 1
                    let close = text.IndexOf("'\n", i, StringComparison.Ordinal)
                    let close = if close < 0 then n else close
                    values.Add(text.Substring(i, close - i))
                    i <- min n (close + 2)

                if i < n && text[i] = ')' then
                    i <- i + 1

                if name.Length > 0 then
                    attrs.Add(name, List.ofSeq values)

                if i < n && text[i] = '/' then
                    i <- i + 1
                    inSet <- false
                elif i < n && (text[i] = '\000' || text[i] = '\n') then
                    i <- n

            sets.Add(variable, List.ofSeq attrs)

        List.ofSeq sets

    /// Subtype 11, as (measure, width option, alignment) per non-continuation variable record.
    /// `count` is 2 or 3 per record; anything else is not interpreted.
    let parseDisplayParameters (swap: bool) (recordCount: int) (e: Extension) =
        let perVar =
            if e.Count = 3 * recordCount then 3
            elif e.Count = 2 * recordCount then 2
            else 0

        if perVar = 0 || recordCount = 0 then
            None
        else
            let s = Src(new MemoryStream(e.Data), swap)

            let measure =
                function
                | 1 -> Nominal
                | 2 -> Ordinal
                | 3 -> Scale
                | _ -> UnknownMeasure

            let alignment =
                function
                | 1 -> AlignRight
                | 2 -> AlignCentre
                | _ -> AlignLeft

            Some
                [| for _ in 1..recordCount ->
                       let m = measure (s.Int32())
                       let w = if perVar = 3 then Some(s.Int32()) else None
                       let a = alignment (s.Int32())
                       m, w, a |]

    /// Subtype 21: (variable name, (value, label) list) per entry.
    let parseLongStringLabels (enc: Encoding) (swap: bool) (data: byte[]) =
        let decode (b: byte[]) = enc.GetString(b).TrimEnd(' ', '\000')
        let s = Src(new MemoryStream(data), swap)

        [ while not s.AtEnd do
              let name = decode (s.Bytes(s.Int32()))
              let _width = s.Int32()
              let n = s.Int32()

              let labels =
                  [ for _ in 1..n do
                        let value = decode (s.Bytes(s.Int32()))
                        let label = decode (s.Bytes(s.Int32()))
                        value, label ]

              name, labels ]

    /// Subtype 22: (variable name, missing values) per entry. The count is a single byte.
    let parseLongStringMissing (enc: Encoding) (swap: bool) (data: byte[]) =
        let decode (b: byte[]) = enc.GetString(b).TrimEnd(' ', '\000')
        let s = Src(new MemoryStream(data), swap)

        [ while not s.AtEnd do
              let name = decode (s.Bytes(s.Int32()))
              let n = int ((s.Bytes 1)[0])
              let valueLen = s.Int32()
              name, [ for _ in 1..n -> decode (s.Bytes valueLen) ] ]

    /// Subtype 16: the 64-bit case count, after an int64 that is always 1.
    let extendedCaseCount (swap: bool) (raw: RawDictionary) =
        extensions raw 16
        |> List.tryPick (fun e ->
            if e.Data.Length >= 16 then
                let s = Src(new MemoryStream(e.Data), swap)
                s.Int64() |> ignore
                Some(s.Int64())
            else
                None)

    let private roleOf (text: string) =
        match text.Trim() with
        | "0" -> Some Input
        | "1" -> Some Target
        | "2" -> Some Both
        | "3" -> Some NoRole
        | "4" -> Some Partition
        | "5" -> Some Split
        | _ -> None

    // ------------------------------------------------------------------------
    //  Building the dictionary
    // ------------------------------------------------------------------------

    /// Builds the typed dictionary. `fingerprint` is the SHA-256 of the dictionary bytes,
    /// computed by whoever holds the stream.
    let build (header: Header) (raw: RawDictionary) (enc: Encoding) (fingerprint: string) : Dictionary =
        let swap = header.Swap
        let decode (b: byte[]) = enc.GetString(b).TrimEnd(' ', '\000')
        let toDouble = Binary.toDoubleWith swap
        let ext = extensions raw

        let textOf (e: Extension) = enc.GetString(e.Data).TrimEnd('\000')

        let pairs subtype =
            ext subtype
            |> List.map (fun e -> parsePairs (textOf e))
            |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m) Map.empty

        let longNames = pairs 13
        let veryLong = pairs 14
        let rawVars = Array.ofList raw.Variables

        // Fold very long strings: a string wider than 255 bytes is stored as a run of segment
        // variables, each 255 wide but the last; (w + 251) / 252 segments in all.
        let folded = ResizeArray<RawVar * int * RawVar list * int>() // first, width, segments, raw ordinal
        let mutable k = 0

        while k < rawVars.Length do
            let r = rawVars[k]
            let short = (decode r.ShortName).ToUpperInvariant()

            let w =
                veryLong.TryFind short
                |> Option.bind (fun t ->
                    match Int32.TryParse t with
                    | true, v -> Some v
                    | _ -> None)

            match w with
            | Some w when w > 255 ->
                let count = min ((w + 251) / 252) (rawVars.Length - k)
                folded.Add((r, w, List.ofArray rawVars[k .. k + count - 1], k))
                k <- k + count
            | _ ->
                folded.Add((r, r.Width, [ r ], k))
                k <- k + 1

        let display = ext 11 |> List.tryPick (parseDisplayParameters swap rawVars.Length)

        // Value labels by dictionary index (type 3/4) and, for long strings, subtype 21.
        let numLabels = Dictionary<int, ResizeArray<float * string>>()
        let strLabels = Dictionary<int, ResizeArray<string * string>>()
        let byIndex = folded |> Seq.map (fun (r, _, _, _) -> r.Index, r) |> dict

        for idx, labels in raw.LabelSets do
            for i in idx do
                match byIndex.TryGetValue i with
                | true, r ->
                    for value, label in labels do
                        if r.Width = 0 then
                            let list =
                                match numLabels.TryGetValue i with
                                | true, l -> l
                                | _ ->
                                    let l = ResizeArray()
                                    numLabels[i] <- l
                                    l

                            list.Add(toDouble value, decode label)
                        else
                            let list =
                                match strLabels.TryGetValue i with
                                | true, l -> l
                                | _ ->
                                    let l = ResizeArray()
                                    strLabels[i] <- l
                                    l

                            list.Add(decode value, decode label)
                | _ -> ()

        let shortOf (r: RawVar) = decode r.ShortName

        let nameOf (r: RawVar) =
            longNames.TryFind((shortOf r).ToUpperInvariant())
            |> Option.defaultValue (shortOf r)

        let same (a: string) (b: string) =
            String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

        let findVar (name: string) =
            folded
            |> Seq.tryFind (fun (r, _, _, _) -> same (nameOf r) name || same (shortOf r) name)

        let resolveName (name: string) =
            findVar name
            |> Option.map (fun (r, _, _, _) -> nameOf r)
            |> Option.defaultValue name

        for e in ext 21 do
            for name, labels in parseLongStringLabels enc swap e.Data do
                findVar name
                |> Option.iter (fun (r, _, _, _) ->
                    let list =
                        match strLabels.TryGetValue r.Index with
                        | true, l -> l
                        | _ ->
                            let l = ResizeArray()
                            strLabels[r.Index] <- l
                            l

                    list.AddRange labels)

        let longMissing = Dictionary<int, string list>()

        for e in ext 22 do
            for name, values in parseLongStringMissing enc swap e.Data do
                findVar name
                |> Option.iter (fun (r, _, _, _) ->
                    let existing =
                        match longMissing.TryGetValue r.Index with
                        | true, l -> l
                        | _ -> []

                    longMissing[r.Index] <- existing @ values)

        // Variable attributes (subtype 18); `$@Role` becomes Role when it parses.
        let attributes = Dictionary<int, Map<string, string list>>()
        let roles = Dictionary<int, Role>()

        for e in ext 18 do
            for variable, attrs in parseAttributes true (textOf e) do
                variable
                |> Option.bind findVar
                |> Option.iter (fun (r, _, _, _) ->
                    let mutable m =
                        match attributes.TryGetValue r.Index with
                        | true, m -> m
                        | _ -> Map.empty

                    for name, values in attrs do
                        match name, values with
                        | "$@Role", [ v ] when (roleOf v).IsSome -> roles[r.Index] <- (roleOf v).Value
                        | _ -> m <- Map.add name values m

                    attributes[r.Index] <- m)

        let variables =
            folded
            |> Seq.mapi (fun position (r, width, segments, ordinal) ->
                let isString = width > 0

                let missing =
                    if isString then
                        let fromRecord = r.MissingValues |> List.map decode

                        let fromExt =
                            match longMissing.TryGetValue r.Index with
                            | true, l -> l
                            | _ -> []

                        match List.distinct (fromRecord @ fromExt) with
                        | [] -> NoMissing
                        | l -> StringMissing l
                    else
                        match r.MissingCode, r.MissingValues |> List.map toDouble with
                        | c, vals when c > 0 -> Discrete vals
                        | -2, [ lo; hi ] -> Range(lo, hi)
                        | -3, [ lo; hi; d ] -> RangeAnd(lo, hi, d)
                        | _ -> NoMissing

                let measure, displayWidth, alignment =
                    match display with
                    | Some d when ordinal < d.Length ->
                        let m, w, a = d[ordinal]
                        Some m, w, Some a
                    | _ -> None, None, None

                { Name = nameOf r
                  ShortName = shortOf r
                  Label = r.Label |> Option.map decode |> Option.defaultValue ""
                  Width = width
                  Kind = Format.kindOf width r.Print
                  PrintFormat = r.Print
                  WriteFormat = r.Write
                  Measure = measure
                  DisplayWidth = displayWidth
                  Alignment = alignment
                  Role =
                    match roles.TryGetValue r.Index with
                    | true, role -> Some role
                    | _ -> None
                  Missing = missing
                  NumLabels =
                    match numLabels.TryGetValue r.Index with
                    | true, l -> l |> List.ofSeq |> List.distinctBy fst |> List.sortBy fst
                    | _ -> []
                  StrLabels =
                    match strLabels.TryGetValue r.Index with
                    | true, l ->
                        l
                        |> List.ofSeq
                        |> List.distinctBy fst
                        |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
                    | _ -> []
                  Attributes =
                    match attributes.TryGetValue r.Index with
                    | true, m -> m
                    | _ -> Map.empty
                  Position = position
                  Segments = segments |> List.map (fun s -> s.Width)
                  Index = r.Index })
            |> Array.ofSeq

        let labelOf (name: string) =
            variables
            |> Array.tryFind (fun v -> same v.Name name)
            |> Option.map (fun v -> v.Label)
            |> Option.defaultValue ""

        let mrSets =
            [ for subtype, extended in [ 7, false; 19, true ] do
                  for e in ext subtype do
                      for s in parseMrSets enc e.Data do
                          let vars = s.Variables |> List.map resolveName

                          let kind =
                              match s.TypeChar, s.CountedValue with
                              | 'C', _ -> MultipleCategory
                              | _, counted ->
                                  let source =
                                      if s.LabelSourceFlag = Some 11 then
                                          FirstVariableLabel
                                      else
                                          ExplicitLabel

                                  MultipleDichotomy(
                                      counted |> Option.defaultValue "" |> (fun c -> c.TrimEnd ' ', source)
                                  )

                          let label =
                              match kind, vars with
                              | MultipleDichotomy(_, FirstVariableLabel), first :: _ -> labelOf first
                              | _ -> s.Label

                          { Name = s.Name
                            Label = label
                            Kind = kind
                            Extended = extended
                            Variables = vars } ]

        let variableSets =
            ext 5
            |> List.collect (fun e -> parseVariableSets (textOf e))
            |> List.map (fun (name, vars) -> name, vars |> List.map resolveName)

        let fileAttributes =
            ext 17
            |> List.collect (fun e -> parseAttributes false (textOf e))
            |> List.collect snd
            |> List.fold (fun m (k, v) -> Map.add k v m) Map.empty

        let cases =
            match extendedCaseCount swap raw with
            | Some n -> n
            | None -> int64 header.NCases

        let weight =
            if header.WeightIndex > 0 then
                variables
                |> Array.tryFind (fun v -> v.Index = header.WeightIndex)
                |> Option.map (fun v -> v.Name)
            else
                None

        let hostLittle = BitConverter.IsLittleEndian

        { Variables = variables
          WeightVariable = weight
          MrSets = mrSets
          VariableSets = variableSets
          Documents = raw.Documents |> List.map decode
          FileLabel = decode header.Raw[109..172]
          FileAttributes = fileAttributes
          Encoding = enc.WebName
          Cases = (if cases < 0L then None else Some cases)
          Compression =
            match header.Compression with
            | 0 -> Uncompressed
            | 1 -> Bytecode
            | _ -> Zlib
          ByteOrder = (if hostLittle <> swap then LittleEndian else BigEndian)
          Fingerprint = fingerprint }
