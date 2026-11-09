// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

module Fuaran.Survey.Sav.Tests.CsvRenderingTests

open System.IO
open Expecto
open Fuaran.Survey.Sav

let private codes =
    { Labels = false
      UserMissingBlank = false }

let private labels =
    { Labels = true
      UserMissingBlank = true }

let tests =
    testList
        "csv rendering"
        [ for name in Fixtures.names do
              test (sprintf "%s renders as codes byte-for-byte equal to expected.csv" name) {
                  use file = Fixtures.openFixture name
                  let actual = Fixtures.renderCsv codes file
                  let expected = File.ReadAllBytes(Fixtures.path "expected.csv")
                  Expect.equal actual expected "codes rendering"
              }

              test (sprintf "%s renders with labels and blank user-missing equal to expected-labels.csv" name) {
                  use file = Fixtures.openFixture name
                  let actual = Fixtures.renderCsv labels file
                  let expected = File.ReadAllBytes(Fixtures.path "expected-labels.csv")
                  Expect.equal actual expected "labels rendering"
              }

          test "negative control: decoding the UTF-8 fixture as windows-1252 runs but does not match" {
              use file = SavFile.openFile (Fixtures.path "plain-le.sav") (Some "windows-1252")
              let actual = Fixtures.renderCsv codes file
              let expected = File.ReadAllBytes(Fixtures.path "expected.csv")
              Expect.notEqual actual expected "a wrong encoding must not reproduce the expected text"
              Expect.equal file.Encoding.WebName "windows-1252" "the override is what was used"
          }

          test "Csv.cell quotes only when the text needs it" {
              Expect.equal (Csv.cell "plain") "plain" "no quoting"
              Expect.equal (Csv.cell "a,b") "\"a,b\"" "delimiter"
              Expect.equal (Csv.cell "say \"hi\"") "\"say \"\"hi\"\"\"" "quotes doubled"
              Expect.equal (Csv.cell "line1\nline2") "\"line1\nline2\"" "LF"
              Expect.equal (Csv.line [ "a"; "b,c" ]) "a,\"b,c\"\r\n" "CR LF line end"
          }

          test "duration and date-time formatting match the FSharpSAV conventions" {
              Expect.equal (Values.fmtDuration (System.TimeSpan.FromSeconds 3723.0)) "1:02:03" "h:mm:ss"
              Expect.equal (Values.fmtDuration (System.TimeSpan.FromHours 100.0)) "100:00:00" "hours do not wrap at 24"
              Expect.equal (Values.fmtDuration (System.TimeSpan.FromSeconds -1.5)) "-0:00:01.500" "sign and millis"
              Expect.equal (Values.fmtNum 1e301) "HIGHEST" "HIGHEST"
              Expect.equal (Values.fmtNum -1e301) "LOWEST" "LOWEST"

              Expect.equal
                  (Values.fmtDateTime (System.DateTime(2021, 3, 4, 5, 6, 7, 8)))
                  "2021-03-04 05:06:07.008"
                  "millis only when present"
          } ]
