# Global Mouse & Keyboard

Use one mouse and keyboard with several Windows computers. Move the pointer off the edge of one computer's screen and it appears on the next computer. The keyboard follows the mouse, and the clipboard (text and images) is shared.

Computers find each other automatically, also when they are on different home networks such as `192.168.0.x` and `192.168.1.x` behind two routers. Discovery uses only those two networks.

- One file: `GlobalMK.exe` (about 270 KB). Nothing to install. Runs on Windows 10 and 11 with the built-in .NET Framework 4.8.
- The same exe runs on every computer. You choose its role in the app.
- All traffic is encrypted and authenticated with a passphrase you choose.
- Optional helper service: type into a client's lock screen and UAC prompts with the shared keyboard.

## How it works

| Role | What it does |
| --- | --- |
| **Server** | The computer the physical mouse and keyboard are plugged into. It watches your input and sends it to whichever computer the pointer is on. You arrange the screen layout here. |
| **Client** | Every other computer. It receives mouse and keyboard input from the server and replays it. |

Each computer looks for the others, listens for connections, and connects to the ones it finds. Only **one** of two computers needs to be able to reach the other. See [How computers find each other](#how-computers-find-each-other).

## Before you start

You need:

- `GlobalMK.exe` on each computer (from `dist\` after [building](#build-from-source), or copied from wherever you keep it).
- A passphrase of at least 8 characters to use on every computer.

## Set up the server

Do this on the computer with the physical mouse and keyboard.

1. Run `GlobalMK.exe`. The window opens on the **Setup** tab.
2. Under **This computer is**, choose **Server**.
3. Check **Computer name**. Each computer needs a different name.
4. Enter your **Passphrase**.
5. Choose **Save & apply**.
6. Choose **Allow through Windows Firewall…** and approve the Windows prompt. This lets other computers find and connect to this one. The rule only allows home and private network addresses.

The header shows **Waiting for other computers**.

## Set up each client

Do this on every computer you want to control.

1. Run `GlobalMK.exe`.
2. Under **This computer is**, choose **Client**.
3. Give it a unique **Computer name**.
4. Enter the **same passphrase** you used on the server.
5. Choose **Save & apply**.
6. Choose **Allow through Windows Firewall…** and approve the Windows prompt.

When the connection works, the client's header shows **Controlled by** followed by the server's name. The server's header shows **1 computer connected**.

- On the same network this takes a few seconds.
- On another home network it takes up to about a minute.

If they don't connect, open the **Connections & log** tab. The log says what went wrong. See [Troubleshooting](#troubleshooting).

To use the shared keyboard on this computer's lock screen and in UAC prompts, also [install the helper service](#type-on-the-lock-screen-and-in-uac-prompts).

## Arrange the screens

On the server, open the **Screen layout** tab. Each computer appears as a group of rectangles, one per monitor, sized to its resolution.

1. Drag each computer so that its screens touch the edge you want the pointer to cross. Edges snap together.
2. Release the mouse. The layout saves immediately.

The pointer crosses wherever screens line up. Gaps between computers are fine, as long as the screens overlap along the edge you cross. A new computer first appears to the right of the others.

To check the result, move the pointer off that edge. It should appear on the other computer, and typing should go there too.

> **Tip:** Right-click an offline computer to forget it.

## Use it day to day

| Action | How |
| --- | --- |
| Move to another computer | Push the pointer off the edge that faces it in the layout. |
| Keep the pointer on the current computer | Press **Scroll Lock** or **Ctrl+Alt+Shift+L**. Press it again to unlock. Or use the tray menu. |
| Bring the pointer back to the server | Press **Ctrl+Alt+Shift+H**, or choose **Bring cursor back to this computer** in the tray menu. |
| Copy and paste between computers | Copy normally on one computer, then paste on another. Text and images are shared. |
| Hide the window | Close it. The app keeps running in the notification area. To quit, right-click the tray icon and choose **Exit**. |

While you hold a mouse button, the pointer does not cross to another computer. This lets you drag windows to a screen edge without switching.

If the pointer jumps to another computer when you aim for a window's close button or the Start button, raise one of the **Switching** options on the **Setup** tab:

- **Wait … ms**: the pointer must stay at the edge this long before it crosses.
- **ignore … px near corners**: edges don't switch within this distance of a corner.

## How computers find each other

You don't need to enter any addresses in the usual home setups. The app uses two methods, and only on your two home networks, `192.168.0.x` and `192.168.1.x`. It ignores messages from any other address, and it sends nothing to any other address.

| Method | Finds computers… | How |
| --- | --- | --- |
| Broadcast | on the same network | Every computer announces itself every 3 seconds. |
| Probes | on the other home network | Every minute, each computer sends a small "who's there?" message to every address on `192.168.0.x` and `192.168.1.x`, and to computers it found or connected to before. Every copy of the app answers. |

When a computer gets an answer, it connects. Both roles do this, so whichever computer can reach the other makes the connection.

### Example: a router plugged into another router

Router A gives `192.168.1.x` addresses. Router B is plugged into router A and gives `192.168.0.x` addresses.

- A computer behind router B can reach computers on router A, but not the other way round.
- The computer behind router B probes every address on `192.168.1.x`. The other computer answers.
- The two connect. It doesn't matter which of the two is the server.

The **Found automatically** list on the **Connections & log** tab shows each computer that was found and whether it's on **this network** or an **other home network**.

### Probes stay inside your home

Probes only go to `192.168.0.x` and `192.168.1.x`. Each probe has a limit of 3 hops, enough to cross two routers. A probe that nobody answers is dropped within a few hops of your router, and private addresses like these are never routed on the internet.

To turn this off, clear **Also look for computers on the other home network** on the **Setup** tab. Computers on the same network are still found.

### When automatic discovery isn't enough

Enter the other computer's IP address in **Connect to** if:

- Neither computer can reach the other. For example, two routers side by side, each connected to the internet on its own. In that case use one of these options:
  - Forward TCP port 24860 on one router to the computer behind it, and enter that router's address.
  - Install [Tailscale](https://tailscale.com) or a similar private network on both computers, and enter the Tailscale address (`100.x.x.x`).
- A computer is on a network other than `192.168.0.x` or `192.168.1.x`. Discovery doesn't look there.

To check which direction works, open PowerShell on one computer and test the other computer's address and port:

```powershell
Test-NetConnection 192.168.1.20 -Port 24860
```

Replace `192.168.1.20` with the other computer's address. `TcpTestSucceeded : True` means this computer can reach that one. Enter the address on the computer where the test succeeds.

Computers that connected once are remembered and dialed directly afterwards.

### Ports

| Port | Used for |
| --- | --- |
| TCP 24860 | Mouse, keyboard and clipboard traffic (configurable on the **Setup** tab) |
| UDP 24861 | Finding computers: broadcasts, probes and answers (always the TCP port + 1) |

If you change the port, change it on every computer, then choose **Allow through Windows Firewall…** again.

## Type on the lock screen and in UAC prompts

Windows doesn't let normal apps type into the lock screen, the sign-in screen or UAC prompts. These run on a protected "secure desktop". The helper service makes this possible on a client computer, so you can unlock it with the shared keyboard.

### What you need

- Administrator rights on the client computer.
- The app on that computer running as administrator. Choose **Restart as administrator**, and select **Start with Windows** while it runs as administrator so it starts that way automatically.

### Install the helper service

On the client computer:

1. On the **Setup** tab, find **Lock screen and UAC prompts**.
2. Choose **Install helper service…** and approve the Windows prompt.
3. If the app isn't running as administrator yet, choose **Restart as administrator**.

When it's ready, the row says **Ready: the shared keyboard and mouse also work on this computer's lock screen and in UAC prompts.**

To check that it works:

1. Lock the client with **Windows+L** using its own keyboard.
2. Move the shared pointer onto the client and type your PIN or password with the shared keyboard.
3. To test UAC, open an administrator command prompt on the client with the shared mouse, and approve the prompt with it.

### How the helper works

The installer copies `GlobalMK.exe` to `C:\Program Files\GlobalMK` and registers the service **Global Mouse & Keyboard helper**. The service runs that protected copy, never a file from a folder you can write to.

The service starts a small input helper in your session. The helper:

- follows whichever screen is active: the normal desktop, the lock screen or a UAC prompt;
- has no network connection: only the app on the same computer gives it input;
- accepts only mouse and keyboard events, through a local channel that only administrator programs can open;
- is checked by the app before use: the app only uses a helper running as the system account, never an impostor.

Requiring an administrator app keeps UAC meaningful. A normal program on the client can't use the helper to approve a UAC prompt by itself.

### Update or remove the helper service

- After you replace `GlobalMK.exe` with a newer version, the row shows **Update helper service…**. Choose it to update the copy in Program Files.
- To remove it, choose **Remove helper service…**. This deletes the service and `C:\Program Files\GlobalMK`.

The helper writes its log to `C:\Program Files\GlobalMK\helper.log` and `service.log`.

## Administrator windows and startup

Windows blocks programs from sending input to windows that run as administrator, such as Task Manager, installers and admin command prompts. It also stops the server from capturing input while such a window has focus.

To make the shared mouse and keyboard work in those windows, choose **Restart as administrator** on each computer.

To start the app when you sign in, select **Start with Windows** and choose **Save & apply**:

- If the app runs as administrator when you do this, it creates a Task Scheduler task that starts it as administrator at sign-in, with no prompt.
- Otherwise it adds a normal startup entry that runs without administrator rights.

Select **Start minimized to the notification area** to start without showing the window.

## Troubleshooting

Check the **Connections & log** tab first. The log explains most failures in plain language.

| Log message or symptom | What to do |
| --- | --- |
| `the passphrases don't match` | Enter the same passphrase on every computer, then choose **Save & apply** on each. |
| `both computers are set to Server` (or `Client`) | Set exactly one computer to **Server**. |
| `both computers are named …` | Give each computer a different **Computer name**. |
| A computer isn't found | Choose **Allow through Windows Firewall…** on both computers; answers to probes need the UDP rule. Wait a minute for probes on other networks. If it's still missing, enter its address in **Connect to** (see [When automatic discovery isn't enough](#when-automatic-discovery-isnt-enough)). |
| `Found … but can't connect to it` | The other computer was found, but its firewall blocks TCP port 24860. Choose **Allow through Windows Firewall…** on it. |
| `Can't reach 192.168.x.x:24860 — timed out` | The computer in **Connect to** is off, isn't running the app, or its firewall blocks the port. |
| `Refused connection from … not a private network address` | The connection came from a public address. If that's expected, clear **Only accept connections from home/private network addresses**. |
| `Cannot listen on port 24860` | Another program uses the port. Choose a different **Port** on every computer. |
| The pointer won't cross to a computer | Make sure the screens line up along that edge in **Screen layout**. Scroll Lock may be on: check the header for **locked**. |
| A key seems stuck on a client | Press and release that key once. Moving the pointer back to the server also releases every key on the client. |
| Clipboard doesn't sync | Check that **Share the clipboard** is selected on both computers. Copied files are not shared, only text and images. |
| Helper row: **only an app running as administrator may use it** | Choose **Restart as administrator** on that computer. |
| Helper row: **Another program is pretending to be the helper** | Something else created the helper's channel first. Restart the computer. If the message stays, scan the computer for malware. |
| No pointer is visible on a client without a mouse | Windows hides the pointer on computers with no mouse attached. Turn on **Settings > Accessibility > Mouse > Mouse keys**, or plug in any mouse. |

Settings and the app's log are stored in `%APPDATA%\GlobalMK\` (`config.json` and `log.txt`).

## Known limitations

- **Sign-in screen after a restart.** Until someone signs in to the client, the app isn't running there, so use the client's own keyboard once. The lock screen works with the [helper service](#type-on-the-lock-screen-and-in-uac-prompts).
- **Ctrl+Alt+Del** always acts on the server, and the app can't send it to a client. Clients that require Ctrl+Alt+Del to unlock need their own keyboard for that step.
- **Win+L** locks the server. When the server locks, the pointer returns to the server automatically.
- **Windows only.** macOS and Linux are not supported.
- **Dragging files between computers** is not supported. Use the shared clipboard for text and images.

## Security

- Computers authenticate each other with the shared passphrase. A computer with the wrong passphrase can't connect or send input.
- Each connection uses a fresh elliptic-curve Diffie–Hellman (P-256) key exchange, mixed with a key derived from the passphrase (PBKDF2-SHA256, 200,000 iterations). Messages are encrypted with AES-256 in counter mode and authenticated with HMAC-SHA256. Sequence numbers protect against replayed, reordered or dropped messages.
- Keystrokes, including passwords you type on a client, travel encrypted.
- The passphrase is stored encrypted for your Windows account (DPAPI).
- By default, only private addresses can connect: `10.x`, `172.16–31.x`, `192.168.x`, link-local and `100.64–127.x` (Tailscale).
- Discovery messages contain only the computer's name, role and port, and only messages from `192.168.0.x` and `192.168.1.x` are accepted. Answering a probe never starts a connection; connections always need the passphrase.

Use a passphrase that other people can't guess. With the helper service installed, anyone who knows the passphrase and can reach the computer can type into its lock screen and UAC prompts, so treat the passphrase like an administrator password. Someone on your network who could intercept a connection attempt could try to guess the passphrase offline.

## Build from source

You need the .NET SDK (6 or later) and the .NET Framework 4.8 Developer Pack. Both are free from Microsoft.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

The script builds the app, runs the self-tests, and writes `dist\GlobalMK.exe`.

The self-tests cover:

- layout geometry;
- encryption and tamper detection;
- real loopback connections, plus role and passphrase checks and duplicate-connection handling;
- clipboard transfer;
- the helper channel: message validation, rejecting malformed input and impostors, and falling back to local input;
- discovery: which networks are used, probes, and connecting automatically to a computer found by a probe.

They don't install input hooks or the service.

### Project layout

| Path | Contents |
| --- | --- |
| `src/GlobalMK/Core` | Networking, discovery, encryption, protocol, layout geometry, helper channel, settings. No UI. |
| `src/GlobalMK/Input` | Server input capture (low-level hooks) and client input replay (`SendInput`). |
| `src/GlobalMK/Service` | Helper service, input helper (secure desktop) and their installer. |
| `src/GlobalMK/UI` | Main window, layout editor, tray icon, clipboard sync, firewall and startup helpers. |
| `tests/GlobalMK.Tests` | Self-test runner. |
| `tools/make-icon.ps1` | Regenerates `app.ico`. |
