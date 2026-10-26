[<AutoOpen>]
module Xake.Logging

/// <summary>
/// Log levels.
/// </summary>
type Level = 
    | Message
    | Error
    | Command
    | Warning
    | Info
    | Debug
    | Verbose
    | Never

/// <summary>
/// Output verbosity level.
/// </summary>
type Verbosity = 
    | Silent
    | Quiet
    | Normal
    | Loud
    | Chatty
    | Diag

/// Converts a log level to its short display string.
let LevelToString =
    function 
    | Message -> "MSG"
    | Error -> "ERROR"
    | Command -> "CMD"
    | Warning -> "WARN"
    | Info -> "INF"
    | Debug -> "DBG"
    | Verbose -> "TRACE"
    | _ -> ""

let private logFilter = 
    function 
    | Silent -> set []
    | Quiet -> set [ Message; Error ]
    | Normal -> set [ Message; Error; Command ]
    | Loud -> set [ Message; Error; Command; Warning ]
    | Chatty -> set [ Message; Error; Command; Warning; Info ]
    | Diag -> set [ Message; Error; Command; Warning; Info; Debug; Verbose ]

/// <summary>
/// The inteface loggers need to implement.
/// </summary>
type ILogger = 
    abstract Log : Level -> Printf.StringFormat<'a, unit> -> 'a

/// <summary>
/// The values of secret variables resolved in this process. Every logger Xake creates replaces
/// each of them with `***` before writing, as CI systems mask their secrets. Values shorter than
/// `MinLength` are not registered (masking them would garble unrelated text).
/// </summary>
module SecretMask =

    /// Shortest value that is masked.
    let MinLength = 4

    /// The replacement text.
    let Mask = "***"

    let private gate = obj ()
    // longest first, so a value that contains another is replaced whole
    let mutable private values : string[] = [||]

    /// Registers a value to mask in all output from now on.
    let register (value: string) =
        if not (isNull value) && value.Length >= MinLength then
            lock gate (fun () ->
                if not (Array.contains value values) then
                    values <- Array.append values [| value |] |> Array.sortBy (fun v -> -v.Length))

    /// Replaces every registered value in a text with `***`.
    let apply (text: string) =
        let current = values
        if current.Length = 0 || isNull text then text
        else current |> Array.fold (fun (t: string) v -> if t.Contains v then t.Replace(v, Mask) else t) text

let private createFileLogger fileName = 
    MailboxProcessor.Start(fun mbox -> 
        let rec loop() = 
            async { 
                let! msg = mbox.Receive()
                System.IO.File.AppendAllLines(fileName, [ msg ])
                return! loop()
            }
        loop())

/// <summary>
/// Creates a custom logger.
/// </summary>
/// <param name="filter">The filter to apply to messages</param>
/// <param name="writeFn">The function that dumps a message</param>
let CustomLogger filter writeFn = 
    { new ILogger with
          member __.Log level format = 
              let write = 
                  if filter level then 
                      SecretMask.apply >> sprintf "[%s] %s" (LevelToString level) >> writeFn
                  else ignore
              Printf.kprintf write format }

/// <summary>
/// A logger that writes to file.
/// </summary>
/// <param name="name"></param>
/// <param name="maxLevel"></param>
let FileLogger name maxLevel = 
    let filterLevels = logFilter maxLevel
    System.IO.File.Delete name
    let logger = createFileLogger name
    { new ILogger with
          member __.Log level format = 
              let write = 
                  match Set.contains level filterLevels with
                  | true -> 
                      SecretMask.apply >> sprintf "[%s] [%A] %s" (LevelToString level) 
                          System.DateTime.Now >> logger.Post
                  | false -> ignore
              Printf.kprintf write format }

/// The running-task count of the console progress line as Braille dots.
module ProgressDots =

    /// Cells drawn at most; one cell holds 8 dots.
    let MaxCells = 4

    /// The default cap: about 30 tasks (3 full cells and a partial one).
    let DefaultCap = 30

    // A cell fills column-wise: the left column top to bottom, then the right one
    // (dots 1,2,3,7 then 4,5,6,8), so a row of cells reads as a bar growing left to right.
    let private cellFill = [| '\u2800'; '⠁'; '⠃'; '⠇'; '⡇'; '⡏'; '⡟'; '⡿'; '⣿' |]

    /// Encodes the running-task count: one dot per task, at most min(cap, 32) dots in at most
    /// 4 cells; a count above the cap draws 4 full cells and "+". 0 gives an empty string.
    let dots (running: int) (cap: int) : string =
        let cap = cap |> max 1 |> min (MaxCells * 8)
        if running > cap then
            String.replicate MaxCells (string cellFill.[8]) + "+"
        else
            let n = max 0 running
            let full, partial = n / 8, n % 8
            String.replicate full (string cellFill.[8])
            + (if partial > 0 then string cellFill.[partial] else "")

    /// The colour of the dots: green up to half the cap, yellow up to the cap, red above it.
    let color (running: int) (cap: int) : System.ConsoleColor =
        if running > cap then System.ConsoleColor.Red
        elif running * 2 > cap then System.ConsoleColor.Yellow
        else System.ConsoleColor.Green

