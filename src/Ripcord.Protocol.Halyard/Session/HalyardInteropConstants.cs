using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;

namespace Ripcord.Protocol.Halyard.Session;

/// <summary>
/// The bundled default source for the v1 protocol's interoperability constants — the control-plane KDF
/// tables (a PS5 pair and a PS4 pair), the four field context keys, the registration key table, the
/// material-wrap table, and the context selector offset.
///
/// <para>
/// <b>What these are.</b> Values the console itself computes against. They are read by every client that
/// speaks this protocol and cannot be altered without breaking interoperability, so they are interface facts
/// rather than authored expression. They are data, not logic: this type only parses and hands them to the
/// existing <see cref="HalyardControlSecrets"/> / <see cref="HalyardRegistrationSecrets"/> seams. See the
/// repository's <c>NOTICE</c> for the interoperability statement.
/// </para>
///
/// <para>
/// <b>They are the last resort, not the first.</b> Both loaders resolve in the order: explicit environment
/// override → platform config directory → dev-tree fixture → this bundled default. A developer with local
/// material therefore keeps using it, and cannot silently end up testing against the bundle. Every loader
/// reports which path won through its <c>source</c> out-parameter; that string surfaces in the diagnostics
/// overlay, so check it rather than assuming.
/// </para>
///
/// <para>
/// <b>Omitting the bundle.</b> The JSON is embedded conditionally. Build with
/// <c>-p:BundleInteropConstants=false</c> and it is not compiled in; <see cref="IsBundled"/> becomes
/// <see langword="false"/>, both accessors return <see langword="null"/>, and the session falls back to the
/// passthrough stub exactly as it does on a machine with no constants — a clean, diagnosable failure rather
/// than a crash. No code change is needed to produce that build.
/// </para>
/// </summary>
public static partial class HalyardInteropConstants
{
    private const string ResourceName = "Ripcord.Protocol.Halyard.Data.halyard-v1-constants.json";

    private static readonly Lazy<Bundle?> Loaded = new(Read, isThreadSafe: true);

    /// <summary>Whether this build has the constants compiled in (see the remarks on omitting them).</summary>
    public static bool IsBundled => Loaded.Value is not null;

    /// <summary>The bundled control-plane secrets, or <see langword="null"/> if this build omits them.</summary>
    public static HalyardControlSecrets? Control()
    {
        var b = Loaded.Value;
        if (b?.KdfTable1 is null || b.KdfTable2 is null || b.ContextKeys is null)
            return null;

        var k = b.ContextKeys;
        if (k.CodecInHigh is null || k.SelectorOne is null || k.SelectorZero is null || k.FallbackZero is null)
            return null;

        // PS4 (mode 0) tables are optional — an older bundle without them still keys PS5 fine, so pass them
        // only when both are present.
        ReadOnlyMemory<byte> ps4Table1 = b.Ps4KdfTable1 is null ? default : Hex(b.Ps4KdfTable1);
        ReadOnlyMemory<byte> ps4Table2 = b.Ps4KdfTable2 is null ? default : Hex(b.Ps4KdfTable2);

        return new HalyardControlSecrets(
            Hex(b.KdfTable1), Hex(b.KdfTable2),
            new HalyardFieldContextKeys(Hex(k.CodecInHigh), Hex(k.SelectorOne), Hex(k.SelectorZero), Hex(k.FallbackZero)),
            ps4Table1, ps4Table2);
    }

    /// <summary>
    /// The bundled registration secrets plus the field context key the registration cipher needs, or
    /// <see langword="null"/> if this build omits them.
    /// </summary>
    public static (HalyardRegistrationSecrets Secrets, byte[] ContextKey)? Registration()
    {
        var b = Loaded.Value;
        if (b?.RegistrationTable is null || b.ContextKey is null)
            return null;

        var secrets = new HalyardRegistrationSecrets(
            Hex(b.RegistrationTable),
            b.SelectorOffset,
            b.MaterialWrapTable is null ? default : Hex(b.MaterialWrapTable));
        return (secrets, Hex(b.ContextKey));
    }

    private static Bundle? Read()
    {
        try
        {
            using Stream? s = typeof(HalyardInteropConstants).Assembly.GetManifestResourceStream(ResourceName);
            if (s is null)
                return null; // built with -p:BundleInteropConstants=false

            return JsonSerializer.Deserialize(s, BundleContext.Default.Bundle);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return null; // malformed bundle behaves as an absent one: fall through to the stub
        }
    }

    private static byte[] Hex(string value) => Convert.FromHexString(value);

    // Source-generated rather than reflection-based, and this is the one that matters most. Under
    // PublishTrimmed the reflection serializer throws NotSupportedException; this loader would then return
    // null, HalyardControlSecrets would be absent, IsControlEstablished would be false, and the launchSpec
    // would go out UNENCRYPTED. A trimmed build failing open on encryption is not an acceptable degradation,
    // so the bundle read must not depend on reflection at all.
    //
    // Nested so it can see the private DTOs below without widening them to satisfy the generator.
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(Bundle))]
    private partial class BundleContext : JsonSerializerContext;

    private sealed class Bundle
    {
        public string? RegistrationTable { get; set; }
        public string? MaterialWrapTable { get; set; }
        public int SelectorOffset { get; set; }
        public string? ContextKey { get; set; }
        public string? KdfTable1 { get; set; }
        public string? KdfTable2 { get; set; }
        public string? Ps4KdfTable1 { get; set; }
        public string? Ps4KdfTable2 { get; set; }
        public ContextKeysDto? ContextKeys { get; set; }
    }

    private sealed class ContextKeysDto
    {
        public string? CodecInHigh { get; set; }
        public string? SelectorOne { get; set; }
        public string? SelectorZero { get; set; }
        public string? FallbackZero { get; set; }
    }
}
