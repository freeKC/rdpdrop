# RdpDrop

[![CI](https://github.com/freeKC/rdpdrop/actions/workflows/ci.yml/badge.svg)](https://github.com/freeKC/rdpdrop/actions/workflows/ci.yml)

RdpDrop lets you drag files or folders from your desktop and drop them into a Remote Desktop session, on the application under the cursor. Explorer, a mail client, a browser or a business application receives them as if you had dropped them inside the session. RdpDrop is made of a client that hosts the Microsoft Remote Desktop control, a small agent in the session, a virtual channel between the two, and the standard drive redirection for the transfer itself.

## Why

The Microsoft Remote Desktop client rejects drops on its window, because RDP has no drag and drop channel. Copy and paste works, and so does drive redirection, but neither is a drop. RdpDrop adds the gesture on top of what RDP already offers.

While you drag over the client window, the agent replays the same drag inside the session as a real OLE drag and drop. Any remote application that accepts dropped files therefore accepts them from RdpDrop, and shows its own drop feedback while you move. When nothing under the cursor accepts the drop, the agent copies the files to a fallback folder instead, as version 0.1 did.

The file bytes travel through drive redirection, never through the RdpDrop channel.

## What you need

- A client computer with Windows 10 or 11, 64 bit.
- A server with Windows Server 2016 or later, or a Windows 10 or 11 computer acting as a Remote Desktop host.
- An .rdp file that redirects drives. In the Remote Desktop Connection settings, this is Local Resources, then More, then Drives.
- The ability to run the agent in your user session on the server.

The client is one portable executable. It runs without administrator rights. The agent archive also holds one executable.

RdpDrop works through a Remote Desktop Gateway.

Version 0.2 has a deliberately narrow scope:

- One direction only, from the local computer to the session. For the other direction, keep using copy and paste.
- Full desktop sessions only. RemoteApp is not supported.
- The session always starts in a window, even if the .rdp file asks for full screen. You can switch to full screen afterwards. In full screen, you can only drop from a monitor that the session does not cover.

## Install

Each zip holds a single `.exe` file.

### On the server

1. Download the `RdpDrop.Agent` zip from the [Releases page](https://github.com/freeKC/rdpdrop/releases).
2. Unzip it to a folder that every user can read, such as `C:\Program Files\RdpDrop`.
3. From an elevated command prompt, run:

   ```
   "C:\Program Files\RdpDrop\RdpDrop.Agent.exe" --install-all-users
   ```

   The agent then starts with each user session and shows a tray icon. Users who are already signed in get it at their next sign in.

To install it for your own account only, run the same command with `--install` from a normal prompt. This also starts the agent at once.

Other options:

- `--uninstall` stops the agent from starting with your session and stops the running copy. Run it from an elevated prompt to remove the all users entry as well.
- `--help` lists the options.

To update from 0.1, exit the running agent from its tray menu, replace the executable, then start it again or sign in again.

### On your computer

1. Download the `RdpDrop.Client` zip from the [Releases page](https://github.com/freeKC/rdpdrop/releases).
2. Unzip it anywhere. There is nothing to install. RdpDrop writes nothing to the registry.
3. Start it with your connection file:

   ```
   RdpDrop.Client.exe path\to\connection.rdp
   ```

   You can also double click `RdpDrop.Client.exe` and pick the .rdp file in the dialog.

## Use

### Sessions and tabs

Each session opens in its own tab. The status bar and the menus follow the active tab. The File menu holds:

- **Open .rdp...** (Ctrl+O): opens a connection file in a new tab.
- **Recent**: the last ten connection files. **Clear recent** empties the list.
- **Reopen in new tab**: opens the file of the active tab once more, in a second tab.
- **Close tab** (Ctrl+W): disconnects the active session and closes its tab.
- **Forget saved credentials**: see "Saved credentials" below.
- **Quit**.

The Session menu holds Connect, Disconnect and Look for the agent again.

### Dragging into the session

1. Start the client. It connects with the settings of the .rdp file. Log on as usual.
2. Watch the status bar at the bottom of the window. The agent label goes from "Agent: waiting" to "Agent ready (v0.2.0, native drop)". With a 0.1 agent, it shows "Agent ready (v0.1.0)" and drops work as in 0.1.
3. Drag files or folders from Explorer onto the session. A blue banner at the top of the session says "Drop here to send the files into the session".
4. Move over the remote application you want. It shows its own drop feedback, for example a highlighted folder or an attachment area.
5. Release the button on the target. The remote application receives the files as a normal drop.

If nothing under the cursor accepts the drop, the agent copies the files to the fallback folder. The fallback folder is the Desktop by default. You can change it from the agent tray menu, under Fallback folder: Desktop, Downloads, or Choose to pick any folder.

When the target is Explorer, the usual Windows copy dialog appears in the session. It shows the progress and asks what to do when a file already exists.

RdpDrop only offers copy and link to the remote application. It never offers a move. Ctrl, Shift and Alt are not passed to the session during a drag in 0.2.

### Status bar messages

Messages of a drag:

- **"Dragging 2 item(s) into the session..."**: the agent is replaying the drag.
- **"Starting the drag in the session..."**: the agent needs a moment to start the drag.
- **"Copying 2 item(s) into the session before the drag starts..."**: the agent copies the files into the session first, because "Stage files in the session before the drop" is on.
- **"Dropped on Inbox (copy)"**: the remote window accepted the drop. It names the window and says copy or link.
- **"Copied to C:\Users\me\Desktop (nothing under the cursor accepted the drop)"**: the agent used the fallback folder.
- **"The agent did not acknowledge the drag"**: no answer from the agent within a second. See Troubleshooting.
- **"Drop refused"** followed by the reason from the agent. For example "Drop refused: The previous drop is still being delivered in the session. Try again once it is done."
- **"Drop failed: Nothing under the cursor accepted the drop, and the copy to the fallback folder is turned off."**
- **"Cancelled"** followed by the reason, for example "Cancelled: The copy was cancelled." when you cancel the Windows copy dialog.

Messages of the connection, as in 0.1:

- **"No agent on the remote computer"**: the client sent hello messages for sixty seconds and got no answer. Start the agent in the session, then use Session, Look for the agent again.
- **"This .rdp file does not redirect drives: drops will not work."**: the .rdp file does not share any local drive. Turn on drives in the .rdp file and reconnect.
- **"The session sees no redirected drive: drops will not work."**: the agent answered, but no `\\tsclient` drive is reachable from the session. The server may block drive redirection.
- **"Drop refused: not connected."**: there is no session yet.
- **"Drop refused: the RdpDrop agent has not answered yet."**: wait for "Agent ready".
- **"Drop refused: the RdpDrop agent is not running in the remote session."**: start the agent in the session.
- **"Drop refused"** with a path: only files and folders on a local drive letter can be dropped. Network (UNC) paths are refused, because the session reaches your files through drive redirection.
- **"Drop failed"**: the agent could not finish. The rest of the message gives its reason.

### View menu

- **Resize session to window**: the default. The session resolution follows the window size.
- **Scale to window**: the session keeps its resolution and the picture is scaled to the window, as in 0.1.
- **Scale factor**: Auto, or 100 % to 200 %. Auto follows the monitor that holds the window. A change applies to a new connection, and the client offers to reconnect.
- **Full screen** (F11, or Ctrl+Alt+Pause): toggles full screen. The connection bar is shown and can be pinned.
- **Use all monitors**: the session covers every monitor in full screen. It applies to a new connection.
- **Windows keys**: where key combinations such as Alt+Tab go. "On this computer", "In the session", or "In the session when full screen", which is the default.

Your View choices are saved for the sessions you open later. When the .rdp file sets `desktopscalefactor`, `use multimon` or `keyboardhook`, the file wins.

### Saved credentials

Before connecting, the client reads the generic credential `TERMSRV/<host>` from the Windows Credential Manager. When it exists and holds a password, the client applies it and turns off the prompt for that connection. RdpDrop never writes a credential. You can create one with:

```
cmdkey /generic:TERMSRV/<host> /user:<domain\user> /pass
```

`cmdkey` then asks for the password.

The "Remember me" box of the Windows credential prompt saves a different credential type, a domain password. Windows keeps that password for itself, so RdpDrop cannot read it. The Windows prompt itself may still use it.

File, Forget saved credentials deletes both kinds of credential for the host of the active tab. The open session is not affected, and the next connection asks again.

## Agent tray menu and settings

The tray menu of the agent holds:

- **Fallback folder**: Desktop, Downloads or Choose.
- **Stage files in the session before the drop**: off by default. When on, the agent copies the files into the session before the drag starts, and the remote application receives local paths instead of `\\tsclient` paths. Turn it on for applications that refuse network paths. Large files take time to copy before the drag starts.
- **Copy to the fallback folder when nothing accepts the drop**: on by default. When off, a drop that nothing accepts ends with an error and no copy.
- **Open log folder**, **About** and **Exit**.

Staged copies go to `%LOCALAPPDATA%\RdpDrop\staging\<id>` in the session. Each folder is deleted one hour after its drop, because the target may still be reading the files. When the agent starts, it deletes the folders that are older than one hour.

The agent keeps its settings in `%LOCALAPPDATA%\RdpDrop\agent.json` on the server:

```json
{
  "fallback": "custom",
  "customFolder": "D:\\Inbox",
  "logLevel": "debug",
  "stageFiles": false,
  "fallbackCopy": true
}
```

`fallback` is `desktop`, `downloads` or `custom`. `customFolder` is used when `fallback` is `custom`. `logLevel` is `debug`, `info`, `warn` or `error`, and `info` when absent. `stageFiles` is `false` when absent. `fallbackCopy` is `true` when absent. Restart the agent after editing the file by hand.

## How it works

The client hosts the Microsoft Remote Desktop ActiveX control and registers itself as a drop target on its windows. The agent runs in the tray of your session and listens on a static virtual channel named `RDPDROP`. The client never sends file contents. It sends the local paths and the cursor position, and the session reads the files from the redirected drives.

1. When the drag first moves over the session, the client converts the cursor position to remote desktop coordinates and sends `dragEnter` with the paths.
2. The agent opens an invisible proxy window over the whole remote screen. It moves the remote cursor to the point and simulates a left button press on that window. The press starts a real OLE drag inside the session, with the files as `\\tsclient\<drive>\...` paths, or as staged copies.
3. The agent answers `dragAck`. Without that answer within a second, the client stops the drag.
4. While you move, the client sends `dragMove`. The agent moves the remote cursor, so the application under it sees the drag and shows its feedback.
5. When you release the button, the client sends `drop`. The agent moves the cursor to the final point and releases the simulated button there. If you leave the session instead, the client sends `dragLeave` and the agent cancels the drag.
6. The agent reports `dropResult` with the effect: `copy` or `link` when the target accepted, `fallbackCopy` when it copied to the fallback folder, or `none`. The client shows it in the status bar.

The drag offers copy and link only, never move. Modifier keys are recorded in the logs but not injected in 0.2.

A 0.2 client talks to a 0.1 agent with the 0.1 `drop` message, which copies to the folder under the cursor. A 0.2 agent handles the 0.1 `drop` message the same way, so a 0.1 client keeps working with it.

Messages are JSON objects sent as ASCII lines, and each side detects at run time how the control maps channel bytes to characters. See [docs/design.md](docs/design.md) for the details.

## Troubleshooting

- **The agent never becomes ready.** Check that the agent runs in the session: its icon should be in the tray. A server policy may also block virtual channels. The agent log says so when it cannot open the channel.
- **"The agent did not acknowledge the drag".** The agent did not answer the start of the drag within a second. A slow connection or a busy agent can cause it. A 0.1 agent does not cause this message: with it, the agent label has no "native drop" and the client sends the 0.1 drop. When the status bar says "The session does not accept simulated input", the session may be locked, or a policy may block simulated input. Please report it with both logs.
- **The target shows no drop feedback.** The application under the cursor may not accept files. Some applications also refuse network paths such as `\\tsclient\C\...`. For those, turn on "Stage files in the session before the drop" in the agent tray menu.
- **The first drop on Explorer takes several seconds.** Explorer reads the redirected drive the first time, which can be slow. Later drops are usually faster.
- **Files landed in the fallback folder.** Nothing under the cursor accepted the drop. The status bar says so. To stop these copies, turn off "Copy to the fallback folder when nothing accepts the drop".
- **Ctrl+W or Ctrl+O does nothing.** When the session has the keyboard, these keys go to the remote computer, like every other key. Use the File menu instead.
- **Drops are refused because of the drives.** Turn on drive redirection in the .rdp file, under Local Resources, More, Drives. On the server, check the policy "Do not allow drive redirection".
- **The drop lands on the wrong window.** Coordinate scaling after a window resize, or full screen over several monitors, can be the cause. Please report it with both logs.
- **Where the logs are.** The client writes `%LOCALAPPDATA%\RdpDrop\logs\client-yyyyMMdd.log` on your computer. The agent writes `agent-yyyyMMdd.log` in the same folder on the server. Both keep fourteen days of logs. The client opens its folder from Help, Open log folder, and the agent from its tray menu, Open log folder.

## Build from source

You need the .NET 10 SDK on Windows.

```
dotnet build RdpDrop.sln -c Release
dotnet test RdpDrop.sln
```

To publish a self contained single file executable without symbols:

```
dotnet publish src/RdpDrop.Client -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false
dotnet publish src/RdpDrop.Agent -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false
```

Releases are built by GitHub Actions when a `v*` tag is pushed. The workflow builds, tests, publishes both applications and attaches the two zips to the release.

## Security notes

- The agent only reads from `\\tsclient\`, that is from the drives you chose to share. It only writes inside the session of the same user, in the folder you drop on, the fallback folder or its staging folder. It does not run as a service and has no privilege beyond your own.
- The agent simulates input only inside your own session, and only between the start and the end of a drag. It only uses the left mouse button. It injects the press only once its own proxy window is under the cursor, so the press never reaches another application. To cancel, it posts Escape to its proxy window instead of simulating a key.
- The agent never offers a move. A move accepted in the session could delete the file on your local disk through the redirected drive.
- Messages carry file paths, coordinates, modifier key flags and window names only. No file content goes through the channel.
- The client never writes credentials and never logs them. It reads the generic credential `TERMSRV/<host>` when one exists, and hands it to the Microsoft control. Other credential prompts come from the Microsoft control.

## Status

Version 0.2.0 is a test release.

Verified in a real Remote Desktop session, with 0.1:

- The copy drop: a drop landed on the remote desktop as expected.
- The channel byte model: one byte per character in both directions.

The live drag of 0.2 was verified on a local desktop with Explorer as the target. It is not yet verified inside a Remote Desktop session.

Feedback that helps:

- Whether the live drag works in your session, and with which applications.
- How long the first drop on Explorer takes.
- Whether drops land in the right place in full screen, with one monitor or all monitors.
- Whether saved credentials work, and which kind you have in the Credential Manager.

Please send it through [GitHub issues](https://github.com/freeKC/rdpdrop/issues), with both logs.

## License

MIT. See [LICENSE](LICENSE).