module private ConsoleSink =

    open System

    // Going to render progress bar this way:
    // | 22% [MM------------------] 0m 12s left
    let ProgressBarLen = 20

    type RenderCommand =
        | WriteLine of Level * string
        | SetStatus of (ConsoleColor * string) list option
        | Flush of AsyncReplyChannel<unit>

    type RenderState = {
        StatusBarText: string option  // raw bar text without spinner, for dedup
        StatusText: string option     // full rendered text with spinner, for display/clear
        StatusSegments: (ConsoleColor * string) list  // the same text in coloured pieces, for display
        StatusVisible: bool
        Interactive: bool
        SpinnerTick: int
    }

    let levelToColor = function
        | Level.Message -> Some (ConsoleColor.White, ConsoleColor.White)
        | Command -> Some (ConsoleColor.White, ConsoleColor.Gray)
        | Error   -> Some (ConsoleColor.Red, ConsoleColor.DarkRed)
        | Debug -> Some (ConsoleColor.Green, ConsoleColor.DarkGreen)
        | Warning -> Some (ConsoleColor.Yellow, ConsoleColor.DarkYellow)
        | Info    -> Some (ConsoleColor.Cyan, ConsoleColor.DarkCyan)
        | Verbose -> Some (ConsoleColor.Magenta, ConsoleColor.DarkMagenta)
        | _ -> None

    let fmtTs (ts:System.TimeSpan) =
        if ts.TotalHours >= 1.0 then sprintf "%dh %02dm" (int ts.TotalHours) ts.Minutes
        else if ts.TotalMinutes >= 1.0 then sprintf "%dm %02ds" (int ts.TotalMinutes) ts.Seconds
        else sprintf "%ds" ts.Seconds

    let isInteractiveConsole =
        not Console.IsOutputRedirected && not Console.IsErrorRedirected

    let spinnerFrames = [|"⠋";"⠙";"⠹";"⠸";"⠼";"⠴";"⠦";"⠧";"⠇";"⠏"|]

    let formatStatus (timeLeft:System.TimeSpan, pct, activeTasks) =
        match pct with
        | a when a >= 100 || a < 0 || timeLeft.TotalMilliseconds < 100.0 -> None
        | _ ->
            let partialChars = [|""; "▏"; "▎"; "▍"; "▌"; "▋"; "▊"; "▉"|]
            let filledSubunits = pct * ProgressBarLen * 8 / 100
            let fullBlocks = filledSubunits / 8
            let partial = filledSubunits % 8
            let emptyLen = ProgressBarLen - fullBlocks - (if partial > 0 then 1 else 0)
            let bar = sprintf "%s%s%s" (String.replicate fullBlocks "█") partialChars.[partial] (String.replicate emptyLen " ") // '░'
            let head = sprintf "%3d%% [%s]  " pct bar
            let tail = sprintf " · %s left" (fmtTs timeLeft)
            match isInteractiveConsole with
            | true ->
                // a fixed width (blank Braille cells, a space for the "+") keeps the line steady
                let cap = ProgressDots.DefaultCap
                let d = ProgressDots.dots activeTasks cap
                let d = d.PadRight(ProgressDots.MaxCells, '\u2800').PadRight(ProgressDots.MaxCells + 1)
                Some [ ConsoleColor.White, head; ProgressDots.color activeTasks cap, d; ConsoleColor.White, tail ]
            | false ->
                let taskStr = if activeTasks = 1 then "1 task" else sprintf "%d tasks" activeTasks
                Some [ ConsoleColor.White, head + taskStr + tail ]

    let po = MailboxProcessor.Start(fun mbox ->

        let rec loop state =
            // Writes \r + spaces + \r to fully erase current status bar, cursor returns to col 0
            let eraseStatus () =
                let len = state.StatusText |> Option.fold (fun _ -> String.length) 0
                Console.Write ("\r" + String.replicate len " " + "\r")

            let drawStatus (segments: (ConsoleColor * string) list) =
                match state.Interactive with
                | true ->
                    // caller already at col 0 after eraseStatus
                    for color, text in segments do
                        Console.ForegroundColor <- color
                        Console.Write text
                    Console.ResetColor()
                | _ -> ()

            let renderLineWithInfo (color, textColor) level (txt: string) =
                // caller has already erased the status bar so cursor is at col 0
                Console.ForegroundColor <- color
                Console.Write (sprintf "[%s] " level)
                Console.ForegroundColor <- textColor
                Console.Write txt
                // pad any remaining status bar width so old chars don't bleed through
                let statusLen = state.StatusText |> Option.fold (fun _ -> String.length) 0
                let writtenLen = level.Length + 3 + txt.Length  // "[level] " = level+3
                let pad = statusLen - writtenLen
                if pad > 0 then Console.Write (String.replicate pad " ")
                Console.WriteLine()

            let writeLines level (text: string) =
                match level |> levelToColor with
                | Some colors ->
                    text.Split '\n'
                    |> Seq.iteri (fun index (part: string) ->
                        let line = part.TrimEnd '\r'
                        match index with
                        | 0 -> renderLineWithInfo colors (LevelToString level) line
                        | _ -> Console.WriteLine line)
                | _ -> ()
                Console.ResetColor()

            async {
                let! msg = mbox.Receive()
                match msg with
                | WriteLine(level, text) ->
                    if state.StatusVisible then eraseStatus()
                    writeLines level text
                    if state.StatusVisible then drawStatus state.StatusSegments
                    return! loop state

                | SetStatus barSegments ->
                    let barText = barSegments |> Option.map (List.map snd >> String.concat "")
                    let nextState =
                        match state.Interactive with
                        | false -> { state with StatusBarText = barText; StatusText = barText; StatusSegments = []; StatusVisible = false }
                        | true ->
                            if state.StatusVisible then eraseStatus()
                            let newTick = (state.SpinnerTick + 1) % spinnerFrames.Length
                            let segments =
                                barSegments
                                |> Option.map (fun segs -> (ConsoleColor.White, spinnerFrames.[newTick] + " ") :: segs)
                                |> Option.defaultValue []
                            let fullText = barText |> Option.map (fun bt -> sprintf "%s %s" spinnerFrames.[newTick] bt)
                            let visible = fullText.IsSome
                            if visible then drawStatus segments
                            { state with StatusBarText = barText; StatusText = fullText; StatusSegments = segments; StatusVisible = visible; SpinnerTick = newTick }
                    return! loop nextState

                | Flush ch ->
                    if state.StatusVisible then eraseStatus()
                    do! Console.Out.FlushAsync() |> Async.AwaitTask
                    ch.Reply ()
                    return! loop { state with StatusVisible = false }
            }

        loop { StatusBarText = None; StatusText = None; StatusSegments = []; StatusVisible = false; Interactive = isInteractiveConsole; SpinnerTick = 0 })


