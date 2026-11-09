// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

module Fuaran.Survey.Sav.Tests.DictionaryTests

open System.Text.Json
open Expecto
open Fuaran.Survey.Sav

let private root () = Fixtures.expectedRoot ()

let private measureOf =
    function
    | "Nominal" -> Nominal
    | "Ordinal" -> Ordinal
    | "Scale" -> Scale
    | s -> failwithf "unknown measure %s" s

let private alignmentOf =
    function
    | "AlignLeft" -> AlignLeft
    | "AlignRight" -> AlignRight
    | "AlignCentre" -> AlignCentre
    | s -> failwithf "unknown alignment %s" s

let private roleOf =
    function
    | "Input" -> Input
    | "Target" -> Target
    | "Both" -> Both
    | "NoRole" -> NoRole
    | "Partition" -> Partition
    | "Split" -> Split
    | s -> failwithf "unknown role %s" s

let tests =
    testList
        "dictionary"
        [ test "variables come in file order with long names applied" {
              use file = Fixtures.openFixture "plain-le.sav"
              let names = file.Dictionary.Variables |> Array.map (fun v -> v.Name) |> List.ofArray
              Expect.equal names (Fixtures.strings (root().GetProperty "variables")) "names"

              for kv in root().GetProperty("longNames").EnumerateObject() do
                  let v = Fixtures.variable file (kv.Value.GetString())
                  Expect.equal v.ShortName kv.Name "short name kept"
                  Expect.equal v.Name (kv.Value.GetString()) "long name"

              Expect.equal
                  (file.Dictionary.Variables |> Array.map (fun v -> v.Position) |> List.ofArray)
                  [ 0..14 ]
                  "positions"
          }

          test "a 300-byte very long string folds to one variable over two segments" {
              use file = Fixtures.openFixture "plain-be.sav"
              let essay = Fixtures.variable file "essay"
              Expect.equal essay.Width 300 "width"
              Expect.equal essay.Segments [ 255; 48 ] "segments: 255, then 300 - 252"
              Expect.equal essay.Kind Kind.String "kind"
              Expect.equal (Format.formatText essay.Width essay.PrintFormat) "A300" "format text reports the true width"

              Expect.isNone
                  (file.Dictionary.Variables |> Array.tryFind (fun v -> v.ShortName = "ESSA0"))
                  "no segment variable survives"

              Expect.equal file.Raw.Variables.Length 16 "the raw dictionary still has both segments"
          }

          test "kinds and formats follow the print format" {
              use file = Fixtures.openFixture "plain-le.sav"
              let kind n = (Fixtures.variable file n).Kind
              Expect.equal (kind "id") Kind.Numeric "id"
              Expect.equal (kind "city") Kind.String "city"
              Expect.equal (kind "dob") Kind.Date "dob"
              Expect.equal (kind "ts") Kind.DateTime "ts"
              Expect.equal (kind "dur") Kind.Duration "dur"

              let fmt n =
                  let v = Fixtures.variable file n in Format.formatText v.Width v.PrintFormat

              Expect.equal (fmt "score") "F8.2" "F8.2"
              Expect.equal (fmt "dob") "DATE11" "DATE11"
              Expect.equal (fmt "comment") "A20" "A20"

              Expect.equal
                  (Fixtures.variable file "score").WriteFormat
                  (Fixtures.variable file "score").PrintFormat
                  "write format kept"
          }

          test "labels: variable labels, value labels in code order, shared label sets" {
              use file = Fixtures.openFixture "bytecode-le.sav"
              Expect.equal (Fixtures.variable file "id").Label "Respondent ID" "variable label"
              Expect.equal (Fixtures.variable file "score").Label "" "no label"

              Expect.equal
                  (Fixtures.variable file "gender").NumLabels
                  [ 1.0, "Male"; 2.0, "Female"; 9.0, "Refused" ]
                  "numeric labels"

              Expect.equal
                  (Fixtures.variable file "city").StrLabels
                  [ "LDN", "London"; "NYC", "New York" ]
                  "short string labels"

              for kv in root().GetProperty("sharedLabelSets").EnumerateObject() do
                  let expected =
                      [ for p in kv.Value.EnumerateObject() -> float p.Name, p.Value.GetString() ]

                  Expect.equal
                      (Fixtures.variable file kv.Name).NumLabels
                      expected
                      (sprintf "%s labels from a shared type-3 record" kv.Name)
          }

          test "long string value labels come from subtype 21" {
              use file = Fixtures.openFixture "zlib-le.zsav"

              for kv in root().GetProperty("longStringLabels").EnumerateObject() do
                  let expected =
                      [ for p in kv.Value.EnumerateObject() -> p.Name, p.Value.GetString() ]

                  Expect.equal (Fixtures.variable file kv.Name).StrLabels expected kv.Name
          }

          test "missing values: discrete, range, short string and long string (subtype 22)" {
              use file = Fixtures.openFixture "plain-le.sav"
              Expect.equal (Fixtures.variable file "gender").Missing (Discrete [ 9.0 ]) "discrete"
              Expect.equal (Fixtures.variable file "score").Missing (Range(90.0, 99.0)) "range"
              Expect.equal (Fixtures.variable file "city").Missing (StringMissing [ "XXX" ]) "short string"
              Expect.equal (Fixtures.variable file "id").Missing NoMissing "none"

              for kv in root().GetProperty("longStringMissing").EnumerateObject() do
                  Expect.equal
                      (Fixtures.variable file kv.Name).Missing
                      (StringMissing(Fixtures.strings kv.Value))
                      kv.Name
          }

          test "documents are kept, one string per 80-byte line" {
              use file = Fixtures.openFixture "plain-le.sav"
              Expect.equal file.Dictionary.Documents (Fixtures.strings (root().GetProperty "documents")) "documents"
              Expect.equal file.Raw.Documents.Length 2 "raw lines"
              Expect.all file.Raw.Documents (fun l -> l.Length = 80) "80 bytes each"
          }

          test "file attributes (subtype 17) including a two-valued attribute" {
              use file = Fixtures.openFixture "bytecode-end-be.sav"

              let expected =
                  [ for p in root().GetProperty("fileAttributes").EnumerateObject() -> p.Name, Fixtures.strings p.Value ]
                  |> Map.ofList

              Expect.equal file.Dictionary.FileAttributes expected "file attributes"
          }

          test "variable attributes (subtype 18): roles are typed, other attributes kept" {
              use file = Fixtures.openFixture "plain-le.sav"

              for p in root().GetProperty("roles").EnumerateObject() do
                  Expect.equal
                      (Fixtures.variable file p.Name).Role
                      (Some(roleOf (p.Value.GetString())))
                      (sprintf "%s role" p.Name)

              Expect.isNone (Fixtures.variable file "gender").Role "no role attribute"

              for p in root().GetProperty("variableAttributes").EnumerateObject() do
                  let expected =
                      [ for a in p.Value.EnumerateObject() -> a.Name, Fixtures.strings a.Value ]
                      |> Map.ofList

                  Expect.equal (Fixtures.variable file p.Name).Attributes expected (sprintf "%s attributes" p.Name)

              Expect.isFalse
                  ((Fixtures.variable file "id").Attributes.ContainsKey "$@Role")
                  "a parsed role leaves the attribute map"

              Expect.isTrue (Fixtures.variable file "id").Attributes.IsEmpty "id has no other attributes"
          }

          test "variable sets (subtype 5)" {
              use file = Fixtures.openFixture "plain-le.sav"

              let expected =
                  [ for s in root().GetProperty("variableSets").EnumerateArray() ->
                        s.GetProperty("name").GetString(), Fixtures.strings (s.GetProperty "variables") ]

              Expect.equal file.Dictionary.VariableSets expected "variable sets"
          }

          test "multiple response sets: D and C from subtype 7, E from subtype 19" {
              use file = Fixtures.openFixture "plain-le.sav"
              let sets = file.Dictionary.MrSets
              let expected = root().GetProperty("mrSets").EnumerateArray() |> List.ofSeq
              Expect.equal sets.Length expected.Length "set count"

              for e in expected do
                  let name = e.GetProperty("name").GetString()
                  let s = sets |> List.find (fun s -> s.Name = name)
                  Expect.equal s.Label (e.GetProperty("label").GetString()) (name + " label")
                  Expect.equal s.Extended (e.GetProperty("extended").GetBoolean()) (name + " extended")
                  Expect.equal s.Variables (Fixtures.strings (e.GetProperty "variables")) (name + " variables")

                  match e.GetProperty("kind").GetString() with
                  | "C" -> Expect.equal s.Kind MultipleCategory (name + " kind")
                  | _ ->
                      let source =
                          match e.GetProperty("labelSource").GetString() with
                          | "firstVariable" -> FirstVariableLabel
                          | _ -> ExplicitLabel

                      Expect.equal
                          s.Kind
                          (MultipleDichotomy(e.GetProperty("countedValue").GetString(), source))
                          (name + " kind")
          }

          test "display parameters (subtype 11) with one entry per segment" {
              use file = Fixtures.openFixture "plain-le.sav"

              for p in root().GetProperty("display").EnumerateObject() do
                  let v = Fixtures.variable file p.Name

                  Expect.equal
                      v.Measure
                      (Some(measureOf (p.Value.GetProperty("measure").GetString())))
                      (p.Name + " measure")

                  Expect.equal v.DisplayWidth (Some(p.Value.GetProperty("width").GetInt32())) (p.Name + " width")

                  Expect.equal
                      v.Alignment
                      (Some(alignmentOf (p.Value.GetProperty("alignment").GetString())))
                      (p.Name + " alignment")
          }

          test "every extension record is kept raw, known or not" {
              use file = Fixtures.openFixture "plain-le.sav"
              let subtypes = file.Raw.Extensions |> List.map (fun e -> e.Subtype)
              Expect.equal subtypes [ 3; 4; 7; 11; 5; 13; 14; 16; 17; 18; 19; 20; 21; 22 ] "subtypes in file order"
              Expect.all file.Raw.Extensions (fun e -> e.Data.Length = e.Size * e.Count) "each holds size * count bytes"
              Expect.isGreaterThan file.Raw.DictionaryEnd 176L "the dictionary end is past the header"
          }

          test "the MR set text parser follows the PSPP examples" {
              let enc = System.Text.Encoding.UTF8

              let text =
                  "$a=C 10 my mcgroup a b c\n$b=D2 55 0  g e f d\n$c=D3 Yes 10 mdgroup #2 h i j\n"

              let sets = Dictionary.parseMrSets enc (enc.GetBytes text)
              Expect.equal (sets |> List.map (fun s -> s.Name)) [ "$a"; "$b"; "$c" ] "names"
              Expect.equal sets[0].Label "my mcgroup" "C label"
              Expect.equal sets[0].Variables [ "a"; "b"; "c" ] "C variables"
              Expect.equal sets[1].CountedValue (Some "55") "D counted"
              Expect.equal sets[1].Label "" "D empty label"
              Expect.equal sets[1].Variables [ "g"; "e"; "f"; "d" ] "D variables"
              Expect.equal sets[2].CountedValue (Some "Yes") "D string counted"
              Expect.equal sets[2].Label "mdgroup #2" "D label"

              let ext = "$d=E 1 2 34 13 third mdgroup k l m\n$e=E 11 6 choice 0  n o p\n"
              let sets = Dictionary.parseMrSets enc (enc.GetBytes ext)
              Expect.equal sets[0].LabelSourceFlag (Some 1) "E explicit"
              Expect.equal sets[0].CountedValue (Some "34") "E counted"
              Expect.equal sets[0].Label "third mdgroup" "E label"
              Expect.equal sets[1].LabelSourceFlag (Some 11) "E varlabel"
              Expect.equal sets[1].CountedValue (Some "choice") "E string counted"
              Expect.equal sets[1].Variables [ "n"; "o"; "p" ] "E variables"
          }

          test "the attribute text parser follows the PSPP example" {
              let sets = Dictionary.parseAttributes true "dummy:fred('23'\n'34'\n)bert('123'\n)"
              Expect.equal sets [ Some "dummy", [ "fred", [ "23"; "34" ]; "bert", [ "123" ] ] ] "variable attributes"
              let file = Dictionary.parseAttributes false "a('x'\n)b('it''s'\n)"
              Expect.equal file [ None, [ "a", [ "x" ]; "b", [ "it''s" ] ] ] "quotes inside a value are literal"
          } ]
