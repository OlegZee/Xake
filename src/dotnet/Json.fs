namespace Xake.Dotnet

/// Just enough JSON for what msbuild's `-getItem`/`-getProperty` writes. A parser of our
/// own rather than System.Text.Json, which is a package dependency on netstandard2.0 and
/// would land on every consumer of Xake.
module internal Json =

    type Value =
        | JString of string
        | JObject of (string * Value) list
        | JArray of Value list
        /// numbers, booleans and null -- msbuild writes none of them, kept so that an
        /// unexpected value does not fail the whole parse
        | JOther of string

    let parse (text: string) =
        let mutable pos = 0
        let fail message = failwithf "malformed json at %d: %s" pos message
        let skipWs () = while pos < text.Length && System.Char.IsWhiteSpace text.[pos] do pos <- pos + 1
        let expect (c: char) =
            skipWs ()
            if pos >= text.Length || text.[pos] <> c then fail (sprintf "expected '%c'" c)
            pos <- pos + 1

        let parseString () =
            expect '"'
            let value = System.Text.StringBuilder()
            let mutable closed = false
            while not closed do
                if pos >= text.Length then fail "unterminated string"
                let c = text.[pos]
                pos <- pos + 1
                match c with
                | '"' -> closed <- true
                | '\\' ->
                    let escaped = text.[pos]
                    pos <- pos + 1
                    match escaped with
                    | 'n' -> value.Append '\n' |> ignore
                    | 't' -> value.Append '\t' |> ignore
                    | 'r' -> value.Append '\r' |> ignore
                    | 'b' -> value.Append '\b' |> ignore
                    | 'f' -> value.Append '\012' |> ignore
                    | 'u' ->
                        value.Append (char (System.Convert.ToInt32 (text.Substring (pos, 4), 16))) |> ignore
                        pos <- pos + 4
                    | c -> value.Append c |> ignore
                | c -> value.Append c |> ignore
            value.ToString()

        // defined ahead of parseValue and taking the item parser as an argument: inside a
        // `let rec ... and ...` group it would be pinned to one item type
        let parseSequence closing (parseItem: unit -> 'item) : 'item list =
            pos <- pos + 1
            let items = ResizeArray()
            skipWs ()
            if text.[pos] = closing then pos <- pos + 1
            else
                let mutable more = true
                while more do
                    items.Add (parseItem ())
                    skipWs ()
                    match text.[pos] with
                    | ',' -> pos <- pos + 1
                    | c when c = closing -> pos <- pos + 1; more <- false
                    | c -> fail (sprintf "unexpected '%c'" c)
            List.ofSeq items

        let rec parseValue () =
            skipWs ()
            if pos >= text.Length then fail "unexpected end of input"
            match text.[pos] with
            | '"' -> JString (parseString ())
            | '{' -> JObject (parseSequence '}' (fun () ->
                        skipWs ()
                        let name = parseString ()
                        expect ':'
                        name, parseValue ()))
            | '[' -> JArray (parseSequence ']' parseValue)
            | _ ->
                let start = pos
                while pos < text.Length && text.[pos] <> ',' && text.[pos] <> '}' && text.[pos] <> ']'
                      && not (System.Char.IsWhiteSpace text.[pos]) do
                    pos <- pos + 1
                JOther (text.Substring (start, pos - start))

        let value = parseValue ()
        skipWs ()
        value

    /// Escapes a string as a json literal.
    let escape (value: string) =
        let escaped = System.Text.StringBuilder()
        for c in value do
            match c with
            | '"' -> escaped.Append "\\\"" |> ignore
            | '\\' -> escaped.Append "\\\\" |> ignore
            | '\n' -> escaped.Append "\\n" |> ignore
            | '\r' -> escaped.Append "\\r" |> ignore
            | '\t' -> escaped.Append "\\t" |> ignore
            | c when c < ' ' -> escaped.AppendFormat ("\\u{0:x4}", int c) |> ignore
            | c -> escaped.Append c |> ignore
        sprintf "\"%s\"" (escaped.ToString())

    let field name = function
        | JObject members -> members |> List.tryPick (fun (n, v) -> if n = name then Some v else None)
        | _ -> None

    let asString = function | JString s -> Some s | _ -> None
    let asArray = function | JArray items -> items | _ -> []
    let asBool = function | JOther "true" -> Some true | JOther "false" -> Some false | _ -> None
