// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

module Fuaran.Survey.Sav.Tests.IntegrityTests

open System.IO
open System.Text
open Expecto
open Fuaran.Survey.Sav

let tests =
    testList
        "integrity"
        [ test "the fingerprint is stable across two reads of one file" {
              use a = Fixtures.openFixture "plain-le.sav"
              use b = Fixtures.openFixture "plain-le.sav"
              Expect.equal a.Dictionary.Fingerprint b.Dictionary.Fingerprint "same bytes, same fingerprint"
              Expect.equal a.Dictionary.Fingerprint.Length 64 "SHA-256 as lower-case hex"
              Expect.equal (a.Dictionary.Fingerprint.ToLowerInvariant()) a.Dictionary.Fingerprint "lower-case"
          }

          test "the fingerprint differs between the little- and big-endian fixtures" {
              use le = Fixtures.openFixture "plain-le.sav"
              use be = Fixtures.openFixture "plain-be.sav"
              Expect.notEqual le.Dictionary.Fingerprint be.Dictionary.Fingerprint "different dictionary bytes"
          }

          test "every fixture scans to four cases, Complete" {
              for name in Fixtures.names do
                  use file = Fixtures.openFixture name
                  let _, count, ending = Fixtures.readAll file
                  Expect.equal (count, ending) (4, Complete) name
          }

          test "a file cut short part-way through a case reports Truncated" {
              let bytes = File.ReadAllBytes(Fixtures.path "plain-le.sav")
              let dir = Fixtures.tempDir ()
              let cut = Path.Combine(dir, "truncated.sav")
              File.WriteAllBytes(cut, bytes[0 .. bytes.Length - 101])
              use file = SavFile.openFile cut None
              let _, count, ending = Fixtures.readAll file
              Expect.equal ending Truncated "ending"
              Expect.equal count 3 "the three whole cases were delivered"
          }

          test "a file whose header promises more cases than it holds reports ShortOfHeader" {
              let bytes = File.ReadAllBytes(Fixtures.path "plain-le.sav")
              use probe = Fixtures.openFixture "plain-le.sav"

              let caseBytes =
                  (probe.Raw.Variables
                   |> List.sumBy (fun v -> if v.Width = 0 then 1 else (v.Width + 7) / 8))
                  * 8

              let dir = Fixtures.tempDir ()
              let cut = Path.Combine(dir, "short.sav")
              File.WriteAllBytes(cut, bytes[0 .. bytes.Length - caseBytes - 1])
              use file = SavFile.openFile cut None
              let _, count, ending = Fixtures.readAll file
              Expect.equal (count, ending) (3, ShortOfHeader) "one whole case missing"
          }

          test "a file that is not an SPSS system file is refused on open" {
              let dir = Fixtures.tempDir ()
              let bad = Path.Combine(dir, "not-spss.sav")
              File.WriteAllText(bad, String.replicate 20 "not an SPSS file ")
              Expect.throws (fun () -> (SavFile.openFile bad None :> System.IDisposable).Dispose()) "magic check"
              let tiny = Path.Combine(dir, "tiny.sav")
              File.WriteAllBytes(tiny, Encoding.ASCII.GetBytes "$FL2")
              Expect.throws (fun () -> (SavFile.openFile tiny None :> System.IDisposable).Dispose()) "too short"
          }

          test "an unknown encoding override is refused" {
              Expect.throws
                  (fun () ->
                      (SavFile.openFile (Fixtures.path "plain-le.sav") (Some "no-such-encoding") :> System.IDisposable)
                          .Dispose())
                  "unknown encoding"
          } ]
