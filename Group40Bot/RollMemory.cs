using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Group40Bot;

/// <summary>
/// Stores message→expression mappings for public rerolls.
/// </summary>
public sealed class RollMemory
{
    private readonly Dictionary<ulong, StoredRoll> _map = new();
    private readonly object _gate = new();

    public void Store(ulong messageId, StoredRoll roll)
    {
        lock (_gate) _map[messageId] = roll;
    }

    public bool TryGet(ulong messageId, out StoredRoll roll)
    {
        lock (_gate) return _map.TryGetValue(messageId, out roll!);
    }

    public void Remove(ulong messageId)
    {
        lock (_gate) _map.Remove(messageId);
    }
}

public sealed record StoredRoll(string Expr);