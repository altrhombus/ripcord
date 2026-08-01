using Ripcord.ProtocolLab;

// Ripcord ProtocolLab - a console harness for driving and verifying the connect pipeline against
// real hardware and captures, without the full UI. Stages 0-4 of docs/phase1-lan-build-plan.md.
//
// Cloud commands read the OAuth client credential from environment variables so no secret is
// embedded: RIPCORD_CLIENT_ID, RIPCORD_CLIENT_SECRET, RIPCORD_REDIRECT_URI, RIPCORD_REFRESH_TOKEN.

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
        "replay" => await LabCommands.ReplayAsync(args),
        "authurl" => LabCommands.PrintAuthUrl(),
        "cloud" => await LabCommands.CloudAsync(),
        "connect" => await LabCommands.ConnectAsync(args),
        "mediademo" => await LabCommands.MediaDemoAsync(),
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
          replay synth                   run synthetic stream packets through the demuxer (Stage 0)
          replay <file>                  replay length-prefixed packets from a file through the demuxer
          authurl                        print the OAuth authorize URL (needs client env vars)
          cloud                          sign in via RIPCORD_REFRESH_TOKEN and list the account's consoles
          connect <ip> [ctrlPort] [strmPort]   run the direct /sess handshake (stub crypto; MAC will be rejected)
          mediademo                      wire synthetic stream -> demux -> decode pipeline, and a controller frame -> input packet (Stage 6)
        """);
}
