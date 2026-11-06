# RdpDrop design

Date: 2026-11-05

## Goal

Let a user drag files or folders from the local Windows desktop and drop them
into a Remote Desktop session, at the place where the cursor is released.
The transfer uses the standard RDP drive redirection; RdpDrop only adds the
"drop" gesture on top of it.

Scope of version 1:

* Direction: local computer to remote session only. The other direction keeps
  using copy and paste.
* Client: Windows 10 or 11, 64 bit. A single portable executable, nothing to
  install, nothing written to the registry.
* Server: Windows Server (2016 or later) or Windows 10/11 acting as a Remote
  Desktop host. A small agent runs in the user session.
* Single remote monitor.
* Works through a Remote Desktop Gateway, because it only relies on a static
  virtual channel and on drive redirection, both of which the gateway forwards.

Out of scope for version 1: drag from the session to the local desktop,
multi monitor sessions, RemoteApp, connections without drive redirection.

## Why not something simpler

The RDP protocol has no drag and drop channel. The Microsoft client (mstsc)
rejects drops on its window before the server is ever involved. So a piece of
code must run on the client side to catch the drop, and a piece of code must
run inside the session to perform it. The transfer itself does not need any
new code: once drive redirection is on, every local drive is reachable from
the session as `\\tsclient\<letter>`.

## Architecture

Three projects in one .NET 10 solution, C# everywhere.

```
RdpDrop.sln
src/RdpDrop.Core/          class library, no UI, fully unit tested
src/RdpDrop.Client/        WinForms app, hosts the Microsoft RDP ActiveX control
src/RdpDrop.Agent/         WinForms tray app, runs inside the remote session
tests/RdpDrop.Core.Tests/  xUnit
.github/workflows/ci.yml       build and test on every push and pull request
.github/workflows/release.yml  build, publish and attach zips on tags v*
docs/design.md             this document
docs/releases/<tag>.md     release notes, used as the release body
README.md, LICENSE (MIT), .gitignore, .editorconfig
```

### Data flow

```
local desktop      RdpDrop.Client                 RDP           RdpDrop.Agent (in session)
    |                    |                         |                    |
    | drag file(s)       |                         |                    |
    |------------------->| drop at (x,y)           |                    |
    |                    | map to remote coords    |                    |
    |                    | "drop" message -------->| static VC -------->| resolve folder under (x,y)
    |                    |                         |                    | copy \\tsclient\C\... -> folder
    |                    |<------- "dropResult" ---|<-------------------| (Explorer progress dialog)
    |                    | show result in status bar                    |
```

### RdpDrop.Core

Pure logic shared by the two apps and covered by tests.

* `Messages`: the message records below, serialized with System.Text.Json.
* `LineFraming`, `LineReader` and `ChannelTextCodec`: ASCII line framing
  and byte model detection, described under "Wire encoding" below.
* `ClientPathMapper`: turns a local path such as `C:\Users\me\a.txt` into
  `\\tsclient\C\Users\me\a.txt`. Only paths with a drive letter are accepted.
  UNC paths and relative paths are rejected with a clear reason.
* `RdpFile`: parser for `.rdp` files (`key:type:value` lines, `s`, `i`, `b`
  types, UTF-16 with BOM or UTF-8, blank lines and comments ignored). Exposes
  typed getters with defaults.
* `TargetFolderPolicy`: given what the agent found under the cursor (an
  Explorer window showing a file system folder, the desktop, or something
  else) and the configured fallback, returns the folder to use. No COM here;
  the agent feeds it plain values.

### Protocol

Static virtual channel named `RDPDROP` (seven characters, the maximum for a
static channel). Every message is one line (see "Wire encoding" below).
Messages are JSON objects with a `type` field.

Client to agent:

* `hello` : `{ "type": "hello", "version": 1, "clientVersion": "0.1.0" }`
* `drop` : `{ "type": "drop", "id": "<guid>", "x": 412, "y": 300,
  "items": [ { "clientPath": "C:\\Users\\me\\a.txt", "isDirectory": false } ] }`
  `x` and `y` are pixels in the remote desktop coordinate space.

