# UltimateRDP design

Date: 2026-11-05, updated on 2026-11-09 for version 0.3.0.

## Goal

Let a user drag files or folders from the local Windows desktop and drop them
into a Remote Desktop session, on the application under the cursor when the
button is released. The application receives them as a normal drop. When
nothing under the cursor accepts them, the drop is ignored.
The transfer uses the standard RDP drive redirection; UltimateRDP only adds the
"drop" gesture on top of it.

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

### Protocol

Static virtual channel named `ULTRDP` (six characters, within the maximum of
seven for a static channel). Every message is one line (see "Wire encoding"
below). Messages are JSON objects with a `type` field. Fields that have no
value are not written.

Client to agent:

* `hello` : `{ "type": "hello", "version": 1, "clientVersion": "0.3.0",
  "clientLanguage": "fr" }`
* `dragEnter` : `{ "type": "dragEnter", "id": "<guid>", "x": 412, "y": 300,
  "items": [ { "clientPath": "C:\\Users\\me\\a.txt", "isDirectory": false } ],
  "keyState": 0 }`
* `dragMove` : `{ "type": "dragMove", "id": "<guid>", "x": 420, "y": 310,
  "keyState": 0 }`
* `drop` : `{ "type": "drop", "id": "<guid>", "x": 420, "y": 310,
  "keyState": 0 }`
* `dragLeave` : `{ "type": "dragLeave", "id": "<guid>" }`
* `logoff` : `{ "type": "logoff", "id": "<guid>" }`

Agent to client:

* `hello` : `{ "type": "hello", "version": 1, "agentVersion": "0.3.0",
  "driveRedirectionAvailable": true }`
* `dragAck` : `{ "type": "dragAck", "id": "<guid>", "accepted": true,
  "message": "optional text" }`
* `dropResult` : `{ "type": "dropResult", "id": "<guid>",
  "status": "ok" | "error" | "cancelled", "effect": "copy" | "link" | "none",
  "message": "human readable text", "targetWindow": { "className": "CabinetWClass",
  "title": "Documents", "childClassName": "DirectUIHWND", "processName": "explorer" } }`
* `logoffResult` : `{ "type": "logoffResult", "id": "<guid>", "accepted": true,
  "message": "human readable text" }`

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
   below), Session (Connect, Disconnect, Look for the agent again), View
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
   menu: Stage files in the session before the drop, Open log folder,
   About, Exit. About is a task dialog with the version, the publisher
   APROGSYS and its web site, and the project page, as links. Settings in
   `%LOCALAPPDATA%\UltimateRDP\agent.json` (`stageFiles`, and `logLevel`
   and `trayIcon` which are only editable in the file). Logs in
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
  acknowledge the drag", sends `dragLeave` and refuses the drop.
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

## Security notes

* The agent only reads from `\\tsclient\`, that is from the drives the client
  user chose to share. It writes only to its own staging folder in the
  session, when staging is on. Where the dropped files go is decided by the
  application that accepts the drop. The agent does not run as a service and
  has no privilege beyond the user's own.
* Simulated input happens only inside the user's own session, only during a
  drag, and only with the left button. See "Step 2" for the details.
* Messages carry file names, coordinates, modifier key flags and window names
  only. No file content transits through the channel.
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

