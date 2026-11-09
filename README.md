# UltimateRDP

[![CI](https://github.com/freeKC/ultimaterdp/actions/workflows/ci.yml/badge.svg)](https://github.com/freeKC/ultimaterdp/actions/workflows/ci.yml)

UltimateRDP lets you drag files or folders from your desktop and drop them into a Remote Desktop session, on the application under the cursor. Explorer, a mail client, a browser or a business application receives them as if you had dropped them inside the session. When nothing under the cursor accepts them, the drop is ignored. UltimateRDP is made of a client that hosts the Microsoft Remote Desktop control, a small agent in the session, a virtual channel between the two, and the standard drive redirection. Large files travel through the virtual channel before the drop, which is faster than drive redirection.

## Why

The Microsoft Remote Desktop client rejects drops on its window, because RDP has no drag and drop channel. Copy and paste works, and so does drive redirection, but neither is a drop. UltimateRDP adds the gesture on top of what RDP already offers.

While you drag over the client window, the agent replays the same drag inside the session as a real OLE drag and drop. Any remote application that accepts dropped files therefore accepts them from UltimateRDP, and shows its own drop feedback while you move. When nothing under the cursor accepts the drop, the drop is ignored: nothing is copied anywhere.

Small files travel through drive redirection. Large files travel through the UltimateRDP channel before the drop: see "Large files" below.

## What you need

- A client computer with Windows 10 or 11, 64 bit.
- A server with Windows Server 2016 or later, or a Windows 10 or 11 computer acting as a Remote Desktop host.
- An .rdp file that redirects drives. In the Remote Desktop Connection settings, this is Local Resources, then More, then Drives.
- The ability to run the agent in your user session on the server.

The client is one portable executable. It runs without administrator rights. The agent archive also holds one executable.

UltimateRDP works through a Remote Desktop Gateway.

UltimateRDP has a deliberately narrow scope:

- One direction only, from the local computer to the session. For the other direction, keep using copy and paste.
- Full desktop sessions only. RemoteApp is not supported.
- The session always starts in a window, even if the .rdp file asks for full screen. You can switch to full screen afterwards. In full screen, you can only drop from a monitor that the session does not cover.

## Install

Each zip holds a single `.exe` file.

### On the server

1. Download the `UltimateRDP.Agent` zip from the [Releases page](https://github.com/freeKC/ultimaterdp/releases).
2. Unzip it to a folder that every user can read, such as `C:\Program Files\UltimateRDP`.
3. From an elevated command prompt, run:

   ```
   "C:\Program Files\UltimateRDP\UltimateRDP.Agent.exe" --install-all-users
   ```

   The agent then starts with each user session. Users who are already signed in get it at their next sign in. Running the command again changes nothing. If you had installed the agent for your own account with `--install`, the command also removes that entry and says so, so that the agent does not start twice in your session. It refuses an executable inside a user profile, such as a Downloads folder, because other users may not be able to read it.

To install it for your own account only, run the same command with `--install` from a normal prompt. This also starts the agent at once.

Other options:

- `--uninstall` stops the agent from starting with your session and stops the running copy. Run it from an elevated prompt to remove the all users entry as well.
- `--status` prints the version, the `Run` entries under `HKEY_CURRENT_USER` and `HKEY_LOCAL_MACHINE`, the settings file and whether the agent runs in your session. It changes nothing.
- `--help` lists the options.

To update on a server, stop every copy of the agent from an elevated prompt with `taskkill /im UltimateRDP.Agent.exe /f`, replace the executable, then let the users sign in again. To update your own copy, exit it from its tray menu or with `--uninstall`, replace the executable, then install it again. The client and the agent must have the same version.

### Sessions opened with the Microsoft client

On a server, most users may connect with the Microsoft client (mstsc), which has no UltimateRDP channel. For them the agent stays out of sight and does nothing visible:

- It shows no icon, no window and no message. Its tray icon appears only while an UltimateRDP client is connected to the session, and goes away when that client disconnects. The `trayIcon` setting below changes this.
- While it waits, it uses a few megabytes of memory and no measurable processor time. It tries to open its channel every ten seconds, and at once when a client connects to the session, so that a user who reconnects with the UltimateRDP client finds the agent ready.
- Its log gets one line when it starts and at most one line per hour while it waits.
- It survives disconnections and reconnections, from either client. At sign out it releases the mouse button if a drop was in progress, closes the channel and exits, without delaying the sign out.
- An unexpected error never shows a dialog: it is written to the log, and the agent either goes on or exits quietly.

### On your computer

1. Download the `UltimateRDP` zip from the [Releases page](https://github.com/freeKC/ultimaterdp/releases).
2. Unzip it anywhere. There is nothing to install. UltimateRDP writes nothing to the registry.
3. Start it with your connection file:

   ```
   UltimateRDP.exe path\to\connection.rdp
   ```

   You can also double click `UltimateRDP.exe` and pick the .rdp file in the dialog.

## Use

### Sessions and tabs

Each session opens in its own tab. The status bar and the menus follow the active tab. The File menu holds:

- **Open .rdp...** (Ctrl+O): opens a connection file in a new tab.
- **Recent**: the last ten connection files. **Clear recent** empties the list.
- **Reopen in new tab**: opens the file of the active tab once more, in a second tab.
- **Close tab** (Ctrl+W): disconnects the active session and closes its tab.
- **Log off the remote session**: ends your Windows session on the server. See "Ending the session" below.
- **Forget saved credentials**: see "Saved credentials" below.
- **Quit**: disconnects every session and closes the window.

The Session menu holds Connect, Disconnect, Look for the agent again and Cancel the transfer. See "Large files" below.

### Ending the session

Quit, Close tab and Session, Disconnect only disconnect, as the Microsoft Remote Desktop client does. Your Windows session keeps running on the server, with its applications, and you find it again at the next connection.

To really end the session, use File, Log off the remote session. The entry is available once the status bar says "Agent ready". The client asks for a confirmation, then the agent logs off the session, as the Sign out command of the Start menu would. Applications with unsaved work ask what to do, in the session, before it closes. The status bar says "Logging off the remote session...", then shows the disconnection.

### Dragging into the session

1. Start the client. It connects with the settings of the .rdp file. Log on as usual.
2. Watch the status bar at the bottom of the window. The agent label goes from "Agent: waiting" to "Agent ready (v0.4.2)".
3. Drag files or folders from Explorer onto the session. A blue banner at the top of the session says "Drop here to send the files into the session".
4. Move over the remote application you want. It shows its own drop feedback, for example a highlighted folder or an attachment area.
5. Release the button on the target. The remote application receives the files as a normal drop.

A progress window appears at every drop. The window "Drop into the session" opens over the session as soon as you release the button. It shows the dropped items, a bar that runs without a value, "Dropping into the session..." and the name of the remote computer. Then it shows the result, such as "Dropped on Inbox (copy)" or "Nothing under the cursor accepted the files.", and closes by itself three seconds later, unless the mouse is over it. When the drop fails, it shows the reason in red and stays open until you click Close. For large files the same window shows the transfer first, see "Large files" below. The window never takes the keyboard from the session, stays above the main window, and stays open when you switch tabs. Closing the tab of the session closes it.

If nothing under the cursor accepts the drop, the drop is ignored and the status bar says "Nothing under the cursor accepted the files." The agent never copies files on its own.

When the target is Explorer, Explorer shows its usual Windows copy dialog in the session. It shows the progress and asks what to do when a file already exists.

UltimateRDP only offers copy and link to the remote application. It never offers a move. Ctrl, Shift and Alt are not passed to the session during a drag.

### Large files

A remote application reads a file through drive redirection in small blocks, with one round trip over the network per block. On a connection with some latency this is slow: in one measurement, 3.6 MB/s on a link that carried 9.7 MB/s. For large files UltimateRDP therefore sends the files through its own channel, in a continuous stream, before the drop.

By default this happens when the dropped files and folders hold 10 MiB or more. The agent setting "Fast transfer of large files" changes it, see "Agent tray menu and settings" below.

What you see:

1. The drag over the session works as usual: the remote application shows its drop feedback.
2. When you release the button, the drag ends in the session without a drop, and the window "Drop into the session" shows the transfer. The status bar shows the progress too: "Transferring 3 file(s): 42 % (12.3 MB/s, 1 min left)".
3. Once every file is in the session, the agent drops the copies where you released the button, and the status bar says "Dropped on ..." as usual.

During a transfer, the window "Drop into the session" shows the name of the file being sent with "file 2 of 3", a progress bar, a line such as "42 %, 2.6 MB/s, 1 min 40 s left", and the name of the remote computer. While the client lists the files, the bar runs without a value and the line says "Preparing...". Its Cancel button stops the transfer, as Session, Cancel the transfer does. After a success it says "Transferred 3 file(s) in 3 min 13 s at 2.6 MB/s, dropping into the session...", then shows the result of the drop and closes by itself three seconds later, unless the mouse is over it. After a failure or a cancel it shows the reason in red and stays open until you click Close. Closing the tab of the session cancels the transfer and closes the window.

The copies go to `%LOCALAPPDATA%\UltimateRDP\staging` in the session and are deleted one hour after the drop. The agent checks the size and the SHA-256 checksum of every file. If anything goes wrong, nothing is dropped and the status bar says why. Session, Cancel the transfer stops it; the agent then deletes what it received.

Things to know:

- One transfer at a time. While one runs, a new drag over the session is refused, and the progress window says "Wait for this transfer before dropping more files." for five seconds.
- Drive redirection must still be on: the drag over the session uses it.
- If you release the button very quickly, before the agent answered the start of the drag, the files go through drive redirection as before.
- Empty folders inside a dropped folder are not created in the session.
- The agent must run version 0.4.1 or later, and the client too. The progress window is new in client 0.4.2 and works with an agent 0.4.1. With an older client or agent, every drop goes through drive redirection.

The client sends the files in chunks of 32 KiB. The setting `transferChunkBytes` in `%LOCALAPPDATA%\UltimateRDP\client.json` changes that size, from 4096 (4 KiB) to 262144 (256 KiB). A value outside these bounds is brought back within them. The setting is only editable in the file, and applies once the client restarts. Leave it alone unless a transfer fails at its start.

```json
{
  "transferChunkBytes": 32768
}
```

### Status bar messages

Messages of a drag:

- **"Dragging 2 item(s) into the session..."**: the agent is replaying the drag.
- **"Starting the drag in the session..."**: the agent needs a moment to start the drag.
- **"Copying 2 item(s) into the session before the drag starts..."**: the agent copies the files into the session first, because "Stage files in the session before the drop" is on.
- **"Dropped on Inbox (copy)"**: the remote window accepted the drop. It names the window and says copy or link.
- **"Nothing under the cursor accepted the files."**: the window under the cursor did not take the drop. Nothing was copied.
- **"The agent did not acknowledge the drag"**: no answer from the agent within a second. See Troubleshooting.
- **"Drop refused"** followed by the reason from the agent. For example "Drop refused: The previous drop is still being delivered in the session. Try again once it is done."
- **"Drop refused: the drag did not start in the session. Drag the files again."**: the client got the drop without a drag in progress for the same files.
- **"Cancelled"** followed by the reason, for example "Cancelled: The drag was cancelled: Escape was pressed in the session."

The progress window that appears at every drop shows the result of the drop too, and the progress of a transfer with more detail, see "Dragging into the session" and "Large files" above. The status bar keeps its texts.

Messages of a transfer of large files:

- **"Preparing the transfer of 2 item(s)..."**: the client lists the files to send.
- **"Transferring 3 file(s): 42 % (12.3 MB/s, 1 min left)"**: the files are on their way. The rate covers the last five seconds; MB means a million bytes.
- **"Transferred 3 file(s) at 12.3 MB/s, dropping..."**: every file is in the session, and the agent drops the copies.
- **"Transfer cancelled."**: you cancelled the transfer from the Session menu or the progress window.
- **"A transfer is in progress. Wait for its end, or cancel it from the Session menu."**: a new drag was refused during a transfer.
- **"Transfer failed"** followed by the reason from the agent, for example "Transfer failed: Data was lost or damaged on the way. Try again."
- **"Drop refused: There is not enough free space in the session: 1,234 MB needed, 800 MB free."**: the disk of the session cannot hold the files.
- **"Cannot read"** followed by a file name and the Windows reason: the client could not read a file on your computer.
- **"The agent stopped answering during the transfer."**: no acknowledgement for a minute.
- **"The transfer stopped: the session was disconnected."**: the connection ended during the transfer.

Messages of the connection:

- **"No agent on the remote computer"**: the client sent hello messages for sixty seconds and got no answer. Start the agent in the session, then use Session, Look for the agent again.
- **"This .rdp file does not redirect drives: drops will not work."**: the .rdp file does not share any local drive. Turn on drives in the .rdp file and reconnect.
- **"The session sees no redirected drive: drops will not work."**: the agent answered, but no `\\tsclient` drive is reachable from the session. The server may block drive redirection.
- **"Drop refused: not connected."**: there is no session yet.
- **"Drop refused: the UltimateRDP agent has not answered yet."**: wait for "Agent ready".
- **"Drop refused: the UltimateRDP agent is not running in the remote session."**: start the agent in the session.
- **"Drop refused"** with a path: only files and folders on a local drive letter can be dropped. Network (UNC) paths are refused, because the session reaches your files through drive redirection.
- **"Drop failed"**: the agent could not finish. The rest of the message gives its reason.

Messages of a log off:

- **"Logging off the remote session..."**: the agent accepted the request. The disconnection follows once the session is closed. If an application asks to save your work, answer it in the session.
- **"The agent did not answer the log off request."**: no answer within five seconds. The session is still open; try again, or sign out from the Start menu of the session.
- **"Log off failed"**: Windows refused to log off the session. The rest of the message gives the Windows reason.

These examples are in English. The status bar uses the language of the client, see "Language" below.

### View menu

- **Resize session to window**: the default. The session resolution follows the window size.
- **Scale to window**: the session keeps its resolution and the picture is scaled to the window.
- **Scale factor**: Auto, or 100 % to 200 %. Auto follows the monitor that holds the window. A change applies to a new connection, and the client offers to reconnect.
- **Full screen** (F11, or Ctrl+Alt+Pause): toggles full screen. The connection bar is shown and can be pinned.
- **Use all monitors**: the session covers every monitor in full screen. It applies to a new connection.
- **Windows keys**: where key combinations such as Alt+Tab go. "On this computer", "In the session", or "In the session when full screen", which is the default.
- **Language**: see below.

Your View choices are saved for the sessions you open later. When the .rdp file sets `desktopscalefactor`, `use multimon` or `keyboardhook`, the file wins.

### Language

The client and the agent speak English, French, German, Spanish, Italian, Portuguese and Dutch.

The View, Language submenu lists "Automatic (Windows language)" and the seven languages, each named in its own language. A choice applies at once to the menus, the status bar and the tabs, and the client remembers it. Automatic, the default, follows the display language of Windows, and uses English when Windows runs in another language.

The choice is the `language` setting in `%LOCALAPPDATA%\UltimateRDP\client.json`: `auto`, `en`, `fr`, `de`, `es`, `it`, `pt` or `nl`.

```json
{
  "language": "fr"
}
```

The client tells the agent its language, and the agent writes the messages it sends to the status bar in that language. The tray menu of the agent follows the display language of Windows on the server.

Some texts come from Windows and follow its own language: the Yes and No buttons of the questions, the reason of a disconnection, and Windows error messages. The logs are always in English.

### Saved credentials

Before connecting, the client reads the generic credential `TERMSRV/<host>` from the Windows Credential Manager. When it exists and holds a password, the client applies it and turns off the prompt for that connection. UltimateRDP never writes a credential. You can create one with:

```
cmdkey /generic:TERMSRV/<host> /user:<domain\user> /pass
```

`cmdkey` then asks for the password.

The "Remember me" box of the Windows credential prompt saves a different credential type, a domain password. Windows keeps that password for itself, so UltimateRDP cannot read it. The Windows prompt itself may still use it.

File, Forget saved credentials deletes both kinds of credential for the host of the active tab. The open session is not affected, and the next connection asks again.

## Agent tray menu and settings

By default the tray icon of the agent is visible only while an UltimateRDP client is connected to the session. Its menu holds:

- **Stage files in the session before the drop**: off by default. When on, the agent copies the files into the session before the drag starts, and the remote application receives local paths instead of `\\tsclient` paths. Turn it on for applications that refuse network paths. Large files take time to copy before the drag starts. When the files come through the channel, see below, they arrive as local copies anyway.
- **Fast transfer of large files**: Auto, Always or Never. Auto, the default, sends the files through the UltimateRDP channel before the drop when they hold 10 MiB or more. Always does it for every drop, Never for none: the remote application then reads the files through drive redirection, as in version 0.3.
- **Open log folder**, **About** and **Exit**.

The menu follows the display language of Windows on the server.

Staged copies go to `%LOCALAPPDATA%\UltimateRDP\staging\<id>` in the session. Each folder is deleted one hour after its drop, because the target may still be reading the files. When the agent starts, it deletes the folders that are older than one hour.

The agent keeps its settings in `%LOCALAPPDATA%\UltimateRDP\agent.json` on the server:

```json
{
  "logLevel": "debug",
  "stageFiles": false,
  "fastTransfer": "auto",
  "fastTransferMinBytes": 10485760,
  "trayIcon": "auto"
}
```

| Setting | Values | When absent | Meaning |
| --- | --- | --- | --- |
| `stageFiles` | `true`, `false` | `false` | The tray menu entry "Stage files in the session before the drop". |
| `fastTransfer` | `auto`, `always`, `never` | `auto` | The tray menu entry "Fast transfer of large files". |
| `fastTransferMinBytes` | a number of bytes | `10485760` (10 MiB) | The size from which `auto` sends the files through the channel, counted over all the dropped items. Only editable in the file. |
| `logLevel` | `debug`, `info`, `warn`, `error` | `info` | The lowest level written to the log. Only editable in the file. |
| `trayIcon` | `auto`, `always`, `never` | `auto` | `auto` shows the tray icon only while an UltimateRDP client is connected to the session. `always` shows it from the start of the agent. `never` never shows it: the agent is then controlled with `--uninstall` and its `Run` entry only. Only editable in the file. |

An unknown value gives the default and a warning in the log. Restart the agent after editing the file by hand.

## How it works

The client hosts the Microsoft Remote Desktop ActiveX control and registers itself as a drop target on its windows. The agent runs in your session and listens on a static virtual channel named `ULTRDP`. For small files the client sends the local paths and the cursor position, and the session reads the files from the redirected drives. Large files are sent through the channel, as described after the steps below.

1. When the drag first moves over the session, the client converts the cursor position to remote desktop coordinates and sends `dragEnter` with the paths.
2. The agent opens an invisible proxy window over the whole remote screen. It moves the remote cursor to the point and simulates a left button press on that window. The press starts a real OLE drag inside the session, with the files as `\\tsclient\<drive>\...` paths, or as staged copies.
3. The agent answers `dragAck`. Without that answer within a second, the client stops the drag.
4. While you move, the client sends `dragMove`. The agent moves the remote cursor, so the application under it sees the drag and shows its feedback.
5. When you release the button, the client sends `drop`. The agent moves the cursor to the final point and releases the simulated button there. If you leave the session instead, the client sends `dragLeave` and the agent cancels the drag.
6. The agent reports `dropResult` with the effect: `copy` or `link` when the target accepted, or `none` when nothing under the cursor accepted the files. With `none`, the drop is ignored. The client shows the result in the status bar.

The drag offers copy and link only, never move. Modifier keys are recorded in the logs but not injected.

For large files, the `dragEnter` carries the size of each item, and the agent answers `dragAck` with `transfer: true` when they must come through the channel. At step 5 the client then sends `drop` with `deferred: true`: the agent cancels the drag in the session and keeps the point. The client sends the files as base64 chunks of 256 KiB with at most 4 MiB not yet acknowledged, the agent writes them into its staging folder and checks the size and SHA-256 of each one, answers `transferResult`, then drops the copies at the kept point with a short drag, and reports `dropResult` as in step 6.

To log off, the client sends `logoff`. The agent answers `logoffResult`, waits half a second so that the answer reaches the client, then asks Windows to log off the session. It never forces applications that have unsaved work.

Messages are JSON objects sent as ASCII lines, and each side detects at run time how the control maps channel bytes to characters. See [docs/design.md](docs/design.md) for the details.

## Troubleshooting

- **The agent never becomes ready.** Run `UltimateRDP.Agent.exe --status` in the session: it says whether the agent runs there and where it is set to start from. The tray icon only appears once the client is connected, so its absence before that is normal. A server policy may also block virtual channels: the agent log then has a warning that it cannot open the channel.
- **"The agent did not acknowledge the drag".** The agent did not answer the start of the drag within a second. A slow connection or a busy agent can cause it, and so can an agent that exited or restarted in the session, which the client cannot see otherwise. The client then looks for the agent again: the agent label goes to "Agent: waiting", and back to "Agent ready" when the agent answers. When the status bar says "The session does not accept simulated input", the session may be locked, or a policy may block simulated input. Please report it with both logs.
- **The target shows no drop feedback, or the status bar says "Nothing under the cursor accepted the files."** The application under the cursor may not accept files at that place. Some applications also refuse network paths such as `\\tsclient\C\...`. For those, turn on "Stage files in the session before the drop" in the agent tray menu.
- **The first drop on Explorer takes several seconds.** Explorer reads the redirected drive the first time, which can be slow. Later drops are usually faster.
- **Ctrl+W or Ctrl+O does nothing.** When the session has the keyboard, these keys go to the remote computer, like every other key. Use the File menu instead.
- **My applications are still open on the server after Quit.** This is expected: Quit and Close tab disconnect without ending the session. Use File, Log off the remote session.
- **The texts are in the wrong language.** Choose the language in View, Language. A message from the agent follows the language the client announced at connection time; after a language change, the next message uses the new one.
- **Drops are refused because of the drives.** Turn on drive redirection in the .rdp file, under Local Resources, More, Drives. On the server, check the policy "Do not allow drive redirection".
- **The drop lands on the wrong window.** Coordinate scaling after a window resize, or full screen over several monitors, can be the cause. Please report it with both logs.
- **A transfer of large files fails, or is slow.** The status bar gives the reason. Both logs end each transfer with a summary line: files, bytes, seconds and MB/s. The client log also gives the length of the first chunk line it sent, and the agent log has a line for the first chunk of each file it received. When the status bar says "The agent did not acknowledge the first data of the transfer.", the data did not get through: the client log then gives the byte model, the chunk size, the length of the lines and the number of lines sent. A smaller `transferChunkBytes` may help, see "Large files". To go back to drive redirection, choose Never in the agent tray menu, Fast transfer of large files.
- **Where the logs are.** The client writes `%LOCALAPPDATA%\UltimateRDP\logs\client-yyyyMMdd.log` on your computer. The agent writes `agent-yyyyMMdd.log` in the same folder on the server. Both keep fourteen days of logs. The client opens its folder from Help, Open log folder, and the agent from its tray menu, Open log folder.

## Build from source

You need the .NET 10 SDK on Windows.

```
dotnet build UltimateRDP.sln -c Release
dotnet test UltimateRDP.sln
```

To publish a self contained single file executable without symbols:

```
dotnet publish src/UltimateRDP.Client -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false
dotnet publish src/UltimateRDP.Agent -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false
```

Releases are built by GitHub Actions when a `v*` tag is pushed. The workflow builds, tests, publishes both applications and attaches the two zips to the release.

The executables of a release are signed by APROGSYS, and Windows shows this publisher in its prompts. An executable you build yourself is not signed.

## Security notes

- The agent only reads from `\\tsclient\`, that is from the drives you chose to share. The agent itself only writes to its staging folder in your session, when staging is on or when files come through the channel. It refuses any file name that would leave that folder. The application you drop on decides where the files go. The agent does not run as a service and has no privilege beyond your own.
- The agent simulates input only inside your own session, and only between the start and the end of a drag. It only uses the left mouse button. It injects the press only once its own proxy window is under the cursor, so the press never reaches another application. To cancel, it posts Escape to its proxy window instead of simulating a key.
- The agent never offers a move. A move accepted in the session could delete the file on your local disk through the redirected drive.
- Messages carry file paths, coordinates, modifier key flags and window names. File contents go through the channel only for a transfer of large files, inside the encrypted Remote Desktop connection.
- The client never writes credentials and never logs them. It reads the generic credential `TERMSRV/<host>` when one exists, and hands it to the Microsoft control. Other credential prompts come from the Microsoft control.

## Status

Version 0.4.2 is a test release.

Verified in a real Remote Desktop session, with version 0.2.1:

- The virtual channel between the client and the agent, with one byte per character in both directions.
- The transfer through drive redirection to the remote desktop.
- The live drag into a remote application.

The log off command and the translations are new in 0.3.0. The fast transfer of large files is new in 0.4.0. Its first test against a real server failed. Version 0.4.1 fixes the two defects found there; with it, a file of 496 MB reached a real session in 193 seconds. Version 0.4.2 adds a progress window at every drop and only changes the client.

Feedback that helps:

- Whether the live drag works in your session, and with which applications.
- Whether the log off works for you, and the wording of your language.
- The rate of a transfer of large files on your connection, from the summary line of both logs.

- How long the first drop on Explorer takes.
- Whether drops land in the right place in full screen, with one monitor or all monitors.
- Whether saved credentials work, and which kind you have in the Credential Manager.

Please send it through [GitHub issues](https://github.com/freeKC/ultimaterdp/issues), with both logs.

## Credits

UltimateRDP is developed and published by [APROGSYS](https://www.aprogsys.com/). The source code is on [GitHub](https://github.com/freeKC/ultimaterdp), under the MIT license.

## License

MIT. See [LICENSE](LICENSE).
