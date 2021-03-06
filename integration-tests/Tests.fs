module Main

open Npgsql.FSharp
open Npgsql.FSharp.OptionWorkflow
open System

printfn "Running Postgres integration tests"

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

let execute name f =
    printfn ""
    printfn " ============= Start %s =========== " name
    printfn ""
    try f()
    with | ex ->
        printfn "Errored!!! '%s'" name
        printfn "%A" ex
    printfn ""
    printfn " ============= End %s =========== " name
    printfn ""
    printfn ""

let defaultConnection() =
    Sql.host "localhost"
    |> Sql.port 5432
    |> Sql.username "postgres"
    |> Sql.password "postgres"
    |> Sql.database "postgres"
    |> Sql.str

let config() =
    Sql.host "localhost"
    |> Sql.port 5432
    |> Sql.username "postgres"
    |> Sql.password "postgres"
    |> Sql.database "postgres"
    |> Sql.sslMode SslMode.Require
    |> Sql.trustServerCertificate true

let seedDefaultDatabase() =
    defaultConnection()
    |> Sql.connect
    |> Sql.query "create table fsharp_tests (test_id int, test_name text)"
    |> Sql.executeNonQuery
    |> ignore

    defaultConnection()
    |> Sql.connect
    |> Sql.executeTransaction [
        "INSERT INTO fsharp_tests (test_id, test_name) values (@id, @name)", [
            [ "@id", Sql.Value 1; "@name", Sql.Value "first test" ]
            [ "@id", Sql.Value 2; "@name", Sql.Value "second test" ]
            [ "@id", Sql.Value 3; "@name", Sql.Value "thrid test" ]
        ]
    ]
    |> ignore

let cleanupDefaultDatabase() =
    defaultConnection()
    |> Sql.connect
    |> Sql.query "drop table if exists fsharp_tests"
    |> Sql.executeNonQuery
    |> ignore

execute "Database cleanup" cleanupDefaultDatabase

execute "Seeding database" seedDefaultDatabase

let handleInfinityConnection() =
    Sql.host "localhost"
    |> Sql.port 5432
    |> Sql.username "postgres"
    |> Sql.password "postgres"
    |> Sql.database "postgres"
    |> Sql.convertInfinityDateTime true
    |> Sql.str
    |> fun x ->
        printfn "%s" x
        x



execute "simple select and Sql.executeTable" <| fun _ ->
    defaultConnection()
    |> Sql.connect
    |> Sql.query "SELECT * FROM fsharp_tests"
    |> Sql.executeTable
    |> List.iter (printfn "%A")

execute "Sql.mapEachRow" <| fun _ ->
    defaultConnection()
    |> Sql.connect
    |> Sql.query "SELECT * FROM fsharp_tests"
    |> Sql.prepare
    |> Sql.executeTable
    |> Sql.mapEachRow (fun row ->
        option {
            let! id = Sql.readInt "test_id" row
            let! name = Sql.readString "test_name" row
            return { test_id = id; test_name = name }
        })
    |> printfn "%A"


execute "Sql.executeReader" <| fun _ ->
    defaultConnection()
    |> Sql.connect
    |> Sql.query "SELECT * FROM fsharp_tests"
    |> Sql.prepare
    |> Sql.executeReader (fun reader ->
        let row = Sql.readRow reader
        option {
            let! id = Sql.readInt "test_id" row
            let! name = Sql.readString "test_name" row
            return { test_id = id; test_name = name }
        })
    |> printfn "%A"


execute "Null roundtrip" <| fun _ ->
    defaultConnection()
    |> Sql.connect
    |> Sql.query "SELECT @nullValue"
    |> Sql.parameters [ "nullValue", SqlValue.Null ]
    |> Sql.executeScalar
    |> function
        | SqlValue.Null -> printfn "Succesfully returned null"
        | otherwise -> printfn "Unexpected %A" otherwise

execute "Reading time" <| fun _ ->
    defaultConnection()
    |> Sql.connect
    |> Sql.query "SELECT NOW()"
    |> Sql.executeScalar
    |> Sql.toDateTime
    |> printfn "%A"

execute "Reading time with reader" <| fun _ ->
    defaultConnection()
    |> Sql.connect
    |> Sql.query "SELECT NOW()::timestamp AS time"
    |> Sql.executeReader (Sql.readRow >> Sql.readTimestamp "time")
    |> printfn "%A"

