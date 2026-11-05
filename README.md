# RdpDrop

[![CI](https://github.com/freeKC/rdpdrop/actions/workflows/ci.yml/badge.svg)](https://github.com/freeKC/rdpdrop/actions/workflows/ci.yml)

RdpDrop lets you drag files or folders from your desktop and drop them into a Remote Desktop session, right where the cursor is. It is made of a client that hosts the Microsoft Remote Desktop control, a small agent in the session, a virtual channel between the two, and the standard drive redirection for the transfer itself.

## Why

The Microsoft Remote Desktop client rejects drops on its window, because RDP has no drag and drop channel. Copy and paste works, and so does drive redirection, but neither is a drop. RdpDrop adds the gesture on top of what RDP already offers. The file bytes travel through drive redirection, never through the RdpDrop channel.

## What you need

- A client computer with Windows 10 or 11, 64 bit.
- A server with Windows Server 2016 or later, or a Windows 10 or 11 computer acting as a Remote Desktop host.
- An .rdp file that redirects drives. In the Remote Desktop Connection settings, this is Local Resources, then More, then Drives.
- The ability to run the agent in your user session on the server.

RdpDrop works through a Remote Desktop Gateway.

Version 1 has a deliberately narrow scope:

- One direction only, from the local computer to the session. For the other direction, keep using copy and paste.
- One remote monitor.
- Full desktop sessions only. RemoteApp is not supported.
- The client always runs the session in a window, even if the .rdp file asks for full screen. This is on purpose: a full screen window could not receive a drop from the local desktop.

## Install

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

### On your computer

1. Download the `RdpDrop.Client` zip from the [Releases page](https://github.com/freeKC/rdpdrop/releases).
2. Unzip it anywhere. There is nothing to install, and RdpDrop writes nothing to the registry.
3. Start it with your connection file:

   ```
   RdpDrop.Client.exe path\to\connection.rdp
   ```

   You can also double click `RdpDrop.Client.exe` and pick the .rdp file in the dialog.

## Use

1. Start the client. It connects with the settings of the .rdp file. Log on as usual.
2. Watch the status bar at the bottom of the window. The agent label goes from "Agent: waiting" to "Agent ready (v0.1.0)".
3. Drag files or folders from Explorer onto the session window.
4. Release them where you want them:
   - over an Explorer window in the session, to copy into the folder it shows;
   - over the desktop of the session, to copy to the desktop;
   - anywhere else, to copy to the fallback folder.

The fallback folder is the Desktop by default. You can change it from the agent tray menu, under Fallback folder: Desktop, Downloads, or Choose to pick any folder.

The usual Windows copy dialog appears in the session. It shows the progress and asks what to do when a file already exists. When the copy ends, the status bar of the client shows the result, for example "Copied report.pdf to C:\Users\me\Documents".

Status bar messages that matter:

- **"No agent on the remote computer"**: the client sent hello messages for sixty seconds and got no answer. Start the agent in the session, then use Session, Look for the agent again.
- **"This .rdp file does not redirect drives"**: the .rdp file does not share any local drive, so drops will not work. Turn on drives in the .rdp file and reconnect.
- **"The session sees no redirected drive"**: the agent answered, but no `\\tsclient` drive is reachable from the session. The server may block drive redirection.
- **"Drop refused: not connected"**: there is no session yet.
- **"Drop refused: the RdpDrop agent has not answered yet"**: wait for "Agent ready".
- **"Drop refused: the RdpDrop agent is not running in the remote session"**: start the agent in the session.
- **"Drop refused"** with a path: only files and folders on a local drive letter can be dropped. Network (UNC) paths are refused, because the session reaches your files through drive redirection.
- **"Drop failed"**: the agent could not finish. The rest of the message gives its reason, for example a drive that is not shared or a copy error.

## How it works

The client hosts the Microsoft Remote Desktop ActiveX control and registers itself as a drop target on its windows. The agent runs in the tray of your session and listens on a static virtual channel named `RDPDROP`. When you drop files, the client does not send them. It sends their paths and the cursor position, and the agent copies the files from the redirected drives.

1. The client catches the drop and converts the cursor position to remote desktop coordinates.
2. It sends a message with the local paths and that position on the `RDPDROP` channel.
3. The agent finds the folder under the cursor: an Explorer window, the desktop, or the fallback folder.
4. The agent copies each item from `\\tsclient\<drive>\...` into that folder, with the Windows copy dialog.
5. The agent reports the result, and the client shows it in the status bar.

Messages are JSON objects sent as ASCII lines, and each side detects at run time how the control maps channel bytes to characters. See [docs/design.md](docs/design.md) for the details.

## Troubleshooting

- **The agent never becomes ready.** Check that the agent runs in the session: its icon should be in the tray. A server policy may also block virtual channels. The agent log says so when it cannot open the channel.
- **Drops are refused because of the drives.** Turn on drive redirection in the .rdp file, under Local Resources, More, Drives. On the server, check the policy "Do not allow drive redirection".
- **Files land on the desktop instead of the folder under the cursor.** The window under the cursor may not be an Explorer window showing a file system folder. A virtual folder such as "This PC" uses the fallback folder too. Coordinate scaling after a window resize can also be the cause. Please report it with both logs.
- **Where the logs are.** The client writes `%LOCALAPPDATA%\RdpDrop\logs\client-yyyyMMdd.log` on your computer. The agent writes `agent-yyyyMMdd.log` in the same folder on the server. Both keep fourteen days of logs. The client opens its folder from Help, Open log folder, and the agent from its tray menu, Open log folder.
- **Agent settings.** The agent keeps its settings in `%LOCALAPPDATA%\RdpDrop\agent.json` on the server:

  ```json
  {
    "fallback": "custom",
    "customFolder": "D:\\Inbox",
    "logLevel": "debug"
  }
  ```

  `fallback` is `desktop`, `downloads` or `custom`. `customFolder` is used when `fallback` is `custom`. `logLevel` is `debug`, `info`, `warn` or `error`, and `info` when absent. Restart the agent after editing the file by hand.

## Build from source

You need the .NET 10 SDK on Windows.

```
dotnet build RdpDrop.sln -c Release
dotnet test RdpDrop.sln
```

To publish a self contained single file executable:

```
dotnet publish src/RdpDrop.Client -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
dotnet publish src/RdpDrop.Agent -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Releases are built by GitHub Actions when a `v*` tag is pushed. The workflow builds, tests, publishes both applications and attaches the two zips to the release.

## Security notes

- The agent only reads from `\\tsclient\`, that is from the drives you chose to share. It only writes inside the session of the same user. It does not run as a service and has no privilege beyond your own.
- Messages carry file names and coordinates only. No file content goes through the channel.
- The client does not store credentials. Credential prompts come from the Microsoft control.

## Status

Version 0.1.0 is a first test release. Two things are not yet verified against a real server:

- The exact byte model the Microsoft control (`mstscax.dll`) uses for channel strings. The protocol detects both possible models, and both logs record the one in use.
- The detection of the folder under the cursor.

Feedback through [GitHub issues](https://github.com/freeKC/rdpdrop/issues) is welcome.

## License

MIT. See [LICENSE](LICENSE).
