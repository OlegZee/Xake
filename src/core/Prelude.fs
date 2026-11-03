[<AutoOpen>]
module Prelude

/// Flips the arguments of a two-argument function.
let (><) f a b = f b a
/// Active pattern that always fails with "no choice". Used as a catch-all in discriminated union matches.
let inline (|OtherwiseFail|) _ = failwith "no choice"
/// Active pattern that always fails with the given error message.
let inline (|OtherwiseFailErr|) message _ = failwith message