Agent to client:

* `hello` : `{ "type": "hello", "version": 1, "agentVersion": "0.1.0",
  "driveRedirectionAvailable": true }`
* `dropResult` : `{ "type": "dropResult", "id": "<guid>",
  "status": "ok" | "error" | "cancelled", "targetFolder": "C:\\Users\\me\\Desktop",
  "message": "human readable text" }`

Version handling: both sides send `version: 1`. A receiver that sees a higher
major version replies with an error `dropResult` or ignores the message and
logs it. Unknown fields are ignored.

#### Wire encoding

The ActiveX control exposes channel data as a COM string (BSTR), and the way
`mstscax.dll` turns bytes into string units is not documented. Two models are
possible: one byte per character, or raw UTF-16LE (two bytes per character,
with an odd trailing byte lost). RdpDrop therefore only ever puts ASCII on
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

### RdpDrop.Client

A WinForms application (`net10.0-windows`, x64) that embeds the Microsoft RDP
ActiveX control (`mstscax.dll`) through the `Devolutions.MsRdpEx` NuGet
package. If that package turns out not to provide a working WinForms host on
.NET 10, the fallback is generating the interop assemblies with `aximp` and
`tlbimp` from the Windows SDK during the build.

Behaviour:

1. Start with `RdpDrop.Client.exe <file.rdp>`; without an argument, show an
   open file dialog filtered on `.rdp`.
2. Read the `.rdp` file with `RdpFile` and apply the settings that matter:
   full address, server port, username, domain, gateway host name and usage
   method, gateway credentials source, authentication level, prompt for
   credentials, redirect clipboard, redirect drives and drives to redirect,
   desktop width and height, screen mode id (full screen is ignored in
   version 1 and a windowed session is used instead, because a full screen
   window cannot receive a drop from the local desktop).
   Settings that have no equivalent on the control are ignored.
3. Before connecting, call `CreateVirtualChannels("RDPDROP")` on the control
   and enable drive redirection if the `.rdp` file asks for it. If the file
   does not redirect drives, connect anyway and show a warning in the status
   bar: drops will not work.
4. Desktop size: the control is docked to fill the window. At connect time the
   desktop size is the control client size. Smart sizing is enabled so that a
   later resize scales the picture; drop coordinates are converted with the
   ratio between the desktop size and the control size.
5. Drop target: the main form has `AllowDrop`. In addition, after the control
   is created, the client walks the child windows of the control and
   registers its own `IDropTarget` on each one with `RegisterDragDrop`
   (revoking any existing registration first). This guarantees that the drop
   reaches us even if the ActiveX window has its own registration. Only
   `CF_HDROP` data is accepted.
6. On drop: build the item list, convert the point, send a `drop` message
   with a new id, write "Sending N item(s)…" in the status bar. On
   `dropResult`, show the result text. If the agent is not ready, refuse the
   drop with the "not allowed" cursor and explain in the status bar.
7. Agent discovery: after `OnConnected`, send `hello` every two seconds until
   the agent answers or sixty seconds pass. Status bar shows "Agent: waiting",
   then "Agent ready (vX)" or "No agent on the remote computer".
8. Status bar at the bottom: connection state, agent state, last drop
   result. Menu: Session (Connect, Disconnect, Quit), Help (About, link to
   the README).
9. Disconnection shows the reason text from the control and keeps the window
   open so the user can reconnect.

### RdpDrop.Agent

A WinForms tray application (`net10.0-windows`, x64), single instance, no
main window.

Behaviour:

