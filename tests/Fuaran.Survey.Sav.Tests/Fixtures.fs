// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

/// Where the committed fixtures live, and the small helpers every suite shares.
module Fuaran.Survey.Sav.Tests.Fixtures

open System
open System.IO
open System.Text
open System.Text.Json
open Fuaran.Survey.Sav

/// tests/fixtures, found by walking up from the test binary.
let dir =
    let rec up (d: DirectoryInfo) =
        if isNull d then
            failwith "tests/fixtures/expected.csv was not found above the test binary."

        let candidate = Path.Combine(d.FullName, "tests", "fixtures")

        if File.Exists(Path.Combine(candidate, "expected.csv")) then
            candidate
        else
            up d.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

let path (name: string) = Path.Combine(dir, name)

/// Every fixture: plain / bytecode / bytecode with end code / zlib, each in both byte orders.
let names =
    [ "plain-le.sav"
      "plain-be.sav"
      "bytecode-le.sav"
      "bytecode-be.sav"
      "bytecode-end-le.sav"
      "bytecode-end-be.sav"
      "zlib-le.zsav"
      "zlib-be.zsav" ]

let openFixture (name: string) = SavFile.openFile (path name) None

/// The expected dictionary facts, as gen_sav.py wrote them.
let expectedDictionary =
    lazy (JsonDocument.Parse(File.ReadAllBytes(path "expected-dictionary.json")))

let expectedRoot () = expectedDictionary.Force().RootElement

let strings (e: JsonElement) =
    [ for v in e.EnumerateArray() -> v.GetString() ]

/// Every case of the file, as the reader streams it.
let readAll (file: SavFile) =
    let rows = ResizeArray<RawValue[]>()
    let count, ending = SavFile.scan file (fun _ values -> rows.Add values)
    List.ofSeq rows, count, ending

/// The file rendered as CSV: a header of variable names, then one line per case.
let renderCsv (opts: RenderOptions) (file: SavFile) =
    let vars = file.Dictionary.Variables
    let render = Values.renderer opts vars
    let sb = StringBuilder()
    sb.Append(Csv.line (vars |> Array.map (fun v -> v.Name))) |> ignore

    SavFile.scan file (fun _ values -> sb.Append(Csv.line (render values |> Array.map Values.cellText)) |> ignore)
    |> ignore

    Encoding.UTF8.GetBytes(sb.ToString())

/// A fresh directory under the test binary's own tmp folder.
let tempDir () =
    let d = Path.Combine(AppContext.BaseDirectory, "tmp", Guid.NewGuid().ToString "N")

    Directory.CreateDirectory d |> ignore
    d

let variable (file: SavFile) (name: string) =
    file.Dictionary.Variables
    |> Array.find (fun v -> String.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
