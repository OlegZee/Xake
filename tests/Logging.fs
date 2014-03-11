﻿[<AutoOpen>]
module Xake.Logging
// modified http://fssnip.net/8j

open System

/// Log levels.
type Level =
  | Error
  | Warning
  | Info
  | Debug

let LevelToString level =
  match level with
    | Error -> "Err"
    | Warning -> "Warn"
    | Info -> "Info"
    | Debug -> "Debug"
    | _ -> "Unknown"

/// The current log level.
let mutable current_log_level = Debug

/// The inteface loggers need to implement.
type ILogger = abstract Log : Level -> Printf.StringFormat<'a,unit> -> 'a

/// Writes to console.
let ConsoleLogger = { 
  new ILogger with
    member __.Log level format =
      System.Console.Out.Flush
      Printf.kprintf (printfn "[%s] [%A] %s" (LevelToString level) System.DateTime.Now) format
  }

/// Defines which logger to use.
let mutable DefaultLogger = ConsoleLogger

/// Logs a message with the specified logger.
let logUsing (logger: ILogger) = logger.Log

/// Logs a message using the default logger.
let log level message = logUsing DefaultLogger level message
let logInfo message = logUsing DefaultLogger Level.Info message