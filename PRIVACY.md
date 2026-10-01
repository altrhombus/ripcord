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

That is the whole list. Ripcord makes no other network connections of its own.

**One thing to know about signing in.** Ripcord asks PlayStation Network for the same set of permissions the
official client asks for, and one of them, `sbahn:pc.telemetry.publish`, would allow sending usage data to
Sony. Ripcord never uses it: nothing in the code sends anything to that service. Whether sign-in works without
asking for it has not been tested yet, and dropping it is on the roadmap.

## What it keeps on your machine

Everything lives in `%LocalAppData%\Ripcord`, and nowhere else:

| File | What it holds |
|---|---|
| `consoles.json` | The consoles you paired: names, addresses, and each console's pairing credential, encrypted with Windows DPAPI for your user account |
| `account.json` | If you signed in, your PlayStation Network refresh token, encrypted the same way |
| `settings.json` | Your settings and controller bindings |
| `WebView2\` | The sign-in page's browser profile, including Sony's sign-in cookies |
| `state\crash.log` | What went wrong, if the app crashed |
| `state\session-trace-*.csv` | Per-stream figures: frame rates, loss, round-trip time, bitrate |
| `state\*-trace.log` | Diagnostic logs some features write while you use them |

None of these files leave your machine unless you send them to someone. If you attach one to a bug report,
read it first: the logs can contain your console's name and its address on your network.

To remove everything, delete `%LocalAppData%\Ripcord`. Signing out in Ripcord deletes `account.json`.

## Questions

Open an issue, or for anything sensitive, see [`SECURITY.md`](SECURITY.md).
