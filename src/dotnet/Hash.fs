namespace Xake.Dotnet

/// The one file hash Xake.Dotnet uses: the format a lock records and a hash check compares.
module Hash =

    /// Lowercase hex SHA-256 of a file. A missing file is an error here: "" is the lock's
    /// own convention for "not hashed" (`Csc.sha256`), and a verification must never
    /// produce it by accident.
    let sha256 (path: string) : string =
        use stream = System.IO.File.OpenRead path
        use algo = System.Security.Cryptography.SHA256.Create()
        algo.ComputeHash stream |> Array.map (sprintf "%02x") |> String.concat ""
