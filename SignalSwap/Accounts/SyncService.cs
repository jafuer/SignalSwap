using SignalSwap.Mapping;

namespace SignalSwap.Accounts;

public sealed class SyncService
{
    const int BatchSize = 500;
    static readonly TimeSpan UploadEvery = TimeSpan.FromSeconds(60);
    static readonly TimeSpan DownloadEvery = TimeSpan.FromMinutes(15);
    static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);
    static readonly TimeSpan DownloadOverlap = TimeSpan.FromHours(1);

    readonly ApiClient api;
    readonly AccountService account;
    readonly ReadingStore store;
    readonly SemaphoreSlim gate = new(1, 1);
    DateTimeOffset nextUpload;
    DateTimeOffset nextDownload;
    TimeSpan backoff = UploadEvery;
    Task? running;

    public SyncService(ApiClient api, AccountService account, ReadingStore store)
    {
        this.api = api;
        this.account = account;
        this.store = store;
    }

    public event Action? Changed;

    public event Action<IReadOnlyCollection<string>>? ReadingsMerged;

    public event Action<string>? NetworkCleared;

    public DateTimeOffset? LastUpload { get; private set; }

    public DateTimeOffset? LastDownload { get; private set; }

    public string? LastError { get; private set; }

    public string? LastRejection { get; private set; }

    public bool Busy => running is { IsCompleted: false };

    public void Reset()
    {
        nextUpload = DateTimeOffset.MinValue;
        nextDownload = DateTimeOffset.MinValue;
        backoff = UploadEvery;
        LastUpload = null;
        LastDownload = null;
        LastError = null;
        LastRejection = null;
        Changed?.Invoke();
    }

    public void Tick(DateTimeOffset now)
    {
        if (!account.IsSignedIn || Busy)
            return;

        bool upload = store.PendingCount > 0 && now >= nextUpload;
        bool download = now >= nextDownload;
        if (upload || download)
            running = RunAsync(upload, download);
    }

    public async Task SyncNowAsync()
    {
        if (running is { IsCompleted: false } current)
            await current;
        if (!account.IsSignedIn)
            return;
        running = RunAsync(true, true);
        await running;
    }

    public async Task RefreshMapsAsync()
    {
        if (!account.IsSignedIn || Busy)
            return;
        if (LastDownload is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(2))
            return;
        running = RunAsync(false, true);
        await running;
    }

    public async Task ClearNetworkAsync(string ssid)
    {
        if (account.Session is not { } session)
            throw new ApiException("Log in first.");

        await gate.WaitAsync();
        try
        {
            try
            {
                await api.ClearAsync(ssid);
            }
            catch (ApiException ex) when (ex.Unauthorized)
            {
                account.Expire();
                throw;
            }

            await store.RemoveNetworkAsync(session.UserId, ssid);
            Preferences.Default.Remove(WatermarkKey(session.UserId, ssid));
            NetworkCleared?.Invoke(ssid);
        }
        finally
        {
            gate.Release();
        }
    }

    async Task RunAsync(bool upload, bool download)
    {
        var now = DateTimeOffset.UtcNow;
        await gate.WaitAsync();
        try
        {
            if (account.Session is not { } session || store.UserId != session.UserId)
                return;

            if (upload)
            {
                nextUpload = now + UploadEvery;
                await UploadAsync(session.UserId);
            }

            if (download)
            {
                nextDownload = now + DownloadEvery;
                await DownloadAsync(session.UserId);
            }

            LastError = null;
            backoff = UploadEvery;
        }
        catch (ApiException ex) when (ex.Unauthorized)
        {
            LastError = ex.Message;
            account.Expire();
        }
        catch (Exception ex)
        {
            LastError = ApiClient.Describe(ex);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            nextUpload = now + backoff;
            if (download)
                nextDownload = now + backoff;
        }
        finally
        {
            gate.Release();
            Changed?.Invoke();
        }
    }

    async Task UploadAsync(int userId)
    {
        while (store.PendingCount > 0 && store.UserId == userId)
        {
            var batch = store.PendingBatch(BatchSize);
            var result = await api.UploadAsync(batch);
            var finished = result.Synced.Concat(result.Rejected.Select(r => r.ClientId)).ToList();
            if (result.Rejected.Count > 0)
                LastRejection = $"{result.Rejected.Count} reading(s) were refused: {result.Rejected[0].Reason}";
            await store.MarkSyncedAsync(userId, finished);
            LastUpload = DateTimeOffset.UtcNow;
            if (finished.Count == 0)
                break;
        }
    }

    async Task DownloadAsync(int userId)
    {
        var changed = new HashSet<string>();
        foreach (var network in await api.GetNetworksAsync())
        {
            string key = WatermarkKey(userId, network.Ssid);
            long mark = Preferences.Default.Get(key, 0L);
            DateTimeOffset? since = mark > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(mark) - DownloadOverlap : null;
            if (mark > 0 && network.LastRecordedAt <= DateTimeOffset.FromUnixTimeMilliseconds(mark))
                continue;

            var readings = await api.GetReadingsAsync(network.Ssid, since);
            if (store.UserId != userId)
                return;

            foreach (var ssid in await store.MergeAsync(userId, readings))
                changed.Add(ssid);
            if (readings.Count > 0)
                Preferences.Default.Set(key, readings.Max(r => r.RecordedAt).ToUnixTimeMilliseconds());
        }

        LastDownload = DateTimeOffset.UtcNow;
        if (changed.Count > 0)
            ReadingsMerged?.Invoke(changed);
    }

    static string WatermarkKey(int userId, string ssid) => $"sync.mark.{userId}.{ssid}";
}
