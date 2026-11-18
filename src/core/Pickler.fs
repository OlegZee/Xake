module Xake.Pickler

open System

/// Pickler Combinators implementation
type OutState = IO.BinaryWriter
type InState = IO.BinaryReader

/// <summary>
/// Main pickler type.
/// </summary>
type 'a PU = { pickle: 'a -> OutState -> unit; unpickle:  InState -> 'a }

/// <summary>
/// Unit pickler, does nothing.
/// </summary>
let unit = {pickle = (fun () _ -> ());   unpickle = ignore}

/// <summary>
/// Translates pickler of one type into another's
/// </summary>
let wrap (d:'a -> 'b, r: 'b -> 'a) (pu: PU<'a>) = {pickle = r >> pu.pickle; unpickle = pu.unpickle >> d}

/// <summary>
/// 'wrap' helper for argumentless variants
/// </summary>
let wrap0 r = wrap ((fun () -> r), ignore) unit

let byte  = {pickle = (fun (b:byte) st -> st.Write(b));   unpickle = fun st -> st.ReadByte()}
let int   = {pickle = (fun (i:Int32) st -> st.Write(i));  unpickle = fun st -> st.ReadInt32()}
let int64 = {pickle = (fun (i:Int64) st -> st.Write(i));  unpickle = fun st -> st.ReadInt64()}
let str   = {pickle = (fun (s:string) st -> st.Write(s)); unpickle = fun st -> st.ReadString()}
let float   = {pickle = (fun (s:single) st -> st.Write(s)); unpickle = fun st -> st.ReadSingle()}
let double   = {pickle = (fun (s:float) st -> st.Write(s)); unpickle = fun st -> st.ReadDouble()}

let date = wrap (DateTime.FromBinary, fun (d:DateTime) -> d.Ticks) int64
let bool = wrap ((<>) 0uy, function | true -> 1uy |false -> 0uy) byte

/// Tuple picklers
let pair pu1 pu2 = {
    pickle = (fun (a,b) st -> (pu1.pickle a st : unit); (pu2.pickle b st))
    unpickle = fun st -> pu1.unpickle st, pu2.unpickle st}
let triple pu1 pu2 pu3 = {
    pickle = (fun (a,b,c) st -> (pu1.pickle a st : unit); (pu2.pickle b st : unit); (pu3.pickle c st))
    unpickle = fun st -> pu1.unpickle st, pu2.unpickle st, pu3.unpickle st}

let quad pu1 pu2 pu3 pu4 =
    wrap ((fun ((a,b),(c,d)) -> (a,b,c,d)), fun(a,b,c,d) -> (a,b),(c,d)) <| pair (pair pu1 pu2) (pair pu3 pu4)

let private mux3 (a,b,c) x = (a x : unit); (b x : unit); (c x : unit)
let private mux2 (a,b) x = (a x : unit); (b x : unit)

/// <summary>
/// List pickler.
/// </summary>
/// <param name="pu"></param>
let list pu =
    let rec listP f = function | [] -> byte.pickle 0uy | h :: t -> mux3 (byte.pickle 1uy, f h, listP f t)
    let rec listUim f acc st = match byte.unpickle st with | 0uy -> List.rev acc | 1uy -> listUim f (f st :: acc) st | n -> failwithf "listU: found number %d" n
    {
        pickle = listP pu.pickle
        unpickle = listUim pu.unpickle []
    }

/// <summary>
/// A non-negative integer in the 7-bit variable-length encoding (`Write7BitEncodedInt` is not
/// public on netstandard2.0).
/// </summary>
let varint64 =
    { pickle = (fun (v: int64) (st: OutState) ->
        if v < 0L then invalidArg "v" "varint: a negative value"
        let mutable u = uint64 v
        while u >= 0x80UL do
            st.Write(Operators.byte (u ||| 0x80UL))
            u <- u >>> 7
        st.Write(Operators.byte u))
      unpickle = fun (st: InState) ->
        let mutable result = 0UL
        let mutable shift = 0
        let mutable more = true
        while more do
            if shift > 63 then failwith "varint: too long"
            let b = st.ReadByte()
            result <- result ||| (uint64 (b &&& 0x7Fuy) <<< shift)
            shift <- shift + 7
            more <- b &&& 0x80uy <> 0uy
        Operators.int64 result }

/// <summary>
/// A non-negative int in the 7-bit variable-length encoding.
/// </summary>
let varint = wrap ((fun (x: int64) -> Operators.int x), (fun (x: int) -> Operators.int64 x)) varint64

/// <summary>
/// List pickler with a varint length in front (database 0.7).
/// </summary>
let vlist pu =
    { pickle = fun (xs: 'a list) st -> varint.pickle (List.length xs) st; xs |> List.iter (fun x -> pu.pickle x st)
      unpickle = fun st -> let n = varint.unpickle st in List.init n (fun _ -> pu.unpickle st) }

/// <summary>
/// Variant (discriminated union) pickler.
/// </summary>
/// <param name="ftag">Maps type to index in array of picklers.</param>
/// <param name="puu">Array of picklers for each type.</param>
let alt<'a> (ftag: 'a -> Core.int) (puu: PU<'a> array): PU<'a> =
    {
        pickle = fun (a:'a) ->
            let tag = ftag a in
            mux2 (tag |> Convert.ToByte |> byte.pickle, puu.[tag].pickle a)
        unpickle = fun st ->
            let tag = st |> byte.unpickle |> Convert.ToInt32 in
            (puu.[tag].unpickle st)
    }

/// <summary>
/// Option type pickler.
/// </summary>
let option pu =
    alt
        (function | None -> 0 | Some _ -> 1)
        [|
            wrap ((fun () -> None), ignore) unit
            wrap (Some, Option.get) pu
        |]
