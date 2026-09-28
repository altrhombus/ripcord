using System.Text.Json.Serialization;

namespace Ripcord.Cloud.Halyard;

/// <summary>
/// Every JSON shape the cloud client reads or writes, source-generated so none of it depends on reflection, which
/// trimming removes. The request bodies used to be anonymous types, which source generation cannot see; they are
/// named records now, each property declared in the order the body carries it, because the serializer writes
/// them in declaration order and several of these orders were matched against our own captures.
/// <c>HalyardCloudClientWireTests</c> pins every body byte for byte against what the anonymous types produced.
/// Default options, as before: the same encoder and no null-skipping.
/// </summary>
[JsonSerializable(typeof(HalyardAccountInfo))]
[JsonSerializable(typeof(HalyardPushServerInfo))]
[JsonSerializable(typeof(HalyardConsoleListResponse))]
[JsonSerializable(typeof(HalyardSessionsResponse))]
[JsonSerializable(typeof(HalyardCommandResponse))]
[JsonSerializable(typeof(HalyardTokenResponse))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(CreateSessionBody))]
[JsonSerializable(typeof(CommandBody))]
[JsonSerializable(typeof(OfferBody))]
[JsonSerializable(typeof(SignalingEnvelope))]
internal sealed partial class HalyardCloudJsonContext : JsonSerializerContext
{
}

// ---- session create ----

internal sealed record CreateSessionBody(
    [property: JsonPropertyName("remotePlaySessions")] CreateSessionEntry[] RemotePlaySessions);

internal sealed record CreateSessionEntry(
    [property: JsonPropertyName("members")] CreateSessionMember[] Members);

internal sealed record CreateSessionMember(
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("deviceUniqueId")] string DeviceUniqueId,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("pushContexts")] PushContextRef[] PushContexts);

internal sealed record PushContextRef(
    [property: JsonPropertyName("pushContextId")] string PushContextId);

// ---- the connect command ----

internal sealed record CommandBody(
    [property: JsonPropertyName("commandDetail")] CommandDetail CommandDetail);

internal sealed record CommandDetail(
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("duid")] string Duid,
    [property: JsonPropertyName("commandType")] string CommandType,
    [property: JsonPropertyName("parameters")] CommandParameters Parameters,
    [property: JsonPropertyName("messageDestination")] string MessageDestination);

internal sealed record CommandParameters(
    [property: JsonPropertyName("initialParams")] string InitialParams);

// ---- signaling ----

internal sealed record OfferBody(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("reqId")] int ReqId,
    [property: JsonPropertyName("error")] int Error,
    [property: JsonPropertyName("connRequest")] OfferConnRequest ConnRequest);

internal sealed record OfferConnRequest(
    [property: JsonPropertyName("sid")] int Sid,
    [property: JsonPropertyName("peerSid")] int PeerSid,
    [property: JsonPropertyName("skey")] string Skey,
    [property: JsonPropertyName("natType")] int NatType,
    [property: JsonPropertyName("candidate")] OfferCandidate[] Candidate,
    [property: JsonPropertyName("defaultRouteMacAddr")] string DefaultRouteMacAddr,
    [property: JsonPropertyName("localPeerAddr")] PeerAddress LocalPeerAddr,
    [property: JsonPropertyName("localHashedId")] string LocalHashedId);

internal sealed record OfferCandidate(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("addr")] string Addr,
    [property: JsonPropertyName("mappedAddr")] string MappedAddr,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("mappedPort")] int MappedPort);

internal sealed record PeerAddress(
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("platform")] string Platform);

internal sealed record SignalingEnvelope(
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("to")] SignalingRecipient[] To);

internal sealed record SignalingRecipient(
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("deviceUniqueId")] string DeviceUniqueId,
    [property: JsonPropertyName("platform")] string Platform);
