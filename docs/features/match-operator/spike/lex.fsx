// which prefix operator names F# accepts
let (~~) (s: string) = "tt:" + s
let (!?) (s: string) = "bq:" + s
let (!@) (s: string) = "ba:" + s
let (~%) (s: string) = "pc:" + s
let (~&) (s: string) = "am:" + s
let (~%%) (s: string) = "pp:" + s
let (~&&) (s: string) = "aa:" + s
let (~@) (s: string) = "at:" + s
printfn "%s %s %s %s %s %s %s %s" ~~"n" !?"n" !@"n" %"n" &"n" %%"n" &&"n" (~@ "n")
