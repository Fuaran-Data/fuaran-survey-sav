// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

module Fuaran.Survey.Sav.Tests.ColumnarTests

open System
open System.IO
open Expecto
open Fuaran.Survey.Sav

/// The storage each fixture column must take, from its data.
let private expectedStorage =
    [ "id", Int32 // 1,000,000 needs more than 16 bits
      "gender", Int8
      "score", Float64
      "city", Utf8Dictionary
      "comment", Utf8Dictionary
      "essay", Utf8Dictionary
      "dob", Float64 // seconds since 1582 exceed int32
      "ts", Float64
      "dur", Int32 // 360,000
      "household_income_band", Int16 // -99 .. 151: 151 is past sbyte
      "mr_a", Int8
      "mr_b", Int8
      "mc_1", Int8
      "mc_2", Int8
      "postcode", Utf8Dictionary ]

let private writeStore (name: string) =
    let dir = Fixtures.tempDir ()
    use file = Fixtures.openFixture name
    let manifest = Columnar.write file dir
    dir, manifest

let tests =
    testList
        "columnar store"
        [ for name in Fixtures.names do
              test (sprintf "%s round-trips every value through the store" name) {
                  let dir, manifest = writeStore name
                  use source = Fixtures.openFixture name
                  let rows, count, _ = Fixtures.readAll source
                  Expect.equal manifest.Cases count "manifest case count"
                  Expect.equal manifest.SourceFingerprint source.Dictionary.Fingerprint "fingerprint recorded"
                  Expect.equal manifest.Columns.Length source.Dictionary.Variables.Length "one column per variable"
                  use store = Columnar.openStore dir
                  Expect.equal store.Manifest manifest "the manifest reads back as written"

                  for v in source.Dictionary.Variables do
                      let col = store.Column v.Name
                      Expect.equal col.Count count (v.Name + " count")
                      Expect.equal col.Kind v.Kind (v.Name + " kind")

                      rows
                      |> List.iteri (fun i row ->
                          match row[v.Position] with
                          | RNum None ->
                              Expect.isFalse (col.IsValid i) (sprintf "%s[%d] invalid" v.Name i)
                              Expect.isTrue (Double.IsNaN(col.Float i)) (sprintf "%s[%d] NaN" v.Name i)
                              Expect.isNone (col.TryFloat i) (sprintf "%s[%d] TryFloat" v.Name i)
                          | RNum(Some x) ->
                              Expect.isTrue (col.IsValid i) (sprintf "%s[%d] valid" v.Name i)
                              Expect.equal (col.Float i) x (sprintf "%s[%d] value" v.Name i)
                              Expect.equal (col.TryFloat i) (Some x) (sprintf "%s[%d] TryFloat" v.Name i)
                              Expect.isNone (col.String i) (sprintf "%s[%d] not a string" v.Name i)
                          | RStr s ->
                              Expect.isTrue (col.IsValid i) (sprintf "%s[%d] valid" v.Name i)
                              Expect.equal (col.String i) (Some s) (sprintf "%s[%d] string" v.Name i)
                              Expect.isNone (col.TryFloat i) (sprintf "%s[%d] not numeric" v.Name i))
              }

          test "storage is the narrowest type that holds the data" {
              let _, manifest = writeStore "plain-le.sav"

              for name, storage in expectedStorage do
                  let c = manifest.Columns |> List.find (fun c -> c.Name = name)
                  Expect.equal c.Storage storage (name + " storage")

                  Expect.equal
                      c.DictionaryFile.IsSome
                      (storage = Utf8Dictionary)
                      (name + " has a dictionary iff string")
          }

          test "two writes of one file are byte-identical" {
              let a, _ = writeStore "zlib-le.zsav"
              let b, _ = writeStore "zlib-le.zsav"

              let files (d: string) =
                  Directory.GetFiles d |> Array.map Path.GetFileName |> Array.sort

              Expect.equal (files a) (files b) "same file names"

              for f in files a do
                  Expect.equal
                      (File.ReadAllBytes(Path.Combine(a, f)))
                      (File.ReadAllBytes(Path.Combine(b, f)))
                      (f + " bytes")
          }

          test "the string dictionary round-trips UTF-8, the 300-byte é string included" {
              let dir, manifest = writeStore "bytecode-be.sav"
              use store = Columnar.openStore dir
              let essay = store.Column "essay"
              Expect.equal (essay.String 3) (Some(String.replicate 150 "é")) "é string"
              Expect.equal (essay.String 2) (Some "") "an empty string is a present value"
              Expect.equal essay.Dictionary.Length 4 "four distinct values"

              Expect.equal
                  (manifest.Columns |> List.find (fun c -> c.Name = "essay")).DictionarySize
                  4
                  "manifest agrees"

              Expect.equal ((store.Column "comment").String 0) (Some "café ünïcödé") "accented text"
              Expect.equal ((store.Column "comment").String 3) (Some "line1\nline2") "embedded newline"

              Expect.equal
                  (List.ofArray essay.Dictionary)
                  (essay.Dictionary
                   |> List.ofArray
                   |> List.sortWith (fun a b -> String.CompareOrdinal(a, b)))
                  "the table is in ordinal order"
          }

          test "columns are looked up case-insensitively and projected in the order asked" {
              let dir, _ = writeStore "plain-le.sav"
              use store = Columnar.openStore dir
              let cols = store.Project [ "POSTCODE"; "Id"; "gender" ]
              Expect.equal (cols |> Array.map (fun c -> c.Name)) [| "postcode"; "id"; "gender" |] "order and names"
              Expect.isTrue (Object.ReferenceEquals(store.Column "id", cols[1])) "a column is mapped once"
              Expect.throws (fun () -> store.Column "no_such" |> ignore) "unknown column"
          }

          test "out-of-range cases read as invalid" {
              let dir, _ = writeStore "plain-le.sav"
              use store = Columnar.openStore dir
              let id = store.Column "id"
              Expect.isFalse (id.IsValid -1) "negative"
              Expect.isFalse (id.IsValid 4) "past the end"
              Expect.isTrue (Double.IsNaN(id.Float 4)) "NaN past the end"
          }

          test "the manifest is deterministic JSON with LF line ends and no timestamp" {
              let dir, _ = writeStore "plain-le.sav"
              let text = File.ReadAllText(Path.Combine(dir, "manifest.json"))
              Expect.isFalse (text.Contains "\r") "LF only"
              Expect.stringStarts text "{\n  \"format\": \"fuaran-survey-sav-columnar\"" "format first"
              Expect.isFalse (text.Contains "time") "no timestamp"
              Expect.stringEnds text "}\n" "trailing newline"
          } ]
