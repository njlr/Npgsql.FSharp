module Main

open Expecto
open Npgsql.FSharp
open Npgsql.FSharp.OptionWorkflow
open System
open ThrowawayDb.Postgres

type FsTest = {
    test_id: int
    test_name: string
}

type TimeSpanTest = {
    id: int
    at: TimeSpan
}

type StringArrayTest = {
    id: int
    values:string array
}

type IntArrayTest = {
    id: int
    integers: int array
}

let buildDatabaseConnection handleInfinity : ThrowawayDatabase =
    let createFSharpTable = "create table if not exists fsharp_test (test_id int, test_name text)"
    let createJsonbTable = "create table if not exists data_with_jsonb (data jsonb)"
    let createTimestampzTable = "create table if not exists timestampz_test (version integer, date1 timestamptz, date2 timestamptz)"
    let createTimespanTable = "create table if not exists timespan_test (id int, at time without time zone)"
    let createStringArrayTable = "create table if not exists string_array_test (id int, values text [])"
    let createIntArrayTable = "create table if not exists int_array_test (id int, integers int [])"
    let createExtensionHStore = "create extension if not exists hstore"
    let createExtensionUuid = "create extension if not exists \"uuid-ossp\""

    // Travis CI uses an empty string for the password of the database
    let databasePassword =
        let runningTravis = Environment.GetEnvironmentVariable "TESTING_IN_TRAVISCI"
        if isNull runningTravis || String.IsNullOrWhiteSpace runningTravis
        then "postgres" // for local tests
        else "" // for Travis CI

    let connection =
        Sql.host "localhost"
        |> Sql.port 5432
        |> Sql.username "postgres"
        |> Sql.password databasePassword
        |> Sql.convertInfinityDateTime handleInfinity
        |> Sql.formatConnectionString

    let database = ThrowawayDatabase.Create(connection)

    database.ConnectionString
    |> Sql.connect
    |> Sql.executeTransaction [
        createFSharpTable, [ ]
        createJsonbTable, [ ]
        createTimestampzTable, [ ]
        createTimespanTable, [ ]
        createStringArrayTable, [ ]
        createExtensionHStore, [ ]
        createIntArrayTable, [ ]
        createExtensionUuid, [ ]
    ]
    |> ignore

    database

let buildDatabase() = buildDatabaseConnection false
let buildInfinityDatabase() = buildDatabaseConnection true

