using System.Text.Json.Serialization;

namespace DomainNotice.Shared.Protocol;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    AllowTrailingCommas = false)]
[JsonSerializable(typeof(Envelope))]
[JsonSerializable(typeof(Enroll))]
[JsonSerializable(typeof(EnrollResult))]
[JsonSerializable(typeof(Hello))]
[JsonSerializable(typeof(Welcome))]
[JsonSerializable(typeof(Status))]
[JsonSerializable(typeof(Refused))]
[JsonSerializable(typeof(Show))]
[JsonSerializable(typeof(Shown))]
[JsonSerializable(typeof(Confirm))]
[JsonSerializable(typeof(ConfirmResult))]
[JsonSerializable(typeof(Withdraw))]
[JsonSerializable(typeof(ProveIdentity))]
[JsonSerializable(typeof(IdentityResult))]
[JsonSerializable(typeof(UpdateAgent))]
[JsonSerializable(typeof(PackageRequest))]
[JsonSerializable(typeof(UpdateStatus))]
[JsonSerializable(typeof(Ping))]
[JsonSerializable(typeof(Pong))]
public sealed partial class ProtocolJsonContext : JsonSerializerContext;
