using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace HouseholdRelay;

public sealed class RelayFailure(int status, string message, int retryAfter = 0) : Exception(message)
{
    public int Status { get; } = status;
    public int RetryAfter { get; } = retryAfter;
}

public sealed class ClientRegistry
{
    private ClientAccess[] clients;
    private readonly Func<IReadOnlyList<ClientAccess>>? readCommitted;
    private readonly object refreshLock = new();
    public ClientRegistry(IEnumerable<ClientAccess> values, Func<IReadOnlyList<ClientAccess>>? readCommitted = null)
    { clients = values.ToArray(); this.readCommitted = readCommitted; }
    public void Replace(IReadOnlyList<ClientAccess> values)
    {
        RelayConfiguration.ValidateClients(values); Volatile.Write(ref clients, values.ToArray());
    }
    public void DisableAll() => Volatile.Write(ref clients, []);
    public ClientAccess? Authenticate(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 512) return null;
        // 每个鉴权边界读取数据库已提交快照，使另一个 CLI 进程的撤销也立即生效。
        lock (refreshLock)
        {
            if (readCommitted is not null)
            {
                try { Replace(readCommitted()); }
                catch { DisableAll(); throw new RelayFailure(503, "Access database unavailable."); }
            }
        }
        var hash = Convert.FromHexString(ConfigurationFiles.Hash(token));
        return Volatile.Read(ref clients).FirstOrDefault(c => c.Enabled && CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(c.TokenHash)));
    }
}

public sealed class Admission(RelayConfiguration config)
{
    private readonly SemaphoreSlim global = new(config.MaxConcurrent);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new();
    private readonly ConcurrentDictionary<string, (long Minute, int Count)> rates = new();
    private int queued, active;
    public int Queued => Volatile.Read(ref queued);
    public int Active => Volatile.Read(ref active);

    public void CheckRate(string name)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); var minute = now / 60;
        var item = rates.AddOrUpdate(name, (minute, 1), (_, previous) => previous.Minute == minute ? (minute, previous.Count + 1) : (minute, 1));
        if (item.Count > config.RequestsPerMinute) throw new RelayFailure(429, "Client request rate limit reached.", (int)(60 - now % 60));
    }

    public async Task<IDisposable> Enter(string name, CancellationToken cancellation)
    {
        if (Interlocked.Increment(ref queued) > config.MaxQueued)
        { Interlocked.Decrement(ref queued); throw new RelayFailure(429, "Relay queue is full.", 5); }
        var gate = gates.GetOrAdd(name, _ => new SemaphoreSlim(config.MaxPerClient));
        var gotClient = false; var gotGlobal = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(config.QueueTimeoutSeconds));
        try
        {
            await gate.WaitAsync(timeout.Token); gotClient = true;
            await global.WaitAsync(timeout.Token); gotGlobal = true; Interlocked.Increment(ref active);
            return new Release(() => { Interlocked.Decrement(ref active); global.Release(); gate.Release(); });
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new RelayFailure(429, "Relay queue wait expired.", 5); }
        finally
        {
            Interlocked.Decrement(ref queued);
            if (gotClient && !gotGlobal) gate.Release();
        }
    }
    private sealed class Release(Action release) : IDisposable
    {
        private Action? callback = release;
        public void Dispose() => Interlocked.Exchange(ref callback, null)?.Invoke();
    }
}
