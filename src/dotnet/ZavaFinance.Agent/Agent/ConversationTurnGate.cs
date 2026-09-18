namespace ZavaFinance.Core.Agent;

internal sealed class ConversationTurnGate
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private sealed class Entry
    {
        internal SemaphoreSlim Semaphore { get; } = new(1, 1);
        internal int Users { get; set; }
    }

    internal async Task<IDisposable> EnterAsync(string key, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!)) _entries.Add(key, entry = new());
            entry.Users++;
        }
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
            return new Lease(this, key, entry);
        }
        catch
        {
            Release(key, entry, entered: false);
            throw;
        }
    }

    private void Release(string key, Entry entry, bool entered)
    {
        if (entered) entry.Semaphore.Release();
        lock (_entries)
        {
            if (--entry.Users == 0)
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Lease(ConversationTurnGate owner, string key, Entry entry) : IDisposable
    {
        public void Dispose() => owner.Release(key, entry, entered: true);
    }
}
