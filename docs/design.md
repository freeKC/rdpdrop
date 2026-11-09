# UltimateRDP design

Date: 2026-11-05, updated on 2026-11-09 for version 0.4.0.

## Goal

Let a user drag files or folders from the local Windows desktop and drop them
into a Remote Desktop session, on the application under the cursor when the
button is released. The application receives them as a normal drop. When
nothing under the cursor accepts them, the drop is ignored.
The transfer uses the standard RDP drive redirection; UltimateRDP only adds the
"drop" gesture on top of it. Since version 0.4, large files travel instead
through the UltimateRDP channel before the drop, which is faster; see
"Version 0.4" below.

Scope:

* Direction: local computer to remote session only. The other direction keeps
  using copy and paste.
* Client: Windows 10 or 11, 64 bit. A single portable executable, nothing to
  install, nothing written to the registry.
* Server: Windows Server (2016 or later) or Windows 10/11 acting as a Remote
  Desktop host. A small agent runs in the user session.
* Full desktop sessions, in a window or in full screen on one monitor or on
  all monitors.
* Works through a Remote Desktop Gateway, because it only relies on a static
  virtual channel and on drive redirection, both of which the gateway forwards.

Out of scope: drag from the session to the local desktop, RemoteApp,
connections without drive redirection.

## Why not something simpler

The RDP protocol has no drag and drop channel. The Microsoft client (mstsc)
rejects drops on its window before the server is ever involved. So a piece of
code must run on the client side to catch the drag, and a piece of code must
run inside the session to replay it. The transfer itself does not need any
new code: once drive redirection is on, every local drive is reachable from
the session as `\\tsclient\<letter>`.

## Architecture

Three projects in one .NET 10 solution, C# everywhere.

```
UltimateRDP.sln
src/UltimateRDP.Core/              class library, no UI, fully unit tested
src/UltimateRDP.Client/            WinForms app, hosts the Microsoft RDP ActiveX control
src/UltimateRDP.Agent/             WinForms tray app, runs inside the remote session
tests/UltimateRDP.Core.Tests/      xUnit
.github/workflows/ci.yml           build and test on every push and pull request
.github/workflows/release.yml      build, sign, publish and attach zips on tags v*
docs/design.md                     this document
docs/releases/<tag>.md             release notes, used as the release body
README.md, LICENSE (MIT), .gitignore, .editorconfig
```

### Names

