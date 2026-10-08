# Privacy

Ripcord collects nothing about you. There is no telemetry, no analytics, no crash reporting service and no
account with this project. Nothing Ripcord does sends data to its authors.

This page covers the dotnet client on Windows.

## What it talks to

- **Your console**, directly, on your network or across the internet, to pair with it, wake it and stream from
  it.
- **PlayStation Network**, only if you sign in. Sign-in, the console list on your account, waking a console
  remotely and connecting over the internet all go to Sony's servers, under Sony's own privacy policy. The
  sign-in page is Sony's, shown inside Ripcord.
- **Public STUN servers**, only when you play over the internet: Google's and Cloudflare's. Before an internet
  session, Ripcord asks two of them what your public address looks like from outside your router, comparing
  the answers to learn how your router maps ports, so the console knows where to reach you. Each request
  carries nothing but a random number; the servers see your public IP address, as any server you contact does.

That is the whole list. Ripcord makes no other network connections of its own.

**An identifier for this PC.** Signing in sends Sony an ID made from this PC's Windows machine ID (the
`MachineGuid` in the registry, which any app on the PC can read), so Sony can tell your devices apart.
Pairing through your account and playing over the internet send a value hashed from that ID, to Sony's servers
and to your console. Pairing by code uses random bytes instead. Nothing else about
your PC is sent.

**One thing to know about signing in.** Ripcord asks PlayStation Network for the same set of permissions the
official client asks for, and one of them, `sbahn:pc.telemetry.publish`, would allow sending usage data to
Sony. Ripcord never uses it: nothing in the code sends anything to that service. Whether sign-in works without
asking for it has not been tested yet, and dropping it is on the roadmap.

## What it keeps on your machine

Everything lives in `%LocalAppData%\Ripcord`, apart from two things listed after the table:

| File | What it holds |
|---|---|
| `consoles.json` | The consoles you paired: names, addresses, and each console's pairing credential, encrypted with Windows DPAPI for your user account |
| `account.json` | If you signed in, your PlayStation Network refresh token, encrypted the same way |
| `settings.json` | Your settings and controller bindings |
| `WebView2\` | The sign-in page's browser profile, including Sony's sign-in cookies |
| `state\crash.log` | What went wrong, if the app crashed |
| `state\session-trace-*.csv` | Per-stream figures: frame rates, loss, round-trip time, bitrate, and at the top the app version, your graphics adapter's name and the settings asked for. One per stream; the newest 20 are kept and older ones deleted |
| `state\diagnostics-*.txt` | What **F8** saves during a stream: the panel's readings, your settings, the app version and Windows version |
| `state\*-trace.log` | Diagnostic logs some features write while you use them |
| `state\video-dump-*.bin` | Only with the `RIPCORD_DUMP_VIDEO` developer switch set: the first seconds of the stream's video, which can show your PSN online ID in the console's own notifications |

Outside that folder: a desktop shortcut, if you make one from a console's menu, is a file on your desktop named for
the console, and stays until you delete it. And the entries in Ripcord's taskbar jump list, which name your paired
consoles, are kept by Windows; Ripcord rebuilds the list from the consoles you have paired, so removing a console
removes its entry.

If Windows can't provide DPAPI, Ripcord doesn't fall back to saving them unencrypted: it saves neither, and
Settings says so.

None of these files leave your machine unless you send them to someone. If you attach one to a bug report,
read it first: the logs can contain your console's name and its address on your network, and, if you played
over the internet, your public address and the console's.

To remove everything, delete `%LocalAppData%\Ripcord`. Signing out in Ripcord deletes `account.json`.

## Questions

Open an issue, or for anything sensitive, see [`SECURITY.md`](SECURITY.md).
