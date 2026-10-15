namespace Tests

open Xake
open Xake.Hermetic.Dotnet

/// The script side of lock generation (docs/hermetic/lock.md "Xake-generated locks"), shared
/// by the tests that generate locks: the phony rule `lock::<name>::<cfg>` over
/// `Locks.generate`, and the `update-locks` target that needs it per project and configuration.
module LockRule =

    /// The canonical lock rule; `refCfg` maps the configuration to the one the referenced
    /// projects' locks are needed in (the doc: the same). `ran` gets `name::cfg` each time the
    /// rule body runs.
    let lockRule (lockOf: string -> string -> string) (outOf: string -> string -> string -> string)
                 (descriptionOf: string -> string) (refCfg: string -> string) (ran: ResizeArray<string>) =
        "lock::(name:*)::(cfg:*)" => recipe {
            let! name = getRuleMatch "name"
            let! cfg  = getRuleMatch "cfg"
            let! d = Description.load (descriptionOf name)
            do! need [ for r in d.ProjectRefs -> $"lock::{r}::{refCfg cfg}" ]
            do! alwaysRerun ()
            lock ran (fun () -> ran.Add (name + "::" + cfg))
            do! Locks.generate lockOf outOf cfg (descriptionOf name)
        }

    /// `update-locks`: every project of `projects` in every configuration of `configs`.
    let updateLocks (configs: string list) (projects: string list) =
        "update-locks" <== [ for n in projects do for cfg in configs -> $"lock::{n}::{cfg}" ]
