// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.Survey.Sav

// ============================================================================
//  SPSS output formats.
//
//  A variable's print and write formats are packed into one int32 each: the
//  low byte is the decimal count, the next the field width, the next the
//  format type (the table in the PSPP "System File Format" appendix, Variable
//  Record). The format type is what decides whether a numeric variable holds a
//  plain number, a date, a date-time or a duration.
// ============================================================================

/// What a variable's values mean once decoded.
[<RequireQualifiedAccess>]
type Kind =
    | Numeric
    | String
    | Date
    | DateTime
    | Duration

module Format =

    /// Format type code -> SPSS format name (PSPP appendix, "System File Output Formats").
    let formatNames: Map<int, string> =
        Map.ofList
            [ 1, "A"
              2, "AHEX"
              3, "COMMA"
              4, "DOLLAR"
              5, "F"
              6, "IB"
              7, "PIBHEX"
              8, "P"
              9, "PIB"
              10, "PK"
              11, "RB"
              12, "RBHEX"
              15, "Z"
              16, "N"
              17, "E"
              20, "DATE"
              21, "TIME"
              22, "DATETIME"
              23, "ADATE"
              24, "JDATE"
              25, "DTIME"
              26, "WKDAY"
              27, "MONTH"
              28, "MOYR"
              29, "QYR"
              30, "WKYR"
              31, "PCT"
              32, "DOT"
              33, "CCA"
              34, "CCB"
              35, "CCC"
              36, "CCD"
              37, "CCE"
              38, "EDATE"
              39, "SDATE"
              40, "MTIME"
              41, "YMDHMS" ]

    /// The format type byte of a packed format.
    let formatType (format: int) = (format >>> 16) &&& 0xFF

    /// The field width byte of a packed format.
    let formatWidth (format: int) = (format >>> 8) &&& 0xFF

    /// The decimals byte of a packed format.
    let formatDecimals (format: int) = format &&& 0xFF

    /// What a variable of this storage width and print format holds.
    let kindOf (width: int) (format: int) =
        if width > 0 then
            Kind.String
        else
            match formatType format with
            | 20
            | 23
            | 24
            | 28
            | 29
            | 30
            | 38
            | 39 -> Kind.Date
            | 22
            | 41 -> Kind.DateTime
            | 21
            | 25
            | 40 -> Kind.Duration
            | _ -> Kind.Numeric

    /// The format as SPSS syntax writes it, e.g. "F8.2", "A20", "DATE11". A very long string
    /// (width > 255) reports its true width, which the format byte cannot hold.
    let formatText (width: int) (format: int) =
        let t = formatType format
        let w = if width > 255 then width else formatWidth format
        let d = formatDecimals format

        let name =
            match formatNames.TryFind t with
            | Some n -> n
            | None -> sprintf "FMT%d" t

        if d > 0 then
            sprintf "%s%d.%d" name w d
        else
            sprintf "%s%d" name w
