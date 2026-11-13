# UltimateRDP design

Date: 2026-11-05, updated on 2026-11-13 for version 0.7.0.

## Goal

Let a user drag files or folders from the local Windows desktop and drop them
into a Remote Desktop session, on the application under the cursor when the
button is released. The application receives them as a normal drop. When
nothing under the cursor accepts them, the drop is ignored.
The transfer uses the standard RDP drive redirection; UltimateRDP only adds the
"drop" gesture on top of it. Since version 0.4, large files travel instead
through the UltimateRDP channel before the drop, which is faster; see
"Version 0.4" below. Since version 0.6, they travel through a direct TLS
connection between the client and the agent when the network allows it, with
the channel as the fallback; see "Version 0.6". Since version 0.7, that
connection is several TCP connections at once; see "Version 0.7".

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
* `U15Codec` and `ChunkEncodings`: the dense encoding of the chunks,
  described under "Version 0.5". `DropDecision`: the drop held for the
  answer of the agent, described under "Version 0.4.5" and "Version 0.5".
* `DirectFrames`, `DirectFrameReader`, `DirectSender`, `DirectReceiver` and
  `DirectTransferPolicy`: the direct connection, described under "Version 0.6",
  over several connections since "Version 0.7", with `DirectPacer`,
  `DirectRateMeter` and `IDirectStorage`.

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

* `hello` : `{ "type": "hello", "version": 1, "agentVersion": "0.6.0",
  "driveRedirectionAvailable": true, "clientToAgentModel": "utf16",
  "reliableTransfer": true }`
* `dragAck` : `{ "type": "dragAck", "id": "<guid>", "accepted": true,
  "message": "optional text", "transfer": true, "transferId": "<guid>",
  "provisional": true, "direct": { "port": 3400, "token": "<64 hex digits>",
  "certSha256": "<64 hex digits>", "host": "192.0.2.10" } }`
* `dropResult` : `{ "type": "dropResult", "id": "<guid>",
  "status": "ok" | "error" | "cancelled", "effect": "copy" | "link" | "none",
  "message": "human readable text", "targetWindow": { "className": "CabinetWClass",
  "title": "Documents", "childClassName": "DirectUIHWND", "processName": "explorer" } }`
* `logoffResult` : `{ "type": "logoffResult", "id": "<guid>", "accepted": true,
  "message": "human readable text" }`
* `fileAck` and `transferResult`: the fast transfer, described under
  "Version 0.4".
* `transferResend` : `{ "type": "transferResend", "transferId": "<guid>",
  "from": 1, "to": 2, "damaged": true }`, the lines of a transfer that the
  agent misses, described under "Version 0.5.1"; `damaged` under "Version
  0.5.2".

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
* `clientToAgentModel` of the hello of the agent, `utf16` or
  `oneBytePerChar`, and `provisional` of `dragAck` are new in 0.5.0, see
  "Version 0.5". They are not written when they have no value, and an older
  peer ignores them.
* `reliableTransfer` of the hello of the agent, `seq` of `transferBegin`,
  `fileBegin`, `fileChunk`, `fileEnd` and `fileAck`, and the message
  `transferResend` are new in 0.5.1, see "Version 0.5.1". The client only
  numbers the lines of a transfer when the agent says `reliableTransfer`.
* `damaged` of `transferResend`, and `length` of a numbered base64
  `fileChunk`, are new in 0.5.2, see "Version 0.5.2". They are not written
  when they have no value, and an older peer does not use them.
* `direct` of `dragAck` is new in 0.6.0, see "Version 0.6". It is not
  written when the agent offers no direct connection, and an older client
  ignores it and sends the files through the channel. Its fields `version`
  and `connections` are new in 0.7.0, see "Version 0.7".

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
  used to read. When sending, the client always hands the ASCII lines
  themselves to the control: whatever the control does with them, the agent
  receives either ASCII bytes or UTF-16LE bytes, and both are covered by its
  own detection. One call of the control may carry several lines one after
  the other, up to 200 000 characters by default (see "Version 0.4.3"), and
  since 0.5.1 short lines never go alone just before a line that does not
  fit beside them (see "Version 0.5.1").
* One exception since 0.5.0: when the agent says that the data of the client
  reaches it in the UTF-16 model, the chunks of a transfer travel in `u15`,
  whose units are characters from U+1000 to U+8FFF. Those lines are not
  ASCII, and the agent reads them from the string of the line reader, before
  any ASCII conversion (see "Version 0.5"). Every other line stays ASCII.
* Both sides accumulate until `\n` and parse each line on its own. Blank
  lines are ignored. A line above 1 MiB is a protocol error that drops the
  buffer.

This lives in Core as `LineFraming` (encode a message as an ASCII line),
`LineReader` (accumulate text, yield complete lines), `ChannelTextCodec`
(model detection and byte/string conversion in both directions),
`IncomingTextDecoder` (the reading side of the agent), `SendQueue` (the lines
of the client grouped into calls) and `SentDataRetention` (when the client
frees what it handed to the control), all unit tested.

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
   stop or restart without closing a handle under a pending read (or, with
   the `channelRead` setting at `file`, `ReadFile` on the file handle of the
   channel, see "Version 0.4.4"). Detect the
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
   files (Auto, Always, Never), Direct connection for large files (Auto,
   Never), Open log folder, About, Exit. About is a task
   dialog with the version, the publisher APROGSYS and its web site, and the
   project page, as links. Settings in
   `%LOCALAPPDATA%\UltimateRDP\agent.json` (`stageFiles`, `fastTransfer`,
   `directTransfer`, and `fastTransferMinBytes`, `directPortRange`,
   `logLevel`, `trayIcon` and `channelRead` which are only editable in the
   file). Logs in
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
  about 7 MB and 12 threads before these changes. Version 0.4.4 gives the
  same figures, see "The agent without a client" under "Version 0.4.4".
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
  RDP connection like any other channel data, or through the direct TLS
  connection of version 0.6, whose rules are under "Security rules" in
  "Version 0.6", as changed in "Version 0.7".
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
* The measures of version 0.4.4: the `channelRead` setting, the channel
  headers of the file handle (detection, chunks one per read and several
  per read, a last chunk followed by the next message, every inconsistency,
  and lines read back through the decoder and the line reader), the read
  counters and their summary, the call counters and the acknowledgement
  round trips of the client, the window bounds and its raise above the
  acknowledgement interval, and whole transfers with other windows.
* The dense encoding of version 0.5: round trips of `u15` (nothing, 1 to 16
  bytes, 128 KiB, random lengths), the padding of the last unit, the bounds
  of the alphabet, the hand parser on real lines read through the decoder
  and the line reader in reads of 1600 and 1599 bytes, the conversions of
  the agent path, whole transfers in `u15` with batches of two chunks per
  call, and 100 MB in `u15` through the whole codec path. The held drop:
  provisional and final answers of the agent.
* The reliable transfer of version 0.5.1: the numbers of the lines and their
  place in the hand built chunk lines, the inbox of the agent (order, gaps,
  duplicates, the bound, the repeated requests and their limit), the outbox
  of the client, the batches of short lines, the probe rule, and whole
  transfers through the codec path that lose calls: see "Version 0.5.1".
* The direct connection of version 0.6: the frames and their limits, the
  token, the order of the frames, the paths, the sizes, the checksums, the
  free space, a truncated stream before and after the first
  acknowledgement, a cancel, a file that changed, and 100 MB through an in
  memory connection; see "Version 0.6". Since 0.7, the same over 1, 4 and 8
  connections, with the blocks, the tokens of each connection, the cancels
  and the limit of the rate; see "Version 0.7".
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
   drop that happens before the `dragAck` arrived waits for it on the client,
   for at most 1.5 seconds, and then goes out as if it had happened after
   the `dragAck` (see "Version 0.4.5"); clients 0.4.0 to 0.4.4 sent a normal
   `drop` at once, and the files went through drive redirection. When the drag
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
  `fileAck` messages (`TransferSender` in Core); since 0.4.4 the setting
  `transferWindowBytes` changes it. With 15 ms of latency this
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
transfer must be obvious. The owner then asked for the same window at every
drop, so that any drop gets a visible answer.

Decision: a progress window appears at every drop. One small window,
`DropProgressForm` in the client, titled "Drop into the session", opens as
soon as the local drop went out to the agent, with or without a transfer, and
shows the drop until its result.
A drop that the client refuses before sending it opens no window; the status
bar says why, as before. Only the client changes; the protocol does not.

What it shows, from top to bottom:

* the first dropped item and "2 item(s)", or during a transfer the name of
  the file being sent and "file 2 of 3"; the current file is the first one
  whose bytes are not all acknowledged (`TransferProgress.CurrentFile` in
  Core);
* a progress bar;
* a status text of two lines, which follows the stage;
* "Destination:" and the host name of the session;
* a notice line and one button.

Stages:

1. Preparing or dropping, with a bar that runs without a value (marquee):
   * for a drop through drive redirection (small files, or `fastTransfer`
     set to `never`): "Dropping into the session..." from the local drop
     until the dropResult;
   * for a transfer, while the files are listed, before the first chunk:
     "Preparing...";
   * after a transfer that succeeded: "Transferred 3 file(s) in 3 min 13 s
     at 2.6 MB/s, dropping into the session...", with the seconds and the
     rate of the status bar text, until the dropResult.
2. Transferring, with a determinate bar of the acknowledged bytes over the
   total and "42 %, 2.6 MB/s, 1 min 40 s left": the values of
   `TransferProgress`, the time left rounded up to five seconds under an
   hour, then to the minute. The window follows the acknowledgements at most
   four times a second: it is refreshed where the status bar is, with the
   same throttling.
