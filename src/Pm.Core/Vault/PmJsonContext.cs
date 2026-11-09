using System.Text.Json.Serialization;

namespace Pm.Core.Vault;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(Vault))]
[JsonSerializable(typeof(Entry))]
[JsonSerializable(typeof(Keyset))]
[JsonSerializable(typeof(DeviceInfo))]
[JsonSerializable(typeof(Config))]
[JsonSerializable(typeof(Peer))]
internal partial class PmJsonContext : JsonSerializerContext
{
}