let tests =
    testList "Integration tests" [
        testList "RowReader tests used in Sql.read and Sql.readAsync" [
            test "Sql.read works" {
                use db = buildDatabase()
                Sql.connect db.ConnectionString
                |> Sql.query "CREATE TABLE users (user_id serial primary key, username text not null, active bit not null, salary money not null)"
                |> Sql.executeNonQuery
                |> ignore

                Sql.connect db.ConnectionString
                |> Sql.executeTransaction [
                    "INSERT INTO users (username, active, salary) VALUES (@username, @active, @salary)", [
                        [ ("@username", Sql.text "first"); ("active", Sql.bit true); ("salary", Sql.money 1.0M)  ]
                        [ ("@username", Sql.text "second"); ("active", Sql.bit false); ("salary", Sql.money 1.0M) ]
                        [ ("@username", Sql.text "third"); ("active", Sql.bit true);("salary", Sql.money 1.0M) ]
                    ]
                ]
                |> ignore

                let expected = [
                    {| userId = 1; username = "first"; active = true; salary = 1.0M  |}
                    {| userId = 2; username = "second"; active = false ; salary = 1.0M |}
                    {| userId = 3; username = "third"; active = true ; salary = 1.0M |}
                ]

                Sql.connect db.ConnectionString
                |> Sql.query "SELECT * FROM users"
                |> Sql.execute (fun read ->
                    {|
                        userId = read.int "user_id"
                        username = read.string "username"
                        active = read.bool "active"
                        salary = read.decimal "salary"
                    |})
                |> function
                | Error err -> raise err
                | Ok users -> Expect.equal users expected "Users can be read correctly"
            }
        ]

        testList "Query-only parallel tests without recreating database" [
            test "Null roundtrip" {
                use db = buildDatabase()
                let connection : string = db.ConnectionString
                connection
                |> Sql.connect
                |> Sql.query "SELECT @nullValue::text as output"
                |> Sql.parameters [ "nullValue", Sql.dbnull ]
                |> Sql.execute (fun read -> read.textOrNull "output")
                |> function
                    | Error error -> raise error
                    | Ok output -> Expect.isNone output.[0] "Output was null"
            }

            test "Bytea roundtrip" {
                use db = buildDatabase()
                let input : array<byte> = [1 .. 5] |> List.map byte |> Array.ofList
                db.ConnectionString
                |> Sql.connect
                |> Sql.query "SELECT @manyBytes as output"
                |> Sql.parameters [ "manyBytes", Sql.bytea input ]
                |> Sql.execute (fun read -> read.bytea "output")
                |> function
                    | Error error -> raise error
                    | Ok output -> Expect.equal input output.[0] "Check bytes read from database are the same sent"
            }

            test "Uuid roundtrip" {
                use db = buildDatabase()
                let id : Guid = Guid.NewGuid()
                db.ConnectionString
                |> Sql.connect
                |> Sql.query "SELECT @uuid_input as output"
                |> Sql.parameters [ "uuid_input", Sql.uuid id ]
                |> Sql.execute (fun read -> read.uuid "output")
                |> function
                    | Error error -> raise error
                    | Ok output -> Expect.equal id output.[0] "Check uuid read from database is the same sent"
            }

            test "Money roundtrip with @ sign" {
                use db = buildDatabase()
                db.ConnectionString
                |> Sql.connect
                |> Sql.query "SELECT @money_input::money as value"
                |> Sql.parameters [ "@money_input", Sql.money 12.5M ]
                |> Sql.execute (fun read -> read.decimal "value")
                |> function
                    | Error error -> raise error
                    | Ok money -> Expect.equal money.[0] 12.5M "Check money as decimal read from database is the same sent"
            }

            test "uuid_generate_v4()" {
                use db = buildDatabase()
                db.ConnectionString
                |> Sql.connect
                |> Sql.query "SELECT uuid_generate_v4() as id"
                |> Sql.execute (fun read -> read.uuid "id")
                |> function
                    | Error error -> raise error
                    | Ok [ uuid ] ->  Expect.isNotNull (uuid.ToString()) "Check database generates an UUID"
                    | Ok _ -> failwith "Should not happpen"
            }

            test "String option roundtrip" {
                use db = buildDatabase()
                let connection : string = db.ConnectionString
                let a : string option = Some "abc"
                let b : string option = None
                let row =
                    connection
                    |> Sql.connect
                    |> Sql.query "SELECT @a::text as first, @b::text as second"
                    |> Sql.parameters [ "a", Sql.textOrNull a; "b", Sql.textOrNull b ]
                    |> Sql.execute (fun read -> read.textOrNull "first", read.textOrNull "second")

                match row with
                | Ok [ (Some output, None) ] ->
                    Expect.equal a (Some output) "Check Option value read from database is the same as the one sent"
                | Ok (_) ->
                    failwith "Unexpected results"
                | Error error ->
                    raise error
            }
        ]

        testList "Sequential tests that update database state" [

            test "Sql.execute" {
                let seedDatabase (connection: string) =
                    connection
                    |> Sql.connect
                    |> Sql.executeTransaction [
                        "INSERT INTO fsharp_test (test_id, test_name) values (@id, @name)", [
                            [ "@id", Sql.int 1; "@name", Sql.text "first test" ]
                            [ "@id", Sql.int 2; "@name", Sql.text "second test" ]
                            [ "@id", Sql.int 3; "@name", Sql.text "third test" ]
                        ]
                    ]
                    |> ignore
                use db = buildDatabase()
                let connection : string = db.ConnectionString
                seedDatabase connection

                let table =
                    Sql.connect connection
                    |> Sql.query "SELECT * FROM fsharp_test"
                    |> Sql.prepare
                    |> Sql.execute (fun read -> {
                        test_id = read.int "test_id";
                        test_name = read.string "test_name"
                    })

                let expected = [
                    { test_id = 1; test_name = "first test" }
                    { test_id = 2; test_name = "second test" }
                    { test_id = 3; test_name = "third test" }
                ]

                match table with
                | Error err -> raise err
                | Ok table -> Expect.equal expected table "Check all rows from `fsharp_test` table using a Reader"
            }

            test "Create table with Jsonb data" {
                let seedDatabase (connection: string) (json: string) =
                    connection
                    |> Sql.connect
                    |> Sql.query "INSERT INTO data_with_jsonb (data) VALUES (@jsonb)"
                    |> Sql.parameters ["jsonb", SqlValue.Jsonb json]
                    |> Sql.executeNonQuery
                    |> ignore
                let jsonData = "value from F#"
                let inputJson = "{\"property\": \"" + jsonData + "\"}"
                use db = buildDatabase()
                let connection : string = db.ConnectionString
                seedDatabase connection inputJson

                let dbJson =
                    connection
                    |> Sql.connect
                    |> Sql.query "SELECT data ->> 'property' as property FROM data_with_jsonb"
                    |> Sql.execute(fun read -> read.text "property")

                match dbJson with
                | Error error -> raise error
                | Ok json -> Expect.equal json.[0] jsonData "Check json read from database"
            }

            test "Infinity time" {
                let seedDatabase (connection: string) =
                    connection
                    |> Sql.connect
                    |> Sql.query "INSERT INTO timestampz_test (version, date1, date2) values (1, 'now', 'infinity')"
                    |> Sql.executeNonQuery
                    |> ignore
                use db = buildDatabase()
                let connection : string = db.ConnectionString
                seedDatabase connection

                let dataTable =
                    connection
                    |> Sql.connect
                    |> Sql.query "SELECT * FROM timestampz_test"
                    |> Sql.execute (fun read -> read.timestamptz "date2")

                Expect.isOk dataTable "Should be able to get results"
            }

            //test "Handle infinity connection" {
            //    let seedDatabase (connection: string) =
            //        connection
            //        |> Sql.connect
            //        |> Sql.query "INSERT INTO timestampz_test (version, date1, date2) values (1, 'now', 'infinity')"
            //        |> Sql.executeNonQuery
            //        |> ignore
            //    use db = buildInfinityDatabase()
            //    let connection : string = db.ConnectionString
            //    seedDatabase connection
            //    let dataTable =
            //        connection
            //        |> Sql.connect
            //        |> Sql.query "SELECT date2 FROM timestampz_test"
            //        |> Sql.executeSingleRow (fun read -> read.timestamptz "date2")
//
            //    match dataTable with
            //    | Error error -> raise error
            //    | Ok timestamp -> Expect.isTrue timestamp.IsInfinity "Returned timestamp is infinity"
            //}

            //test "Handle TimeSpan" {
            //    let t1 = TimeSpan(13, 45, 23)
            //    let t2 = TimeSpan(16, 17, 09)
            //    let t3 = TimeSpan(20, 02, 56)
            //    let seedDatabase (connection: string) =
            //        connection
            //        |> Sql.connect
            //        |> Sql.executeTransaction [
            //            "INSERT INTO timespan_test (id, at) values (@id, @at)", [
            //                [ "@id", Sql.Value 1; "@at", Sql.Value t1 ]
            //                [ "@id", Sql.Value 2; "@at", Sql.Value t2 ]
            //                [ "@id", Sql.Value 3; "@at", Sql.Value t3 ]
            //            ]
            //        ]
            //        |> ignore
            //    use db = buildDatabase()
            //    let connection : string = db.ConnectionString
            //    seedDatabase connection
//
            //    // Use `parseEachRow<T>`
            //    let table : list<TimeSpanTest> =
            //        connection
            //        |> Sql.connect
            //        |> Sql.query "SELECT * FROM timespan_test"
            //        |> Sql.executeReader (Sql.readRow >> Some)
            //        |> Sql.parseEachRow<TimeSpanTest>
            //    Expect.equal
            //        [
            //            { id = 1; at = t1 }
            //            { id = 2; at = t2 }
            //            { id = 3; at = t3 }
            //        ]
            //        table
            //        "All rows from `timespan_test` table using `parseEachRow`"
//
            //    // Use `mapEachRow` + `readTime`
            //    let table =
            //        connection
            //        |> Sql.connect
            //        |> Sql.query "SELECT * FROM timespan_test"
            //        |> Sql.prepare
            //        |> Sql.executeReader (Sql.readRow >> Some)
            //        |> Sql.mapEachRow (fun row ->
            //            option {
            //                let! id = Sql.readInt "id" row
            //                let! at = Sql.readTime "at" row
            //                return { id = id; at = at }
            //            })
            //    Expect.equal
            //        [
            //            { id = 1; at = TimeSpan(13, 45, 23) }
            //            { id = 2; at = TimeSpan(16, 17, 09) }
            //            { id = 3; at = TimeSpan(20, 02, 56) }
            //        ]
            //        table
            //        "All rows from `timespan_test` table using `mapEachRow`"
            //}

            test "Handle String Array" {
                let getString () =
                    let temp = Guid.NewGuid()
                    temp.ToString("N")
                let a = [| getString() |]
                let b = [| getString(); getString() |]
                let c : string array = [||]
                let seedDatabase (connection: string) =
                    connection
                    |> Sql.connect
                    |> Sql.executeTransaction [
                        "INSERT INTO string_array_test (id, values) values (@id, @values)", [
                            [ "@id", Sql.int 1; "@values", Sql.stringArray a ]
                            [ "@id", Sql.int 2; "@values", Sql.stringArray b ]
                            [ "@id", Sql.int 3; "@values", Sql.stringArray c ]
                        ]
                    ]
                    |> ignore

                use db = buildDatabase()
                let connection : string = db.ConnectionString
                seedDatabase connection

                let table =
                    connection
                    |> Sql.connect
                    |> Sql.query "SELECT * FROM string_array_test"
                    |> Sql.execute (fun read -> {
                        id = read.int "id"
                        values = read.stringArray "values"
                    })

                let expected = [
                    { id = 1; values = a }
                    { id = 2; values = b }
                    { id = 3; values = c }
                ]

                match table with
                | Error error -> raise error
                | Ok values -> Expect.equal expected values "All rows from `string_array_test` table"
            }

            test "Handle int Array" {
                let a = [| 1; 2 |]
                let b = [| for i in 0..10 do yield i |]
                let c : int array = [||]
                let seedDatabase (connection: string) =
                    connection
                    |> Sql.connect
                    |> Sql.executeTransaction [
                        "INSERT INTO int_array_test (id, integers) values (@id, @integers)", [
                            [ "@id", Sql.int 1; "@integers", Sql.intArray a ]
                            [ "@id", Sql.int 2; "@integers", Sql.intArray b ]
                            [ "@id", Sql.int 3; "@integers", Sql.intArray c ]
                        ]
                    ]
                    |> ignore

                use db = buildDatabase()
                let connection : string = db.ConnectionString
                seedDatabase connection

                let table =
                    connection
                    |> Sql.connect
                    |> Sql.query "SELECT * FROM int_array_test"
                    |> Sql.execute (fun read -> {
                        id = read.int "id"
                        integers = read.intArray "integers"
                    })

                let expected = [
                    { id = 1; integers = a }
                    { id = 2; integers = b }
                    { id = 3; integers = c }
                ]

                match table with
                | Error error -> raise error
                | Ok table -> Expect.equal expected table  "All rows from `int_array_test` table"
            }

        ] |> testSequenced

    ]

[<EntryPoint>]
let main args = runTestsWithArgs defaultConfig args tests