3. Result:
   * a dropResult with status `ok`: the text of the status bar ("Dropped on
     ...", or "Nothing under the cursor accepted the files."), after the
     summary of the transfer when there was one, and a full bar; the window
     closes three seconds later, and waits while the mouse is over it;
   * a dropResult with status `error` or `cancelled`, a transfer that failed
     or was cancelled, or a disconnection: the reason, the same text as in
     the status bar, in red, and the button becomes Close. A disconnection
     while a drop without transfer waits for its result gives "The drop
     stopped: the session was disconnected.", which only the window shows.
     The window stays open until the user closes it, or until a new drag
     starts.

The button is Cancel while a transfer runs and takes the path of Session,
Cancel the transfer; otherwise it is Close. While a transfer runs, a drag or
a drop over the session is refused as before with the status text, and the
window also says "Wait for this transfer before dropping more files." for
five seconds after the last refused drag over. A new drag closes the window
of an earlier drop that is no longer transferring; its drop opens a new one.

Window rules:

* Owned by the main form, not topmost, so it stays above the main form
  only. It is shown without activation and the code never activates it, so
  the keyboard stays in the session. A click on its button activates it, as
  any click would.
* Centered over the session area when it opens, within the working area of
  the screen, until the user moves it. It is about 460 by 170 logical pixels
  of client area, laid out at 96 DPI and scaled to the DPI of its monitor.
* No close box: the button closes it when no transfer runs.
* It belongs to the session view of the drop: switching tabs leaves it on
  screen, and closing that tab cancels a transfer in progress, while the
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


## Version 0.4.3

A short line sent to the channel just before a large one was sometimes lost,
without any error on either side. Two incidents of 2026-11-09, with the logs
of both sides:

* With 0.4.0, in the UTF-16 model: the client sent `drop` (101 characters),
  9 ms later `transferBegin` (101 characters), then `fileBegin`, then a chunk
  line of 350 000 characters. The agent received `drop`, then `fileBegin`:
  the `transferBegin` line never came out of its line reader, and no protocol
  error was logged.
* With 0.4.1 on both sides, in the one byte per character model: the client
  sent `transferBegin` (101 characters), then `fileBegin` (about 200
  characters, `SendOnVirtualChannel` returned without an exception), then
  8 ms later the first chunk line of 174 868 characters. The agent, at the
  debug level, received `transferBegin`, then the chunk ("A chunk of file 0
  came while file (none) is open"), never `fileBegin`, without any warning
  about NUL characters or long lines. Two other transfers of the same day,
  18 MB and 496 MB, worked with the same code.

So the line is lost between the return of `SendOnVirtualChannel` and the read
of the agent. The likely mechanism, not proven: the control sends after the
call returned, from the BSTR that the interop marshaller frees as soon as the
call returns; when the heap gives that memory to something else first, the
control reads a wrong length or wrong content. The fixes below hold whether
or not this is the exact mechanism.

### The client owns the strings it sends

`IMsTscAxOwnedData` in the client is a copy of the IMsTscAx interface whose
`SendOnVirtualChannel` takes the data as a pointer. It was checked by
reflection against `Interop.MSTSCLib.dll` of Devolutions.MsRdpEx 2026.10.26
(the legacy interop): IID `8C11EFAE-92C3-11D1-BC1E-00C04FA31489`, a dual
interface that derives from `IMsTscAx_Redist`, which declares no member, and
declares 30 methods; `SendOnVirtualChannel` is the 30th, DISPID 34, slot 36
of the table of functions after IUnknown and IDispatch. The copy repeats the
30 methods in the same order and with the same signatures. A throwaway
program confirmed the layout on the control itself: a server name written
through slot 7 of the copy reads back through the interop, and the desktop
width and the version read through the copy match the interop.

`OwnedChannelData` in the client allocates every string it hands to the
control with `Marshal.StringToBSTR` and keeps it (`SentDataRetention` in
Core):

* a call that carried chunks of a transfer is freed once the agent
  acknowledged the first chunk of a later call: the channel keeps the order,
  so the agent then received the earlier call in full;
* a call without chunks is freed 10 seconds after it went out, and a call
  with chunks 60 seconds after, which only happens when its transfer ended
  without an acknowledgement;
* when more than 64 MiB are kept, the oldest calls are freed first, with a
  line in the log;
* when the tab closes, what is kept is freed 60 seconds later.

During a transfer, about the window in base64 and one call are kept: some
13 MB, two bytes per character. Before the first connection of a tab, the client checks that the
copy has the IID and the methods, in their order, of the interface of the
interop assembly, and that the control gives it; otherwise it uses the
interop call as before. The log says once which path is used:
"SendOnVirtualChannel goes through the hand written interface (slot 36)", or
a warning that gives the reason.

### The client sends the lines together

The line protocol allows several lines in one call, and the line reader of
the agent already splits them. `AgentLink` now puts every line into a
`SendQueue` (Core) and sends the queue at the end of the current message of
the UI thread, through a posted call, or at once when more characters wait
than one call may hold. Each call of the control holds at most 200 000
characters by default; a single longer line, a chunk of more than about
146 KiB, goes alone. The order of the lines never changes. The limit is the
client setting `transferBatchCharacters` in `client.json`, from 50 000 to
1 048 576 characters, logged at startup with the other settings, so that it
can be tuned without a release.

Sequence of the calls for a drop with transfer, with the default chunk of
32 KiB:

* before, one call per message: `drop`, `transferBegin`, `fileBegin`, then
  one call per chunk line of about 44 000 characters;
* now, `drop` leaves at the end of the UI message that handled the drop. The
  background thread of the transfer then posts `transferBegin`, `fileBegin`
  and the first chunks; whatever was posted before the queue is sent leaves
  in the same call, so `transferBegin` and `fileBegin` normally travel with
  the first chunks instead of alone in front of them. The chunks that follow
  go by up to 4 per call while the window has room, and the `fileEnd` and
  `fileBegin` lines between two files travel with them. With chunks of
  128 KiB (174 868 characters) each chunk goes alone, with the short lines
  that fit beside it.

The limit of 200 000 characters keeps every call close to the size seen to
work in production, 174 868 characters in one call, as long as the chunks
stay at 128 KiB or less; with 0.4.0, calls of 350 000 characters in the
UTF-16 model delivered nothing. Calls of up to
1 MiB of characters were considered and not taken, since nothing shows that
the control delivers them.

A call that fails empties the queue and reports the lost lines: a transfer
that had lines among them stops with the text of a send refused by the
control, as before, and a drop without transfer shows that the drop was not
sent. A lost `dragEnter` or `logoff` is caught by the wait for its answer, as
before. A disconnection drops the lines still waiting, with a line in the log.

### Logs

The client has a `logLevel` setting in `client.json`: `info` by default, or
`debug`. At the debug level the client logs every message it queues with its
type and length (`fileBegin` and `fileEnd` with their file, each chunk with
its file and the bytes of the transfer sent with it, the moves too), and one
line per call of the control: number of lines, characters, type and subject
of the first line, and the strings kept. The info level keeps the lines it
had: the drag messages except the moves, `transferBegin`, `transferCancel`,
hello, logoff and the first chunk line of a transfer.

### The agent names a lost message

`TransferReceiver` fails with the new reason `MessageLost` when a later
message shows that one never arrived:

* a chunk or a `fileEnd` for a file whose `fileBegin` never arrived:
  "fileBegin for file N never arrived";
* a `fileBegin` for the next file while a file is open: "fileEnd for file N
  never arrived";
* a message of a file before `transferBegin`, as in 0.4.1.

The client shows the message of the agent as before, which now reads "A
message of the transfer was lost on the way: fileBegin for file 0 never
arrived. Try again." in the language of the client. The log of the agent has
the English detail. `IncomingTextDecoder` moved from the agent to Core, so
that the tests read several lines of one call through the same code as the
agent.

An automatic retry of the transfer after such a loss was considered: the
agent would keep the drop point for 10 seconds after the failure and restart
its receiver on a new `transferBegin` with the same id. It is not done. The
agent forgets the drag when its transfer fails, chunks of the failed attempt
still on their way would reach the new receiver before its `transferBegin`,
and the failure deletes the staging folder of the id; handling all three
changes the state machine of the drop on the agent and adds a field to
`transferResult`, for a loss that the two fixes above should remove. The
user drops the files again.

The client and the agent should both run 0.4.3. A 0.4.1 or 0.4.2 agent works
with a 0.4.3 client, since several lines in one call were always allowed; it
only reports a lost `fileBegin` as "Data was lost or damaged on the way." A
0.4.3 agent works with a 0.4.1 or 0.4.2 client.


## Version 0.4.4

Measured in production with 0.4.3: 2.1 to 2.6 MB/s of file data, about
3.5 MB/s of base64 on the channel, with chunks of 32 KiB as with chunks of
128 KiB, on a link that carries at least 12 MB/s. The codec handles 100 MB
in about two seconds in the tests, so it is not the limit. Three places can
be: the calls of the client to the control, the reads and the processing of
the agent, or the flow control of the Remote Desktop connection between the
two. Version 0.4.4 measures each of them and changes nothing in the
protocol; it does not make the transfer faster by itself.

### What is measured

Client, during each transfer, from the deferred drop to its end
(`ChannelCallStats` and `AckRoundTrips` in Core, kept by `AgentLink`):

* the time inside every call of `SendOnVirtualChannel`, with
  `Stopwatch.GetTimestamp` around the call itself, on the path of the hand
  written interface and on the interop path alike;
* for every acknowledgement of the agent, the time since the call that
  carried its last byte returned, and the bytes handed to the control and
  not yet acknowledged at that moment.

Agent, for every read of the channel thread (`ChannelReadStats` in Core,
kept by `ChannelLoop`):

* the bytes of the read, sorted in three counts: exactly 1600 bytes
  (CHANNEL_CHUNK_LENGTH), fewer, more;
* the time blocked inside the read call, with or without data;
* the time from the return of the read to the next call, which is the
  processing of the data: decoding, line reader and the handling of the
  messages, of which the handling alone (JSON, base64, checksum, file
  write) is counted apart.

The counters exist only while a transfer runs: the transfer creates them
when its receiver is created at the deferred drop, the channel thread
fills them while they exist, and the transfer logs them and drops them at
its end, with the traffic counters of the session from `WTSSessionInfoEx`
when Windows gives them (`ChannelMeter` in the agent). Without a transfer
the channel thread takes no timestamp at all; during one, a read costs two
calls of `Stopwatch.GetTimestamp` and a few additions.

Once per connection, when the client first speaks on the channel, the agent
logs the client address (`WTSClientAddress`), the name of the connection
and the session flags and counters of `WTSSessionInfoEx`. Windows does not
tell a process of the session whether the connection uses UDP; the
connection bar of the client shows it (the signal icon, then "Connection
information").

### The window of the client

The client setting `transferWindowBytes` in `client.json` replaces the fixed
window of 4 MiB: 4194304 by default, from 1048576 (1 MiB) to 33554432
(32 MiB), brought within these bounds. The agent acknowledges every 2 MiB
and at the end of each file, so a window below 2 MiB plus one chunk would
wait for an acknowledgement that never comes: such a window is raised to
2 MiB plus one chunk (`TransferSender.EffectiveWindow`), and the start line
of the transfer gives the window used and the setting. The strings kept for
the control (see "Version 0.4.3") may take four times the window, 64 MiB at
least, so that a large window does not free them early. The queue of the
agent for the messages that come before the deferred drop now allows twice
the upper bound of the window, 32 MiB, in base64.

### The two read modes of the agent

The agent setting `channelRead` in `agent.json` is `wts` (the default) or
`file`, read at startup:

* `wts`: `WTSVirtualChannelRead` with a timeout of 500 ms and a buffer of
  64 KiB, as before. Each read gives at most one chunk of 1600 bytes.
* `file`: the agent gets the file handle of the channel with
  `WTSVirtualChannelQuery(WTSVirtualFileHandle)`, duplicates it, and reads
  it with `ReadFile` and a buffer of 64 KiB (`ChannelFileReader`). The
  handle is opened for overlapped operations, so each read is an overlapped
  one: the channel thread waits for it 500 ms at a time and checks its stop
  and restart flags in between, as in `wts` mode; when the channel closes,
  the pending read is cancelled with `CancelIoEx` and waited for before its
  buffer is freed. Writes still use `WTSVirtualChannelWrite`. The question
  is whether one read can then return more than one chunk, and whether the
  rate changes.

Reads through the file handle of a static channel may put a
CHANNEL_PDU_HEADER of 8 bytes before each chunk: the total length of the
message the chunk belongs to (one `SendOnVirtualChannel` call of the
client, in bytes), then the flags, little endian, with CHANNEL_FLAG_FIRST
(1) on the first chunk of a message and CHANNEL_FLAG_LAST (2) on its last
one. `ChannelPduFraming` in Core finds out on the first block of a
connection which case applies: a block that starts with `{` holds the data
itself and every later block passes through unchanged; a block whose first
8 bytes read as the header of a first chunk followed by `{` holds headers,
which are then taken out of every block. A block may hold several chunks: a
chunk that is not the last one ends with the block, unless a header of the
same message follows it exactly 1600 bytes later; a last chunk takes the
bytes its message still misses, and the header of the next message may
follow. The agent logs once per connection which framing it found.

Any inconsistency stops the file handle for the rest of the connection: a
first block that is neither data nor a header, flags other than first,
last, show protocol and shadow persistent (suspend, resume or compression),
a length that changes inside a message, a first chunk inside a message, a
chunk without the first flag outside a message, a last chunk too short, a
chunk without the last flag that completes its message, or a block that
ends inside a header; a failure of `WTSVirtualChannelQuery`,
`DuplicateHandle` or `ReadFile` too. The agent then logs a warning with the
reason, drops the block that did not fit, and reads with
`WTSVirtualChannelRead` until the channel closes; the next connection tries
the file handle again. A line cut by the dropped block fails the transfer in
progress, which the user starts again. The rules are unit tested; this
development computer has no Remote Desktop session, so the file handle
itself was not exercised.

### Logs

Client, at the info level, at the start and at the end of each transfer (the
end covers a result of the agent, a failure, a cancel and a disconnection):

```
Transfer <id> of drag <id>: started with 1 item(s), chunks of 32768 bytes, window of 4194304 bytes (setting 4194304).
Transfer <id> channel calls: 2410 calls, 482000000 characters, 9100 ms inside SendOnVirtualChannel, 3.78 ms per call on average, 41.20 ms at most; 5 % of the 190000 ms from the first call to the last, 5 % of the 193000 ms of the transfer.
Transfer <id> acknowledgements: 237 matched a call, round trip 1480 ms on average, 20 ms at least, 1900 ms at most; 3700000 bytes in flight on average when one came, 4194304 at most, 88 % of the window of 4194304 bytes.
```

At the debug level the line of each call of the control gives the time
inside it ("3.12 ms in the call"), and each acknowledgement gets a line with
its round trip and the bytes in flight.

Agent, at the info level, once per connection and at the end of each
transfer:

```
Session connection: client address 192.0.2.10, connection RDP-Tcp#3, session flags 1; counters from WTSSessionInfoEx: 0 bytes and 0 frames in, 0 compressed bytes in, 0 bytes out.
Transfer <id> channel reads: 302000 reads with data and 12 empty, 482000000 bytes, 1596 bytes per read on average; 301000 of 1600 bytes, 1000 shorter, 0 longer; blocked in the read 178000 ms, processing 14000 ms (9000 ms handling messages), over 193000 ms; read mode wts.
Transfer <id> session counters: 0 bytes, 0 frames and 0 compressed bytes in, 0 bytes out, from WTSSessionInfoEx (zero when Windows does not count them).
```

The numbers above only show the format. How to read them:

* The client spends a large share of the time from the first call to the
  last inside `SendOnVirtualChannel` (well above half): the UI thread is
  busy in the control, and the control limits the transfer. A larger
  `transferBatchCharacters` or chunk only helps if the time per call does
  not grow with the size.
* The agent spends nearly all of the transfer processing (processing close
  to the elapsed time, blocked time small): the agent limits the transfer, and the
  handling time says whether JSON, base64 and the file write are the cost.
* The client calls take a small share, the agent is blocked in the read for
  nearly all of the transfer, and the reads are full chunks of 1600 bytes: neither end
  is busy, and the data waits between the control and the agent. When the
  bytes in flight stay near the window and the round trip is close to the
  window divided by the rate, the pipe is full: run a transfer with
  `transferWindowBytes` at 16777216. If the rate stays and the round trip
  grows four times, the window is not the limit and the flow control of the
  Remote Desktop connection is; only another transport helps. If the rate
  grows, the latency was the limit and a larger window is the remedy.
* The bytes in flight stay well below the window: the client does not fill
  it, and the sending thread (file read, base64) or the UI thread is the
  limit, which the share of the calls then tells apart.
* In `file` mode, reads counted as longer than 1600 bytes, or several
  channel headers per read, mean that one read returned several chunks.

### The agent without a client

The agent runs in every session of a server where most users connect with
mstsc, so nothing of this version may change its behaviour in a session
without an UltimateRDP client:

* the waiting loop of the channel thread (open, wait, retry, the hourly
  line) has no changed line;
* the `channelRead` setting is read once at start with the others, and its
  value joins the start line, which stays one line; `wts` is the default,
  and the file handle is only opened when the setting is `file` and a
  channel is open;
* the read counters, the measure of a transfer and the queries of
  `WTSSessionInfoEx` only exist from the start of a transfer to its end; the
  line about the connection is written when a client first speaks on the
  channel;
* no thread, timer or registry read was added.

Measured on this development computer on 2026-11-09, at idle in a session
without the channel, sixty seconds per run, started with PowerShell
`Start-Process` without a client, one build after the other: the private
working set and the private bytes at the end, the threads at the end, the
processor time after the first five seconds (counted in steps of 15.6 ms),
and the new lines of the log. "Published" is the self contained single file
executable that the release workflow builds; "bin" is the Release build of
`src/UltimateRDP.Agent`.

| Build | Runs | Private working set | Private bytes | Threads | Processor time after 5 s | Log lines |
| --- | --- | --- | --- | --- | --- | --- |
| 0.3.0, above | | about 4 MB | | 5 | not measurable | 1 |
| 0.4.3, published | 2 | 4.7 MB each | 7.7 to 7.8 MB | 5 each | 16 ms once, 0 once | 1 each |
| 0.4.4, published | 2 | 4.7 to 4.8 MB | 7.8 MB | 5 each | 0 each | 1 each |
| 0.4.3, bin | 3 | 4.0 MB each | 7.2 MB | 5 each | 31 ms once, 0 twice | 1 each |
| 0.4.4, bin | 4 | 4.0 MB three times, 1.9 MB once | 7.2 to 7.3 MB | 5 three times, 6 once | 0 each | 1 each |

The single file executable takes about 0.7 MB more private working set than
the bin build, in 0.4.3 as in 0.4.4. The thread counts sampled every five
seconds give the same series for both versions. The 1.9 MB and the sixth thread came
once each and did not come back in the next run, as the 31 ms of processor
time of one run of 0.4.3 did not.

### Versions

There is no protocol change. The client summaries need a 0.4.4 client, the
agent summaries a 0.4.4 agent; both sides at 0.4.4 give the full picture.
A 0.4.4 client works with a 0.4.1 to 0.4.3 agent and the other way round.
## Display scaling of the client

The client is per monitor DPI aware (PerMonitorV2). The main window, the drop
window and the drop banner use `AutoScaleMode.Dpi` with sizes given at 96 DPI,
so Windows Forms scales them to the monitor they open on and again at each
`DpiChanged` when they go to a monitor with another DPI. The menus and the
status bar take the font of the form through the runtime option
`System.Windows.Forms.ApplyParentFontToMenus`: their own default font kept the
size of the DPI the window opened on. The main window opens on the monitor
under the mouse, at four fifths of its working area. The drop window sets its
client area from its 96 DPI size after each change of DPI, and stays on a
monitor it was taken to. A change of DPI does not reconnect a session: the
next connection takes the scale factor that Auto gives on the new monitor, and
the status bar says so when the session uses Auto. Drop points stay in
physical pixels on every monitor.


## Version 0.4.5

Seen in production with 0.4.4 on 2026-11-09, in the log of the client: the
drag entered the session, the user released the button 282 ms later after 4
moves, and the `dragAck` that offered a transfer arrived 53 ms after the
release. The client had sent a normal `drop` at once, as step 3 of the flow
of version 0.4 then said, so the remote Explorer copied the file of 700 MB
through `\\tsclient` at 3.7 MB/s with its own copy dialog, while the progress
window said "Dropping into the session...". The fast transfer never ran, and
the client logged no transfer summary. A quick release must not lose the
transfer.

Decision: the client holds a drop that happens while the `dragAck` of its
drag is pending. Only the client changes; the protocol and the agent do not.

### The held drop

`DropDecision` in Core takes the decision and `SessionView` applies it:

* On the local drop, when the `dragAck` of the drag has not arrived, the
  client keeps the point, the keys and the items, and sends nothing yet. The
  progress window opens in its preparing stage ("Preparing...", a bar without
  a value), and the status bar says "Dragging 1 item(s) into the
  session...".
* The wait lasts at most 1.5 seconds (`DropDecision.MaxWait`). The timer of
  one second from `dragEnter`, which refuses a drag that gets no answer, is
  stopped at the drop: the wait takes over. The agent sends a provisional
  accepted `dragAck` after 700 ms when the drag is slow to start, so a
  running agent answers within the wait.
* The `dragAck` arrives accepted with `transfer: true` and a `transferId`:
  the client goes on exactly as if the drop had happened after it, with
  `drop` and `deferred: true`, then the transfer; the same window moves on to
  the transfer.
* The `dragAck` arrives accepted without a transfer: the client sends the
  normal `drop`, and the window shows "Dropping into the session...".
* The `dragAck` arrives refused: no drop goes out. The client sends
  `dragLeave`, as for a drop after a refusal, and shows the reason of the
  agent in the status bar and in red in the window.
* No `dragAck` within the wait: the client sends the normal `drop`, as 0.4.4
  did at once, and starts a new discovery of the agent with a hello, as after
  a missing `dragAck` during a drag.
* The first answer decides. A later `dragAck` of the same drag is handled as
  for any drop that went out.

During the wait:

* The moves go on. A move to the drop point leaves within a thirtieth of a
  second, then one move every second while nothing else is sent, so the
  watchdog of the agent, which cancels a live drag after five seconds without
  a message, does not end the live drag while the client waits.
* A new drag over the session is refused with the text used during a
  transfer, "A transfer is in progress. Wait for its end, or cancel it from
  the Session menu.", and the window says "Wait for this transfer before
  dropping more files." for five seconds. No text was added.
* The Cancel button of the window and Session, Cancel the transfer give the
  drop up: the client sends `dragLeave` and shows "Drop cancelled.". Closing
  the tab does the same. A disconnection gives the drop up without a message
  to the agent and shows "The drop stopped: the session was disconnected."

### The agent

The agent does not change. A deferred `drop` that arrives right after the
`dragAck` takes the path it always took: the transfer was registered when
the agent decided it, before any `dragAck` offered it, so transfer messages
that come before the drop wait in the queue of `TransferRegistry`. When a
provisional `dragAck` offered the transfer before the live drag existed, the
preparation sees the deferred drop and cancels the live drag as soon as it
creates it.

When the live drag ended before the deferred drop arrived (the watchdog,
Escape in the session, a failure of the drag), the agent forgets the drag at
that moment: it withdraws the transfer, sends an `error` `transferResult`
with the reason the drag ended, and a refused `dragAck`. The deferred drop
that follows gets an `error` `dropResult` with the same reason, and the
transfer messages already sent are ignored as late. The client shows the
reason in the status bar and in red in the window. Accepting the transfer in
that case was considered and not done: it changes the state machine of the
drop on the agent for a case that the moves of the wait make rare.

### Logs

The client logs the drop when it is held ("Drop after 4 move(s), held until
the agent answers the drag", with the usual details), then one info line
when the wait ends, with its outcome:

```
Drop <id> held 53 ms for the agent answer: the agent offered transfer <id>, so the drop goes out deferred and the files follow through the channel.
```

The other outcomes read "the agent accepted the drag without a transfer, so
the drop goes out and the files are read through drive redirection", "the
agent refused the drag, so the drop is not sent", "no dragAck within 1500 ms,
so the drop goes out without a transfer and the files are read through drive
redirection", and "the drop was given up before the agent answered", followed
by the cause.

### Limits

* A provisional accepted `dragAck` decides like a final one. When the agent
  sends it before it decided the transfer, because the check of the items
  through drive redirection took more than 700 ms, a drop held at that
  moment, or a drop after it, goes through drive redirection as in 0.4.4;
  the later `dragAck` that offers the transfer is then ignored. Telling a
  provisional answer from a final one needs a field in `dragAck`. Version
  0.5.0 adds that field, `provisional`, see "Version 0.5".
* The client gives the copy effect back to the local source as soon as the
  drop is held, even when the agent refuses the drag afterwards. A source
  does nothing with a copy effect.
* With an agent that does not answer, the drop goes out 1.5 seconds later
  than with 0.4.4.

### Versions

Only the client changes. The agent can stay at 0.4.4; its 0.4.5 build is
the same with another version number. A 0.4.5 client works with agents
0.4.1 to 0.4.4.

No 0.4.5 release was published: the held drop ships with 0.5.0, together
with the provisional answer that closes the first limit above.


## Version 0.5

Decided on 2026-11-09 from the logs of both sides of a transfer in
production with 0.4.4. Version 0.5.0 multiplies the file rate by about 2.5
on the same channel, without another transport. In this section MB means a
million bytes.

### What was measured

* Client: 946 calls of `SendOnVirtualChannel`, 0.32 ms each on average, 1
  percent of the time of the transfer. The client is not the limit.
* Agent: blocked in `WTSVirtualChannelRead` 55.3 s of 56.9 s, processing
  1.3 s. The agent is not the limit.
* The reads return 1597 bytes on average, the chunks of 1600 bytes of the
  static channel, and the acknowledgements come 1.9 s behind with the
  window full: the flow control of the Remote Desktop connection keeps the
  pipe full.
* The agent read 330 514 754 bytes for 165 257 360 characters sent: from the
  client to the agent, the control sends each character as its two UTF-16
  bytes. From the agent to the client, the client sees one byte per
  character. The two directions differ, and the detection on each side
  already handled both.
* The channel carried about 5.8 MB/s on that link, and the file data went at
  2.1 MB/s: base64 costs 4/3 and UTF-16 costs 2, so every byte of a file took
  8/3 bytes on the wire.

The limit is the encoding: the wire is full, mostly of padding.

### The dense encoding

The agent tells the client how the data of the client reaches it. Its hello
gets the field `clientToAgentModel`, `utf16` or `oneBytePerChar`, the model
its decoder detected on the first bytes of the connection. The hello of the
client is the first line of a connection, so the model is known when the
agent answers it, and computing the field costs nothing more. The client
keeps it in `AgentLink` and logs it with "Agent ready".

The client chooses the encoding of each transfer from that model
(`ChunkEncodings.Choose` in Core) and announces it in `transferBegin`:

```
{"type":"transferBegin","transferId":"<id>","files":1,"bytes":700000000,"chunkEncoding":"u15"}
```

* `base64`: the chunks of 0.4. Used when the data of the client reaches the
  agent one byte per character, or when the agent does not say.
* `u15`: used when the data of the client reaches the agent in the UTF-16
  model. Each UTF-16 code unit of the chunk carries 15 bits of the file: the
  value v, from 0 to 32767, is the unit `0x1000 + v`. The bits are taken from
  the bytes in order, the high bit of each byte first, and the last unit is
  padded with zero bits (`U15Codec` in Core).

The units U+1000 to U+8FFF hold no surrogate, no control character, no
space, no line break, no quote and no backslash. JSON takes them as they
are, the line reader, which splits on U+000A and trims spaces and U+000D at
the end of a line, never touches them, and they never sit at either end of a
line, where the agent trims NUL characters and spaces. The range holds
Unicode separators such as U+2028; nothing on the path splits or trims on
them. A .NET string of such units is a valid BSTR, and in the UTF-16 model the
control sends the units as they are, two bytes each, as the byte count
measured on the agent shows.

A `u15` chunk line stays one JSON object on one line, built by
`TransferLines` without the serializer, with the ASCII fields first and the
data last:

```
{"type":"fileChunk","transferId":"<id>","fileIndex":0,"offset":262144,"length":131072,"enc":"u15","data":"<units>"}
```

`length` is the number of bytes, which ends the data: the units alone do not
tell it, since 100 and 101 bytes both take 54 units. A wrong length is caught
by the size and the checksum of the file. base64 chunks keep the line of 0.4,
without `length` and `enc`.

On the agent, the channel loop now hands each line to the handler as its
string. `TransferLines.TryParseChunk` reads a `u15` chunk line by hand: it
checks the known prefix and the order of the fields, reads the numbers, and
keeps the units as they are; the receiver decodes them into its buffer. Any
other line, base64 chunks included, takes the path of 0.4: its ASCII bytes go
to the JSON reader. A `u15` line must never go through that conversion,
which turns every unit into a question mark; a line whose fields are not in
the expected order, or whose data holds JSON escapes, goes to the JSON reader
and still reads correctly. `IncomingTextDecoder` turns the byte pairs of the
UTF-16 model back into the same units without any check, and the line reader
works on characters, so nothing changed there.

The receiver checks that each chunk is written in the encoding that
`transferBegin` announced, that `length` is 1 to 256 KiB, that the unit count
matches it, that every unit is in the range and that the padding bits are
zero. Any failure ends the transfer with the reason `BadData`, as a bad
base64 chunk did. An unknown `chunkEncoding` fails at `transferBegin`.

Nothing else changes: the chunk size setting stays in bytes, the SHA-256,
the acknowledgements, the window and the progress are the same, and the
client still groups its lines into calls of at most 200 000 characters. A
chunk of 128 KiB takes 69 906 units, about 70 000 characters with its fields,
so two chunks travel in one call; a chunk of 32 KiB takes 17 477 units.

### Wire budget

Bytes on the wire per byte of file data, without the fields of each line
(`ChunkEncodings.WireBytesPerFileByte` in Core), and the file rate they
allow on a wire of 5.8 MB/s:

| Encoding | Data of the client reaches the agent | Wire bytes per file byte | File rate at 5.8 MB/s |
| --- | --- | --- | --- |
| base64 | one byte per character | 4/3, 1.333 | about 4.3 MB/s |
| base64 | UTF-16 | 8/3, 2.667 | about 2.2 MB/s, 2.1 measured |
| u15 | UTF-16 | 16/15, 1.067 | about 5.4 MB/s |

The fields of a line add about 140 characters per chunk: 0.8 percent with
chunks of 32 KiB, 0.2 percent with chunks of 128 KiB. Over 100 MB in the
tests, all lines counted, the wire carried 1.073 bytes per file byte with
chunks of 32 KiB and 1.068 with chunks of 128 KiB. In the UTF-16 model `u15`
carries 2.5 times the file data of base64 on the same wire.

A mode for the one byte per character model, with 8 bits per character, was
considered and not done: how the control narrows characters above 0xFF in
that model is not known, and the measured link uses the UTF-16 model.

Measured without a server, in the tests, on this development computer: 100
MB in `u15` go through the whole codec path (chunk lines, channel bytes in
blocks of 1600, model detection, line reader, hand parser, decoding,
checksums on both sides, batches of 200 000 characters) in 0.90 to 0.93 s,
with chunks of 32 KiB as with chunks of 128 KiB, about 110 MB/s, over two runs.

### The provisional answer

0.4.5 left one gap: a provisional accepted `dragAck`, sent when the check of
the items takes more than 700 ms, decided a held drop like a final one, so
the transfer offered by the later `dragAck` was lost. The agent now writes
`provisional: true` on an accepted `dragAck` sent before it decided whether
the files are transferred, and then always sends the final `dragAck` once
the drag starts in the session: with the transfer when one was decided,
without it otherwise. A provisional answer sent after the decision is not
marked, since it already says everything.

On the client (`DropDecision` in Core):

* A provisional answer does not decide. A drop held at that moment keeps
  waiting for the final answer, within the same 1.5 seconds from the drop.
* A drop that happens after a provisional answer, before the final one, is
  held as a drop before any answer is: `DropDecision.MustHold`.
* The final answer decides as in 0.4.5: transfer, plain drop or refusal.
* Without a final answer within the wait, the plain drop goes out, and the
  log says "only a provisional dragAck within 1500 ms". The agent answered,
  so the client does not look for it again.

A 0.4.5 client ignores the field and handles the final `dragAck` like any
later accept.

### Logs

Client, at the info level:

```
Agent ready, version 0.5.0, drive redirection available: True, data of the client reaches the agent in the Utf16 model, so transfer chunks go in u15.
Transfer <id> of drag <id>: started with 1 item(s), chunks of 131072 bytes, window of 4194304 bytes (setting 4194304), chunks in u15, data of the client reaches the agent in the Utf16 model, 1.067 wire bytes per file byte.
Transfer <id>: first chunk line sent, 70038 characters for chunks of 131072 bytes in u15.
```

Agent, at the info level:

```
Hello from client 0.5.0, language fr (texts in fr); drive redirection available: True; data of the client in the Utf16 model.
Transfer <id> of drag <id>: receiving 1 file(s), 700000000 bytes, chunks in u15.
Transfer <id>: first chunk of file 0 received, 131072 bytes in 69906 characters of u15.
Drag <id>: provisional dragAck before the transfer decision, Starting the drag...
```

The summaries of 0.4.4 stay as they were. With a wire of about 5.8 MB/s, the
summary of the transfer should show about 5.4 MB/s, the characters handed to
the control about 2.5 times fewer for the same file, and the agent still
blocked in its reads for nearly all of the time.

### Versions

Both sides ship together as 0.5.0. An agent before 0.5.0 does not send
`clientToAgentModel`, so a 0.5.0 client sends it base64, as before; a client
before 0.5.0 sends no `chunkEncoding`, which a 0.5.0 agent reads as base64.
The dense encoding needs both sides at 0.5.0.

The agent without a client does not change: the channel thread hands each
line as a string instead of its ASCII bytes, the hello reply reads a model
the channel loop already keeps, and nothing new runs while no client speaks
on the channel.


## Version 0.5.1

From 0.4.0 to 0.5.0, transfers failed at their start because a short call of
`SendOnVirtualChannel` never reached the agent. In every case the logs of both
sides show the same thing: a call of 100 to 400 characters, followed within
about 10 ms by a call of 140 000 to 350 000 characters, is lost in its
entirety between the Remote Desktop control and `WTSVirtualChannelRead` of the
agent. The call before it and the calls after it arrive intact and in order,
the control returns without an error, and nothing is logged anywhere. The
four incidents, documented with the logs of both sides:

| Version | Byte model of the data of the client | Call lost | Next call |
| --- | --- | --- | --- |
| 0.4.0, 2026-11-09 13:50 | UTF-16 | `transferBegin`, 101 characters, 9 ms after `drop` | `fileBegin`, then a chunk line of 350 000 characters |
| 0.4.0, 2026-11-09 13:50:42 to 13:51:43 | UTF-16 | large calls lost entirely: every chunk call, one line of about 350 000 characters (256 KiB of base64, about 700 KB on the wire) | none reached the agent after `transferBegin`; 4 MiB in flight, no acknowledgement for 60 s, then the client cancelled |
| 0.4.1 (client 0.4.2), 2026-11-09 15:47 | one byte per character | `fileBegin`, about 200 characters | a chunk line of 174 868 characters, 8 ms later |
| 0.5.0, 2026-11-10 11:03:46 | UTF-16 | `transferBegin` and `fileBegin` in one call, 314 characters | two `u15` chunks, 140 081 characters, 8 ms later |

The second incident has another profile: calls above roughly 200 000
characters in the UTF-16 model were not delivered at all. Its standing
mitigation is the limit of 200 000 characters per call since 0.4.3, with
calls of 174 868 characters seen to work; 0.5.1 keeps it. The three other
incidents are the pattern of the short call followed by a large one, which
the numbered lines below now recover. In every incident the loss is a whole
call, never a part of a line: the line reader of the agent never saw a cut
or damaged line.

Keeping the strings alive (0.4.3) did not prevent it, and neither did the
grouping of lines into calls: in the last incident the queue was sent before
the first chunk was queued. The cause is inside the control and cannot be
fixed from the client. Version 0.5.1 therefore treats the channel from the
client to the agent as lossy at the granularity of a whole call, and makes
the transfer protocol recover by itself. It also shapes the calls so that the
case becomes rare.

### Numbered lines

Every line of a transfer, `transferBegin`, `fileBegin`, `fileChunk` and
`fileEnd`, carries `"seq": N`, its number in the transfer: 0 for
`transferBegin`, then one more for each line, in the order of sending
(`TransferSender` with `sequenced`). `transferCancel` carries none and is
handled at once.

```
{"type":"transferBegin","transferId":"<id>","files":1,"bytes":700000000,"chunkEncoding":"u15","seq":0}
{"type":"fileBegin","transferId":"<id>","fileIndex":0,"relativePath":"a.bin","size":700000000,"modified":"2026-11-10T10:00:00Z","seq":1}
{"type":"fileChunk","transferId":"<id>","fileIndex":0,"offset":0,"length":131072,"enc":"u15","seq":2,"data":"<units>"}
{"type":"fileChunk","transferId":"<id>","fileIndex":0,"offset":0,"seq":2,"data":"<base64>"}
{"type":"fileEnd","transferId":"<id>","fileIndex":0,"sha256":"<hex>","seq":5343}
```

In the chunk lines built by `TransferLines`, `seq` is an ASCII field just
before `data`, after `enc` in `u15`, where the serializer writes it too. The
hand parser of the agent reads it when it is there, and still reads a line
without it.

### The agent puts the lines back in order

`ReliableInbox` in Core, one per transfer in `StagedTransfer`, hands the
numbered lines to the receiver in the order of their numbers:

* A line with the expected number is handled, with the lines that waited
  behind it.
* A line with a higher number waits, and the agent sends at once
  `{"type":"transferResend","transferId":"<id>","from":E,"to":S-1}`, from the
  expected line to the line before the first one that waits.
* While those lines are missing, the request goes again once nothing arrived
  for 500 ms since the last line and the last request. Counting from the last
  line, and not only from the last request, matters: the lines that the
  client sent before the resend are still on the way, behind up to a full
  window, and the resend comes after them. A timer of the transfer checks
  every 100 ms, and only while lines are missing.
* After ten requests for the same lines without progress, the transfer fails
  with "Lines of the transfer were lost on the way and could not be
  recovered. Try again.", in the language of the client; the log of the agent
  says which lines. Progress, even partial, starts the count again. A missing
  range that no request named yet, the second of two separate losses, is
  asked for as soon as the first is filled.
* A line whose number is below the expected one, or that already waits, is a
  duplicate from a resend and is ignored, with a line at the debug level.
* At most the bound of the queue of an offered transfer waits, twice the
  largest window of the client in base64 characters. Beyond it the transfer
  fails with the same text. A client that keeps to its window never reaches
  it: nothing behind a missing line is acknowledged, so the client stops at
  its window.
* `fileAck` gains `"seq": N`: every line up to N was processed. The client
  frees the lines it kept up to N.

Lines without `seq`, from a client before 0.5.1, are handled as they arrive,
as before, including the `MessageLost` failure of 0.4.3.

### The client sends the lines again

`ReliableOutbox` in Core keeps every numbered line of the transfer as its
string until an acknowledgement covers it, so that a line can go again long
after the call of the control returned. The strings handed to the control
are still kept as in 0.4.3; a call that holds only chunks sent again is kept
for 60 seconds and is not freed by the acknowledgement of its own chunks,
since the first copy may be the one that arrived.

* On `transferResend` the client queues the lines it still keeps from `from`
  to `to`, in order and together, through the usual send queue, and logs
  "Transfer <id>: resending lines 1 to 2 asked by the agent, 2 line(s) of 314
  characters, the first one transferBegin." at the info level. Lines already
  acknowledged are left out.
* When the last call of what was sent is lost, no later line shows the agent
  a gap: at the end of the transfer, or when the lost call held the chunk
  that would have brought the next acknowledgement and the window is then
  full. So a client that waits for an acknowledgement and got none, and sent
  no chunk, for 2 seconds sends its newest line again (`TransferAckWatch`),
  and again every 2 seconds while it waits. The agent then sees the gap and
  asks for it, or handles the line if it was the only one missing. When the
  agent already processed that line, the acknowledgement was the part that
  went missing, and the agent answers it with a fresh `fileAck`.
* The first acknowledgement timeout of 10 seconds and the rule of 60 seconds
  without any acknowledgement stay as a last resort.

The summary of the transfer in the log of the client ends with "2 line(s)
sent again for 1 request(s) of the agent, the newest line sent again 0
time(s) after a silence". The summary of the agent ends with "1 resend
request(s), 30 line(s) held until the missing ones came, 0 line(s) received
twice" when it asked for anything.

### Shaping the calls

The loss needs a short call followed closely by a large one, so the client
avoids that pattern:

* `TransferUpload` holds `transferBegin`, `fileBegin` and `fileEnd` and hands
  them to the queue together with the next chunk, in the same message of the
  UI thread, so that they leave in the same call as that chunk. It hands
  them over on their own before it waits for room in the window, since the
  agent may need a `fileEnd` to acknowledge, at the end of the transfer, and
  when 100 000 characters of them or more wait, for a folder of many empty
  files.
* When the queue is about to send less than 1000 characters and the transfer
  thread is about to hand over a chunk, from its start until its first lines
  are queued and from the moment the window has room for the next chunk until
  it is queued, the send waits for that chunk, at most 20 ms (a timer of the
  UI thread, so about 20 to 30 ms), and the short lines leave in the same
  call. The `drop` line that starts a transfer waits this way for the first
  chunk.
* `SendQueue` never ends a batch with short lines when the next line does not
  fit beside them: when the lines taken hold less than 1000 characters, or a
  tenth of the limit when that is lower, the next line joins them, and the
  call may go past the limit of 200 000 characters by less than 1000
  characters. With chunks of 128 KiB or less a chunk line and its short lines
  always fit within the limit; the exception only applies to larger chunk
  lines, which went alone and above the limit already.

The limit of 200 000 characters per call stays.

### Logs

Client, at the info level:

```
Agent ready, version 0.5.1, ..., so transfer chunks go in u15; transfer lines numbered, the agent asks for the missing ones.
Transfer <id> of drag <id>: started with 1 item(s), ..., numbered lines.
Transfer <id>: first chunk line sent, 70046 characters for chunks of 131072 bytes in u15, with 2 short line(s) in front of it.
Transfer <id>: resending lines 0 to 1 asked by the agent, 2 line(s) of 314 characters, the first one transferBegin.
Transfer <id>: no acknowledgement for 2 s after the last file; sending line 5343 (fileEnd) again, so that the agent sees what it misses.
```

Agent, at the info level:

```
Transfer <id> of drag <id>: receiving 1 file(s), 700000000 bytes, chunks in u15, numbered lines.
Transfer <id>: line 2 came while line 0 was expected; asking the client for lines 0 to 1 (request 1).
Transfer <id>: lines 0 to 1 still missing; asking the client again (request 2).
Transfer <id>: the missing lines from 0 arrived; 30 line(s) that waited behind them follow.
Transfer <id>: line 5343, the last one processed, came again; acknowledging it again.
```

and, when the lines never came, a warning "Transfer <id>: lines 0 to 1 of the
transfer never arrived after 10 requests to the client." At the debug level
the client logs each line with its number, "Sent again" for a resend, and
the waits of short lines for a chunk.

What to look for after a transfer: "A message of the transfer was lost on the
way" should no longer appear with both sides at 0.5.1. A request of the agent
and the matching resend of the client show that a call was lost and
recovered, and the counts at the end of both summaries say how often.

### Measured in the tests

`LostCallTests` runs whole transfers through the codec path of the tests,
numbered lines, batches of the send queue, bytes of the channel in blocks of
1600, model detection, line reader, hand parser, the inbox and the outbox,
in base64 in both byte models and in `u15` in the UTF-16 model. Time only
passes in the test when the client has nothing to do, in steps of 100 ms.

* The call of `transferBegin` and `fileBegin` sent alone and lost, as in the
  incident of 0.5.0: one request for lines 0 to 1, two lines sent again, the
  files arrive intact.
* A call of chunks lost: one request, the lines of that call sent again.
* The last call, with the last chunks and the last `fileEnd`, lost: the
  client sends its newest line again after 2 simulated seconds, the agent
  asks for the rest. The last `fileEnd` lost alone: the line sent again is
  that line.
* Two chunk calls lost just before the window fills: the newest line sent
  again once, then one request.
* A resend lost once: two requests 500 ms apart, then success.
* Calls delivered twice: the copies are ignored, nothing is asked.
* Every resend lost: ten requests, then the error, and nothing written stays.
* Lines without numbers: the old failure, "transferBegin never arrived".
* 100 MB with two lost calls: 0.7 to 1.0 s once the code is warm, 2.7 s for
  the first run of the test process, on this development computer.

### Versions

Both sides ship together as 0.5.1. The agent says `reliableTransfer: true` in
its hello, and only then does the client number its lines. This matters: the
hand parser of an agent 0.5.0 expects `data` right after `enc` in a `u15`
chunk line, so it would not read a numbered line, and the line would then go
to the JSON reader as ASCII and fail the transfer as bad data. A 0.5.0 agent
reads the numbered lines of base64 and the other numbered lines, since its
JSON reader skips unknown fields, but it never receives any, since it does
not say `reliableTransfer`. With an agent 0.5.0, a client 0.5.1 sends the
lines of 0.5.0, with the shaping of the calls. A client 0.5.0 ignores the
new field of the hello and sends lines without numbers, which an agent 0.5.1
handles as before; it never receives `transferResend`, which it would log as
an invalid line and ignore. The recovery needs both sides at 0.5.1.

The agent without a client does not change: its hello has one more field,
and the inbox and its timer only exist during a transfer, the timer only
while lines are missing.


## Version 0.5.2

### The incident

On 2026-11-10 at 14:05:59, with 0.5.1 on both sides, the first call of a
transfer carried `transferBegin`, `fileBegin` and the first `u15` chunk, as
the shaping of 0.5.1 wants it: "first chunk line sent, 70046 characters for
chunks of 131072 bytes in u15, with 2 short line(s) in front of it". The
agent received the first two lines, "receiving 1 file(s), 790626304 bytes,
chunks in u15, numbered lines", and failed at once on the third: "A chunk at
byte 0 is not valid u15 of 1 to 262144 bytes". The transfer ended with "Data
was lost or damaged on the way. Try again.", and nothing was asked again,
since the receiver of 0.5.1 ended the transfer on any invalid chunk. The
same drop six seconds later decoded its first chunk and ran at 2.5 MB/s, and
a whole transfer of 790 MB in `u15` had succeeded at 11:03:58 the same day.

So `u15` units normally travel intact, and this was a damage inside one
call: the ASCII fields of the line read well, its data did not. A block of
the channel missing or changed inside the data of the line explains it. It
is the first damage seen in the logs; every earlier incident was the loss of
a whole call.

### Damaged equals lost

A numbered chunk line that arrives damaged is handled as a lost line. The
receiver (`TransferReceiver.Handle(message, out damage)` in Core) finds a
chunk damaged when, with the expected number or a higher one:

* it does not start where the file stands (its `offset`),
* it names another encoding than `transferBegin`, or one this agent does not
  know,
* its data is not valid in its encoding: a `u15` unit outside 0x1000 to
  0x8FFF, a unit count that does not match `length`, padding bits that are
  not zero, a character that base64 does not use or a base64 length that is
  not a multiple of four,
* or its data does not hold the number of bytes its `length` says, 1 to 256
  KiB.

Such a chunk is not consumed: nothing is written, hashed or counted.
`ReliableInbox` then treats its number as missing: the lines after it wait as
behind a gap, and the line is asked for with the rules of 0.5.1, at once the
first time, again after 500 ms without any line or request, at most ten
times without progress. A copy that arrives damaged again counts as no
progress. After the tenth request the transfer ends with the text of 0.5.1,
"Lines of the transfer were lost on the way and could not be recovered. Try
again.", and the log of the agent says "lines 82 to 82 of the transfer never
arrived intact after 10 requests to the client; line 82 arrived damaged."
A line that waited behind a gap and turns out damaged when its turn comes is
dropped and asked for the same way.

The request says `"damaged": true` when its first line arrived damaged:

```
{"type":"transferResend","transferId":"<id>","from":2,"to":2,"damaged":true}
```

The field only feeds the log and the counts of the client; the lines go
again as for any request. The client keeps each line as the string it built,
and `ReliableOutbox.Take` hands that very string again, so a chunk sent again
is the same line, character for character, as the first time.

Other problems of a numbered chunk still end the transfer as before: a chunk
of another file than the open one, a file longer than announced. A wrong
checksum at `fileEnd` too: data changed inside the alphabet of the encoding,
with the right count, cannot be seen in the chunk, and the SHA-256 of the
file catches it.

A line whose number cannot be read is not a chunk to ask for. A line that is
not JSON any more is a protocol error, logged and ignored, as before. A
transfer line that reads without its `seq`, among numbered lines, lost it on
the way: the agent ignores it with an info line, and the next line shows the
gap, which the agent asks for. Before any numbered line arrived, lines
without a number are handled as they arrive, as with a client before 0.5.1.

### The length of base64 lines

A numbered base64 chunk line now carries `length` as a `u15` line does,
before `seq`:

```
{"type":"fileChunk","transferId":"<id>","fileIndex":0,"offset":0,"length":131072,"seq":2,"data":"<base64>"}
```

A block of the channel holds 1600 bytes: 800 characters in the UTF-16 model,
1600 in the one byte per character model, both multiples of four. Base64
that lost such a block is still valid base64, only shorter, and without the
field the agent would write it and then fail on the next chunk, at the wrong
offset, ten times. With it, the short chunk is damaged and asked for. The
field costs 15 characters per line with chunks of 32 KiB, 16 with chunks of
128 KiB. A base64 line without `seq` keeps the form of 0.4, and an agent
0.5.1 reads the field and does not use it.

### Logs

Agent, at the info level, for the incident as the tests replay it, a block
of 800 units lost in the first chunk of 128 KiB:

```
Transfer <id>: line 2 arrived damaged: A chunk at byte 0 is not valid u15 of 1 to 262144 bytes. Chunk of file 0 at byte 0 in u15: 69106 unit(s) of data where 69906 were expected for 131072 bytes, every unit in the range; line of 69245 characters where 70045 were expected for that chunk size.
Transfer <id>: line 2 arrived damaged; asking the client for lines 2 to 2 (request 1).
Transfer <id>: lines 2 to 2 still missing, line 2 arrived damaged; asking the client again (request 2).
Transfer <id>: a fileChunk line without a number among numbered lines, damaged on the way; ignored, line 82 is expected.
```

The first line is the diagnostic, written once per damaged copy, without
any of the data:

* the number of the line, then what the receiver found wrong, in the words
  of the failure of 0.5.1;
* the file, the offset and the encoding the line names;
* the characters of data in the line, and those expected: for the `length`
  of the line, or in base64 without `length`, for the chunk size seen
  before it, at most the rest of the file; "the expected count is not known"
  before any chunk;
* in `u15` the position, from 0 in the data, of the first unit outside the
  range and its value in hex, as "first unit outside the range at 100,
  0x0041", or "every unit in the range"; in base64 the first character that
  base64 does not use, or a padding character before the last two, as
  "first invalid character at 50, 0x002A", or "no invalid character";
* the length of the line, without its line break, against the length of the
  line the client builds for the expected data. The client logs its first
  chunk line with the line break, one character more.

What the damage looks like then reads from the counts: a lost block shows
fewer units by a multiple of 800 or 1600 and every unit in the range; a
changed block shows the right count and a unit outside the range.

Client, at the info level:

```
Transfer <id>: resending lines 2 to 2 asked by the agent, line 2 arrived damaged, 1 line(s) of 70046 characters, the first one fileChunk.
```

The summaries count the damaged lines apart. Client: "1 line(s) sent again
for 1 request(s) of the agent (1 for damaged lines), the newest line sent
again 0 time(s) after a silence". Agent: "1 resend request(s) (1 for damaged
lines), 1 line(s) held until the missing ones came, 0 line(s) received
twice, 1 line(s) received damaged".

### Measured in the tests

`DamagedChunkTests` runs whole transfers through the codec path of the tests
of 0.5.1, with a channel that changes chosen lines:

* Line 82 of a file of 5 MB, in the middle, damaged once: one unit replaced
  by 0x0041, one unit removed, one unit added, `length` 100 bytes too large
  or `offset` one byte too far in `u15`; a star in the data, one character
  removed or added, `offset` one byte too far in base64, in both byte models.
  Each time exactly one request, for line 82 alone, one line sent again, the
  file intact, and the diagnostic gives the position and value of the bad
  unit and the lengths the test expects.
* The incident: the first chunk of 128 KiB, in the call of `transferBegin`
  and `fileBegin`, loses 800 characters: one request for line 2, then
  success, in both encodings.
* The copy sent again damaged too: two requests 500 ms apart, then success.
* Every copy damaged: ten requests, the error above, and nothing written
  stays.
* The number of line 82 damaged: the line is ignored, line 83 shows the gap,
  one request, success.
* Lines without numbers: a damaged chunk still fails the transfer at once.
* 100 MB with two lost calls and one damaged chunk: 1.0 to 1.1 s once the
  code is warm, 2.4 s for the first run of the test process, on this
  development computer.

`u15` lines did not change, and the wire budget of 0.5 stays.

### Versions

Both sides ship together as 0.5.2. The recovery itself is in the agent: an
agent 0.5.2 also asks a client 0.5.1 for a damaged line, which that client
sends again since it ignores the new field, except for base64 that lost a
whole block, which needs the `length` of a client 0.5.2. An agent 0.5.1 still
ends the transfer on a damaged chunk, with "Data was lost or damaged on the
way. Try again.", whatever the client.

The agent without a client does not change: everything new runs inside a
transfer, and the diagnostic only on a damaged chunk.


## Version 0.6

### Why a direct connection

Decided on 2026-11-10. Every transfer measured in production went through
the RDP virtual channel at 2 to 4 MB/s of file data: 2.1 MB/s with base64
in 0.4.4, about 2.5 MB/s in `u15` with 0.5.2, while the channel itself
carried about 5.8 MB/s of wire data on a client link of over 2 Gbit/s. Drive
redirection read 3.6 to 3.7 MB/s. The dense encoding of 0.5 removed the
padding; what is left is the flow control of the Remote Desktop connection
over its virtual channels, which nothing in UltimateRDP can change. In this
section MB means a million bytes.

Version 0.6 opens a direct TCP connection between the client and the agent,
outside RDP, protected by TLS, for the files of a fast transfer. The RDP
channel keeps everything else: the drag, the offer of the direct connection,
the deferred drop, the dropResult. It also stays the path of the files
whenever the direct connection cannot be used: through a Remote Desktop
Gateway, where the client has no route to the server, when the firewall does
not open the ports, or with a peer before 0.6.0.

The server of the first deployment hosts about fifty sessions, most of them
on mstsc, and accepts TCP 3389 and 5985 only from the allowed client
addresses. The owner opens a dedicated range of inbound TCP ports for the
direct connection, on the Windows firewall and on the firewall of the
hosting provider, restricted to the same addresses as RDP.

### The offer

When the agent decides a fast transfer at `dragEnter`, as in 0.4, and the
setting `directTransfer` of agent.json is `auto` (the default; `never` turns
it off, and the tray menu "Direct connection for large files" offers Auto
and Never), it opens a `DirectListener` before any dragAck offers the
transfer:

* a TCP listener on the first free port of `directPortRange`, "3400-3449"
  by default, bound to all addresses, IPv6 and IPv4, with exclusive use of
  the port, so that no other socket may take it on one address only; the
  range takes a single port such as "3400" too, from 1025 to 65535, at most
  1000 ports;
* a token of 32 random bytes, new for every offer;
* the certificate of the agent process, created at the first offer (see
  "The certificate" below);
* the primary IPv4 address of the server, the first address of an interface
  that is up and has an IPv4 gateway, as a hint.

The accepted dragAck carries them:

```
{"type":"dragAck","id":"<id>","accepted":true,"transfer":true,"transferId":"<id>","direct":{"port":3400,"token":"<64 hex digits>","certSha256":"<64 hex digits>","host":"192.0.2.10"}}
```

`host` is absent when the agent knows no such address. Several transfers in
several sessions each take their own port, which is why a range is used.
When no port of the range is free, or the certificate cannot be created, the
dragAck carries no `direct` and the files come through the channel, with a
line in the log. In a session where a direct transfer still runs, the next
offer has no `direct` either.

The listener accepts exactly one connection and stops listening as soon as
it has it: a second connection to the port is refused by TCP. A connection
that does not finish the TLS handshake and present the token within 5
seconds is dropped. The listener also closes when the drag ends without a
deferred drop, when a newer drag replaces it, when the client sends
`transferBegin` through the channel, when the agent stops, and 10 seconds
after the deferred drop when no connection came: the client may have fallen
back to the channel, which then carries the files as in 0.5.2. The 10
seconds count from the deferred drop, not from the offer, because the client
only connects once the button is released; before that, the listener lives
as long as the drag that offered it.

### The connection

After the local drop the client sends the deferred `drop` through the
channel, as before, then, when its own setting `directTransfer` of
client.json is `auto` (the default; `never` keeps the channel), connects
(`DirectUpload` in the client):

1. TCP to the host of the session, the `full address` of the .rdp file
   without its port, and the port of the offer, within 3 seconds. Only when
   that host cannot be reached does the client try the `host` hint, within 3
   seconds too.
2. A TLS client handshake with `SslStream`, TLS 1.2 or 1.3, that accepts the
   certificate of the server only when its SHA-256 is the `certSha256` of
   the offer. Neither the chain nor the name is checked: the pin replaces
   them.
3. The token frame; the agent compares it in constant time and answers with
   the ready frame. A wrong token gets no answer: the agent closes.
4. The files are listed only now, so that a long listing of a folder cannot
   use up the 10 seconds of the agent, then sent frame by frame.

### The frames

Every frame is a length of 4 bytes, little endian, that counts the payload
only, one type byte, then the payload (`DirectFrames` in Core). Numbers are
little endian; a text is UTF-8 without a length, at the end of its payload.
Nothing is base64 or JSON. A payload above 1 MiB, or an unknown type, breaks
the connection.

| Type | Byte | From | Payload |
| --- | --- | --- | --- |
| token | 1 | client | version 1 (1 byte), the token (32 bytes) |
| ready | 2 | agent | none |
| begin | 3 | client | files (4 bytes), bytes (8 bytes) |
| fileHeader | 4 | client | index (4 bytes), size (8 bytes), last write time in UTC as ticks (8 bytes, 0 when unknown), relative path |
| data | 5 | client | 1 byte to 1 MiB of the open file |
| fileTrailer | 6 | client | index (4 bytes), SHA-256 of the file (32 bytes) |
| cancel | 7 | client | reason, in English, for the log |
| ack | 8 | agent | index of the file (4 bytes), bytes of that file (8 bytes), bytes of the transfer (8 bytes) |
| final | 9 | agent | status as in `dropResult` (1 byte: 0 ok, 1 error, 2 cancelled), failure (1 byte), bytes received (8 bytes), milliseconds of the agent (8 bytes), text for the user in the language of the client |

The client writes the data of a file in frames of 1 MiB, one write each.
The agent acknowledges every 8 MiB and at the end of each file; the
acknowledgements drive the progress window and the status bar exactly as
the `fileAck` of the channel do: the rate, the time left, the current file.
After the last file the agent sends the final frame, then reads and drops
what the client may still send until it closes, at most 2 seconds, so that
a reset cannot lose the final frame; then both sides close. The flow
control is the one of TCP. The sockets of both sides have a timeout of 60
seconds once the token was accepted, as the channel path stops a transfer
after 60 seconds without data.

`DirectReceiver` in Core is the agent side. It checks the order of the
frames, that every relative path passes `TransferPaths.IsAllowed` and comes
once, that the files fit in the total of the begin frame, that a file never
goes past its size, the size and the SHA-256 at each trailer, and at the end
that every dropped file arrived. Any failure ends the transfer with an error
final frame whose failure byte names it (protocol, token, no space, bad
name, size, checksum, write, incomplete) and whose text is the one the
channel path would show; what was written is deleted at once.
`DirectSender` in Core is the client side; a thread of its own reads the
acknowledgements and the final frame while the data goes out. Both run over
any stream, so that the tests use an in memory connection and the
applications an `SslStream`.

The files go to `%LOCALAPPDATA%\UltimateRDP\staging\<transferId>-direct`,
a folder of their own beside the folder of the channel transfer, with the
same storage, the same path checks and the same deletion one hour after the
drop.

### The drop

The agent registers the transfer and creates its channel receiver at the
deferred drop, as before; only a success of the direct connection changes
what follows:

* When every file came through the direct connection, the agent withdraws
  the transfer through the channel without a `transferResult`
  (`StagedTransfer.Withdraw`) and drops the copies of the direct folder at
  the kept point with the short drag of 0.4; the usual `dropResult`
  follows through the channel. When the direct transfer ends before the
  deferred drop was handled, the deferred drop does it.
* Any other end of the direct connection deletes what came through it and
  leaves the drop to the channel. While the direct data flows, the channel
  receiver does not stop for the lack of channel data.

The client acts on the outcome (`DirectSendOutcome`):

| Outcome on the client | What the client does |
| --- | --- |
| the final frame says ok | "Transferred 3 file(s) in ... dropping into the session...", then the dropResult |
| no TCP connection within 3 s, to the host and then to the hint | the channel, at once |
| TLS handshake fails, or the certificate is not the pinned one | the channel, at once |
| the agent closes without answering the token | the channel, at once |
| the stream breaks before the first acknowledgement | the channel, at once |
| the stream breaks after an acknowledgement | "The direct connection was lost. Try again.", `transferCancel` through the channel, nothing is dropped |
| an error final frame (checksum, name, space, write) | its text in red, `transferCancel` through the channel |
| a file cannot be read or changed | cancel frame, then the text of the channel path, `transferCancel` through the channel |
| the user cancels, or the tab closes | cancel frame, `transferCancel` through the channel |
| a `transferResult` or `dropResult` of the drag through the channel | the direct connection stops and the text is shown |

The fallback goes on with the same drop and the same progress window: the
client starts the `TransferUpload` of 0.5.2 for the same transfer id, and
the agent, whose channel receiver waited since the deferred drop, receives
it. The reason is in the client log at the info level. The user does
nothing.

The whole matrix, by what is installed and what the network allows:

| Situation | Path of the files |
| --- | --- |
| client and agent 0.6.0, port reachable | direct |
| client 0.6.0, agent 0.5.2 or older | channel: the dragAck has no `direct` |
| client 0.5.2 or older, agent 0.6.0 | channel: the client ignores `direct`; the listener closes at `transferBegin`, or 10 s after the drop |
| `directTransfer` `never` on the agent | channel: no offer |
| `directTransfer` `never` on the client | channel; the listener closes at `transferBegin` |
| no free port in the range, or no certificate | channel: no offer |
| Remote Desktop Gateway, or ports closed by a firewall | channel, after at most 3 s, or 6 s with the hint |
| another direct transfer runs in the session | channel: no offer |

### Security rules

These rules are part of the design; a change to any of them is a change of
the design.

* The listener exists only while a transfer is offered or runs: from the
  decision of a fast transfer to the end of its direct transfer, or to the
  closing rules above. Without a transfer the agent listens on nothing.
* TLS is mandatory, version 1.2 or 1.3; no clear text frame ever goes on the
  connection.
* The client pins the certificate by its SHA-256, which it received through
  the RDP channel, already authenticated and encrypted by the Remote Desktop
  connection. A process that took the port, or a man in the middle, cannot
  present that certificate: the client refuses it, sends nothing, and falls
  back to the channel.
* The token is 32 random bytes, new for every offer, sent only inside TLS,
  compared in constant time, and good for one connection: the agent clears
  its copy once it is accepted. It is never written to a log.
* One connection per offer: the listener stops listening as soon as it
  accepted one. A connection that does not authenticate within 5 seconds is
  dropped and ends the offer, and the channel takes the files.
* One listener per offer, and per session: the agent of each session runs
  with the rights of its user, offers its own port, and only accepts the
  token of its own offer.
* The agent writes nothing outside the staging folder of the transfer: the
  relative paths pass the checks of `TransferPaths`, and the storage checks
  again that every full path stays in the folder.
* Every failure is logged at the warning level, with the address of the
  client and without the token.
* No new privilege: on Windows Server, binding a port above 1024 needs no
  administrator right, and the agent still runs as the user. The firewall
  rule is created by the administrator, separately (see below).

### The certificate

The certificate is self signed, ECDSA P-256 with SHA-256, for "CN=UltimateRDP
agent", with the server authentication usage. It is created at the first
direct offer of the agent process, in about 100 ms, kept until the process
exits, and never added to a certificate store; the agent idle in a session
without client never creates it.

The request was a key kept in memory only. SChannel, which `SslStream` uses
on Windows, refuses such a key for a TLS server ("the platform does not
support ephemeral keys"), as a throwaway program confirmed on this
development computer with TLS 1.2 and 1.3. The private key is therefore a
named key of the CNG key store of the session user, in the Microsoft
Software Key Storage Provider, not exportable, named
`UltimateRDP.Agent.Direct.S<session id>`: it is written to the profile of
the user, protected by Windows for that user only, like any user key. The
agent deletes it when it exits, at the end of the session too, and after an
unexpected error before it ends; a key that an agent left behind is
replaced by the next agent of the same session.

### The firewall rule

The administrator opens the range on the Windows firewall of the server,
from an elevated PowerShell, restricted to the addresses allowed for RDP:

```
New-NetFirewallRule -DisplayName "UltimateRDP direct transfer" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 3400-3449 -RemoteAddress <allowed client addresses> -Program "C:\Program Files\UltimateRDP\UltimateRDP.Agent.exe" -Profile Any
```

and the same range, TCP inbound to the server, from the same addresses only,
on the firewall of the hosting provider. `-Program` limits the rule to the
agent; with it Windows has a rule for the program and does not ask the users
when the agent starts to listen. On a server where the users may not see
such questions at all, `Set-NetFirewallProfile -Profile Domain,Private,Public
-NotifyOnListen False` turns them off. Without the rule the connection does
not come and every transfer goes through the channel, after the 3 seconds of
the attempt.

### Logs

Agent, at the info level:

```
Direct connection: TLS certificate ECDSA P-256 created in 135 ms, SHA-256 <hex>, key UltimateRDP.Agent.Direct.S1 of the user key store, not exportable.
Transfer <id> of drag <id>: direct connection offered on port 3400, address hint 192.0.2.10.
Transfer <id>: direct connection from 198.51.100.7:50122 on port 3400.
Transfer <id>: direct connection from 198.51.100.7:50122 accepted with Tls13 (TLS_AES_256_GCM_SHA384); receiving the files.
Transfer <id> of drag <id>: Succeeded through the direct connection from 198.51.100.7:50122 on port 3400, Tls13 (TLS_AES_256_GCM_SHA384), 1 of 1 file(s), 500000000 of 500000000 bytes in 1.4 s, 351.6 MB/s, 60 acknowledgement(s).
Transfer <id> of drag <id>: the files came through the direct connection; nothing came through the channel, which is no longer waited for.
Transfer <id>: direct connection on port 3400 closed, no connection came within 10 s of the drop; the files come through the channel.
```

and at the warning level a failed handshake, a wrong token, a stream that
broke, or a port range without a free port. The summary of a transfer
through the channel now says "through the channel".

Client, at the info level:

```
Transfer <id> of drag <id>: started with 1 item(s) through the direct connection, port 3400 of <host>, address hint 192.0.2.10, certificate pinned to SHA-256 <hex>; the channel takes the files when the connection cannot be used.
Transfer <id>: direct connection to <host>:3400 with Tls13 (TLS_AES_256_GCM_SHA384); the agent accepted the token.
Transfer <id> of drag <id>: Ok through the direct connection to <host>:3400, port 3400, Tls13 (TLS_AES_256_GCM_SHA384), 1 file(s), 500000000 of 500000000 bytes in 1.6 s, 319.0 MB/s from the drop; 1.4 s and 351.6 MB/s measured by the agent.
Transfer <id>: the direct connection cannot be used, cannot connect, <host>:3400 did not answer within 3 s; the files go through the channel.
```

The status bar adds "Direct connection" to the stage of the transfer, as in
"Transferring 1 file(s): 42 % (85.0 MB/s, 5 s left) · Direct connection",
and the progress window shows it on the second line of its status while the
transfer runs. It is one new text in each application, in the seven
languages; the client also has "The direct connection was lost. Try again.",
and the agent the tray menu entry.

### Measured

In the tests, `DirectTransferTests` runs whole transfers between a
`DirectSender` and a `DirectReceiver` through an in memory connection with a
bounded buffer per direction: several files with a folder and a name in
UTF-8, the acknowledgements by 8 MiB and per file, a wrong token, another
version, the token used twice, a stream cut before and after the first
acknowledgement, a wrong checksum, a cancel frame, a path that leaves the
items, a missing file, no free space, a file that got shorter, a file that
cannot be opened, items that cannot be listed, frames out of order or too
long, and a storage that fails. 100 MB go through in about 0.3 s on this
development computer.

Over loopback, on this development computer, a throwaway program ran the
real `DirectListener` and `DirectCertificate` of the agent, by reflection,
with the client side of `DirectUpload`: TLS 1.3 with
TLS_AES_256_GCM_SHA384, a handshake of about 120 ms with the creation of
the certificate, and 500 MB written to a staging folder on disk in 1.57 s
from the connection to the final frame, 319 MB/s, 352 MB/s measured by the
agent. The same program saw a wrong pin refused by the client, a wrong
token refused by the agent, a second connection refused by TCP, a silent
connection dropped after 5 seconds, a listener closed 10 seconds after the
drop without connection, and the key gone from the user key store after the
agent deleted it. The production rate depends on the link; with the 12
MB/s measured upstream, the direct connection is expected to carry the files
at the rate of the link instead of 2 to 4 MB/s.

### Versions

Both sides ship together as 0.6.0 for the direct connection. A client 0.5.2
or older ignores the `direct` field, since unknown fields are ignored, and
sends the files through the channel; the listener of the agent then closes
at its `transferBegin`, or 10 seconds after the deferred drop. An agent
0.5.2 or older sends no `direct`, so a client 0.6.0 uses the channel. The
channel path of 0.5.2 does not change.

The agent without a client does not change: no listener, no certificate, no
key and no thread exist while no transfer is offered, and nothing of the
direct connection runs before the first fast transfer of the session.

## Version 0.7

### What was measured

In this section MB means a million bytes, as in the section of 0.6.

The first production test of the direct connection, on 2026-11-10, made four drops, all through
the direct connection, with TLS 1.3 on port 3400. The rate was about 135 to 180 MB/s for the
first seconds, then fell to about 20 MB/s. The agent measured, for the transfers that moved:

| Drop | Received | Time | Average |
| --- | --- | --- | --- |
| 1 | 609 MB | 4.5 s | 135 MB/s |
| 3 | 1906 MB | 30 s | 63 MB/s |
| 4 | 1246 MB | 33.8 s | 37 MB/s |

On 2026-11-13 a raw TCP probe, without TLS, disk or files, sent random bytes into a sink that
discards them on the server, over a path with a round trip of 15 ms. One connection showed the
same curve: 90 MB/s in the first second, then 65, 46, 37, 31, 27, then 20 to 22 MB/s steady.
Four connections at once carried 270 to 300 MB/s for 4 seconds, the whole upload of 2.3 Gbit/s
of the client, then decayed to about 90 MB/s, about 22 MB/s per connection. The server has NVMe
disks, a 25 Gbit/s adapter and plenty of memory, and the client read its files from NVMe. The
limit is therefore per TCP connection, somewhere on the path, and several connections add up.
Version 0.7 carries a transfer over several connections.

Two more findings of 2026-11-10:

* In the second drop the agent received 0 bytes in 9.4 s, although TLS and the token were
  accepted. The sender thread of the client stayed blocked, and only reported "Cannot access a
  disposed object (SslStream)" 14 s after the user cancelled. The same file of 3.6 GB, an
  executable, went through at once two drops later. Probably its first open or read blocked
  on the client, as an antivirus scanning a large executable does. Nothing in the logs of 0.6
  can prove it; 0.7 logs it.
* The agent logged each cancel of the user as a warning, "Failed through the direct connection
  ... Cannot access a disposed object. Object name: 'System.Net.Sockets.Socket'". 0.7 counts a
  cancel of the client as cancelled, at the info level.

### Protocol version 2

Version 2 replaces version 1 entirely: no code path for version 1 remains on either side.

The offer in the dragAck gains two fields:

```
{"type":"dragAck","id":"<id>","accepted":true,"transfer":true,"transferId":"<id>","direct":{"port":3400,"token":"<64 hex digits>","certSha256":"<64 hex digits>","host":"<address>","version":2,"connections":8}}
```

`version` is the version of the protocol, 2. `connections` is the number of connections the agent
accepts for the transfer at most: the setting `directConnections` of agent.json, 1 to 16, 8 by default.
A client 0.7.0 uses only an offer of version 2 with 1 to 16 connections
(`DirectTransferOffer.IsWellFormed`), and the channel otherwise.

The client opens the minimum of: the connections of the offer, its own setting
`directConnections` of client.json (1 to 16, 8 by default), and one connection per started
32 MiB of the transfer (`DirectTransferPolicy.ConnectionsFor`). A transfer of 20 MB uses one
connection, one of 6.3 GB eight.

It opens connection 0 first, exactly as in 0.6: TCP to the host of the .rdp file within 3
seconds, then to the address hint, then TLS pinned by the SHA-256 of the offer, then the token.
Once connection 0 got its ready frame and the files are listed, it opens the others in parallel,
to the address connection 0 reached, within 3 seconds each, and uses those that succeed; a
connection that fails is logged at the info level and is not an error. A connection that opens
late starts carrying blocks as soon as it is ready.

### The token of each connection

The first frame of every connection is its token frame: the version byte 2, the index of the
connection (1 byte), and 32 bytes of proof:

```
proof = HMAC-SHA256(key = the 32 bytes of the token of the offer, message = ASCII "UltimateRDP direct v2" followed by the index byte)
```

The token itself never goes on a connection in version 2, and each connection has a proof of its
own. The agent compares the proof in constant time, before anything that depends on the index,
and accepts each index once, below the `connections` of its offer. Connection 0 must come
first: another index before it is refused. A refused token gets no answer: the agent closes
that socket and goes on listening. The agent clears its copy of the token once every index was
used or the transfer ended.

The listener keeps listening after connection 0, until the transfer ends or every connection of
the offer presented its token. It accepts at most N + 4 sockets per offer in total, N being the
connections of the offer, so that a scanner from an allowed address cannot keep it busy, and
each socket must finish its TLS handshake and present its token within 5 seconds.

### The frames

The framing of 0.6 does not change: a length of 4 bytes, little endian, that counts the payload,
one type byte, then the payload. A payload above 1 MiB and 12 bytes, or an unknown type, breaks
the connection.

| Type | Byte | From | Connection | Payload |
| --- | --- | --- | --- | --- |
| token | 1 | client | each, first | version 2 (1 byte), index (1 byte), proof (32 bytes) |
| ready | 2 | agent | each | none |
| begin | 3 | client | 0 | files (4 bytes), bytes (8 bytes) |
| fileHeader | 4 | client | 0 | index (4 bytes), size (8 bytes), last write time in UTC as ticks (8 bytes, 0 when unknown), relative path |
| data | 5 | client | any | file index (4 bytes), offset (8 bytes), then 1 byte to 1 MiB of the file |
| fileTrailer | 6 | client | 0 | index (4 bytes), SHA-256 of the file (32 bytes) |
| cancel | 7 | client | 0 | reason, in English, for the log |
| ack | 8 | agent | 0 | index of the file being checked (4 bytes), bytes of that file checked (8 bytes), bytes of the transfer received and written (8 bytes) |
| final | 9 | agent | 0 | as in 0.6 |
| listEnd | 10 | client | 0 | none |
| go | 11 | agent | 0 | none |
| end | 12 | client | each | the number of data frames the client sent on this connection (8 bytes) |

Connection 0 is the control connection and carries data too. On it, in order: token, ready,
begin, one fileHeader per file, all of them before any data, listEnd. The agent checks the whole
list as before (`TransferPaths`, a path that comes twice, the free space, a dropped file missing
from the list, the files adding up to the bytes of begin), creates every file, and answers go.
Only then the data flows, on every connection: data frames, and on connection 0 the trailers
between them. Each connection ends with its end frame. The cancel frame may come at any time on
connection 0. Acknowledgements and the final frame come on connection 0 only; on the other
connections the agent sends nothing after ready.

Blocks are aligned: the offset is a multiple of 1 MiB and the length is the smaller of 1 MiB and
the rest of the file. An empty file has no block. The agent keeps the state of each block of
each file, and a block that comes twice, a misaligned one, one of the wrong length, or one beyond
its file or beyond the files is a protocol error. A data frame before go, or after the end frame
of its connection, is a protocol error too.

The end frame has a purpose of its own: once every connection that
carries blocks sent it, the agent knows that no block is on its way any more, so that a block
that never came fails the transfer at once as incomplete, rather than after the 60 seconds of
the socket timeout. Its count must be the number of data frames that arrived on that
connection, which the agent checks.

### The sender

`DirectSender` in Core. One reader thread reads the files in order, by blocks of 1 MiB, into
buffers of a pool that leave room for the 17 bytes in front of the data, so that a data frame
goes out in one write. It computes the SHA-256 of each file in order as it reads it, as in 0.6,
and puts the blocks in a bounded queue of two blocks per connection. When it has read a file,
it writes the trailer on connection 0. One writer thread per connection takes the next block
from the shared queue and writes it to its connection, so that a slow connection does not hold
the others: it only takes a block when it has written the one before. The writes on connection
0, from its writer, from the reader for the trailers, and from a cancel, take turns on a lock. A
thread of its own reads connection 0: go, the acknowledgements and the final frame.

The sockets keep their buffer sizes: SO_SNDBUF and SO_RCVBUF are not set, since setting them
turns off the autotuning of TCP on Windows. NoDelay is on, and the timeouts of 60 seconds of 0.6
stay.

### The receiver and the verification

`DirectReceiver` in Core. One thread per connection reads its frames and writes each block at
its offset (`IDirectStorage`, in the agent `RandomAccess.Write` on a `SafeFileHandle`), so that
no buffer puts the blocks back in order. The files are created at listEnd, and each one is
opened at its first block and closed once checked, so that a folder of many small files does not
hold a handle per file for the whole transfer.

A verifier thread checks the files. Two designs were possible: a verifier that hashes the part
of each file that is complete from its start by reading it back from the file as the blocks
arrive, or a hasher that receives the blocks in memory and in order, with a bounded buffer and
backpressure on the connections. Version 0.7 reads back:

* the verifier takes the files in order, waits until the next block of the current file is
  written, reads it back with a positional read, and adds it to the SHA-256 of the file. Its
  memory is one block, whatever the order the blocks come in;
* nothing holds the connections back: a slow connection makes the verifier wait for its block,
  while the other connections keep writing; an in order hasher with a bounded buffer would
  instead stop every connection behind the slow one;
* it checks what is in the file, not only what came on the wire;
* the cost is a second read of data just written, which the file cache of the server serves
  from memory. On this development computer it ran at about 1.5 GB/s with one connection.

Once the whole file is hashed and its trailer came, the verifier compares the two, closes the
file, sets its last write time, and acknowledges the file. It also sends the acknowledgements
every 8 MiB of the transfer received and written, as before; they drive the progress window.
After the last file it sends the final frame. When every connection sent its end frame and a
block or a trailer is still missing, it fails the transfer as incomplete. The tests check blocks
that arrive in any order and on any connection, a block that comes twice, misaligned blocks, a
missing block, a wrong checksum, and many small and empty files.

Once the transfer is over, the files are closed and, unless it succeeded, deleted as soon as no
thread writes or reads them any more.

A connection lost after go fails the transfer with the text of 0.6, "The direct connection was
lost. Try again." on the client once an acknowledgement came; every connection closes and
nothing partial is dropped. Before the first acknowledgement the client falls back to the
channel, as in 0.6. A connection other than 0 that closes before it carried any block is only
dropped, with a line at the info level, and the transfer goes on without it.

### The limit of the rate

The setting `directMaxMegabytesPerSecond` of agent.json, 0 by default for no limit, protects
the server. `DirectPacer` in Core is a token bucket shared by the connections of a transfer:
after each block, a connection takes its bytes from the bucket and sleeps the time it is told
before it reads again, at most a second at a time. Bytes taken beyond the bucket are a debt that
every connection pays, so that all of them together keep the limit; while they sleep, TCP flow
control slows the client down. The bucket holds a tenth of a second at the limit, and at least
two blocks. The tests check the burst, the long run rate, the shared debt and the refill on a
clock of their own.

### Cancellation

On the client a cancel of the user, or the closing of the tab, sends the cancel frame on
connection 0 when the lock of its writes can be taken within 1 second, waits for the final frame
in the rest of that second, then closes every socket and the file being read. A writer blocked
in a socket write, or a reader blocked in a file read, does not hold the cancel: closing the
socket ends a blocked write at once; a file read that the close cannot end returns later, and
the reader then sends nothing and logs a line. A timer closes everything 1.5 seconds after the
cancel if the thread of the transfer is held elsewhere. The client then sends `transferCancel`
through the channel, as in 0.6.

On the agent, a cancel of the client is counted as Cancelled, at the info level, with the
bytes and the rate, whether it comes as the cancel frame, as `transferCancel` through the
channel, or as connections that close right before the `transferCancel`: when the connections
of a transfer close without a cancel frame, the summary waits up to 2 seconds for a
`transferCancel` before it decides. A transfer that the agent closes itself, because the drag
ended, a newer drag replaced it or the agent stops, is logged at the info level as closed by the
agent. Only the other failures are warnings, and none of them carries the text of an object
disposed by the cancel.

### Logs

Client, at the info level:

```
Transfer <id> of drag <id>: started with 1 item(s) through the direct connection, port 3400 of <host>, up to 8 connection(s) (the agent accepts 8, the setting allows 8), certificate pinned to SHA-256 <hex>; the channel takes the files when the connection cannot be used.
Transfer <id>: connection 0 to <host>:3400 with Tls13 (TLS_AES_256_GCM_SHA384); the agent accepted the token.
Transfer <id>: the agent checked the list and created the files; the data goes over up to 8 connection(s).
Transfer <id>: Opening <path> took <n> ms; nothing was sent meanwhile.
Transfer <id>: The first read of <path> took <n> ms; nothing was sent meanwhile.
Transfer <id>: Connection 5 is not used: <host>:3400 did not answer within 3 s
Transfer <id>: direct connection statistics: 8 of 8 planned connection(s), bytes per connection 0: <bytes>, 1: <bytes>, ...; <s> s from the go frame; the reader spent <s> s opening and reading the files and waited <s> s for room in the queue; the writers spent <s> s in socket writes and waited <s> s for blocks, summed over the connections; average <rate> MB/s, first 5 s <rate> MB/s, last 5 s <rate> MB/s.
Transfer <id>: The reader is still reading <path>; it ends when that call returns, and sends nothing more.
Transfer <id>: The read of <path> returned <s> s after the connections closed; nothing more is sent.
```

and at the debug level one line per second:

```
Transfer <id>: <rate> MB/s in the last second through the direct connection, <bytes> bytes written, <bytes> acknowledged.
```

Agent, at the info level:

```
Transfer <id> of drag <id>: direct connection offered on port 3400, protocol version 2, up to 8 connection(s), address hint <address>.
Transfer <id>: connection 0 from <address>:<port> accepted with Tls13 (TLS_AES_256_GCM_SHA384); receiving the list of the files.
Transfer <id>: connection 3 from <address>:<port> accepted with Tls13 (TLS_AES_256_GCM_SHA384).
Transfer <id> of drag <id>: Cancelled through the direct connection from <address>:<port> on port 3400, Tls13 (TLS_AES_256_GCM_SHA384), 0 of 1 file(s), 20971520 of 2000000000 bytes in 3.0 s, 6.9 MB/s, 2 acknowledgement(s). Cancelled by the client through the channel, The user cancelled the transfer.; the files that came are deleted.
Transfer <id>: direct connection statistics: 8 connection(s), bytes per connection 0: 250609664, 1: 254120960, ...; socket reads 3.69 s, file writes 15.56 s, hashing 3.82 s, summed over the threads; average 520.3 MB/s, first 5 s 520.3 MB/s, last 5 s 520.3 MB/s.
```

with "waits for the rate limit" in the statistics when `directMaxMegabytesPerSecond` is set, a
line for a connection other than 0 that closed before it carried any block, and at the debug
level one line per second with the rate of that second. A token of another version, or a wrong
token, is a warning that names the reason.

How to read the statistics. The times of several threads are added up, so a time may exceed the
length of the transfer; compare it with the seconds times the connections.

* The reader of the client close to the whole time, and writers that wait long for blocks: the
  files limit the transfer on the client, its disk or an antivirus. The lines about a slow open
  or first read say which file.
* Writers of the client in socket writes for most of the time, times the connections, and a
  reader that waits for room in the queue: the network limits the transfer. A first 5 s much
  faster than the last 5 s is the per connection limit measured above.
* Socket reads of the agent close to the whole time, times the connections: the agent waits for
  the network, which confirms the line of the client.
* File writes or hashing of the agent close to the whole time: the disk or the processor of the
  server limits the transfer.

The progress window and the status bar keep the preparing stage of 0.6, "Preparing..." with
"Direct connection", until the first acknowledgement, so that a file that takes long to open no
longer looks like a transfer at 0 B/s.

### Versions and compatibility

| Client | Agent | Path of the files |
| --- | --- | --- |
| 0.7.0 | 0.7.0 | direct, up to 16 connections |
| 0.7.0 | 0.6.0 | channel: the offer has no `version`, the client ignores it |
| 0.6.0 | 0.7.0 | channel: the client ignores the new fields, connects and sends a token of version 1; the agent closes without an answer, the client sees a refusal before any data and falls back at once; the listener closes at `transferBegin` |
| 0.6.0 | 0.6.0 | direct, one connection, as in 0.6 |
| 0.5.2 or older | any | channel |
| any | 0.5.2 or older | channel |

The rest of the matrix of 0.6, the setting `never` on either side, a Gateway, closed ports, no
free port, does not change.

### Settings

| Where | Setting | Values | When absent | Meaning |
| --- | --- | --- | --- | --- |
| agent.json | `directConnections` | 1 to 16 | 8 | The `connections` of the offer. |
| agent.json | `directMaxMegabytesPerSecond` | 0 to 100000 | 0 | The limit of the rate of a transfer, all its connections together, in MB/s; 0 for none. |
| client.json | `directConnections` | 1 to 16 | 8 | How many connections the client opens at most. |

They are only editable in the files, and read at the start of the application. A value out of
range gives the default and a warning in the log. No new text is shown to the user.

### Security rules, as changed

The rules of 0.6 hold, with these changes: an offer accepts up to `connections` connections
instead of one, each with a proof of the token for its index instead of the token, each index
once, connection 0 first, at most N + 4 sockets per offer, each of them within 5 seconds. The
listener still exists only while a transfer is offered or runs, and the agent without a client
does not change: no listener, no certificate and no thread while no transfer is offered.

### Measured

In the tests, `DirectTransferTests` runs whole transfers between a `DirectSender` and a
`DirectReceiver` through in memory connections with a bounded buffer per direction, over 1, 4
and 8 connections, and frames written by hand where the rules of the receiver are tested: blocks
in any order, a block twice, misaligned blocks, a missing block, an end frame with a wrong
count, a wrong checksum, a cancel at the first acknowledgement, before the list, while the
reader is blocked in a file read and while a writer is blocked in a socket write, a cancel
through the channel, connections closed before it, a close by the agent, one connection broken
in the middle, an extra connection that cannot be opened or that closes before any block, a
token of version 1, a wrong token, an index beyond the offer, a replayed index, an index before
connection 0, 400 small and empty files over four connections, and the limit of the rate. 200 MB
go through 8 connections in about 0.2 s on this development computer. `DirectPacingTests` checks
the token bucket and the rates of the summary on a clock of its own.

Over loopback on this development computer, a throwaway program ran the real `DirectListener`
and `DirectCertificate` of the agent, by reflection, with a staging folder on disk, and the
client side of `DirectUpload` with the real `DirectSender`, TLS 1.3 on every connection:

| Transfer | Time from the first connection to the final frame | Rate |
| --- | --- | --- |
| 2 GB over 1 connection | 6.3 to 6.5 s | 307 to 318 MB/s |
| 2 GB over 8 connections | 3.8 to 4.0 s | 504 to 528 MB/s |

On loopback, client and agent share the processor with the TLS of every connection; with eight
connections the verifier of the agent was busy for about the whole transfer, so that it is the
limit there, above the rates expected on the network. With the agent paced at 5 MB/s so that
every writer was blocked in a socket write, a cancel returned in 1.0 s when the `transferCancel`
of the channel came 1.5 s later, and in 0.1 to 0.6 s when it came right after the cancel, as the
client sends it; the agent logged both as cancelled at the info level. A cancel while the first
read of a file was held returned at once, and the read that returned 2 seconds later left its
line. The same program saw a token of version 1 refused without an answer, a wrong pin refused
by the client, the seventh socket refused for an offer of two connections, six silent sockets
dropped after 5 seconds, and the key gone from the user key store after the agent deleted it.

On the production path, with the 20 to 22 MB/s per connection measured after the first seconds,
eight connections are expected to carry about 170 MB/s once the first seconds are over, against
about 20 MB/s with one; the statistics lines of both logs will tell.

### Versions

Both sides ship together as 0.7.0. The channel path of 0.5.2 does not change.