UltimateRDP is published by APROGSYS (https://www.aprogsys.com/). The source
is at https://github.com/freeKC/ultimaterdp.

* Executables: `UltimateRDP.exe` (the client) and `UltimateRDP.Agent.exe`.
* Static virtual channel: `ULTRDP`.
* Settings and logs: `%LOCALAPPDATA%\UltimateRDP` on each side, with
  `client.json`, `agent.json`, `logs\client-<date>.log`,
  `logs\agent-<date>.log` and the agent `staging` folder.
* Agent single instance mutex `Local\UltimateRDP.Agent`, exit event
  `Local\UltimateRDP.Agent.Exit`, and the `Run` value `UltimateRDP` under
  `HKCU` or `HKLM`.

### Data flow

```
local desktop      UltimateRDP.Client              RDP              UltimateRDP.Agent (in session)
    |                    |                           |                      |
    | drag file(s)       |                           |                      |
    |------------------->| first drag over at (x,y)  |                      |
    |                    | map to remote coords      |                      |
    |                    | "dragEnter" (paths) ----->| static VC ---------->| proxy window, simulated press,
    |                    |                           |                      | DoDragDrop with \\tsclient\C\... paths
    |                    |<------------ "dragAck" ---|<---------------------|
    |                    | "dragMove" while moving ->|--------------------->| move the remote cursor, the window
    |                    |                           |                      | under it shows its drop feedback
    | release button     | "drop" at (x,y) --------->|--------------------->| release the button on the target
    |                    |<--------- "dropResult" ---|<---------------------| copy, link, or none (ignored)
    |                    | show result in status bar                        |
```

Leaving the session during the drag sends `dragLeave` instead of `drop`, and
the agent cancels the drag inside the session.

### UltimateRDP.Core

Pure logic shared by the two apps and covered by tests.

* `Messages`: the message records below, serialized with System.Text.Json.
* `LineFraming`, `LineReader` and `ChannelTextCodec`: ASCII line framing
  and byte model detection, described under "Wire encoding" below.
* `ClientPathMapper`: turns a local path such as `C:\Users\me\a.txt` into
  `\\tsclient\C\Users\me\a.txt`. Only paths with a drive letter are accepted.
  UNC paths and relative paths are rejected with a `PathRejection` kind, which
  each application words in the language of its user, and an English reason
  for the logs.
* `RdpFile`: parser for `.rdp` files (`key:type:value` lines, `s`, `i`, `b`
  types, UTF-16 with BOM or UTF-8, blank lines and comments ignored). Exposes
  typed getters with defaults.
* `SessionDisplay`: size and scale rules for the remote desktop, as accepted
  by the control and by the display control channel of the server.
* `AccountName`: splits the user name of a saved Windows credential into the
  user name and domain that the control takes.
* `RecentList`: rules of the list of recent connection files.
* `UiLanguages`: the supported languages and the rules that turn a language
  choice or a Windows language into one of them.
* `TrayIconModes`, `ChannelRetryPolicy`, `IdleReport` and `InstallLocation`:
  rules of the agent, described under "Production behaviour of the agent".
* `FastTransferPolicy`, `TransferSender`, `TransferReceiver`, `TransferLines`,
  `TransferPaths`, `TransferHash` and `TransferProgress`: the fast transfer of
  large files, described under "Version 0.4".

### Protocol

Static virtual channel named `ULTRDP` (six characters, within the maximum of
seven for a static channel). Every message is one line (see "Wire encoding"
below). Messages are JSON objects with a `type` field. Fields that have no
value are not written.

Client to agent:

* `hello` : `{ "type": "hello", "version": 1, "clientVersion": "0.3.0",
  "clientLanguage": "fr" }`
* `dragEnter` : `{ "type": "dragEnter", "id": "<guid>", "x": 412, "y": 300,
  "items": [ { "clientPath": "C:\\Users\\me\\a.txt", "isDirectory": false,
  "size": 52428800 } ], "keyState": 0 }`
* `dragMove` : `{ "type": "dragMove", "id": "<guid>", "x": 420, "y": 310,
  "keyState": 0 }`
* `drop` : `{ "type": "drop", "id": "<guid>", "x": 420, "y": 310,
  "keyState": 0, "deferred": true }`
* `dragLeave` : `{ "type": "dragLeave", "id": "<guid>" }`
* `logoff` : `{ "type": "logoff", "id": "<guid>" }`
* `transferBegin`, `fileBegin`, `fileChunk`, `fileEnd` and `transferCancel`:
  the fast transfer, described under "Version 0.4".

Agent to client:

* `hello` : `{ "type": "hello", "version": 1, "agentVersion": "0.3.0",
  "driveRedirectionAvailable": true }`
* `dragAck` : `{ "type": "dragAck", "id": "<guid>", "accepted": true,
  "message": "optional text", "transfer": true, "transferId": "<guid>" }`
* `dropResult` : `{ "type": "dropResult", "id": "<guid>",
  "status": "ok" | "error" | "cancelled", "effect": "copy" | "link" | "none",
  "message": "human readable text", "targetWindow": { "className": "CabinetWClass",
  "title": "Documents", "childClassName": "DirectUIHWND", "processName": "explorer" } }`
* `logoffResult` : `{ "type": "logoffResult", "id": "<guid>", "accepted": true,
  "message": "human readable text" }`
* `fileAck` and `transferResult`: the fast transfer, described under
  "Version 0.4".

Field notes:

* `id` is chosen by the client in `dragEnter` and repeated by every message of
  the same drag, including `drop`, `dragAck` and `dropResult`.
* `x` and `y` are pixels in the remote desktop coordinate space, counted from
  the top left corner of the session desktop.
* `keyState` holds the modifier keys held on the client: 1 for Ctrl, 2 for
  Shift, 4 for Alt. Zero when absent.
* `dragAck` with `accepted: false` refuses the drag; `message` gives the
  reason. An accepted `dragAck` may carry a note, such as a long staging.
* `dropResult` with status `ok` and effect `copy` or `link` means the target
  accepted the drop. Status `ok` with effect `none` means nothing under the
  cursor accepted the files: the drop is ignored, nothing is copied, and the
  message is "Nothing under the cursor accepted the files." Status `error`
  means the drop could not be delivered; the message says why, for example
  "No drag in progress for this drop." for a `drop` whose id matches no live
  drag. Status `cancelled` means the drag was cancelled after the drop was
  sent, for example by Escape in the session.
* `targetWindow` describes the window under the drop point, when known.
* `clientLanguage` is the two letter code of the language of the client user
  interface: `en`, `fr`, `de`, `es`, `it`, `pt` or `nl`. The agent writes every
  `message` it sends in that language; an absent or unknown code gives
  English. The client sends a new hello when the user changes the language.
* `logoff` asks the agent to log off the Windows session it runs in. The
  agent answers `logoffResult` with `accepted: true` before the session
  closes. If Windows refuses, a second `logoffResult` with the same id and
  `accepted: false` gives the reason in `message`.
* `size` of an item, `deferred` of `drop`, and `transfer` and `transferId` of
  `dragAck` belong to the fast transfer of version 0.4. They are not written
  when they have no value, and an older peer ignores them.

Version handling: both sides send `version: 1` in their hello. The agent
answers a message that carries a higher version with an error `dropResult`
when the message has an id, and logs it; the client ignores and logs an agent
hello with a higher version. Unknown fields are ignored. The client and the
agent must run the same version of UltimateRDP.

#### Wire encoding

The ActiveX control exposes channel data as a COM string (BSTR), and the way
`mstscax.dll` turns bytes into string units is not documented. Two models are
possible: one byte per character, or raw UTF-16LE (two bytes per character,
with an odd trailing byte lost). UltimateRDP therefore only ever puts ASCII on
the wire, frames messages by lines, and detects the model at run time.

* A message on the wire is its JSON object serialized with ASCII only
  (System.Text.Json escapes everything else as `\uXXXX`), followed by one
  `\n`. The JSON never contains a raw newline.
* Agent side: the agent detects the model from the first two bytes it
  receives on a connection. `0x7B 0x00` means UTF-16LE; `0x7B` followed by
  any other byte means one byte per character. It keeps that model for the
  rest of the connection and writes replies with the same model: UTF-16LE
  bytes, or ASCII bytes. In the one byte per character model it pads a line
  to an even number of bytes by inserting a space before the `\n` when
  needed, so that nothing is lost if the control pairs bytes into characters.
* Client side: the event delivers a string. If its first character is `{`,
  each character is one byte (take the low byte). Otherwise the string holds
  raw bytes, two per character, little endian (reinterpret the UTF-16 units
  as bytes). The client keeps the detected model and logs it, since this is
  the first thing to check when a drop fails. The detected model is only
  used to read. When sending, the client always hands the ASCII line itself
  to the control: whatever the control does with it, the agent receives
  either ASCII bytes or UTF-16LE bytes, and both are covered by its own
  detection.
* Both sides accumulate until `\n` and parse each line on its own. Blank
  lines are ignored. A line above 1 MiB is a protocol error that drops the
  buffer.

This lives in Core as `LineFraming` (encode a message as an ASCII line),
`LineReader` (accumulate text, yield complete lines) and `ChannelTextCodec`
(model detection and byte/string conversion in both directions), all unit
tested.

### UltimateRDP.Client

A WinForms application (`net10.0-windows`, x64) that embeds the Microsoft RDP
ActiveX control (`mstscax.dll`) through the `Devolutions.MsRdpEx` NuGet
package.

Behaviour:

1. Start with `UltimateRDP.exe <file.rdp>`; without an argument, show an
   open file dialog filtered on `.rdp`. Each session opens in its own tab
   (see "Step 3: connections" below).
2. Read the `.rdp` file with `RdpFile` and apply the settings that matter:
   full address, server port, username, domain, gateway host name and usage
   method, gateway credentials source, authentication level, prompt for
   credentials, redirect clipboard, redirect drives and drives to redirect,
   desktop width and height, and the keys listed under "Step 1: packaging and
   display" below. The session always starts in a window, even when the file
   asks for full screen, because a full screen window cannot receive a drop
   from the local desktop; full screen is available afterwards from the View
   menu. Settings that have no equivalent on the control are ignored.
3. Before connecting, call `CreateVirtualChannels("ULTRDP")` on the control
   and enable drive redirection if the `.rdp` file asks for it. If the file
   does not redirect drives, connect anyway and show a warning in the status
   bar: drops will not work.
4. Desktop size: the control is docked to fill the window. At connect time the
   desktop size is the control client size. By default the session is
   resized to follow the window; the View menu can instead scale the picture
   (smart sizing). Drop coordinates are converted with the ratio between the
   desktop size and the control size.
5. Drop target: the main form has `AllowDrop`. In addition, after the control
   is created, the client walks the child windows of the control and
   registers its own `IDropTarget` on each one with `RegisterDragDrop`
   (revoking any existing registration first). This guarantees that the drag
   reaches us even if the ActiveX window has its own registration. Only
   `CF_HDROP` data is accepted.
6. During a drag: on the first drag over the session, map the items with
   `ClientPathMapper` (a UNC path refuses the drag), convert the point and
   send `dragEnter` with a new id; then send `dragMove` while the cursor moves,
   and `drop` or `dragLeave` at the end. The details are in "Step 2: native
   drop into any remote application" below. On `dropResult`, show the result
   text in the status bar. If the session is not connected or the agent is not
   ready, refuse the drag with the "not allowed" cursor and explain in the
   status bar.
7. Agent discovery: after `OnConnected`, send `hello` every two seconds until
   the agent answers or sixty seconds pass. Any hello from the agent makes it
   ready. Status bar shows "Agent: waiting", then "Agent ready (vX)" or
   "No agent on the remote computer". When the agent reports that no
   redirected drive is reachable, the status bar says that drops will not
   work.
8. Status bar at the bottom: connection state, agent state, last drop
   result. Menus: File (with Log off the remote session, see "Version 0.3"
   below), Session (Connect, Disconnect, Look for the agent again, Cancel the
   transfer, see "Version 0.4" below), View
   (with Language) and Help (About, Project page, Open log folder). About
   is a task dialog with the version, the publisher APROGSYS and its web
   site, and the project page; both addresses are links.
9. Disconnection shows the reason text from the control and keeps the window
   open so the user can reconnect.

### UltimateRDP.Agent

A WinForms tray application (`net10.0-windows`, x64), single instance, no
main window.

Behaviour:

1. Channel loop on a background thread:
   `WTSVirtualChannelOpen(WTS_CURRENT_SERVER_HANDLE, WTS_CURRENT_SESSION, "ULTRDP")`,
   then `WTSVirtualChannelRead` with a short timeout, so that the loop can
   stop or restart without closing a handle under a pending read. Detect the
   byte model from the first two bytes, convert chunks to text with
   `ChannelTextCodec`, feed a `LineReader`, dispatch messages, write replies
   with `WTSVirtualChannelWrite` in the same byte model. On any error
   (session disconnected, channel closed, not an RDP session) close
   everything, wait and retry forever; the wait is described under
   "Production behaviour of the agent" below. Also listen to
   `WM_WTSSESSION_CHANGE` to retry at once after a connection or a logon.
2. On `hello`: reply with the agent version and whether at least one
   `\\tsclient\<letter>` is reachable.
3. On `dragEnter`: map every item with `ClientPathMapper` and check that the
   mapped path exists; a missing item or missing drive redirection refuses
   the drag with a `dragAck` that names the cause. When staging is on, copy
   the items into the staging folder first. Then replay the drag inside the
   session with `DoDragDrop`, as described in "Step 2: native drop into any
   remote application" below, and answer `dragAck`.
4. On `dragMove`, `drop` and `dragLeave`: move the remote cursor, release
   the simulated button on the target, or cancel the drag. When `DoDragDrop`
   ends, reply `dropResult`: `ok` with effect `copy` or `link` when the
   target accepted, `ok` with effect `none` when nothing under the cursor
   accepted the files, `cancelled` or `error` otherwise. A drop that nothing
   accepts is ignored: the agent never copies files on its own. A `drop`
   whose id matches no live drag gets an error result, "No drag in progress
   for this drop.", and is logged.
5. On `logoff`: reply `logoffResult` accepted, wait about 500 ms so that the
   reply reaches the client, then call `ExitWindowsEx` with `EWX_LOGOFF` and
   `EWX_FORCEIFHUNG`. Applications with unsaved work ask the user, as for a
   log off from the Start menu; `EWX_FORCE` is never used. When the call
   fails, a second `logoffResult` with `accepted: false` carries the Windows
   error text.
6. Tray icon, shown as the `trayIcon` setting says (see below), with its
   menu: Stage files in the session before the drop, Fast transfer of large
   files (Auto, Always, Never), Open log folder, About, Exit. About is a task
   dialog with the version, the publisher APROGSYS and its web site, and the
   project page, as links. Settings in
   `%LOCALAPPDATA%\UltimateRDP\agent.json` (`stageFiles`, `fastTransfer`,
   and `fastTransferMinBytes`, `logLevel` and `trayIcon` which are only
   editable in the file). Logs in
   `%LOCALAPPDATA%\UltimateRDP\logs\agent-<date>.log`, rotated by day, kept
   fourteen days.
7. Command line:
   * `--install`: add a `Run` value under `HKCU` so the agent starts with the
     user session, then start the agent.
   * `--uninstall`: remove that value, and the `HKLM` value too when run
     elevated; stop the running instance through its exit event, say so when
     none was running, and exit with code 1 when it still runs after five
     seconds.
   * `--install-all-users`: same under `HKLM` (needs an elevated prompt).
     Running it again changes nothing. It refuses an executable inside a
     user profile (the profile of the current user, or the folder of all
     profiles) and suggests `C:\Program Files\UltimateRDP`. It removes the
     `HKCU` value of the current user, which would start a second copy, and
     says so.
   * `--status`: print the version, the path of the executable, the `Run`
     values under `HKCU` and `HKLM`, the settings file, the `trayIcon` mode,
     the log folder and whether an agent runs in this session (its mutex
     exists). Changes nothing.
   * `--help`: list the options.
   * no argument: run.

#### Production behaviour of the agent

The agent is meant to start in every session of a Remote Desktop server,
where most users connect with mstsc and never have the `ULTRDP` channel. For
them it must be invisible, harmless and light.

* Silence. Without an option, nothing is ever shown: no window, no message
  box, no console output. An error in a handler of the UI thread is logged
  and the agent goes on. An unhandled error on any other thread (channel,
  drag, timer, thread pool) is logged, the simulated button is released if
  a drag held it, and the process ends at once with exit code 1 through
  `TerminateProcess`, before Windows can show an error report. A task error
  that nobody observed is only logged. The drag threads keep their own
  handler, which cancels the drag. A settings file that cannot be read gives
  the defaults; a log folder that cannot be created leaves the agent running
  without a log.
* Tray icon on demand. `trayIcon` is `auto` (the default), `always` or
  `never`. In `auto` the icon appears when an UltimateRDP client speaks on a
  newly opened channel, and disappears when that channel closes. In `never`
  there is no icon; `--uninstall` and the `Run` value control the agent. The
  parsing and the visibility rule are `TrayIconModes` in Core.
* Footprint. The icon, its menu, the drag infrastructure, the staging folder
  and the log off handler are created when first needed. Until the icon
  exists, the UI thread runs a plain `GetMessage` loop for its hidden window;
  the WinForms loop, which loads GDI+, only starts with the icon. The exit
  event of `--uninstall` is watched by the channel thread, so the waiting
  agent has no other thread of its own. Garbage collection is workstation,
  not concurrent, with `GCConserveMemory` 5. Measured on Windows 11 at idle,
  in a session without the channel, over sixty seconds: about 4 MB of
  private working set, 5 threads, and no measurable processor time, against
  about 7 MB and 12 threads before these changes.
* Retry without spinning. `ChannelRetryPolicy` in Core decides the wait
  before the next open: two seconds after a closed channel on which a client
  spoke, or an unexpected failure (a channel that closes before any client
  data counts as a failure), ten seconds when the session is not remote
  (`GetSystemMetrics(SM_REMOTESESSION)`), has no client, when the open fails
  with "not found" (the client did not ask for the channel), or after three
  failures in a row. For thirty seconds after the start, a connection or a
  logon, the wait stays at two seconds, because the channel may need a
  moment to appear. `WTS_REMOTE_CONNECT`, `WTS_CONSOLE_CONNECT` and
  `WTS_SESSION_LOGON` trigger an attempt at once.
* Session life. A disconnection closes the channel and hides the icon; a
  reconnection, from mstsc or from UltimateRDP, retries at once. The hidden
  window answers `WM_QUERYENDSESSION` with TRUE at once and, on
  `WM_ENDSESSION`, releases the simulated button, cancels the drag, closes
  the channel within half a second each and exits. Every log line is on
  disk as soon as it is written, so nothing needs flushing.
* Logging at scale. The default level stays `info`, but the waiting state
  writes one line at start, one line when the channel opens or closes, and
  at most one line per hour ("Still waiting for a client channel", with the
  reason; `IdleReport` in Core). A failure that a session without client
  does not explain, such as a policy that blocks the channel, is a warning,
  once until the channel opens.

### Localization

The user interface of both applications exists in English, French, German,
Spanish, Italian, Portuguese and Dutch. English is the source language and
the fallback for anything missing.

* Each application keeps its texts in `Resources/Strings.resx`, with one
  `Strings.<code>.resx` per other language. The build generates a `Strings`
  class from the English file, so a missing key fails the build. The
  translations become satellite assemblies, which the single file publish
  bundles into the executable.
* Code never shows a literal: a text is a resource key with its arguments,
  formatted with composite formatting. The client keeps the status texts in
  that form, so that a change of language can write them again; the log
  always gets the English form.
* Client: the setting `language` in `client.json` holds `auto` (the default)
  or a two letter code. `auto` follows the Windows display language of the
  user, read when the client starts, mapped by its two letter code to a
  supported language, English otherwise. The View menu has a Language
  submenu: Automatic (Windows language), then each language named in its own
  language. A choice applies at once: the client sets the user interface
  culture of the process and writes every menu, status text, tab text and the
  drop zone banner again through one `ApplyTexts` path. Dialogs opened later
  use the new language. The buttons of the Windows message boxes and dialogs
  follow the language of Windows.
* Agent: the texts it sends to the client follow the `clientLanguage` field
  of the client hello. Its tray menu, About dialog and command line output
  follow the Windows display language of the session.
* Texts that come from elsewhere stay as they are: the disconnection reason
  of the Remote Desktop control and Windows error texts are in the language
  of Windows; a text the agent already sent keeps its language until the next
  one. Product names, file names, menu shortcuts and the logs stay in
  English.

## Error handling

* Drop without agent: refused on the client, explained in the status bar.
* Drop without drive redirection: the agent refuses the drag with a text that
  says the drives are not redirected and points to the Local Resources
  setting.
* Item not reachable from the session (path on a drive that is not
  redirected, removable drive unplugged): the agent refuses the drag and names
  the item.
* No `dragAck` within a second: the client shows "The agent did not
  acknowledge the drag", sends `dragLeave` and refuses the drop. Since
  0.4.1 it also starts a new agent discovery, see "Version 0.4.1".
* No message from the client for five seconds during a drag: the agent
  cancels the drag in the session. The watchdog stops once the client
  dropped.
* Nothing under the cursor accepts the files: `dropResult` with status `ok`
  and effect `none`; the drop is ignored.
* A `drop` whose id matches no live drag: `dropResult` with status `error`
  and the message "No drag in progress for this drop."
* Agent cannot open the channel: when the session cannot have an UltimateRDP
  client (console session, no client, or a client without the channel),
  this is the normal state, reported at most once per hour. Any other
  failure, such as a policy forbidding the channel, is logged once at
  warning level. Retries go on in both cases.
* Protocol error (a line too long or bad JSON): logged, line reader reset,
  connection kept.
* No `logoffResult` within five seconds of a `logoff`: the client shows "The
  agent did not answer the log off request." and enables the command again.
* The errors of the fast transfer are listed under "Version 0.4".

## Security notes

* The agent only reads from `\\tsclient\`, that is from the drives the client
  user chose to share. It writes only to its own staging folder in the
  session, when staging is on or when files come through the channel. Where
  the dropped files go is decided by the application that accepts the drop.
  The agent does not run as a service and has no privilege beyond the user's
  own.
* A file that comes through the channel is written below the folder of its
  transfer only: the agent refuses a relative path that does not start with
  the name of a dropped item, that holds `..`, a device name (CON, PRN, AUX,
  NUL, COM0 to COM9 and LPT0 to LPT9 with their superscript digit forms,
  CONIN$, CONOUT$), an alternate data stream or a character that Windows
  refuses, and checks again that the full path stays in the folder.
* Simulated input happens only inside the user's own session, only during a
  drag, and only with the left button. See "Step 2" for the details.
* Messages carry file names, coordinates, modifier key flags and window names.
  File contents go through the channel only for a fast transfer, inside the
  RDP connection like any other channel data.
* The client never writes credentials. It reads the generic credential
  `TERMSRV/<host>` when one exists; other credential prompts come from the
  Microsoft control.

## Testing

* `UltimateRDP.Core.Tests` covers: framing round trip, split and merged chunks,
  oversize lines, byte model detection and conversion in both directions,
  JSON round trip of every message (hello with its language, dragEnter,
  dragMove, drop, dragLeave, dragAck, dropResult, logoff, logoffResult) and
  rejection of malformed ones, key state flags, path mapping (drive letter
  case, UNC rejection, relative rejection, rejection kinds), `.rdp` parsing
  (UTF-16 and UTF-8, types, defaults), session display sizes and scale
  factors, account names of saved credentials, the recent connections list,
  the language rules, and the agent rules: the `trayIcon` setting and its
  visibility, the retry waits of the channel, the spacing of the waiting log
  line and the folders refused by `--install-all-users`.
* The fast transfer is covered without a server: the new messages and their
  rejection when malformed, the `fastTransfer` setting and its decision, the
  free space rule, the relative paths, the sender (window, acknowledgements,
  checksums) and the receiver (order, offsets, sizes, checksums, names,
  cancel, write errors), the progress text rules, and whole transfers of a
  few megabytes through the ASCII line codec in both byte models, with a lost
  chunk, a damaged chunk and a cancel. A test sends 100 MB through the whole
  codec path and fails beyond a minute; it takes under a second.
* Client and agent are exercised by hand against a real session; the status
  bar and the agent log give enough detail to diagnose a failed drop.
* CI runs the tests on `windows-latest` for every push and pull request.

## Release

* Tag `vX.Y.Z` on `main` triggers the release workflow: build, test, publish
  both applications as self contained single file executables for `win-x64`,
  zip them as `UltimateRDP-vX.Y.Z-win-x64.zip` and
  `UltimateRDP.Agent-vX.Y.Z-win-x64.zip`, create the GitHub release with
  `docs/releases/vX.Y.Z.md` as body and attach both zips.
* Between the build and the release, a signing job signs both executables
  with the APROGSYS code signing certificate through Azure Artifact Signing.
  It is the only job that receives the Azure secrets.
* The signing job verifies every signature before the release job zips the
  files: `signtool verify /pa` must pass, the signer must be `CN=APROGSYS`,
  and the signature must carry an RFC 3161 timestamp.
* Self contained publishing means the user installs nothing, at the cost of a
  larger download.

## Version 0.2 plan

Scope chosen on 2026-11-06 after the first successful test against a real
server (channel byte model observed: one byte per character in both
directions; the control registers no drop target of its own; a drop landed
on the remote desktop as expected). Work is done in four sequential steps,
each one built, tested and committed before the next starts.

### Step 1: packaging and display

* Release archives contain one portable executable only: no `.pdb`, no
  extra file. Publish with `-p:DebugType=none -p:DebugSymbols=false`, and
  delete anything that is not the `.exe` before zipping. The executable runs
  without elevation (manifest `asInvoker`, extraction of native libraries
  to the user temp folder).
* View menu with "Scale to window" (smart sizing) and
  "Resize session to window" (default): on a window resize, debounced by
  about 300 ms, call `UpdateSessionDisplaySettings` on the control
  (`IMsRdpClient9` or later) so that the session resolution follows the
  window. If the server refuses, fall back to `Reconnect(width, height)`.
* DPI: before connecting, set `DesktopScaleFactor` and `DeviceScaleFactor`
  through `IMsRdpExtendedSettings` from the DPI of the monitor that holds
  the window. View menu entry to override the scale factor (100, 125, 150,
  175, 200) and reconnect.
* Full screen: F11 and Ctrl+Alt+Pause toggle `FullScreen` on the control,
  with the connection bar shown and pinnable; "Use all monitors" (View menu,
  needs a reconnect) sets `UseMultimon`. Drops from the local desktop are
  only possible in windowed mode or from a monitor the session does not
  cover; in full screen the drop point is mapped against the screen area
  the session covers (one monitor, or the virtual screen with all monitors).
* Keyboard: View menu "Windows keys" with the three `KeyboardHookMode`
  values (local, remote, remote in full screen; default remote in full
  screen like mstsc).
* Apply more `.rdp` keys: `audiomode`, `audiocapturemode`,
  `redirectprinters`, `redirectcomports`, `redirectsmartcards`,
  `keyboardhook`, `use multimon`, `desktopscalefactor`.
* Coordinates: the drop mapper keeps using the ratio between the session
  size and the control size, which is 1:1 after a resize in "resize" mode.

### Step 2: native drop into any remote application

Goal: a file dropped from the local desktop onto the session is received by
the remote application under the cursor as a normal OLE drag and drop, not
only by Explorer. Mail clients, editors, browsers, business applications:
anything that accepts a file drop in the session accepts it from UltimateRDP.

Principle: the agent becomes the drag source inside the session. It replays
the local drag in real time and ends it with a real drop at the point where
the user released the button.

* Live drag messages from the client, on the same channel and in the same
  ASCII line format: `dragEnter` (id, x, y, items, key state), `dragMove`
  (id, x, y, key state, throttled to about thirty per second and only when
  the point or the keys changed, plus one every second while the cursor
  rests, so that the five second watchdog of the agent does not end a
  drag that is only paused), `drop` (id, x, y, key state) and
  `dragLeave` (id). Key state carries the Ctrl, Shift and Alt flags for
  logging and for a later version; in 0.2 the agent injects no modifier
  key, and the allowed effects are copy and link only. Move is never
  offered: a move accepted by the remote Explorer would delete the file on
  the user's local disk through the redirected drive.
* Agent side, on `dragEnter`: on a dedicated STA thread with `OleInitialize`,
  create an invisible layered proxy window that covers the whole virtual
  screen (alpha 1, not 0, so that it really receives the click), move the
  remote cursor to the point and simulate a left button press on that
  window with `SendInput`; from the mouse down handler of the proxy, call
  `DoDragDrop` with a data object that offers
  `CF_HDROP` with the `\\tsclient\<drive>\...` paths or, for applications
  that do not accept UNC paths, with the same files staged in
  `%LOCALAPPDATA%\UltimateRDP\staging\<id>\` instead, when the setting "stage
  files in the session before the drop" is on (off by default, staging
  happens before `DoDragDrop` and can take time for large files). The
  points are offsets from the top left corner of the session desktop; the
  agent adds the virtual screen origin, which is negative when a monitor
  sits left of or above the primary one. The press is only injected once
  `WindowFromPoint` returns the proxy, so that it can never click the
  application under the cursor.
* The agent implements `IDropSource`: `QueryContinueDrag` returns continue
  while the client keeps sending `dragMove`, drop when `drop` arrived,
  cancel when `dragLeave` arrived or no message came for five seconds.
  Each `dragMove` moves the remote cursor with `SendInput` so that OLE
  delivers `DragOver` to the window under it and the target shows its own
  feedback. On `drop`, the agent moves the cursor to the final point and
  releases the simulated button once the target answered a `DragOver`
  there (or after 300 ms), since a target that never saw the drag at that
  point refuses the drop; on cancel it releases the button and returns
  cancel from `QueryContinueDrag`. The five second watchdog stops once the
  client dropped, because the target may keep its `Drop` call open while
  it shows a dialog.
* Result: `dropResult` carries an `effect` (`copy` or `link` when the
  target accepted, `none` otherwise; move cannot happen since it is never
  offered) and, when known, `targetWindow` with the class and title of the
  target. When `DoDragDrop` ends with effect none, the drop is ignored: the
  agent copies nothing and replies status `ok`, effect `none` and the
  message "Nothing under the cursor accepted the files.", which the client
  shows as is.
* Acknowledgement and timeouts: the agent answers `dragEnter` with
  `dragAck` once the drag loop runs, or with a refusal and its reason.
  When that takes more than 700 ms (staging of large files), it first
  sends an accepted `dragAck` with a message ("Copying N item(s) into the
  session before the drag starts...") and sends a refusal later if the
  drag cannot start. Without any `dragAck` within a second the client
  shows "The agent did not acknowledge the drag", sends `dragLeave` and
  refuses the drop. `dropResult` has no timeout because the target may
  show dialogs.
* Client side: a drop zone banner (a translucent strip at the top of the
  session that lets the mouse through) is shown during the drag; the
  client sends `dragEnter` on the first `DragOver` over the session (not on
  `DragEnter`, so that the point is known), `dragMove` while hovering,
  `drop` or `dragLeave` at the end. The local drag effect shown to the user
  is copy while the agent is in the drag, none when it refused.
* Security: the simulated input happens only inside the user's own
  session, only between a `dragEnter` and the matching `drop` or
  `dragLeave`, and only with the left button. Cancelling posts an Escape
  key message to the proxy window, never through `SendInput`, so that no
  key reaches the foreground application. Staged folders are deleted when
  the agent starts and one hour after their drop, not right after it: the
  target may still be reading the files.

### Step 3: connections

* `SessionView` user control that owns one control, one agent link and the
  drop logic; `MainForm` becomes a host with tabs, one per session, and a
  status bar that follows the active tab. Keyboard shortcuts and menus act
  on the active tab.
* File menu: Open .rdp, Recent connections (ten entries, stored in
  `%LOCALAPPDATA%\UltimateRDP\client.json`), New tab from the same file,
  Close tab.
* Credentials: before connecting, read the generic credential
  `TERMSRV/<host>` from the Windows Credential Manager, like mstsc does; if
  present, apply user name, domain and password to the control and skip the
  prompt. Nothing is written by UltimateRDP; saving happens through the Windows
  prompt of the control, which offers "Remember me". A File menu entry
  forgets the saved credential for the current host.

### Step 4: review, documentation, release

* Code review of the three steps, README update (new menus, modifiers,
  tabs, credentials), release notes `docs/releases/v0.2.0.md`, tag
  `v0.2.0`.

## Version 0.2.1

Simplification decided on 2026-11-07:

* A drop that nothing under the cursor accepts is ignored. The agent never
  copies files on its own: the fallback copy is gone, with its folder
  setting and its tray menu entries.
* The live drag is the only drop path. The `drop` message only ends a live
  drag and carries no items; the agent hello has no capability flag. The
  client and the agent must both run 0.2.1.

## Version 0.3

Two features, decided on 2026-11-09:

* Log off the remote session. Quit, Close tab and Disconnect only disconnect,
  as the Microsoft client does: the Windows session keeps running on the
  server with its applications. File, Log off the remote session ends it.
  The entry is enabled when the session is connected and the agent is ready.
  After a Yes/No confirmation ("This closes your Windows session on the
  server and ends the applications running there. Continue?"), the client
  sends `logoff` and shows "Logging off the remote session..." in the status
  bar; the disconnection that follows is reported by the control as usual.
* Localization of both user interfaces, as described under "Localization"
  above, with the `clientLanguage` field in the client hello.

The client and the agent must both run 0.3.0: an older agent ignores the log
off request and writes its texts in English.

Before the release, the agent was also prepared for servers where most
sessions use mstsc: it stays silent and light without a client, shows its
tray icon only while an UltimateRDP client is connected (setting
`trayIcon`), exits cleanly at log off, and gains `--status`. See
"Production behaviour of the agent" above.

The project was first called RdpDrop. Before the release of 0.3.0 it was
renamed UltimateRDP, and APROGSYS became its publisher. The names listed
under "Names" above replace the old ones: `RdpDrop.Client.exe` and
`RdpDrop.Agent.exe`, the channel `RDPDROP`, the folder
`%LOCALAPPDATA%\RdpDrop`, the mutex `Local\RdpDrop.Agent` and the `Run`
value `RdpDrop`. Since the channel name changed, a 0.2.1 agent never hears a
0.3.0 client: it must be exited and replaced, and its `Run` value removed.
The settings and logs of the old folder are not carried over.

## Version 0.4

### Fast transfer of large files

Decided on 2026-11-09 after a measurement in production: the remote
application read a dropped file through drive redirection at 3.6 MB/s, while
the link carried at least 9.7 MB/s upstream. Drive redirection reads in
blocks of 64 KB with one round trip per block, so 15 ms of latency caps it. A
continuous stream over the UltimateRDP channel has no round trip per block.
In this section MB means a million bytes.

Decision, on the agent. The setting `fastTransfer` is `auto` (the default),
`always` or `never`; `fastTransferMinBytes` is the threshold of `auto`,
10485760 bytes (10 MiB) by default. The tray menu "Fast transfer of large
files" offers Auto, Always and Never and saves the choice; the threshold is
only editable in the file. In `auto` the files are transferred when the
total size of the items of the `dragEnter` is at least the threshold;
`always` transfers every drop; `never` keeps the behaviour of version 0.3.
The client gives `size` for each item: the length of a file, or the total
length of the files of a folder, counted for at most 250 ms for all the
items together when the drag starts, so a very large folder may report less
than its real size. The rule
is `FastTransferPolicy` in Core. When it transfers, the agent checks that the
volume of the staging folder has the total size plus 10 percent free, and
refuses the drag otherwise with "There is not enough free space in the
session: N MB needed, M MB free." Two items with the same name are not
transferred: their files are read through drive redirection. When staging is
on and the files are transferred, the transfer replaces the staging copy.

Flow:

1. The drag starts as before: `dragEnter` with the sizes, the agent checks
   the items through drive redirection and starts the live drag over the
   `\\tsclient` paths. Its accepted `dragAck` carries `transfer: true` and a
   new `transferId`. When a provisional `dragAck` went out before the
   decision, the agent sends a second accepted one that carries them. The
   agent registers the transfer as offered before any `dragAck` carries the
   id (`TransferRegistry` in Core): from then on, a message of the transfer
   that arrives before the deferred drop was handled waits in a queue instead
   of being lost, whatever the timing.
2. The drag over the session goes on as today: live cursor, feedback from the
   target.
3. On the local drop the client sends `drop` with the point and
   `deferred: true`. The agent cancels the live drag (the button is released,
   nothing is dropped), keeps the point and creates the receiver of the
   transfer, which first handles the queued messages, in their order. A
   client that drops before the `dragAck` arrived sends a normal `drop`, and
   the files are dropped through drive redirection as before. When the drag
   ends without a deferred drop, the offer is withdrawn; if messages of the
   transfer were already waiting, the agent answers with an `error`
   `transferResult` that gives the reason the drag ended.
4. The client sends the files into
   `%LOCALAPPDATA%\UltimateRDP\staging\<transferId>\` on the agent, with the
   messages below.
5. When every file arrived and was checked, the agent sends `transferResult`
   with status `ok`, then drops the copies at the kept point with a short
   drag: it presses on its proxy window there, calls `DoDragDrop` with the
   local paths of the copies, and releases as soon as the target answered a
   `DragOver` there, or after 300 ms, as the live drag does for its final
   point. The usual `dropResult` follows, including effect `none` when
   nothing accepted the files. The copies are deleted one hour after the
   drop, like staged copies.

Messages, client to agent:

* `transferBegin` : `{ "type": "transferBegin", "transferId": "<guid>",
  "files": 3, "bytes": 157286400 }`, sent once before the first file. The
  agent checks the free space again with this exact total, and knows when the
  last file arrived.
* `fileBegin` : `{ "type": "fileBegin", "transferId": "<guid>",
  "fileIndex": 0, "relativePath": "Photos\\2026\\a.jpg", "size": 5242880,
  "modified": "2026-11-09T10:30:00Z" }`. Files are numbered from zero and sent
  one after the other. The first segment of `relativePath` is the name of the
  dropped item: the file itself, or the dropped folder that holds it, whose
  files follow with their paths in the folder. `modified` is the last write
  time in UTC, which the copy keeps.
* `fileChunk` : `{ "type": "fileChunk", "transferId": "<guid>",
  "fileIndex": 0, "offset": 32768, "data": "<base64>" }`. The client sends
  32 KiB of raw bytes per chunk by default, about 44 000 characters of
  base64, so that one line stays small on the wire in both byte models
  (about 88 KB in the UTF-16 model). Its setting `transferChunkBytes` in
  `client.json` changes the size, from 4 KiB to 256 KiB; a value outside
  these bounds is brought back within them. The agent accepts any chunk up
  to 256 KiB, about 350 000 characters, within the line limit of 1 MiB. The
  base64 stays inside the JSON string, so the ASCII line protocol is
  unchanged.
* `fileEnd` : `{ "type": "fileEnd", "transferId": "<guid>", "fileIndex": 0,
  "sha256": "<64 hexadecimal digits in lower case>" }`.
* `transferCancel` : `{ "type": "transferCancel", "transferId": "<guid>",
  "reason": "The user cancelled the transfer." }`, the reason in English for
  the log of the agent.

Messages, agent to client:

* `fileAck` : `{ "type": "fileAck", "transferId": "<guid>", "fileIndex": 0,
  "received": 2097152 }`, the bytes of the file received and written so far,
  sent after the first chunk of each file, every time 2 MiB more arrived,
  and at the end of each file.
* `transferResult` : `{ "type": "transferResult", "transferId": "<guid>",
  "status": "ok" | "error" | "cancelled", "message": "text", "bytes": 157286400,
  "seconds": 16.2 }`, with the time measured by the agent from
  `transferBegin` to the end.

Flow control and checks:

* The client keeps at most 4 MiB sent and not acknowledged, measured with the
  `fileAck` messages (`TransferSender` in Core). With 15 ms of latency this
  keeps the link busy far beyond the measured rate.
* The agent checks the order of the messages, that each chunk starts where
  the previous one ended (a lost chunk is detected there), the base64, that a
  file never goes past its size, the size and the SHA-256 at `fileEnd`, that
  the sizes of the files fit in the total of `transferBegin`, and at the end
  that every dropped file arrived (`TransferReceiver` in Core). Any failure
  ends the whole transfer with an `error` result whose message says what
  happened, and deletes what was written at once.
* A transfer that receives nothing for 60 seconds stops with an error. A new
  `dragEnter` cancels the transfer in progress, as it cancels a drag. When
  the agent exits, it cancels the transfer.
* After the end of a transfer, the chunks still on their way are dropped
  without an answer. A message for a transfer the agent never offered gets
  one `error` result. The queue of an offered transfer holds at most twice
  the window of the client in base64; beyond that the transfer stops with an
  `error` result, which a client that keeps to its window never meets.

Client:

* After the deferred drop, a background thread lists the files of the items
  (folders file by file, hidden and system files included, junctions and
  symbolic links to folders skipped), reads each file with a 1 MiB
  `FileStream` buffer, computes its SHA-256 and builds each `fileChunk` line
  directly from the bytes, without a copy of the whole file. Each send is
  handed to the UI thread, where the control lives, in order; the thread
  waits while the window is full. The UI stays responsive.
* The status bar shows "Preparing the transfer of 2 item(s)...", then
  "Transferring 3 file(s): 42 % (12.3 MB/s, 1 min left)" from the
  acknowledgements, at most four times a second, with the rate measured over
  the last five seconds and the time left rounded up. Once the agent has
  every file: "Transferred 3 file(s) at 12.3 MB/s, dropping...", then the
  usual "Dropped on ...".
* Session, Cancel the transfer is enabled during a transfer. It stops the
  thread, sends `transferCancel` and shows "Transfer cancelled.". A file that
  cannot be read, a file whose length changed since it was listed ("changed
  while it was being sent. Try again."), a send that the control refuses, no
  acknowledgement 10 seconds after the first chunk of the transfer went out
  ("The agent did not acknowledge the first data of the transfer."), or no
  acknowledgement for 60 seconds while the window is full or after the last
  file ("The agent stopped answering during the transfer.") also send
  `transferCancel`, with the reason in the status bar. The rules are
  `TransferAckWatch` in Core. After a missing acknowledgement the client
  logs the byte model detected on the data it receives, the chunk size, the
  length of the longest chunk line, the number of chunk lines sent and the
  bytes in flight, then looks for the agent again with a hello, which tells
  whether short messages still get through.
* One transfer at a time: while one runs, a new drag over the session is
  refused with "A transfer is in progress. Wait for its end, or cancel it
  from the Session menu."
* A disconnection ends the transfer: "The transfer stopped: the session was
  disconnected." The agent deletes what it received after 60 seconds without
  data.
* The log of each side gets one line at the start and a summary at the end
  (files, bytes, seconds, MB/s). Beyond that, the client logs the length of
  the first chunk line it sends, and the agent logs the first chunk it
  receives of each file, with its size; nothing else per chunk. The client
  no longer logs the strings it receives when they only hold
  acknowledgements.

Limits:

* Drive redirection is still needed: the live drag offers the `\\tsclient`
  paths, and the agent checks the items through it before the drag starts.
* base64 adds a third to the data: in the one byte per character model the
  file rate can reach about three quarters of the upstream capacity of the
  channel; in the UTF-16 model every character costs two bytes on the wire
  and the rate drops to about three eighths. The model in use is in both
  logs. The compression of the RDP connection may win some of it back.
* Empty folders inside a dropped folder are not created; an empty dropped
  folder is.
* The drop happens where the button was released, after the transfer: a
  window moved in the meantime may no longer be under that point.

Measured without a server, in the tests: 100 MB go through the whole codec
path (chunk lines, channel bytes in blocks of 1600, model detection, line
reader, JSON, base64, checksums on both sides) in about two seconds in
each byte model on a development computer, with chunks of 32 KiB as with
chunks of 256 KiB, so the codec is far from limiting the transfer.

The client and the agent must both run 0.4.0 for the fast transfer. A 0.3.0
client sends no sizes and never defers its drops, and a 0.3.0 agent never
asks for a transfer: in both cases the files are read through drive
redirection as before.


## Version 0.4.1

Two defects of the fast transfer, seen on 2026-11-09 in the first test of
0.4.0 against a real server, with a file of 496 MB. In that session the agent
received the data of the client in the UTF-16 model.

* First attempt: the client sent `drop`, `transferBegin`, `fileBegin` and the
  first chunks at once. The agent created the receiver of the transfer when
  it handled the deferred drop, and the receiver then got `fileBegin`
  without `transferBegin`: the transfer failed at once with "came out of
  order". The log of the agent has no trace of `transferBegin` at all:
  neither accepted, nor refused as unknown, nor malformed. The channel
  thread handles the lines one after the other, and the deferred drop
  created the receiver before it returned, so a `transferBegin` line that
  reached the line reader would have reached the receiver or been logged.
  The line therefore never came out of the line reader as a message: it was
  lost between the control of the client and the agent, or arrived as NUL
  characters, which the agent trimmed from the front of the next line
  without a word. Which of the two cannot be told from 0.4.0. Version 0.4.1
  makes every such case visible and removes any dependency on timing:
  * the transfer is registered when the agent decides to offer it, before
    the `dragAck`, and its messages wait in a queue until the deferred drop
    creates the receiver (`TransferRegistry` in Core, see steps 1 and 3 of
    the flow above);
  * a line made only of NUL characters and spaces, or preceded by more than
    one of them, gives a warning in the agent log;
  * at the debug level, the agent logs every message it receives except the
    chunks and the moves;
  * every transfer message that does not reach a receiver is logged: the
    first one of each transfer and every message that is not a chunk at
    the info level, the other chunks at the debug level;
  * a file message before `transferBegin` fails with "came before
    transferBegin: no transferBegin arrived for this transfer.".
* Second attempt: `transferBegin` arrived, and no chunk ever did. The control
  raised no error on `SendOnVirtualChannel`, and the client gave up after 60
  seconds with 4 MiB in flight. A chunk line of 256 KiB was about 350 000
  characters, about 700 KB on the wire in the UTF-16 model, in one call. The
  control is taken as unable to deliver strings that large: the default
  chunk is now 32 KiB, about 44 000 characters, and the client setting
  `transferChunkBytes` tunes it without a release. The window stays at
  4 MiB, which now holds 128 chunks.

Early failure detection: the agent acknowledges the first chunk of every
file, and the client fails a transfer whose first chunk got no
acknowledgement within 10 seconds, with the diagnostics described under
"Client" above. The first chunk is logged on both sides.

Agent restart: the control does not report that the agent exited or
restarted in the session, so the client stayed on "Agent ready". A
restarted agent still works, since it detects the byte model from the next
message, but it writes its texts in English until it gets a hello; a dead
agent only showed as "The agent did not acknowledge the drag" at each drag.
Now a missing `dragAck`, or a transfer that fails for a missing
acknowledgement, starts one new discovery period: the agent label goes to
"Agent: waiting", a hello goes out every two seconds, and the label comes
back to "Agent ready" when the agent answers, or goes to "No agent on the
remote computer" after sixty seconds. The hello also gives a restarted agent
the language again.

The fallback of version 0.3 stays available: `fastTransfer` set to `never`
in the agent settings, or Never in its tray menu, reads every file through
drive redirection. A transfer that fails does not switch to drive
redirection by itself.

The client and the agent must both run 0.4.1. A 0.4.0 agent works with a
0.4.1 client, but without the queue and the first acknowledgement, so the
client fails after 10 seconds when the data gets through slowly; a 0.4.0
client sends chunks of 256 KiB.


## Version 0.4.2

Decided on 2026-11-09 after the first transfer in production with 0.4.1: a
file of 496 MB took 193 seconds, and the only sign of it was the text in the
status bar. The user took the transfer for a failure and dragged the file
again, which the client refused with the "not allowed" cursor. A running
transfer must be obvious.

Decision: a small window, `TransferProgressForm` in the client, opens when
the transfer starts, that is on the deferred drop after the agent accepted the
drag with a transfer, and closes when it ends. Only the client changes; the
protocol does not.

What it shows, from top to bottom:

* the name of the file being sent, and "file 2 of 3" on the right; the
  current file is the first one whose bytes are not all acknowledged
  (`TransferProgress.CurrentFile` in Core);
* a progress bar of the acknowledged bytes over the total;
* "42 %, 2.6 MB/s, 1 min 40 s left": the values of `TransferProgress`, the
  time left rounded up to five seconds under an hour, then to the minute;
* "Destination:" and the host name of the session;
* a notice line and one button.

Stages:

1. While the files are listed, before the first chunk, the bar runs without a
   value (marquee), the text says "Preparing...", the first line gives the
   first dropped item and the number of items.
2. During the transfer, the window follows the acknowledgements at most four
   times a second: it is refreshed where the status bar is, with the same
   throttling.
3. After a success: "Transferred 3 file(s) in 3 min 13 s at 2.6 MB/s,
   dropping into the session...", with the seconds and the rate of the
   status bar text. When the dropResult arrives, the first part stays and the
   result of the drop ("Dropped on ...", or why nothing took the files)
   follows on a second line; the window closes three seconds later, and
   waits while the mouse is over it.
4. After a failure, a cancel or a disconnection: the reason, the same text
   as in the status bar, in red, and the button becomes Close. The window
   stays open until the user closes it, or until a new drag starts.

The button is Cancel while the transfer runs and takes the path of Session,
Cancel the transfer. While a transfer runs, a drag or a drop over the
session is refused as before with the status text, and the window also says
"Wait for this transfer before dropping more files." for five seconds after
the last refused drag over.

Window rules:

* Owned by the main form, not topmost, so it stays above the main form
  only. It is shown without activation and the code never activates it, so
  the keyboard stays in the session. A click on its button activates it, as
  any click would.
* Centered over the session area when it opens, within the working area of
  the screen, until the user moves it. It is about 460 by 170 logical pixels
  of client area, laid out at 96 DPI and scaled to the DPI of its monitor.
* No close box: the button closes it once the transfer ended.
* It belongs to the session view that runs the transfer: switching tabs
  leaves it on screen, and closing that tab cancels the transfer, while the
  agent can still be told so that it deletes what it received at once, then
  closes the window. A change of language writes its texts again.

The texts of the progress line and the durations are built by
`TransferProgressText` in Core from templates of the resources and the
current culture, so that they are tested without the user interface. The
status bar texts are unchanged.

Limits: over a session in full screen on the monitor of the main form, the
window, which is not topmost, may be hidden by the full screen window.

The client runs 0.4.2; the agent can stay at 0.4.1, since it does not change.
A 0.4.2 agent is the same as a 0.4.1 agent with another version number.
