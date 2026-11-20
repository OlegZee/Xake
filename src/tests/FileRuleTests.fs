module ``File rule``

// The decision about one observation of a file, as a pure function (docs/features/idle-perf,
// design.md L1): one test per row of the rule and per case after hashing.

open NUnit.Framework

open Xake
open Xake.Storage
open Xake.FileProbe
open Xake.FileStaleness
open Xake.FileProbe
open Xake.FileStaleness
open Xake.FileStaleness.FileRule

let private t0 = System.DateTime(2026, 1, 1)
let private record changed hash = { Stamp = t0; Size = 10L; Hash = hash; Changed = changed }
let private same = Present (t0.AddMilliseconds 5.0, 10L)        // within 10 ms, the same size
let private moved = Present (t0.AddSeconds 1.0, 12L)
let private rc = { Epoch = 7; Hashing = false; SeenByVerdict = false; RecordedBefore = false }
let private hashing = { rc with Hashing = true }
let private movedState hash changed = { Stamp = t0.AddSeconds 1.0; Size = 12L; Hash = hash; Changed = changed }

let private modes = [ Verdict; Recording; Output true; Output false; PreHash ]

[<Test>]
let ``a missing file is absent in every mode``() =
    for mode in modes do
        Assert.AreEqual(Keep None, decide (Some (record 1 None)) Missing mode hashing, sprintf "%A" mode)

[<Test>]
let ``a verdict that already saw the file answers from the record``() =
    let k = record 1 None
    Assert.AreEqual(Keep (Some k), decide (Some k) moved Verdict { hashing with SeenByVerdict = true })
    Assert.AreEqual(Keep None, decide None moved Verdict { rc with SeenByVerdict = true })

[<Test>]
let ``an output without cut-off is written as changed in this run, without a hash``() =
    Assert.AreEqual(Write (movedState None 7), decide (Some (record 1 (Some 5UL))) moved (Output false) hashing)

[<Test>]
let ``an output with cut-off is hashed when the run hashes``() =
    Assert.AreEqual(NeedHash, decide (Some (record 1 None)) same (Output true) hashing)
    Assert.AreEqual(NeedHash, decide None moved (Output true) hashing)

[<Test>]
let ``pre-hash hashes an unhashed record with an equal stat, and keeps everything else``() =
    let k = record 1 None
    Assert.AreEqual(NeedHash, decide (Some k) same PreHash hashing)
    Assert.AreEqual(Keep (Some k), decide (Some k) moved PreHash hashing)
    Assert.AreEqual(Keep (Some k), decide (Some k) same PreHash rc)
    let h = record 1 (Some 5UL)
    Assert.AreEqual(Keep (Some h), decide (Some h) same PreHash hashing)
    Assert.AreEqual(Keep None, decide None moved PreHash hashing)

[<Test>]
let ``an equal stat keeps the record, filling in an unknown size``() =
    let k = record 1 (Some 5UL)
    for mode in [ Verdict; Recording; Output false ] do
        match mode with
        | Output false -> ()   // always written as a change (above)
        | _ -> Assert.AreEqual(Keep (Some k), decide (Some k) same mode hashing, sprintf "%A" mode)
    let unknown = { k with Size = -1L }
    Assert.AreEqual(Write { unknown with Size = 10L }, decide (Some unknown) same Verdict hashing)

[<Test>]
let ``a moved stat recorded again in the run is not a change (A1)``() =
    let k = record 1 None
    Assert.AreEqual(Keep (Some k), decide (Some k) moved Recording { hashing with RecordedBefore = true })

[<Test>]
let ``a moved stat is hashed when the run hashes, else changed in this run``() =
    let k = record 1 (Some 5UL)
    for mode in [ Verdict; Recording ] do
        Assert.AreEqual(NeedHash, decide (Some k) moved mode hashing, sprintf "%A" mode)
        Assert.AreEqual(Write (movedState None 7), decide (Some k) moved mode rc, sprintf "%A" mode)

[<Test>]
let ``a file without a record is new in this run``() =
    for mode in [ Verdict; Recording; Output false ] do
        Assert.AreEqual(Write (movedState None 7), decide None moved mode hashing, sprintf "%A" mode)

[<Test>]
let ``after hashing: the same content keeps its run``() =
    let k = record 3 (Some 5UL)
    let stamp, size = t0.AddSeconds 1.0, 12L
    Assert.AreEqual(Write { movedState (Some 5UL) 3 with Stamp = stamp; Size = size },
                    decideHashed (Some k) (Hashed (stamp, size, 5UL)) Verdict hashing)

[<Test>]
let ``after hashing: other content is a change in this run``() =
    let k = record 3 (Some 5UL)
    Assert.AreEqual(Write (movedState (Some 6UL) 7),
                    decideHashed (Some k) (Hashed (t0.AddSeconds 1.0, 12L, 6UL)) Recording hashing)
    Assert.AreEqual(Write (movedState (Some 6UL) 7),
                    decideHashed None (Hashed (t0.AddSeconds 1.0, 12L, 6UL)) Verdict hashing)

[<Test>]
let ``after hashing: an output never hashed and not moved gets its hash, changed kept``() =
    let k = record 3 None
    Assert.AreEqual(Write { k with Hash = Some 6UL; Stamp = t0.AddMilliseconds 5.0 },
                    decideHashed (Some k) (Hashed (t0.AddMilliseconds 5.0, 10L, 6UL)) (Output true) hashing)

[<Test>]
let ``after hashing: pre-hash writes only the hash, and only for an equal stat``() =
    let k = record 3 None
    Assert.AreEqual(Write { k with Hash = Some 6UL }, decideHashed (Some k) (Hashed (t0.AddMilliseconds 5.0, 10L, 6UL)) PreHash hashing)
    Assert.AreEqual(Keep (Some k), decideHashed (Some k) (Hashed (t0.AddSeconds 1.0, 12L, 6UL)) PreHash hashing)
    Assert.AreEqual(Keep (Some k), decideHashed (Some k) (Unreadable moved) PreHash hashing)

[<Test>]
let ``after hashing: unreadable is a change without a hash, or absent when the file is gone``() =
    let k = record 3 (Some 5UL)
    Assert.AreEqual(Write (movedState None 7), decideHashed (Some k) (Unreadable moved) Verdict hashing)
    Assert.AreEqual(Keep None, decideHashed (Some k) (Unreadable Missing) Recording hashing)

[<Test>]
let ``classify compares the run a state changed in with since``() =
    let st = record 5 None
    Assert.AreEqual(Absent, classify 4 None)
    Assert.AreEqual(Changed st, classify 4 (Some st))
    Assert.AreEqual(Unchanged st, classify 5 (Some st))
