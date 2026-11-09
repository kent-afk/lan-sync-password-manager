using System.Text.Json.Serialization;
using Pm.Core.Vault;

namespace Pm.Sync;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(InfoResponse))]
[JsonSerializable(typeof(PairRequest))]
[JsonSerializable(typeof(PairResponse))]
[JsonSerializable(typeof(SyncRequest))]
[JsonSerializable(typeof(SyncResponse))]
[JsonSerializable(typeof(PeerCandidate))]
[JsonSerializable(typeof(Keyset))]
internal partial class PmSyncJsonContext : JsonSerializerContext
{
}
