namespace Xake.Tasks

open Xake
open System.IO

[<AutoOpen>]
module MiscImpl =

    /// <summary>
    /// Writes text to a file.
    /// </summary>
    let writeTargetText content = recipe {
        let! fileName = getTargetFullName()
        do ensureDirCreated fileName
        do File.WriteAllText(fileName, content)
    }

    /// Writes text to a file, ensuring the directory exists.
    let writeText content = writeTargetText content

    /// <summary>
    /// Writes binary data to a file.
    /// </summary>
    let writeTargetBytes content = recipe {
        let! fileName = getTargetFullName()
        do ensureDirCreated fileName
        do File.WriteAllBytes(fileName, content)
    }

    let private read f path = recipe {
        do! need [path]

        let! options = getCtxOptions()
        let fullPath = 
            if Path.IsPathRooted path then path
            else options.ProjectRoot </> path
        let content = f fullPath
        return content
    }

    /// Reads the entire content of a text file and returns it as a string.
    let readText path = read File.ReadAllText path

    /// Reads all lines from a text file and returns them as an array of strings.
    let readAllLines path = read File.ReadAllLines path

    /// Reads the entire content of a binary file and returns it as a byte array.
    let readAllBytes path = read File.ReadAllBytes path