/// <summary>
/// Base console logger.
/// </summary>
/// <param name="maxLevel"></param>
let private ConsoleLoggerBase (write: Level -> string -> unit) maxLevel = 
    let filterLevels = logFilter maxLevel
    { new ILogger with
          member __.Log level format = 
              let write = 
                  match filterLevels |> Set.contains level with
                  | true -> SecretMask.apply >> write level
                  | false -> ignore
              Printf.kprintf write format }

/// Simplistic console logger.
let DumbConsoleLogger =
    ConsoleLoggerBase (fun l -> l |> LevelToString |> sprintf "[%s] %s" >> System.Console.WriteLine)

/// Console logger with colors highlighting
let ConsoleLogger =
    ConsoleLoggerBase (fun level s -> ConsoleSink.WriteLine(level,s) |> ConsoleSink.po.Post)

/// Ensures all logs finished pending output.
let FlushLogs () =
    try
        ConsoleSink.po.PostAndTryAsyncReply (ConsoleSink.Flush, 200) |> Async.RunSynchronously |> ignore
    with _ -> ()

/// Draws a progress bar to console log.
let WriteConsoleProgress =
    fun progressData ->
        progressData
        |> ConsoleSink.formatStatus
        |> ConsoleSink.SetStatus
        |> ConsoleSink.po.Post

/// <summary>
/// Creates a logger that is combination of two loggers.
/// </summary>
/// <param name="log1"></param>
/// <param name="log2"></param>
let CombineLogger (log1 : ILogger) (log2 : ILogger) = 
    { new ILogger with
          member __.Log level (fmt : Printf.StringFormat<'a, unit>) : 'a = 
              let write s = log1.Log level "%s" s; log2.Log level "%s" s
              Printf.kprintf write fmt }

/// <summary>
/// A logger decorator that adds specific prefix to a message.
/// </summary>
/// <param name="prefix"></param>
/// <param name="log"></param>
let PrefixLogger (prefix:string) (log : ILogger) = 
    { new ILogger with
          member __.Log level format = 
              let write = sprintf "%s%s" prefix >> log.Log level "%s"
              Printf.kprintf write format }

/// <summary>
/// A logger decorator that masks the values of secret variables (see SecretMask) before
/// passing a message on; Xake wraps the script's logger in it, custom loggers included.
/// </summary>
/// <param name="log"></param>
let MaskingLogger (log : ILogger) = 
    { new ILogger with
          member __.Log level format = 
              let write = SecretMask.apply >> log.Log level "%s"
              Printf.kprintf write format }

/// <summary>
/// Parses the string value into verbosity level.
/// </summary>
/// <param name="parseVerbosity"></param>
let parseVerbosity = function
    | "Silent" -> Silent
    | "Quiet" -> Quiet
    | "Normal" -> Normal
    | "Loud" -> Loud
    | "Chatty" -> Chatty
    | "Diag" -> Diag
    | s ->
        failwithf "invalid verbosity: %s. Expected one of %s" s "Silent | Quiet | Normal | Loud | Chatty | Diag"
