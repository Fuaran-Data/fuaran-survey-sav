// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.Survey.Sav

open System
open System.Collections.Generic
open System.Globalization

// ============================================================================
//  Values: a raw value rendered as a typed cell, and the cell as text.
//
//  The CSV module renders exactly as FSharpSAV's CsvSink did, so a rendering
//  of a file here is byte-identical to that tool's output.
// ============================================================================

/// A rendered value.
type Cell =
    | CBlank
    | CNum of float
    | CText of string
    | CDate of DateTime
    | CDateTime of DateTime
    | CDuration of TimeSpan

type RenderOptions =
    { Labels: bool // value labels instead of codes
      UserMissingBlank: bool } // user-defined missing values render blank (system-missing always does)

module Values =

    let private inv = CultureInfo.InvariantCulture

    /// SPSS dates are seconds since the Gregorian calendar began.
    let spssEpoch = DateTime(1582, 10, 14)

    let defaultOptions =
        { Labels = false
          UserMissingBlank = false }

    let fmtNum (x: float) =
        if x >= 1e300 then "HIGHEST"
        elif x <= -1e300 then "LOWEST"
        else x.ToString("R", inv)

    let fmtDate (d: DateTime) = d.ToString("yyyy-MM-dd", inv)

    let fmtDateTime (d: DateTime) =
        d.ToString(
            (if d.Millisecond <> 0 then
                 "yyyy-MM-dd HH:mm:ss.fff"
             else
                 "yyyy-MM-dd HH:mm:ss"),
            inv
        )

    let fmtDuration (t: TimeSpan) =
        let sign = if t < TimeSpan.Zero then "-" else ""
        let t = t.Duration()

        let hms =
            sprintf "%s%d:%02d:%02d" sign (int64 (floor t.TotalHours)) t.Minutes t.Seconds

        if t.Milliseconds <> 0 then
            hms + sprintf ".%03d" t.Milliseconds
        else
            hms

    /// Whether a numeric value is user-missing under the variable's definition.
    let isUserMissing (v: Variable) (x: float) =
        match v.Missing with
        | Discrete vals -> List.contains x vals
        | Range(lo, hi) -> x >= lo && x <= hi
        | RangeAnd(lo, hi, d) -> (x >= lo && x <= hi) || x = d
        | NoMissing
        | StringMissing _ -> false

    /// Whether a string value is user-missing under the variable's definition.
    let isUserMissingString (v: Variable) (s: string) =
        match v.Missing with
        | StringMissing vals -> List.contains s vals
        | _ -> false

    let private typedNumber (v: Variable) (x: float) =
        let attempt f =
            try
                Some(f ())
            with _ ->
                None

        let typed =
            match v.Kind with
            | Kind.Date -> attempt (fun () -> CDate (spssEpoch.AddSeconds x).Date)
            | Kind.DateTime -> attempt (fun () -> CDateTime(spssEpoch.AddSeconds x))
            | Kind.Duration -> attempt (fun () -> CDuration(TimeSpan.FromSeconds x))
            | Kind.Numeric
            | Kind.String -> None

        typed |> Option.defaultValue (CNum x)

    let private cell
        (o: RenderOptions)
        (v: Variable)
        (numLabel: float -> string option)
        (strLabel: string -> string option)
        (value: RawValue)
        =
        match value with
        | RNum None -> CBlank
        | RNum(Some x) when o.UserMissingBlank && isUserMissing v x -> CBlank
        | RNum(Some x) ->
            match (if o.Labels then numLabel x else None) with
            | Some label -> CText label
            | None -> typedNumber v x
        | RStr s when o.UserMissingBlank && isUserMissingString v s -> CBlank
        | RStr s ->
            match (if o.Labels then strLabel s else None) with
            | Some label -> CText label
            | None when s = "" -> CBlank
            | None -> CText s

    /// Renders one value. Label lookups walk the variable's label list; for a whole file use
    /// `renderer`, which indexes them once.
    let toCell (o: RenderOptions) (v: Variable) (value: RawValue) =
        cell
            o
            v
            (fun x -> v.NumLabels |> List.tryFind (fun (c, _) -> c = x) |> Option.map snd)
            (fun s -> v.StrLabels |> List.tryFind (fun (c, _) -> c = s) |> Option.map snd)
            value

    /// A row renderer with the label lookups indexed once per variable.
    let renderer (o: RenderOptions) (vars: Variable[]) : RawValue[] -> Cell[] =
        let lookups =
            vars
            |> Array.map (fun v ->
                let nums = Dictionary<float, string>()

                for c, l in v.NumLabels do
                    nums[c] <- l

                let strs = Dictionary<string, string>()

                for c, l in v.StrLabels do
                    strs[c] <- l

                let numLabel x =
                    match nums.TryGetValue x with
                    | true, l -> Some l
                    | _ -> None

                let strLabel s =
                    match strs.TryGetValue s with
                    | true, l -> Some l
                    | _ -> None

                cell o v numLabel strLabel)

        fun (values: RawValue[]) -> Array.init vars.Length (fun i -> lookups[i]values[i])

    let cellText =
        function
        | CBlank -> ""
        | CText s -> s
        | CNum x -> x.ToString("R", inv)
        | CDate d -> fmtDate d
        | CDateTime d -> fmtDateTime d
        | CDuration t -> fmtDuration t

/// CSV rendering: a field is quoted when it holds the delimiter, a quote, CR or LF; quotes are
/// doubled; lines end in CR LF (RFC 4180).
module Csv =

    let delimiter = ','

    let private special = [| delimiter; '"'; '\r'; '\n' |]

    /// One field, quoted if it needs to be.
    let cell (s: string) =
        if s.IndexOfAny special >= 0 then
            "\"" + s.Replace("\"", "\"\"") + "\""
        else
            s

    /// One line of fields, with its CR LF terminator.
    let line (fields: string seq) =
        String.Join(string delimiter, Seq.map cell fields) + "\r\n"
