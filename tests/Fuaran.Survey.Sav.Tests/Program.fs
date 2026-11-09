// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

module Fuaran.Survey.Sav.Tests.Program

open Expecto

let all =
    testList
        "Fuaran.Survey.Sav"
        [ CsvRenderingTests.tests
          HeaderTests.tests
          DictionaryTests.tests
          IntegrityTests.tests
          ColumnarTests.tests ]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv all
