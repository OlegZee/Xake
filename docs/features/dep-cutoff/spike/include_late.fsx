#r "__XAKE_DLL__"
open Xake
let h = ruleHeader { needs "x" }
let r = "p" ..> recipe {
    do! need ["a"]
    including h
}