execute "Reading time with reader async" <| fun _ ->
    defaultConnection()
    |> Sql.connect
    |> Sql.query "SELECT NOW()::timestamp AS time"
    |> Sql.executeReaderAsync (Sql.readRow >> Sql.readTimestamp "time")
    |> Async.RunSynchronously
    |> printfn "%A"

execute "Reading time with reader safe async" <| fun _ ->
    defaultConnection()
    |> Sql.connect
    |> Sql.query "SELECT NOW()::timestamp AS time"
    |> Sql.executeReaderSafeAsync (Sql.readRow >> Sql.readTimestamp "time")
    |> Async.RunSynchronously
    |> function
        | Ok date -> printfn "%A" date
        | Error error ->  printfn "%A" error

execute "Sql.qeuryMany and Sql.executeMany" <| fun _ ->
    let store = "SELECT * FROM fsharp_tests"

    let storeMetadata =
      Sql.multiline
        ["select column_name, data_type"
         "from information_schema.columns"
         "where table_name = 'fsharp_tests'"]

    defaultConnection()
    |> Sql.connect
    |> Sql.queryMany [store; storeMetadata]
    |> Sql.executeMany
    |> List.iter (fun table ->
        printfn "Table:\n%A\n" table)

execute "Enable hstore extension" <| fun _ ->
    defaultConnection()
    |> Sql.connect
    |> Sql.query "CREATE EXTENSION IF NOT EXISTS hstore"
    |> Sql.executeNonQuery
    |> printfn "Create Extention hstore returned %A"


// Unhandled Exception: System.NotSupportedException: Npgsql 3.x removed support for writing a parameter with an IEnumerable value, use .ToList()/.ToArray() instead
// Need to add a NpgsqlTypeHandler for Map ?

execute "HStore roundtrip" <| fun _ ->
    let inputMap =
        ["property", "value from F#"]
        |> Map.ofSeq

    printfn "HStore roundtrip start"

    defaultConnection()
    |> Sql.connect
    |> Sql.query "select @map"
    |> Sql.parameters ["map", Sql.Value inputMap]
    |> Sql.executeScalar
    |> function
        | SqlValue.HStore map ->
            match Map.tryFind "property" map with
            | Some "value from F#" -> "Mapping HStore works"
            | _ -> "Something went wrong when reading HStore"
        | _ -> "Something went wrong when mapping HStore"
    |> printfn "%A"

// printfn "HStore roundtrip end"
let jsonData = "value from F#"
let inputJson = "{\"property\": \"" + jsonData + "\"}"
execute "Jsonb roundtrip" <| fun _ ->
    defaultConnection()
    |> Sql.connect
    |> Sql.query "select @jsonb"
    |> Sql.parameters ["jsonb", SqlValue.Jsonb inputJson]
    |> Sql.executeScalar
    |> function
        | SqlValue.String json ->
            match inputJson = json with
            | true -> "Mapping Jsonb works, but you have to match SqlValue.String"
            | _ -> sprintf "Something went wrong when reading Jsonb, expected %s but got %s" inputJson json
        | x -> sprintf "Something went wrong when mapping Jsonb, %A" x
    |> printfn "%A"

execute "Create table with Jsonb data" <| fun _ ->
    defaultConnection()
    |> Sql.connect
    |> Sql.query "CREATE TABLE IF NOT EXISTS data_with_jsonb (data jsonb) "
    |> Sql.executeNonQuery
    |> printfn "Create Table data_with_jsonb returned %A"

    defaultConnection()
    |> Sql.connect
    |> Sql.query "INSERT INTO data_with_jsonb (data) VALUES (@jsonb)"
    |> Sql.parameters ["jsonb", SqlValue.Jsonb inputJson]
    |> Sql.executeNonQuery
    |> printfn "Insert into data_with_jsonb returned %A"

    defaultConnection()
    |> Sql.connect
    |> Sql.query "SELECT data ->> 'property' FROM data_with_jsonb"
    |> Sql.executeScalar
    |> function
        | SqlValue.String json ->
            match jsonData = json with
            | true -> sprintf "SELECT with json function works. Got *%s* as expected" json
            | _ -> sprintf "Something went wrong when reading json, expected %s but got %s" jsonData json
        | x -> sprintf "Something went wrong when mapping json, %A" x
    |> printfn "%A"

    defaultConnection()
    |> Sql.connect
    |> Sql.query "DROP TABLE data_with_jsonb"
    |> Sql.executeNonQuery
    |> printfn "Drop Table data_with_jsonb returned %A"


