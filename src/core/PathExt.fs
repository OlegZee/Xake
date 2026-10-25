[<AutoOpen>]
module Xake.PathExt

open System.IO

/// <summary>
/// Changes or appends file extension.
/// </summary>
let (-.) path ext = Path.ChangeExtension(path, ext)

/// <summary>
/// Combines two paths. The result uses '/' as the separator on every OS (on Windows, `Path.Combine`
/// joins with '\'), so that a mask or rule pattern built with it does not use the deprecated
/// '\' separator -- in a pattern '\' escapes a reserved character (`\(`).
/// </summary>
let (</>) path1 path2 =
    let combined = Path.Combine(path1, path2)
    if Path.DirectorySeparatorChar = '\\' then combined.Replace('\\', '/') else combined

/// <summary>
/// Appends the file extension.
/// </summary>
let (<.>) path ext = if System.String.IsNullOrWhiteSpace(ext) then path else path + "." + ext