1. Channel loop on a background thread:
   `WTSVirtualChannelOpen(WTS_CURRENT_SERVER_HANDLE, WTS_CURRENT_SESSION, "RDPDROP")`,
   then `WTSVirtualChannelRead` with a short timeout, so that the loop can
   stop or restart without closing a handle under a pending read. Detect the
   byte model from the first two bytes, convert chunks to text with
   `ChannelTextCodec`, feed a `LineReader`, dispatch messages, write replies
   with `WTSVirtualChannelWrite` in the same byte model. On any error
   (session disconnected, channel closed, not an RDP session) close
   everything, wait two seconds and retry forever. Also listen to
   `WM_WTSSESSION_CHANGE` to retry at once after a reconnect.
2. On `hello`: reply with the agent version and whether at least one
   `\\tsclient\<letter>` is reachable.
3. On `drop`:
   * Map every item with `ClientPathMapper` and check that the mapped path
     exists. Missing items produce an error result that names them.
   * Find the folder under the cursor: `WindowFromPoint`, then the root
     window. Enumerate `Shell.Application.Windows()`; a window whose `HWND`
     equals the root and whose document is a folder view gives
     `Document.Folder.Self.Path`. If the root is the desktop window
     (class `Progman` or `WorkerW`), use the desktop folder. Otherwise use the
     configured fallback folder (desktop by default, downloads or a custom
     path as alternatives). Only file system paths are accepted; a virtual
     folder such as "This PC" falls back too. The decision itself lives in
     `TargetFolderPolicy` in Core.
   * Copy with `Microsoft.VisualBasic.FileIO.FileSystem.CopyFile` and
     `CopyDirectory` using `UIOption.AllDialogs`, on an STA thread. This gives
     the regular Windows progress dialog and conflict prompts.
   * Reply `dropResult` with `ok`, `cancelled` (user cancelled the dialog)
     or `error`.
4. Tray menu: Fallback folder (Desktop, Downloads, Choose…), Open log folder,
   About, Exit. Settings in `%LOCALAPPDATA%\RdpDrop\agent.json`. Logs in
   `%LOCALAPPDATA%\RdpDrop\logs\agent-<date>.log`, rotated by day, kept
   fourteen days.
5. Command line:
   * `--install`: add a `Run` value under `HKCU` so the agent starts with the
     user session, then start the agent.
   * `--uninstall`: remove that value and stop any running instance.
   * `--install-all-users`: same under `HKLM` (needs an elevated prompt).
   * no argument: run.

## Error handling

* Drop without agent: refused on the client, explained in the status bar.
* Drop without drive redirection: agent answers with an error that says the
  drives are not redirected and points to the Local Resources setting.
* Item not reachable from the session (path on a drive that is not
  redirected, removable drive unplugged): error result naming the item.
* Agent cannot open the channel (console session, policy forbidding the
  channel): logged once at warning level, then silent retries.
* Protocol error (a line too long or bad JSON): logged, line reader reset,
  connection kept.

## Security notes

