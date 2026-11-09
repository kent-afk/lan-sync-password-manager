using System.Security.Cryptography;
using System.Text;

namespace Pm.Core.Vault;

/// <summary>Entry-wise merge. Versions are Lamport counters: a device only bumps after seeing all known versions.</summary>
public static class Merger
{
    /// <summary>
    /// Merges remote entries into the local vault.
    /// <paramref name="Changed"/> is false when the local entries already contained every incoming entry,
    /// which lets callers skip rewriting the vault on an idle sync.
    /// </summary>
    public static (Vault Merged, int Conflicts, bool Changed) Merge(Vault local, Vault remote)
    {
        var byId = local.Entries.ToDictionary(e => e.Id, e => e);
        var conflicts = 0;
        var changed = false;

        foreach (var incoming in remote.Entries)
        {
            if (!byId.TryGetValue(incoming.Id, out var current))
            {
                byId[incoming.Id] = incoming;
                changed = true;
                continue;
            }

            // Deletion is authoritative: a tombstone wins over a live edit, even a higher version.
            // Two tombstones are reconciled deterministically. A conflict copy of a live edit would
            // reintroduce the secret the delete removed.
            if (current.Deleted || incoming.Deleted)
            {
                var tombstone = current.Deleted && incoming.Deleted
                    ? current.Version != incoming.Version
                        ? (current.Version > incoming.Version ? current : incoming)
                        : (string.CompareOrdinal(current.Device, incoming.Device) <= 0 ? current : incoming)
                    : (current.Deleted ? current : incoming);
                if (!ReferenceEquals(byId[incoming.Id], tombstone))
                {
                    byId[incoming.Id] = tombstone;
                    changed = true;
                }
                continue;
            }

            if (current.Version > incoming.Version)
            {
                // current is causally later; incoming is a stale edit
                continue;
            }

            if (current.Version < incoming.Version)
            {
                byId[incoming.Id] = incoming;
                changed = true;
                continue;
            }

            if (current.SameContent(incoming))
            {
                // same write no conflict
                continue;
            }

            // concurrent edits at the same version: deterministic winner loser preserved once
            var winner = string.CompareOrdinal(current.Device, incoming.Device) <= 0 ? current : incoming;
            var loser = ReferenceEquals(winner, current) ? incoming : current;
            byId[incoming.Id] = winner;
            byId[ConflictId(loser)] = MakeConflict(loser, Math.Max(current.Version, incoming.Version) + 1);
            conflicts++;
            changed = true;
        }

        var merged = new Vault { Entries = byId.Values.ToList() };
        merged.Entries.Sort((a, b) => string.CompareOrdinal(a.Title, b.Title));
        return (merged, conflicts, changed);
    }

    private static string ConflictId(Entry loser) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{loser.Id}:{loser.Version}:{loser.Device}")))
            [..24].ToLowerInvariant();

    private static Entry MakeConflict(Entry loser, long version)
    {
        var copy = loser.Clone();
        copy.Id = ConflictId(loser);
        copy.Version = version;
        copy.Device = "conflict";
        copy.Title = loser.Title + " (конфликт)";
        return copy;
    }
}
