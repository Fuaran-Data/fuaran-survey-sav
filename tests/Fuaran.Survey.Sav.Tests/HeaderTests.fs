// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

module Fuaran.Survey.Sav.Tests.HeaderTests

open Expecto
open Fuaran.Survey.Sav

let tests =
    testList
        "header"
        [ for name in Fixtures.names do
              test (sprintf "%s reports its byte order, compression and case count" name) {
                  use file = Fixtures.openFixture name
                  let d = file.Dictionary

                  let order =
                      if name.EndsWith "-le.sav" || name.EndsWith "-le.zsav" then
                          LittleEndian
                      else
                          BigEndian

                  let compression =
                      if name.StartsWith "plain" then Uncompressed
                      elif name.StartsWith "bytecode" then Bytecode
                      else Zlib

                  Expect.equal d.ByteOrder order "byte order"
                  Expect.equal d.Compression compression "compression"
                  Expect.equal file.Header.Zsav (compression = Zlib) "$FL3 magic only for zlib"
                  Expect.equal file.Header.NCases 4 "header case count"
                  Expect.equal d.Cases (Some 4L) "case count (subtype 16 agrees with the header)"
                  Expect.equal file.Header.Bias 100.0 "bias"
              }

          test "the header names the product, the creation date and time and the file label" {
              use file = Fixtures.openFixture "plain-le.sav"
              let root = Fixtures.expectedRoot ()
              Expect.equal file.Header.ProductName (root.GetProperty("productName").GetString()) "product name"
              Expect.equal file.Header.CreationDate "08 Oct 26" "creation date"
              Expect.equal file.Header.CreationTime "12:00:00" "creation time"
              Expect.equal file.Header.FileLabel (root.GetProperty("fileLabel").GetString()) "header file label"
              Expect.equal file.Dictionary.FileLabel (root.GetProperty("fileLabel").GetString()) "dictionary file label"
              Expect.equal file.Header.Raw.Length 176 "the raw header is kept whole"
          }

          test "the weight variable is resolved from the header's dictionary index" {
              use file = Fixtures.openFixture "zlib-be.zsav"
              let root = Fixtures.expectedRoot ()
              Expect.equal file.Dictionary.WeightVariable (Some(root.GetProperty("weight").GetString())) "weight"
              Expect.equal file.Header.WeightIndex 3 "weight index is 1-based over variable records"
          }

          test "the encoding comes from the subtype 20 record" {
              use file = Fixtures.openFixture "plain-be.sav"
              Expect.equal file.Dictionary.Encoding "utf-8" "encoding"

              Expect.equal
                  (Dictionary.characterCode file.Header.Swap file.Raw)
                  (Some 65001)
                  "character code (subtype 3)"

              Expect.equal (Dictionary.declaredEncoding file.Raw) (Some "UTF-8") "declared name (subtype 20)"
          } ]