execute "bytea roundtrip" <| fun _ ->
    let bytesInput =
        [1 .. 5]
        |> List.map byte
        |> Array.ofList

    defaultConnection()
    |> Sql.connect
    |> Sql.query "SELECT @manyBytes"
    |> Sql.parameters [ "manyBytes", Sql.Value bytesInput ]
    |> Sql.executeScalar
    |> function
        | SqlValue.Bytea output ->
            if (output <> bytesInput)
            then failwith "Bytea roundtrip failed, the output was different"
            else printfn "Bytea roundtrip worked"

        | _ -> failwith "Bytea roundtrip failed"

execute "Uuid roundtrip" <| fun _ ->
    let guid = System.Guid.NewGuid()

    defaultConnection()
    |> Sql.connect
    |> Sql.query "SELECT @uuid_input"
    |> Sql.parameters [ "uuid_input", Sql.Value guid ]
    |> Sql.executeScalar
    |> function
        | SqlValue.Uuid output ->
            if (output.ToString() <> guid.ToString())
            then failwith "Uuid roundtrip failed, the output was different"
            else printfn "Uuid roundtrip worked"

        | _ -> failwith "Uuid roundtrip failed"

defaultConnection()
|> Sql.connect
|> Sql.query "SELECT @money_input::money"
|> Sql.parameters [ "money_input", Sql.Value 12.5M ]
|> Sql.executeScalar
|> function
    | SqlValue.Decimal 12.5M -> printfn "Money as decimal roundtrip worked"
    | _ -> failwith "Money as decimal roundtrip failed"

execute "uuid_generate_v4()" <| fun _ ->
    defaultConnection()
    |> Sql.connect
    |> Sql.query "CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\""
    |> Sql.executeNonQuery
    |> printfn "Create Extention uuid-ossp returned %A"

    defaultConnection()
    |> Sql.connect
    |> Sql.query "SELECT uuid_generate_v4()"
    |> Sql.executeScalar
    |> function
        | SqlValue.Uuid output -> printfn "Uuid generated: %A" output
        | _ -> failwith "Uuid could not be read failed"

execute "test inifinity time" <| fun _ ->
    defaultConnection()
    |> Sql.connect
    |> Sql.query "CREATE TABLE IF NOT EXISTS data (version integer, date1 timestamptz, date2 timestamptz) "
    |> Sql.executeNonQuery
    |> printfn "Create Table data returned %A"

    let delete = "DELETE from data"
    let insert = "INSERT INTO data (version, date1, date2) values (1, 'now', 'infinity')"

    defaultConnection()
    |> Sql.connect
    |> Sql.queryMany [ delete; insert ]
    |> Sql.executeMany
    |> printfn "Insert into Table data returned %A"

    defaultConnection()
    |> Sql.connect
    |> Sql.query "SELECT * FROM data"
    |> Sql.executeTableSafe
    |> function
        | Ok _ -> failwith "Should be able to convert infinity to datetime"
        | Error ex -> printfn "Fails as expected with %A" ex.Message

execute "Handle infinity connection" <| fun _ ->
    handleInfinityConnection()
    |> Sql.connect
    |> Sql.query "SELECT * FROM data"
    |> Sql.executeTableSafe
    |> function
        | Ok r -> printfn "Succeed as expected : %A vs %A" (r.Head.Item 2) System.DateTime.MaxValue
        | Error err ->
            printfn "%A" err
            failwith "Should not fail"

execute "Handle TimeSpan" <| fun _ ->
    defaultConnection()
    |> Sql.connect
    |> Sql.query "create table if not exists timespan_test (id int, at time without time zone)"
    |> Sql.executeNonQuery
    |> ignore

    let t1 = TimeSpan(13, 45, 23)
    let t2 = TimeSpan(16, 17, 09)
    let t3 = TimeSpan(20, 02, 56)

    defaultConnection()
    |> Sql.connect
    |> Sql.executeTransaction [
        "INSERT INTO timespan_test (id, at) values (@id, @at)", [
            [ "@id", Sql.Value 1; "@at", Sql.Value t1 ]
            [ "@id", Sql.Value 2; "@at", Sql.Value t2 ]
            [ "@id", Sql.Value 3; "@at", Sql.Value t3 ]
        ]
    ]
    |> ignore

    // Use `parseEachRow<T>`
    defaultConnection()
    |> Sql.connect
    |> Sql.query "SELECT * FROM timespan_test"
    |> Sql.executeTable
    |> Sql.parseEachRow<TimeSpanTest>
    |> printfn "TimeSpan records: %A"

    // Use `mapEachRow` + `readTime`
    defaultConnection()
    |> Sql.connect
    |> Sql.query "SELECT * FROM timespan_test"
    |> Sql.prepare
    |> Sql.executeTable
    |> Sql.mapEachRow (fun row ->
        option {
            let! id = Sql.readInt "id" row
            let! at = Sql.readTime "at" row
            return { id = id; at = at }
        })
    |> ignore

    defaultConnection()
    |> Sql.connect
    |> Sql.query "drop table if exists timespan_test"
    |> Sql.executeNonQuery
    |> ignore

