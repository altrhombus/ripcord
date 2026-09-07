using Ripcord.ProtocolLab;

// Ripcord ProtocolLab - a console harness for driving and verifying the connect pipeline against
// real hardware and captures, without the full UI. Stages 0-4 of docs/history/phase1-lan-build-plan.md.
//
// Cloud commands read the OAuth client credential from the environment (RIPCORD_CLIENT_ID,
// RIPCORD_CLIENT_SECRET, RIPCORD_REDIRECT_URI) or from client.json in the config directory, so no
// secret is embedded. `signin` stores a refresh token in the same account.json the app reads, so
// signing in here signs the app in too; RIPCORD_REFRESH_TOKEN still works for a token obtained
// out of band.

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "discover" => await LabCommands.DiscoverAsync(),
        "register" => await LabCommands.RegisterAsync(args),
        "accountpair" => await LabCommands.AccountPairAsync(args),
        "accountconnect" => await LabCommands.AccountConnectAsync(args),
        "sessions" => await LabCommands.SessionsAsync(args),
        "replay" => await LabCommands.ReplayAsync(args),
        "authurl" => LabCommands.PrintAuthUrl(),
        "signin" => await LabCommands.SignInAsync(),
        "cloud" => await LabCommands.CloudAsync(),
        "cloudwake" => await LabCommands.CloudWakeAsync(args),
        "signaling" => await LabCommands.SignalingAsync(args),
        "wanconnect" => await LabCommands.WanConnectAsync(args),
        "connect" => await LabCommands.ConnectAsync(args),
        "mediademo" => await LabCommands.MediaDemoAsync(),
        "vectors" => LabVectors.Emit(args),
        _ => Unknown(args[0]),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static int Unknown(string verb)
{
    Console.Error.WriteLine($"unknown command: {verb}");
    PrintUsage();
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        Ripcord ProtocolLab

        Commands:
          discover                       LAN SRCH discovery; lists reachable consoles
          register <ip> <passcode> <accountId> [clientIdHex] [ps4|ps5]   live PIN pairing -> pairing record (Stage 5)
          accountpair <ip> <duid> [ps4|ps5]   live account (no-PIN) pairing: the console delivers the seed over the cloud
          Console.WriteLine("  accountconnect <ip> <duid> [ps4|ps5]   open a session over the account route (pair first)");
          sessions <id>... | sessions leave <id>...   read back or leave cloud sessions (PSN has no list endpoint)
          replay synth                   run synthetic stream packets through the demuxer (Stage 0)
          replay <file>                  replay length-prefixed packets from a file through the demuxer
          authurl                        print the OAuth authorize URL (needs client env vars)
          signin                         full first-run sign-in: prints the URL, takes the redirect on stdin
          cloud                          list the account's consoles (uses the stored session, or RIPCORD_REFRESH_TOKEN)
          cloudwake <duid>               ask PSN to wake a console, by the duid `cloud` prints
          signaling <duid> [ip] [port]   drive session-create -> command -> OFFER (stops before the push channel)
          wanconnect <duid>              full rendezvous: session -> wake -> STUN -> OFFER -> read console candidates
          connect <ip> [ctrlPort] [strmPort]   run the direct /sess handshake (stub crypto; MAC will be rejected)
          mediademo                      wire synthetic stream -> demux -> decode pipeline, and a controller frame -> input packet (Stage 6)
          vectors [outPath]              emit control-crypto known-answer vectors for the ripcord-3ds port
        """);
}
