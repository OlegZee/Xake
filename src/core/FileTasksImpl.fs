[<AutoOpen>]
module internal Xake.FileTasksImpl

open System.IO
open Xake

/// Ensures the parent directory of the given file path exists, creating it if necessary.
let ensureDirCreated fileName =
    let dir = fileName |> Path.GetDirectoryName

    if not <| System.String.IsNullOrEmpty(dir) then
        do dir |> Directory.CreateDirectory |> ignore