* The agent only reads from `\\tsclient\`, that is from the drives the client
  user chose to share, and only writes inside the session of the same user.
  It does not run as a service and has no privilege beyond the user's own.
* Messages carry file names and coordinates only. No file content transits
  through the channel.
* The client does not store credentials. Credential prompts come from the
  Microsoft control.

## Testing

* `RdpDrop.Core.Tests` covers: framing round trip, split and merged chunks,
  oversize lines, JSON round trip of every message, path mapping (drive
  letter case, UNC rejection, relative rejection), `.rdp` parsing (UTF-16 and
  UTF-8, types, defaults), target folder policy (Explorer folder, desktop,
  virtual folder, nothing under cursor, each fallback).
* Client and agent are exercised by hand against a real session; the status
  bar and the agent log give enough detail to diagnose a failed drop.
* CI runs the tests on `windows-latest` for every push and pull request.

## Release

* Tag `vX.Y.Z` on `main` triggers the release workflow: build, test, publish
  both applications as self contained single file executables for `win-x64`,
  zip them as `RdpDrop.Client-vX.Y.Z-win-x64.zip` and
  `RdpDrop.Agent-vX.Y.Z-win-x64.zip`, create the GitHub release with
  `docs/releases/vX.Y.Z.md` as body and attach both zips.
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
* View menu with "Scale to window" (smart sizing, the 0.1 behaviour) and
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
anything that accepts a file drop in the session accepts it from RdpDrop.

Principle: the agent becomes the drag source inside the session. It replays
the local drag in real time and ends it with a real drop at the point where
the user released the button.

* Live drag messages from the client, on the same channel and in the same
  ASCII line format: `dragEnter` (id, x, y, items, key state), `dragMove`
  (id, x, y, key state, throttled to about thirty per second and only when
  the point or the keys changed), `drop` (id, x, y, key state) and
  `dragLeave` (id). Key state carries the Ctrl, Shift and Alt flags so that
  the remote target applies its own copy, move or link semantics.
* Agent side, on `dragEnter`: on a dedicated STA thread, create a tiny
  invisible layered window, move it under the cursor point, move the remote
  cursor there and simulate a left button press on that window with
  `SendInput`, then call `DoDragDrop` with a data object that offers
  `CF_HDROP` (the `\tsclient\<drive>\...` paths) and, for applications
  that do not accept UNC paths, the same files staged in
  `%LOCALAPPDATA%\RdpDrop\staging\<id>\` when the setting "stage files in
  the session before the drop" is on (off by default, staging happens
  before `DoDragDrop` and can take time for large files).
* The agent implements `IDropSource`: `QueryContinueDrag` returns continue
  while the client keeps sending `dragMove`, drop when `drop` arrived,
  cancel when `dragLeave` arrived or no message came for five seconds.
  Each `dragMove` moves the remote cursor with `SendInput` so that OLE
  delivers `DragOver` to the window under it and the target shows its own
  feedback. On `drop`, the agent moves the cursor to the final point and
  releases the simulated button; on cancel it releases the button and
  returns cancel from `QueryContinueDrag`.
* Result: `dropResult` carries the effect the target returned (copy, move,
  link, none) and, when known, the window class and title of the target.
  When the target returned none and the setting "copy to the fallback
  folder when nothing accepts the drop" is on (default on, so that 0.1
  behaviour is kept), the agent performs the 0.1 copy into the fallback
  folder and says so in the result.
* Acknowledgement and timeouts: the agent answers `dragEnter` with
  `dragAck` within a second; without it the client shows "no answer from
  the agent" and refuses the drop. `dropResult` has no timeout because the
  target may show dialogs.
* Client side: the drop zone banner from the previous plan stays; the
  client sends `dragEnter` on the first `DragOver` over the session (not on
  `DragEnter`, so that the point is known), `dragMove` while hovering,
  `drop` or `dragLeave` at the end. The local drag effect shown to the user
  is copy while the agent is in the drag, none when it refused.
* Compatibility: a 0.1 agent ignores the new message types; the client
  detects a 0.1 agent from its hello version and falls back to the 0.1
  `drop` message, which keeps the copy to the folder under the cursor.
* Security: the simulated input happens only inside the user's own
  session, only between a `dragEnter` and the matching `drop` or
  `dragLeave`, and only with the left button. The staging folder is wiped
  when the agent starts and after each drop.

### Step 3: connections

* `SessionView` user control that owns one control, one agent link and the
  drop logic; `MainForm` becomes a host with tabs, one per session, and a
  status bar that follows the active tab. Keyboard shortcuts and menus act
  on the active tab.
* File menu: Open .rdp, Recent connections (ten entries, stored in
  `%LOCALAPPDATA%\RdpDrop\client.json`), New tab from the same file,
  Close tab.
* Credentials: before connecting, read the generic credential
  `TERMSRV/<host>` from the Windows Credential Manager, like mstsc does; if
  present, apply user name, domain and password to the control and skip the
  prompt. Nothing is written by RdpDrop; saving happens through the Windows
  prompt of the control, which offers "Remember me". A File menu entry
  forgets the saved credential for the current host.

### Step 4: review, documentation, release

* Code review of the three steps, README update (new menus, modifiers,
  tabs, credentials), release notes `docs/releases/v0.2.0.md`, tag
  `v0.2.0`.