execute "Handle String Array" <| fun _ ->
    defaultConnection()
    |> Sql.connect
    |> Sql.query "create table if not exists string_array_test (id int, values text [])"
    |> Sql.executeNonQuery
    |> ignore

    let getString () =
        let temp = Guid.NewGuid()
        temp.ToString("N")
    let a = [| getString() |]
    let b = [| getString(); getString() |]
    let c : string array = [||]

    defaultConnection()
    |> Sql.connect
    |> Sql.executeTransaction [
        "INSERT INTO string_array_test (id, values) values (@id, @values)", [
            [ "@id", Sql.Value 1; "@values", Sql.Value a ]
            [ "@id", Sql.Value 2; "@values", Sql.Value b ]
            [ "@id", Sql.Value 3; "@values", Sql.Value c ]
        ]
    ]
    |> ignore

    // Use `parseEachRow<T>`
    defaultConnection()
    |> Sql.connect
    |> Sql.query "SELECT * FROM string_array_test"
    |> Sql.executeTable
    |> Sql.parseEachRow<StringArrayTest>
    |> printfn "StringArray records with parseEachRow:\n %A"


    // Use `mapEachRow` + `readStringArray`
    defaultConnection()
    |> Sql.connect
    |> Sql.query "SELECT * FROM string_array_test"
    |> Sql.prepare
    |> Sql.executeTable
    |> Sql.mapEachRow (fun row ->
        option {
            let! id = Sql.readInt "id" row
            let! values = Sql.readStringArray "values" row
            return { id = id; values = values }
        })
    |> printfn "StringArray records with  `mapEachRow` + `readStringArray` :\n %A"

    defaultConnection()
    |> Sql.connect
    |> Sql.query "drop table if exists string_array_test"
    |> Sql.executeNonQuery
    |> ignore


execute "Handle int Array" <| fun _ ->
    defaultConnection()
    |> Sql.connect
    |> Sql.query "create table if not exists int_array_test (id int, integers int [])"
    |> Sql.executeNonQuery
    |> ignore


    let a = [| 1; 2 |]
    let b = [| for i in 0..10 do yield i |]
    let c : int array = [||]

    defaultConnection()
    |> Sql.connect
    |> Sql.executeTransaction [
        "INSERT INTO int_array_test (id, integers) values (@id, @integers)", [
            [ "@id", Sql.Value 1; "@integers", Sql.Value a ]
            [ "@id", Sql.Value 2; "@integers", Sql.Value b ]
            [ "@id", Sql.Value 3; "@integers", Sql.Value c ]
        ]
    ]
    |> ignore

    // Use `parseEachRow<T>`
    defaultConnection()
    |> Sql.connect
    |> Sql.query "SELECT * FROM int_array_test"
    |> Sql.executeTable
    |> Sql.parseEachRow<IntArrayTest>
    |> printfn "StringArray records with parseEachRow:\n %A"


    // Use `mapEachRow` + `readStringArray`
    defaultConnection()
    |> Sql.connect
    |> Sql.query "SELECT * FROM int_array_test"
    |> Sql.prepare
    |> Sql.executeTable
    |> Sql.mapEachRow (fun row ->
        option {
            let! id = Sql.readInt "id" row
            let! integers = Sql.readIntArray "integers" row
            return { id = id; integers = integers }
        })
    |> printfn "StringArray records with  `mapEachRow` + `readStringArray` :\n %A"

    defaultConnection()
    |> Sql.connect
    |> Sql.query "drop table if exists int_array_test"
    |> Sql.executeNonQuery
    |> ignore

execute "Local UTC time" <| fun _ ->
    defaultConnection()
    |> Sql.connect
    |> Sql.query "SELECT localtime"
    |> Sql.executeScalar
    |> Sql.toTime
    |> printfn "%A"

defaultConnection()
|> Sql.connect
|> Sql.query "DROP TABLE data"
|> Sql.executeNonQuery
|> printfn "Drop Table data returned %A"

cleanupDefaultDatabase()
