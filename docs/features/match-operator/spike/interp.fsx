let (?) (m: Map<string,string>) (k: string) = m.[k]
let m = Map ["n","core"]
printfn "%s" $"obj/{m?n}.rsp"
