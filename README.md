# UltimateRDP

[![CI](https://github.com/freeKC/ultimaterdp/actions/workflows/ci.yml/badge.svg)](https://github.com/freeKC/ultimaterdp/actions/workflows/ci.yml)

UltimateRDP lets you drag files or folders from your desktop and drop them into a Remote Desktop session, on the application under the cursor. Explorer, a mail client, a browser or a business application receives them as if you had dropped them inside the session. When nothing under the cursor accepts them, the drop is ignored. UltimateRDP is made of a client that hosts the Microsoft Remote Desktop control, a small agent in the session, a virtual channel between the two, and the standard drive redirection. Large files travel through the virtual channel before the drop, which is faster than drive redirection, and from version 0.6.0 through a direct TLS connection when the network allows it. From version 0.9.0 files also go the other way, from the session to your computer, through the same direct connection: see "Sending files from the session to your computer".

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

UltimateRDP works through a Remote Desktop Gateway. The direct connection for large files does not, and the files then go through the channel: see "Direct connection for large files".

UltimateRDP has a deliberately narrow scope:

- Drops go one way, from the local computer to the session. From version 0.9.0 the session sends files to your computer with its Send To menu instead of a drop, through the direct connection only: see "Sending files from the session to your computer".
- Full desktop sessions, and from version 0.8.0 RemoteApp programs: see "RemoteApp programs".
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

RemoteApp sessions do not use the `Run` entry. To have the agent there too, see "RemoteApp programs" below.

Other options:

- `--uninstall` stops the agent from starting with your session and stops the running copy. Run it from an elevated prompt to remove the all users entry as well.
- `--send <paths>` sends files and folders of the session to the computer of the UltimateRDP client connected to it. The Send To shortcut runs it; see "Sending files from the session to your computer".
- `--status` prints the version, the `Run` entries under `HKEY_CURRENT_USER` and `HKEY_LOCAL_MACHINE`, the settings file, the machine settings file and whether the agent runs in your session. It also says whether the session is a RemoteApp session, and whether the `SysProcs` value and the scheduled task of "RemoteApp programs" exist. It changes nothing.
- `--help` lists the options.

To update on a server, stop every copy of the agent from an elevated prompt with `taskkill /im UltimateRDP.Agent.exe /f`, replace the executable, then let the users sign in again. To update your own copy, exit it from its tray menu or with `--uninstall`, replace the executable, then install it again. The client and the agent must have the same version.

### Sessions opened with the Microsoft client

On a server, most users may connect with the Microsoft client (mstsc), which has no UltimateRDP channel. For them the agent stays out of sight and does nothing visible:

- It shows no icon, no window and no message. Its tray icon appears only while an UltimateRDP client is connected to the session, and goes away when that client disconnects; in a RemoteApp session it never appears. The `trayIcon` setting below changes this.
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
- **Always ask for the password**: from version 0.9.0, every connection asks for the password instead of using a saved credential. See "Saved credentials" below.
- **Quit**: disconnects every session and closes the window.

The Session menu holds Connect, Disconnect, Look for the agent again and Cancel the transfer. See "Large files" below.

### Ending the session

Quit, Close tab and Session, Disconnect only disconnect, as the Microsoft Remote Desktop client does. Your Windows session keeps running on the server, with its applications, and you find it again at the next connection.

To really end the session, use File, Log off the remote session. The entry is available once the status bar says "Agent ready". The client asks for a confirmation, then the agent logs off the session, as the Sign out command of the Start menu would. Applications with unsaved work ask what to do, in the session, before it closes. The status bar says "Logging off the remote session...", then shows the disconnection.

### Dragging into the session

1. Start the client. It connects with the settings of the .rdp file. Log on as usual.
2. Watch the status bar at the bottom of the window. The agent label goes from "Agent: waiting" to "Agent ready (v0.4.3)".
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
- If you release the button very quickly, before the agent answered the start of the drag, the client waits for that answer, for at most a second and a half, with "Preparing..." in the progress window, so that large files still go through the UltimateRDP channel. A new drag is refused during that wait, as during a transfer.
- Empty folders inside a dropped folder are not created in the session.
- The agent must run version 0.4.1 or later, and the client too. The progress window is new in client 0.4.2 and works with an agent 0.4.1. With an older client or agent, every drop goes through drive redirection.
- From version 0.6.0 on both sides, the files go through a direct connection when the server opens its ports for it, and through the channel otherwise. From 0.7.0 on both sides, the direct connection uses several TCP connections at once. See "Direct connection for large files".

From version 0.5.0 the files travel in a denser form when your connection allows it. The agent tells the client how the data of the client reaches it. When each character arrives as two bytes, as measured on a production server, the client packs 15 bits of the file into each character instead of base64, and a file byte costs about 1.07 bytes on the channel instead of 2.67. On a channel that carries 5.8 MB/s, the transfer goes from about 2.1 MB/s to about 5.4 MB/s. When each character arrives as one byte, the client keeps base64, at about three quarters of the channel. Both sides must run 0.5.0 for the denser form; with an older agent the client sends base64 as before. The status bar and the progress window do not change; the client log says which form a transfer uses.

The client sends the files in chunks of 32 KiB. The setting `transferChunkBytes` in `%LOCALAPPDATA%\UltimateRDP\client.json` changes that size, from 4096 (4 KiB) to 262144 (256 KiB). A value outside these bounds is brought back within them. The setting is only editable in the file, and applies once the client restarts. Leave it alone unless a transfer fails at its start.

```json
{
  "transferChunkBytes": 32768,
  "transferBatchCharacters": 200000,
  "transferWindowBytes": 4194304,
  "directTransfer": "auto",
  "directConnections": 8,
  "logLevel": "info"
}
```

The client sends its messages to the agent together, at most `transferBatchCharacters` characters in one call to the Remote Desktop control: 200000 by default, from 50000 to 1048576. A value outside these bounds is brought back within them. Leave it alone unless a transfer fails at its start.

The setting `logLevel` of the same file is `info` by default. Set it to `debug` to log every message the client sends to the agent and every call it makes to the Remote Desktop control, then restart the client.

The client keeps at most `transferWindowBytes` bytes of a transfer sent and not yet acknowledged by the agent: 4194304 (4 MiB) by default, from 1048576 (1 MiB) to 33554432 (32 MiB). A value outside these bounds is brought back within them. The agent acknowledges every 2 MiB, so a window below 2 MiB plus one chunk is raised to that; the log gives the window used at the start of each transfer. Change it only to find out what limits a slow transfer, see below.

#### Measuring a slow transfer

From version 0.4.4 both logs end each transfer with summary lines that say where the time went. Install 0.4.4 on both sides for the full picture; with an older client or agent, the other side still logs its own lines.

The client log has two lines:

```
Transfer <id> channel calls: 2410 calls, 482000000 characters, 9100 ms inside SendOnVirtualChannel, 3.78 ms per call on average, 41.20 ms at most; 5 % of the 190000 ms from the first call to the last, 5 % of the 193000 ms of the transfer.
Transfer <id> acknowledgements: 237 matched a call, round trip 1480 ms on average, 20 ms at least, 1900 ms at most; 3700000 bytes in flight on average when one came, 4194304 at most, 88 % of the window of 4194304 bytes.
```

The agent log has one line per connection about the connection, and two lines per transfer:

```
Transfer <id> channel reads: 302000 reads with data and 12 empty, 482000000 bytes, 1596 bytes per read on average; 301000 of 1600 bytes, 1000 shorter, 0 longer; blocked in the read 178000 ms, processing 14000 ms (9000 ms handling messages), over 193000 ms; read mode wts.
Transfer <id> session counters: 0 bytes, 0 frames and 0 compressed bytes in, 0 bytes out, from WTSSessionInfoEx (zero when Windows does not count them).
```

How to read them:

- **The client spends well over half of the time inside SendOnVirtualChannel.** The Remote Desktop control limits the transfer on your computer.
- **The agent spends nearly all of the time processing, and little blocked in the read.** The agent limits the transfer; the time handling messages covers the JSON, the decoding of the chunks, the checksum and the file write.
- **The client calls take a small share and the agent waits in the read nearly all of the time.** The data waits between the two, in the Remote Desktop connection. When the bytes in flight stay near the window, try a transfer with `transferWindowBytes` at 16777216. If the rate does not change and the round trip grows four times, the flow control of the connection is the limit. If the rate grows, keep the larger window.

Whether the connection uses UDP shows in the connection bar of the Remote Desktop session: click the signal icon for the connection information.

### Sending files from the session to your computer

From version 0.9.0, files and folders of the session go to your computer through the direct connection, at the same rate as large files in the other direction. Both sides must run 0.9.0, and the direct connection must work: see "Direct connection for large files".

In the session:

- **Send To.** Right click files or folders in Explorer, then Send to, **My PC (UltimateRDP)**. The shortcut exists only while an UltimateRDP client that receives files is connected, and goes when it disconnects.
- **The tray menu of the agent**, Send files to my PC..., picks files in a dialog. The tray icon does not show in RemoteApp sessions.
- **The command line**: `UltimateRDP.Agent.exe --send "C:\Users\me\Documents\report.pdf" "C:\Users\me\Pictures\Trip"`.

If the files cannot be sent, a message in the session says why, for example that no client that receives files is connected.

On your computer:

- The files go to your Downloads folder. While they come, they are in a hidden folder `.ultimaterdp-<id>.part` inside it, and they appear once every file was checked with its SHA-256. A file never replaces another: when `report.pdf` exists, the new one is `report (2).pdf`, and a folder becomes `Photos (2)`.
- A window "Files from the session" shows the progress, with Cancel. At the end it says where the files are, and Open folder shows them in Explorer.
- Every received file gets the mark that Windows gives to downloads, so that SmartScreen and Office treat it as a file from the Internet. UltimateRDP never opens or runs a received file.
- When the direct connection cannot be used, for example through a Remote Desktop Gateway or with ports closed by a firewall, nothing is sent and the window says so. There is no slower path; copy the files in the session to a drive of your computer under `\\tsclient` instead.
- Names that Windows refuses, a path that would leave the folder, or not enough free space stop the transfer, and nothing is kept.

Settings in `%LOCALAPPDATA%\UltimateRDP\client.json`, only editable in the file:

```json
{
  "receiveFiles": "auto",
  "receiveFolder": "%USERPROFILE%\\Desktop\\From the server"
}
```

| Setting | Values | When absent | Meaning |
| --- | --- | --- | --- |
| `receiveFiles` | `auto`, `never` | `auto` | `never` refuses the files of the session. `directTransfer` at `never` refuses them too, since only the direct connection carries them. |
| `receiveFolder` | a full path | your Downloads folder | Where the files go. Environment variables such as `%USERPROFILE%` are expanded; the folder is created when missing. |

On the server, `sendToClient` in agent.json, `auto` by default, set to `never` turns the sending off for a user or, in the machine settings, for everybody. `directMaxMegabytesPerSecond` also limits what the agent sends, which protects the outbound link of the server. From version 0.9.2, `sendMaxMegabytesPerSecond` limits the sending alone, so that files from the clients keep the full rate: for example `"sendMaxMegabytesPerSecond": 85` in the machine settings when the outbound link of the server carries about 1 Gbit/s.

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

From version 0.9.0 the client asks for the password instead, and reads no saved credential, when any of these says so:

- the option `--ask-credentials` on the command line;
- File, **Always ask for the password**, which the client remembers as `"credentials": "ask"` in client.json (`saved` by default);
- the line `prompt for credentials:i:1` in the .rdp file, as the Microsoft client does.

The user name of the .rdp file stays filled in, and the prompt does not offer to save the password. The client log says which of the three decided, and never holds a password.

This changes what happens with a .rdp file that already holds `prompt for credentials:i:1`: version 0.8.0 used the saved credential and connected without a prompt; version 0.9.0 asks for the password. Remove the line from the file, or set it to 0, to get the saved credential back.

The "Remember me" box of the Windows credential prompt saves a different credential type, a domain password. Windows keeps that password for itself, so UltimateRDP cannot read it. The Windows prompt itself may still use it.

File, Forget saved credentials deletes both kinds of credential for the host of the active tab. The open session is not affected, and the next connection asks again.

## Agent tray menu and settings

By default the tray icon of the agent is visible only while an UltimateRDP client is connected to the session. Its menu holds:

- **Send files to my PC...**: from version 0.9.0, sends the chosen files to the computer of the client. See "Sending files from the session to your computer".
- **Stage files in the session before the drop**: off by default. When on, the agent copies the files into the session before the drag starts, and the remote application receives local paths instead of `\\tsclient` paths. Turn it on for applications that refuse network paths. Large files take time to copy before the drag starts. When the files come through the channel, see below, they arrive as local copies anyway.
- **Fast transfer of large files**: Auto, Always or Never. Auto, the default, sends the files through the UltimateRDP channel before the drop when they hold 10 MiB or more. Always does it for every drop, Never for none: the remote application then reads the files through drive redirection, as in version 0.3.
- **Direct connection for large files**: Auto or Never. Auto, the default, offers the direct connection for a fast transfer, with the channel as the fallback. Never keeps every fast transfer in the channel. See "Direct connection for large files".
- **Client drives on drive letters**: Auto or Never. Never, the default, leaves the drives of your computer as `\\tsclient\C` and so on. Auto gives them drive letters of the session while an UltimateRDP client is connected. See "Client drives on drive letters" below.
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
  "directTransfer": "auto",
  "directPortRange": "3400-3449",
  "directConnections": 8,
  "directMaxMegabytesPerSecond": 0,
  "sendMaxMegabytesPerSecond": 0,
  "trayIcon": "auto",
  "channelRead": "wts",
  "mapClientDrives": "never",
  "sendToClient": "auto"
}
```

| Setting | Values | When absent | Meaning |
| --- | --- | --- | --- |
| `stageFiles` | `true`, `false` | `false` | The tray menu entry "Stage files in the session before the drop". |
| `fastTransfer` | `auto`, `always`, `never` | `auto` | The tray menu entry "Fast transfer of large files". |
| `fastTransferMinBytes` | a number of bytes | `10485760` (10 MiB) | The size from which `auto` sends the files through the channel, counted over all the dropped items. Only editable in the file. |
| `directTransfer` | `auto`, `never` | `auto` | The tray menu entry "Direct connection for large files". |
| `directPortRange` | a range such as `3400-3449`, or one port | `3400-3449` | The TCP ports of the direct connection, see "Direct connection for large files". Only editable in the file. |
| `directConnections` | 1 to 16 | `8` | How many TCP connections a direct transfer may use at most. Only editable in the file. |
| `directMaxMegabytesPerSecond` | 0, or a number of MB/s | `0` | A limit on the rate of a direct transfer, all its connections together; 0 means no limit. Only editable in the file. |
| `sendMaxMegabytesPerSecond` | 0, or a number of MB/s | `0` | From version 0.9.2, a limit on what the agent sends to the computer of the client, all the connections of a send together. 0 leaves `directMaxMegabytesPerSecond` as the limit of a send too. Only editable in the file. |
| `logLevel` | `debug`, `info`, `warn`, `error` | `info` | The lowest level written to the log. Only editable in the file. |
| `trayIcon` | `auto`, `always`, `never` | `auto` | `auto` shows the tray icon only while an UltimateRDP client is connected to the session. `always` shows it from the start of the agent. `never` never shows it: the agent is then controlled with `--uninstall` and its `Run` entry only. Only editable in the file. |
| `mapClientDrives` | `never`, `auto` | `never` | The tray menu entry "Client drives on drive letters". |
| `sendToClient` | `auto`, `never` | `auto` | Whether files of the session may be sent to the computer of a client that receives them. Only editable in the file. |
| `channelRead` | `wts`, `file` | `wts` | How the agent reads the channel. `wts` reads it with WTSVirtualChannelRead, 1600 bytes at a time. `file` is an experiment: the agent reads the file handle of the channel with a buffer of 64 KiB, takes out the channel headers it may find, logs which framing it found, and goes back to `wts` for the connection at the first inconsistency, with a warning. Only editable in the file. |

An unknown value gives the default and a warning in the log. Restart the agent after editing the file by hand. When the tray menu changes a setting, it writes that key into your agent.json and keeps your other keys; comments in the file are not kept.

### Machine wide settings

An administrator can set defaults for every user of the server in `%ProgramData%\UltimateRDP\agent.json`, with the same keys. The agent reads that file first when it exists, then the agent.json of the user, whose keys win one by one. A key that the user does not set, or sets to `null`, comes from the machine file. The agent never writes the machine file; the tray menu only writes the file of the user. For example, to turn the drive letters on for everybody:

```json
{
  "mapClientDrives": "auto"
}
```

Since a user may create files in `%ProgramData%`, the agent only reads the machine file when its owner is the Administrators group, SYSTEM or TrustedInstaller, as for a file created from an elevated prompt. A file of another owner is left out, with a warning in the log that gives the `icacls` command to fix it. The start line of the agent log lists the keys that come from the machine file, and `--status` says whether the file exists. Restart the agents, or let the users sign in again, after a change.

### Client drives on drive letters

With `mapClientDrives` at `auto`, the agent maps each drive that your computer redirects, `\\tsclient\C`, `\\tsclient\D` and so on, to a drive letter of the session as soon as an UltimateRDP client is connected, so that programs which want a drive letter can use your files.

- A drive keeps its own letter when that letter is free in the session, and gets the first free letter from Z down otherwise. C, the system drive of the session, is usually taken, so your C: drive often becomes Z:.
- The agent never replaces a letter in use, whether a local drive or a mapping, and does not map a drive that a mapping of the session already reaches. It only maps the drives your computer redirects, and never A: or B:.
- The letters are visible to you only, in your own session, and are not remembered for the next sign in.
- They go away when the client disconnects, when you choose Never in the tray menu, and when the agent exits. If a file is open on a drive, the agent removes the letter all the same, but only a letter it made itself. A letter left by an agent that ended badly is removed at its next start, when it still points to the same drive.
- Documents, Desktop and the other folders of the profile are not redirected.
- Every failure is written to the log as a warning, and nothing is shown. The log has one line with the letters given, when the client connects.

## Direct connection for large files

From version 0.6.0, the files of a fast transfer go through a direct TLS connection between the client and the agent, outside the Remote Desktop connection, when the network allows it. Through the UltimateRDP channel, transfers measured in production reached 2 to 4 MB/s, whatever the link; the direct connection carries them at the rate of the network. When it cannot be used, the files go through the channel as before, without anything to do for the user.

From version 0.7.0, a transfer uses several TCP connections at once, eight at most by default. On the production path one TCP connection started at 90 to 180 MB/s and fell to about 20 MB/s within seconds, while four connections together carried four times as much: the limit is per connection, somewhere on the path, and several connections add up.

How it works:

1. When the agent decides a fast transfer, it listens on the first free TCP port of a range, 3400 to 3449 by default, for this transfer only. Each session uses its own port, so a range is needed on a server with many sessions.
2. Its answer to the drag, through the channel, gives the port, a random single use token, the SHA-256 of its TLS certificate, the version of the protocol and the number of connections it accepts.
3. After the drop the client connects to the host of the .rdp file on that port, within 3 seconds. It accepts only the certificate whose SHA-256 the agent gave, proves that it knows the token, and sends the list of the files.
4. Once the agent accepted that first connection, the client opens the others to the same address, one per started 32 MiB of the transfer, up to the number the agent accepts and its own setting. A connection that cannot be opened is not an error: the others carry the files.
5. The files go by blocks of 1 MiB over all the connections. The agent writes each block where it belongs in its file, checks the SHA-256 of every file as through the channel, and drops the copies.

The listener exists only while a transfer is offered or runs. It accepts at most four sockets more than the connections it offers, and drops any socket that does not finish its TLS handshake and present its token within 5 seconds. It closes 10 seconds after the drop when the first connection did not come. The certificate is created in memory by the agent at its first direct transfer; its private key is a non exportable key of the user key store, which the agent deletes when it exits. The agent still runs with the rights of the user: a port above 1024 needs no administrator right.

### Firewall rules

The administrator opens the range, inbound TCP to the server, restricted to the client addresses allowed for RDP. On the Windows firewall of the server, from an elevated PowerShell:

```
New-NetFirewallRule -DisplayName "UltimateRDP direct transfer" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 3400-3449 -RemoteAddress <allowed client addresses> -Program "C:\Program Files\UltimateRDP\UltimateRDP.Agent.exe" -Profile Any
```

Replace `<allowed client addresses>` with the same list as the rule that allows RDP, separated by commas, and the program path with the place of the agent. With `-Program`, Windows has a rule for the agent and does not ask the users when it starts to listen. To make sure that users never see such a question on the server, run `Set-NetFirewallProfile -Profile Domain,Private,Public -NotifyOnListen False`.

At the hosting provider, add the same rule to the firewall in front of the server: TCP, destination ports 3400 to 3449, source the allowed client addresses only.

If you change the range in the agent settings, change both rules with it. The connections of a transfer all use its one port, so the rules are the same as for 0.6.0.

### Fallback

The channel takes the files, as in version 0.5.2, when:

- the client connects through a Remote Desktop Gateway, or a firewall does not open the ports: the first connection fails within 3 seconds;
- the TLS handshake fails, the certificate is not the one the agent gave, or the agent refuses the token;
- a connection breaks before the agent acknowledged the first 8 MiB;
- the client or the agent runs a version before 0.6.0, one side runs 0.6.0 and the other 0.7.0, or one of them has the setting `directTransfer` at `never`;
- no port of the range is free on the server.

When a connection breaks later, the transfer stops with "The direct connection was lost. Try again." and nothing is dropped. The client log says at the info level which path a transfer took and why the direct connection was not used. The status bar and the progress window show "Direct connection" while the files go through it, and keep the preparing stage until the agent acknowledged the first data; when a file takes more than 2 seconds to open, they say "Opening ... Windows may be checking the file." until it is read.

When you cancel, the client tells the agent, waits at most a second for its answer, then closes every connection, even when a connection or the read of a file is stuck. The agent deletes what it received and logs the transfer as cancelled.

### Settings

| Where | Setting | Values | When absent | Meaning |
| --- | --- | --- | --- | --- |
| agent.json | `directTransfer` | `auto`, `never` | `auto` | The tray menu entry "Direct connection for large files". `never` sends every fast transfer through the channel. |
| agent.json | `directPortRange` | a range such as `3400-3449`, or one port | `3400-3449` | The ports the agent may listen on, from 1025 to 65535, at most 1000 ports. Only editable in the file; restart the agent after a change. |
| agent.json | `directConnections` | 1 to 16 | `8` | How many connections the agent accepts for one transfer at most. Only editable in the file; restart the agent after a change. |
| agent.json | `directMaxMegabytesPerSecond` | 0, or 1 to 100000 | `0` | Protects the server: the agent reads all the connections of a transfer together at most at this many MB/s, and TCP slows the client down. 0 means no limit. Only editable in the file; restart the agent after a change. |
| client.json | `directTransfer` | `auto`, `never` | `auto` | `never` makes the client ignore the offer of the agent and use the channel. Only editable in the file. |
| client.json | `directConnections` | 1 to 16 | `8` | How many connections the client opens for one transfer at most, within what the agent accepts. Only editable in the file. |

The direct connection needs both sides at the same version, 0.6.0 or 0.7.0. A client 0.7.0 ignores the offer of an agent 0.6.0 and uses the channel; a client 0.6.0 tries the direct connection of an agent 0.7.0, which refuses its token at once, and then uses the channel. A client or an agent 0.5.2 uses the channel.

### Reading the summary lines

At the end of a direct transfer each log has a line that starts with `direct connection statistics`. The client line gives the connections used, the bytes each one carried, the seconds from the start of the data, the time the reader spent opening and reading the files and the time it waited for room in its queue, the time the writers spent in socket writes and the time they waited for blocks, and the rates: the average, the first 5 seconds and the last 5 seconds. The agent line gives the connections, the bytes of each, the time spent in socket reads, in file writes and in reading the files back to hash them, and the same rates. The times of several threads are added up, so they may exceed the length of the transfer.

- **The client reader takes most of the time and the writers wait for blocks.** Reading the files on your computer limits the transfer, for example an antivirus that scans a large file. The client also logs at the info level any file whose opening or first read takes more than half a second.
- **The client writers spend most of the time in socket writes and the reader waits for room.** The network limits the transfer. A first 5 seconds much faster than the last 5 seconds is the per connection limit described above: more connections help, up to 16.
- **The agent spends much time in file writes or in hashing.** The server limits the transfer: its disk, or its processor.

With `logLevel` at `debug` in client.json or agent.json, both sides also log the rate of every second.

## RemoteApp programs

From version 0.8.0, UltimateRDP also opens RemoteApp programs: a program published on the server that shows in windows of its own on your desktop, without the remote desktop around it. Open its .rdp file as any other, for example the one your administrator gives you or the one of the web feed. A file with `remoteapplicationmode:i:1` opens a RemoteApp tab. Version 0.8.0 was tested once against a real server with Notepad; version 0.9.0 fixes what that test showed, and its changes were not tested against a real server at the time of the release: please report what you see, with both logs.

### From a shortcut or a .bat file

From version 0.9.0, `UltimateRDP.exe` started with a RemoteApp file shows only the windows of the program, as the Microsoft client does. For example, in a file `Notepad on the server.bat`:

```bat
@echo off
start "" "C:\Tools\UltimateRDP\UltimateRDP.exe" "C:\Users\me\Documents\notepad-server.example.com.rdp"
```

or a shortcut whose target is `"C:\Tools\UltimateRDP\UltimateRDP.exe" "C:\Users\me\Documents\notepad-server.example.com.rdp"`.

- A small window "Connecting to Notepad..." with Cancel shows in the bottom right corner of the screen until the window of the program appears.
- The UltimateRDP window stays out of sight. A notification icon names the program and its server, and its menu holds Show UltimateRDP, Disconnect and Exit. No taskbar button shows besides those of the windows of the program.
- When you close the last window of the program, the server keeps the session about 20 seconds, then ends it, and UltimateRDP exits by itself without a message. Until then, only the notification icon is left.
- When the connection fails, or ends for another reason, a message says why; when it failed before the logon, the UltimateRDP window shows too, with the reason in its tab and Session, Connect.
- Starting UltimateRDP again with another file hands it to the copy that runs: one copy per user runs at a time.
- `--show` shows the UltimateRDP window anyway, and `--hidden` keeps it out of sight for a desktop file too. A desktop file shows the window by default.
- `--ask-credentials` asks for the password, see "Saved credentials".

The command line is `UltimateRDP.exe [--show | --hidden] [--ask-credentials] [file.rdp]`.

### On your computer

- The tab shows the name of the program, how many of its windows are open, the state of the connection and of the agent, the result of the last drop, and a Disconnect button. The program itself opens in its own windows, outside the UltimateRDP window, which you can minimize once you are logged on.
- Drop files on a window of the program. Nothing happens while you move over it: the drop happens where you release the button. The progress window "Drop into the session" opens over the window you dropped on, as for a desktop session, and large files go through the channel or the direct connection as described above.
- The remote application shows no drop feedback while you move, since the drag only starts in the session when you release the button.
- View, Resize session to window, Scale to window, Scale factor, Full screen and Use all monitors do not apply to a RemoteApp tab and are disabled there. The session always covers all your monitors.
- When the program closes its last window, the server ends or disconnects the session about 20 seconds later, and the tab closes. The status bar calls it a normal end.
- Closing the tab, or quitting UltimateRDP, while windows of the program are open asks first: the program keeps running on the server and comes back at the next connection with the same file. To end it, close it in its own window, or use File, Log off the remote session.
- Several tabs, desktop and RemoteApp, work side by side. A drop on a window of a program goes to the session of that program.

The client reads `remoteapplicationprogram`, `remoteapplicationname`, `remoteapplicationcmdline`, `remoteapplicationexpandcmdline`, `remoteapplicationexpandworkingdir` and `remoteapplicationfile`. A file that asks for RemoteApp without a program is refused when you open it.

### On the server

A RemoteApp session does not run Explorer, so it ignores the `Run` entry that `--install-all-users` writes: the agent does not start there by itself. The administrator starts it with a scheduled task at logon. A RemoteApp session also ends when no program of the user is left in it, and an agent that keeps running would keep it open. The agent therefore takes care of it in two ways:

- In a RemoteApp session, it lists the processes of its session every 15 seconds. Once nothing is left but the processes that every RemoteApp session runs and the names of the `SysProcs` key of the server, for two checks in a row, and no drop or transfer is in progress, it exits. During its first 90 seconds, as long as no program was seen, the checks do not count, so that a program that is slow to start does not make it leave. It also exits when the session is disconnected and no program is left. In a desktop session it looks once, at start, and does nothing more.
- Its tray icon never appears in a RemoteApp session while `trayIcon` is `auto`. Do not set `trayIcon` to `always` for RemoteApp users.

The administrator adds the agent to `SysProcs` as well, so that Windows itself does not count it. UltimateRDP never writes the registry or creates a task: here are the commands, for an elevated PowerShell on the server. Replace the path of the agent with yours.

1. Read the current state first, and keep the output:

   ```powershell
   Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\SysProcs'
   Get-ScheduledTask -TaskPath '\UltimateRDP\' -ErrorAction SilentlyContinue
   quser
   ```

2. Add the agent to `SysProcs`. Alone, this changes nothing, since the agent does not run in RemoteApp sessions yet. It applies to new sessions; do not restart the Remote Desktop Services for it, which would disconnect every user.

   ```powershell
   New-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\SysProcs' -Name 'UltimateRDP.Agent.exe' -PropertyType DWord -Value 0
   ```

3. Create the task for one test account only:

   ```powershell
   $exe       = 'C:\Program Files\UltimateRDP\UltimateRDP.Agent.exe'
   $action    = New-ScheduledTaskAction -Execute $exe
   $trigger   = New-ScheduledTaskTrigger -AtLogOn -User '<DOMAIN\test account>'
   $principal = New-ScheduledTaskPrincipal -UserId '<DOMAIN\test account>' -LogonType Interactive -RunLevel Limited
   $settings  = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances Parallel -Priority 5 -Compatibility Win8
   Register-ScheduledTask -TaskPath '\UltimateRDP\' -TaskName 'Agent' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Starts the UltimateRDP agent in each user session, RemoteApp sessions included.'
   ```

   `-LogonType Interactive` runs the agent in the session of the user with the rights of the user, and no password is stored. `-RunLevel Limited` keeps it from ever running elevated. `-ExecutionTimeLimit` zero removes the limit of 72 hours of a task. In a desktop session the `Run` entry and the task both start the agent; the second copy sees the first and exits at once.

4. Test with the test account, as described below.

5. Only then, start it for every user:

   ```powershell
   $users     = (New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-32-545').Translate([System.Security.Principal.NTAccount]).Value
   $trigger   = New-ScheduledTaskTrigger -AtLogOn
   $principal = New-ScheduledTaskPrincipal -GroupId $users -RunLevel Limited
   Set-ScheduledTask -TaskPath '\UltimateRDP\' -TaskName 'Agent' -Trigger $trigger -Principal $principal
   ```

   The first line gives the local name of the Users group, which depends on the language of Windows.

After an update of the agent in another folder, point the task to it:

```powershell
Set-ScheduledTask -TaskPath '\UltimateRDP\' -TaskName 'Agent' -Action (New-ScheduledTaskAction -Execute 'C:\Program Files\UltimateRDP\<version>\UltimateRDP.Agent.exe')
```

Never use `Stop-ScheduledTask`, or End in the Task Scheduler, on this task: it would stop the agents of every session the task started.

To remove it all, in this order:

```powershell
Unregister-ScheduledTask -TaskPath '\UltimateRDP\' -TaskName 'Agent' -Confirm:$false
Remove-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\SysProcs' -Name 'UltimateRDP.Agent.exe'
```

Removing the task does not stop the agents already running; they end with their session, or by themselves in RemoteApp sessions. Remove the `SysProcs` value last, once no agent runs in a RemoteApp session.

`UltimateRDP.Agent.exe --status`, run in a session, says whether it is a RemoteApp session, whether the `SysProcs` value of the agent exists and whether the task `\UltimateRDP\Agent` exists. It only reads.

### Testing on one account

Before the task exists for everybody, with the test account only:

1. Without any change, open the program with the Microsoft client, close it, and note what happens to the session with `quser`: it should end, or disconnect, about 20 seconds after the last window closed. This is the behaviour to keep.
2. After steps 2 and 3 above, open the program again with the Microsoft client. The agent must run in the session (`Get-CimInstance Win32_Process -Filter "Name='UltimateRDP.Agent.exe'"`, with its `SessionId`), without any icon or window for the user, and its log must say `RemoteApp session detected`. Close the program: the session must end as in step 1, and the agent log must end with `the agent exits so that the session can end`, or with the end of the session.
3. Open the program with UltimateRDP and drop a small file, then a large one, on a window of the program.
4. The day after step 5 of "On the server", compare the number of sessions, and of disconnected ones, with the output of its step 1.

If a session stays open after its program closed, remove the task first, see above, then report it with the agent log of that session.

## How it works

The client hosts the Microsoft Remote Desktop ActiveX control and registers itself as a drop target on its windows. The agent runs in your session and listens on a static virtual channel named `ULTRDP`. For small files the client sends the local paths and the cursor position, and the session reads the files from the redirected drives. Large files are sent through the channel, as described after the steps below.

1. When the drag first moves over the session, the client converts the cursor position to remote desktop coordinates and sends `dragEnter` with the paths.
2. The agent opens an invisible proxy window over the whole remote screen. It moves the remote cursor to the point and simulates a left button press on that window. The press starts a real OLE drag inside the session, with the files as `\\tsclient\<drive>\...` paths, or as staged copies.
3. The agent answers `dragAck`. Without that answer within a second, the client stops the drag.
4. While you move, the client sends `dragMove`. The agent moves the remote cursor, so the application under it sees the drag and shows its feedback.
5. When you release the button, the client sends `drop`. The agent moves the cursor to the final point and releases the simulated button there. If you leave the session instead, the client sends `dragLeave` and the agent cancels the drag.
6. The agent reports `dropResult` with the effect: `copy` or `link` when the target accepted, or `none` when nothing under the cursor accepted the files. With `none`, the drop is ignored. The client shows the result in the status bar.

The drag offers copy and link only, never move. Modifier keys are recorded in the logs but not injected.

In a RemoteApp session the windows of the server are windows of the client, and anything the agent shows would appear on your desktop too, its proxy window included. So the client sends nothing while you move: at the drop it sends `dragEnter` and then `drop` at the same point, and keeps the drop open on your side until the agent answered. The agent replays the drag right there, with a proxy window of 32 by 32 pixels under the point instead of one over the whole screen.

For large files, the `dragEnter` carries the size of each item, and the agent answers `dragAck` with `transfer: true` when they must come through the channel. At step 5 the client then sends `drop` with `deferred: true`: the agent cancels the drag in the session and keeps the point. The client sends the files in chunks of 32 KiB by default, in base64, or in the denser `u15` form when the agent says the data of the client reaches it as UTF-16, with at most 4 MiB not yet acknowledged, the agent writes them into its staging folder and checks the size and SHA-256 of each one, answers `transferResult`, then drops the copies at the kept point with a short drag, and reports `dropResult` as in step 6.

To log off, the client sends `logoff`. The agent answers `logoffResult`, waits half a second so that the answer reaches the client, then asks Windows to log off the session. It never forces applications that have unsaved work.

Messages are JSON objects sent as ASCII lines, except the `u15` chunks, and each side detects at run time how the control maps channel bytes to characters. See [docs/design.md](docs/design.md) for the details.

## Troubleshooting

- **The agent never becomes ready.** Run `UltimateRDP.Agent.exe --status` in the session: it says whether the agent runs there and where it is set to start from. The tray icon only appears once the client is connected, so its absence before that is normal. A server policy may also block virtual channels: the agent log then has a warning that it cannot open the channel.
- **"The agent did not acknowledge the drag".** The agent did not answer the start of the drag within a second. A slow connection or a busy agent can cause it, and so can an agent that exited or restarted in the session, which the client cannot see otherwise. The client then looks for the agent again: the agent label goes to "Agent: waiting", and back to "Agent ready" when the agent answers. When the status bar says "The session does not accept simulated input", the session may be locked, or a policy may block simulated input. Please report it with both logs.
- **The target shows no drop feedback, or the status bar says "Nothing under the cursor accepted the files."** The application under the cursor may not accept files at that place. Some applications also refuse network paths such as `\\tsclient\C\...`. For those, turn on "Stage files in the session before the drop" in the agent tray menu.
- **The first drop on Explorer takes several seconds.** Explorer reads the redirected drive the first time, which can be slow. Later drops are usually faster.
- **Ctrl+W or Ctrl+O does nothing.** When the session has the keyboard, these keys go to the remote computer, like every other key. Use the File menu instead.
- **My applications are still open on the server after Quit.** This is expected: Quit and Close tab disconnect without ending the session. Use File, Log off the remote session.
- **The texts are in the wrong language.** Choose the language in View, Language. A message from the agent follows the language the client announced at connection time; after a language change, the next message uses the new one.
- **Drops are refused because of the drives.** Turn on drive redirection in the .rdp file, under Local Resources, More, Drives. On the server, check the policy "Do not allow drive redirection".
- **A RemoteApp program opens, but drops on its windows are refused or do nothing.** The client log has one line per window of the program that starts with `RemoteApp window`, with the result of the registration of the drop target, and a warning when it failed. The lines `RemoteApp monitors` and `the target is at` say whether the windows on the server are where they are on your screen; a warning there means the monitors changed since the connection, and a new connection fixes it.
- **A RemoteApp session stays open after its program closed.** Check that the agent log of the session says `RemoteApp session detected`, and what the lines `RemoteApp session:` say: a program that is still listed there keeps the session open, with or without the agent. See "RemoteApp programs".
- **The drop lands on the wrong window.** Coordinate scaling after a window resize, or full screen over several monitors, can be the cause. Please report it with both logs.
- **A transfer of large files fails, or is slow.** The status bar gives the reason. Both logs end each transfer with a summary line: files, bytes, seconds and MB/s, then with the lines described under "Measuring a slow transfer". The client log also gives the length of the first chunk line it sent, and the agent log has a line for the first chunk of each file it received. When the status bar says "The agent did not acknowledge the first data of the transfer.", the data did not get through: the client log then gives the byte model, the chunk size, the length of the lines and the number of lines sent. A smaller `transferChunkBytes` may help, see "Large files". To go back to drive redirection, choose Never in the agent tray menu, Fast transfer of large files.
- **A transfer fails with "A message of the transfer was lost on the way".** The Remote Desktop control sometimes loses a short message sent just before a large one. From 0.5.1 on both sides this should no longer appear: the agent asks the client to send the missing lines again, and the summary of the transfer in both logs counts the lines sent again. If it still appears, one side runs an older version; if a transfer fails with "Lines of the transfer were lost on the way and could not be recovered", report it with both logs taken with `logLevel` set to `debug`.
- **A transfer fails at once with "Data was lost or damaged on the way".** The channel sometimes damages the data of one line on the way. From 0.5.2 on both sides the agent asks for that line again, as for a lost one, and its log says what the damage looked like in a line that contains "arrived damaged"; an agent 0.5.1 still fails the transfer.
- **Large files never use the direct connection.** The client log says why at the start of each transfer: "the direct connection cannot be used" with the reason, such as a port that did not answer within 3 seconds. Check the firewall rules of "Direct connection for large files", on the server and at the hosting provider, and that both sides run 0.7.0. The agent log says on which port it listened, and warns when no port of the range was free.
- **A direct transfer is slower than the link.** Read the `direct connection statistics` lines of both logs, see "Reading the summary lines".
- **Files sent from the session do not arrive.** The client log has a line `Send offer` for each offer, then `Receive <id>:` lines that say whether the direct connection could be made, and why not. The agent log has the same `Send <id>:` lines. When the client log has no `Send offer` line, check that both sides run 0.9.0 and that `receiveFiles` and `directTransfer` are not `never` in client.json; the agent log line `Hello from client` says `receives files of the session: True` when they are not.
- **Where the logs are.** The client writes `%LOCALAPPDATA%\UltimateRDP\logs\client-yyyyMMdd.log` on your computer. The agent writes `agent-yyyyMMdd.log` in the same folder on the server; from version 0.9.0 each of its lines gives the session and the thread, such as `[S3 12]`, since the agents of one user in several sessions write the same file. Both keep fourteen days of logs. The client opens its folder from Help, Open log folder, and the agent from its tray menu, Open log folder.

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
- From version 0.6.0, the files of a transfer of large files may go through a direct TLS connection instead. The agent listens only while a transfer is offered, and accepts a connection only with a proof of the single use token it gave through the encrypted Remote Desktop connection, each connection of the offer once, 16 at most. The client accepts only the certificate whose SHA-256 the agent gave there. The files go to the same staging folder, with the same checks. Open the ports only to the client addresses allowed for RDP.
- From version 0.9.0, the session can send files to your computer. Only the user of the session can start it, through the agent of that session: `--send` reaches that agent through a pipe that only accepts processes of the same user in the same session. The client takes files only after it answered an offer that came through the encrypted Remote Desktop connection of its own session, only through the direct connection whose certificate and token that offer gave, and only into its folder for received files. It never replaces a file, never runs one, and marks each one as a download.
- The client never writes credentials and never logs them. It reads the generic credential `TERMSRV/<host>` when one exists, and hands it to the Microsoft control. Other credential prompts come from the Microsoft control.

## Status

Version 0.9.0 is a test release. The files sent from the session to your computer, the launch of a RemoteApp program from a shortcut, its window fixes and the password option were not tested against a real server at the time of the release; the drive letters of 0.8.0 were not either.

Verified in a real Remote Desktop session, with version 0.2.1:

- The virtual channel between the client and the agent, with one byte per character in both directions.
- The transfer through drive redirection to the remote desktop.
- The live drag into a remote application.

The log off command and the translations are new in 0.3.0. The fast transfer of large files is new in 0.4.0. Its first test against a real server failed. Version 0.4.1 fixes the two defects found there; with it, a file of 496 MB reached a real session in 193 seconds. Version 0.4.2 adds a progress window at every drop and only changes the client. Version 0.4.3 fixes short messages that the Remote Desktop control sometimes lost just before a large one, which failed some transfers at their start. Version 0.4.4 measures where the time of a transfer goes, on both sides, and adds the settings `transferWindowBytes` and `channelRead` to test what limits it.

Its measurements showed that neither side limits the transfer: the channel is full, and the encoding of the data took the larger part of it. Version 0.5.0 sends the files in a denser form when the data of the client reaches the agent as UTF-16, which is expected to raise the rate about 2.5 times, from 2.1 MB/s to about 5.4 MB/s on a channel of 5.8 MB/s. It also keeps the fast transfer when you release the button quickly, before the agent answered the start of the drag, including when that answer comes in two steps. Version 0.5.1 numbers the messages of a transfer, so that the agent asks for the ones the Remote Desktop control lost and the transfer goes on. Version 0.5.2 does the same for a message that arrives damaged. Version 0.6.0 sends large files through a direct TLS connection when the server opens its ports for it, since the channel stayed at 2 to 4 MB/s. Its first production test reached 135 to 180 MB/s for a few seconds, then about 20 MB/s, the limit of one TCP connection on that path; version 0.7.0 therefore uses several connections at once, and logs where the time of a transfer goes. Version 0.8.0 opens RemoteApp programs and takes drops on their windows, keeps the agent from holding a RemoteApp session open, can give the drives of the client letters of the session, and reads machine wide settings. Its first real test with Notepad worked and showed an empty window of the control over a monitor, a main window always shown, ten seconds lost before the program started, and a normal end shown as an error. Version 0.9.0 fixes these, opens RemoteApp programs from a shortcut with only their own windows on the screen, sends files from the session to your computer, and can ask for the password every time.

Feedback that helps:

- Whether the live drag works in your session, and with which applications.
- Whether the log off works for you, and the wording of your language.
- The rate of a transfer of large files on your connection, from the summary line of both logs.

- How long the first drop on Explorer takes.
- Whether drops land in the right place in full screen, with one monitor or all monitors.
- Whether saved credentials work, and which kind you have in the Credential Manager.
- With RemoteApp: whether the program opens, whether a drop on its windows lands where you released the button, and whether the session ends as before when the program closes, with both logs.

Please send it through [GitHub issues](https://github.com/freeKC/ultimaterdp/issues), with both logs.

## Credits

UltimateRDP is developed and published by [APROGSYS](https://www.aprogsys.com/). The source code is on [GitHub](https://github.com/freeKC/ultimaterdp), under the MIT license.

## License

MIT. See [LICENSE](LICENSE).
