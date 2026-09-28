# Pairing a console port from the desktop — design note

Written 2026-09-27, for review before any code. It expands the ROADMAP item "Pair a console port from the
desktop app", proposed 2026-09-16, and supersedes nothing: until this is built and verified, every port keeps the
pairing flow it has.

**This is Ripcord's own protocol, not the console's.** Nothing here is derived from the vendor or from another
implementation, and nothing needs to be: it runs between two Ripcord programs. Its one external basis is
[RFC 9382](https://www.rfc-editor.org/rfc/rfc9382) (SPAKE2), read in its current text for this note. The
`[X]` marks below are the facts about the *console's* behaviour that this design depends on and our records do
not settle.

## What it is for

A port has to pair before it can play, and pairing needs two things a gamepad on a TV is bad at typing:
- **the PSN account id**, a nineteen-digit number;
- **the console's eight-digit code**.

`ports/ripcord-ps3` can read the account id off a PS3 that is signed in to PSN. A PS3 that is not signed in,
and every other port, still has nineteen digits to find and type.

**This moves both questions to a desktop that already has a keyboard and, often, a signed-in account.** The
desktop registers with the console on the port's behalf and hands the finished pairing record to the port. It is
written once in the ports' core and once in the engine, rather than per port.

It is an **alternative**, never a replacement: a port with no desktop beside it keeps its own flow.

## The flow, as a person sees it

1. **On the port:** Settings → Pair → *Pair from a computer*. The TV shows an eight-digit **hand-off code** and the
   port's name ("PS3 in the living room").
2. **On the desktop** (the dotnet client or the Mac app), the pairing surface lists it: *"PS3 in the living room
   wants to pair."* The person chooses which console it should pair with, and types the hand-off code.
3. **The desktop registers with the console,** by whichever route it would use for itself (below). If that
   needs the console's own code, the person reads it off the console and types it on the desktop.
4. **The port receives the record,** says *"Paired."*, and connects.

A wrong hand-off code fails at once on both sides, and the port shows a fresh code; there is no retry against the
same one (below).

## The security design

**The record is the secret.** `registkey` and `companion` are credentials for one console and one account. They
sit on the wrong side of `CLAUDE.md`'s generic-versus-personal line, so they must never cross the LAN in the
clear. They must also never reach whoever answers an announce first.

**The hand-off code authenticates both ends and keys the transfer, through SPAKE2** (RFC 9382). A plain
code-derived key is not enough:
- An eight-digit code is about 10⁸ values. Anyone who captured an exchange keyed by, say, HKDF(code) could try
  them all offline in seconds and read the record.
- A password-authenticated key exchange makes the code worth one guess per live attempt, and the port allows one
  attempt per code. An attacker on the LAN therefore gets a one-in-10⁸ chance per code the person shows,
  and a code shows only while someone is pairing.

The choices:

| Choice | Decision | Why |
|---|---|---|
| Ciphersuite | SPAKE2 over **P-256, SHA-256, HKDF, HMAC** (RFC 9382 §6) | Both cores already have P-256: Mbed TLS's ECP layer in `libripcord`, RustCrypto's `p256` in the engine. SHA-256 and HMAC too. Nothing new is vendored or fetched |
| Roles | The desktop is A, the port is B | A sends first. The port only has to answer, which suits a small device |
| Identities in the transcript | A = `ripcord-desktop`, B = the port's id from its announce | Binds the exchange to the port the person chose |
| `w` from the code | The code's eight digits as ASCII, through HKDF-SHA-256 with a fixed info string, reduced mod p | RFC 9382 §3.2 says a memory-hard function is typical for a *password*. The code is random, single-use and short-lived, so an MHF buys nothing against online guessing, which is the only guessing SPAKE2 leaves. A slow MHF would cost a PS3 more than it costs an attacker |
| M and N | RFC 9382's P-256 points, from §6 | Taken from the RFC and checked against its Appendix A generation, never recalled |
| Key confirmation | Both sides send their `c = MAC(Kc, TT)` (RFC 9382 §4) before anything else | Nothing secret moves until both ends have shown they hold the same key |
| Record encryption | **AES-128-GCM under `Ke`** (16 bytes from SHA-256's half), one message, a fresh 12-byte nonce | Both cores have AES-GCM already |
| Attempts | One per code. Any failure (bad confirmation, bad tag, a peer that is not a group member) ends the attempt and the port shows a new code | Turns the code into a one-time secret |

**Implementation rules from the RFC's security section:**
- **Random scalars** must be uniform and never reused.
- **Received points are validated as group members.** The port aborts on one that is not.
- **Point operations are constant-time.** For the engine that is RustCrypto's `p256`. For the C core it is Mbed TLS,
  whose constant-time behaviour on each port's toolchain is `[X]`.

**What this does not defend against:** someone who can see the TV and is on the LAN at the moment of pairing.
That person could type the code themselves, which is the same trust the console's own pairing code has.

## The wire format

Two messages over the LAN, on one port number of Ripcord's own:
- **The candidate:** 9340, clear of the console's 9295–9304 range and of the ports' own sockets.
- **Registrations:** unregistered with IANA, as the console's ports are. `[X]` until it is checked for clashes on
  the networks the ports run on.

**The announce** is UDP broadcast every second while the port shows a code: a fixed magic, a version, and the
port's id (a random 16 bytes made at first boot), its display name, and its platform. Nothing secret, since
anything on the LAN can read it.

**The exchange** is TCP. The desktop connects to the announcing address, and every frame is a two-byte length
and a type:

| # | From | Carries |
|---|---|---|
| 1 | desktop | `HELLO`: version, then SPAKE2's `pA` (uncompressed point) |
| 2 | port | `pB` and `cB` |
| 3 | desktop | `cA` |
| 4 | port | `READY`: the port shows "Waiting for your computer to finish" |
| 5 | desktop | `RECORD`: nonce ‖ AES-GCM(Ke, record) |
| 6 | port | `DONE` or an error code |

**The desktop registers with the console between 4 and 5**, which can take minutes if the person has to go and
read the console's code. So the connection stays open with a keepalive, and 5 has a deadline the port shows.

**The record** is the fields a pairing already has: host, console id, name, family, account id, registration key
and companion. It is serialised as the ports' pairing file already is, so the port stores it with the code it
has.

## What the console has to allow `[X]`

The design stands or falls on two facts no capture of ours has settled.

1. **Is a pairing tied to the device that made it?**
   - **What our records show.** A PIN registration's body carries only `Client-Type` and the account id. No
     device identity is sent (`libripcord/session/halyard_regist_message.c`). The device id goes out later,
     as `RP-Did` in `/sess/ctrl`.
   - **What the vendor client shows.** In cap73 (2026-08-20), the vendor client on a second device presented
     the byte-identical registkey recorded from another device fifteen days earlier. So one registkey
     served one account and console across two of the vendor's devices (journal, "cap73 ... reframes the
     whole question"). That is the vendor client's own pairing, not a record moved between clients, and it
     is itself `[X]`.
   - **What it suggests.** A record made by the desktop should work from the port, which sends its own
     `RP-Did`. But whether the console remembers the first `RP-Did` a pairing was used with, and refuses
     another, is `[X]`.
   - **The check** is cheap and needs no new code: pair from the Mac, then connect from `ripcord-lab` with a
     different `RP-Did`. It is the first hardware item, because a "no" reshapes the design: the desktop would
     have to register with the port's device id.
2. **Does an account-route registration produce a record another device can use?**
   - **Why it differs.** The account route's rendezvous names the *desktop's* cloud device identity, not only
     its account.
   - **Until settled.** The hand-off uses the **code route** by default, with the person reading the
     console's code on the desktop, and the account route only once a check shows its record travels.

## Where it lives

- **The port half** goes in `libripcord`, since it is the ports' core: the announce, the TCP server, SPAKE2's B
  side over Mbed TLS's ECP functions, and AES-GCM. Each port adds a screen and a call.
  - **Point encoding** is `[X]`: whether the pinned Mbed TLS 2.28 reads compressed points for P-256. If it does
    not, M and N are stored uncompressed, expanded once from the RFC's values and checked in a test.
- **The desktop half and the reference** go in the Rust engine: SPAKE2's A side, the client, and the record
  encryption, in `ripcord-proto` sans-IO with `ripcord-net` driving it.
  - **The Mac** reaches it through RipcordKit as it does everything else.
  - **The dotnet client** reaches it through `ripcord-ffi` too, because `System.Security.Cryptography` has no
    elliptic-curve point arithmetic. SPAKE2 in C# would need a third-party library, which this project avoids.
    That is a first use of the engine from the dotnet client before Phase 4's general move. It is narrow and
    behind a seam, and it is worth saying so in `engine-plan.md` when it happens.
- **The vectors.** The engine is the reference for this protocol, so it generates the known-answer vectors:
  RFC 9382's own test vectors first, then ours for the transcript, the confirmation, and one encrypted record.
  The C core is tested against them, and `ripcord-diff` runs the two against each other. This inverts the usual
  arrangement, where .NET is the reference. Here that is necessary, and it should be stated in
  `engine/README.md`.
- **The desktop UI.**
  - **The dotnet client:** a discovery source behind a `Ripcord.Presentation` seam, feeding the add-console
    flow.
  - **The Mac:** a section of the pairing sheet.
  - **Both:** the port appears beside the consoles found on the network.

## Order of work

1. **Review this note.**
2. **The hardware check:** does a record made on the Mac work from a different `RP-Did`? It needs no new code,
   and it decides step 5's shape.
3. **The spec,** in `docs/` beside this note: the exact frames, magic, identities and HKDF info strings, with the
   RFC's vectors.
4. **The engine side and its vectors,** RFC 9382's first.
5. **The C side,** tested against the vectors and diffed against the engine.
6. **The desktop surfaces,** then the PS3 port's screen.
7. **End to end on hardware:** the PS3 port, a PS5, and each desktop.
