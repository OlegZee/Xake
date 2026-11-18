let (~~) (s: string) = "tt:" + s
let (!?) (s: string) = "bq:" + s
let (~%) (s: string) = "pc:" + s
let (~&) (s: string) = "am:" + s
let (!@) (s: string) = "ba:" + s
let need (xs: string list) = printfn "need %A" xs
need [ "obj/" + ~~"n" + ".rsp" ]
need [ "obj/" + !?"n" + ".rsp" ]
need [ "obj/" + %"n" + ".rsp" ]
need [ "obj/" + &"n" + ".rsp" ]
need [ "obj/" + !@"n" + ".rsp" ]
let f (s: string) = s.Length
printfn "%d %d %d" (f ~~"n") (f %"n") (f !?"n")
printfn "%s" $"""obj/{~~"n"}.rsp"""
printfn "%s" $"""obj/{%"n"}.rsp"""
