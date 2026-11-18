using System.Text.Json;
using SignalSwap.Accounts;

namespace SignalSwap.Mapping;

public sealed class ReadingStore
{
    readonly List<SignalReading> readings = new();
    readonly HashSet<Guid> known = new();
    readonly List<SignalReading> pending = new();
    readonly SemaphoreSlim fileLock = new(1, 1);
    string? readingsPath;
    string? pendingPath;

    public int? UserId { get; private set; }

    public int Version { get; private set; }

    public int Count => readings.Count;

    public int PendingCount => pending.Count;

    public IEnumerable<string> Networks => readings
        .GroupBy(r => r.Ssid)
        .OrderByDescending(g => g.Max(r => r.RecordedAt))
        .Select(g => g.Key);

    public IEnumerable<SignalReading> ForNetwork(string ssid) => readings.Where(r => r.Ssid == ssid);

    public async Task LoadAsync(int? userId)
    {
        await fileLock.WaitAsync();
        try
        {
            readings.Clear();
            known.Clear();
            pending.Clear();
            UserId = userId;
            Version++;

            if (userId is not int id)
            {
                readingsPath = null;
                pendingPath = null;
                return;
            }

            readingsPath = Path.Combine(FileSystem.AppDataDirectory, $"readings-{id}.jsonl");
            pendingPath = Path.Combine(FileSystem.AppDataDirectory, $"pending-{id}.jsonl");

            foreach (var reading in await ReadAsync(readingsPath))
                if (known.Add(reading.ClientId))
                    readings.Add(reading);

            var pendingIds = new HashSet<Guid>();
            foreach (var reading in await ReadAsync(pendingPath))
                if (pendingIds.Add(reading.ClientId))
                    pending.Add(reading);

            Version++;
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task AddAsync(SignalReading reading)
    {
        if (readingsPath == null || pendingPath == null || !known.Add(reading.ClientId))
            return;

        readings.Add(reading);
        pending.Add(reading);
        Version++;

        var line = Serialize(reading) + Environment.NewLine;
        string readingsFile = readingsPath;
        string pendingFile = pendingPath;
        await fileLock.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(readingsFile, line);
            await File.AppendAllTextAsync(pendingFile, line);
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task<IReadOnlyCollection<string>> MergeAsync(int userId, IEnumerable<SignalReading> downloaded)
    {
        if (UserId != userId || readingsPath == null)
            return Array.Empty<string>();

        var added = downloaded.Where(r => known.Add(r.ClientId)).ToList();
        if (added.Count == 0)
            return Array.Empty<string>();

        readings.AddRange(added);
        Version++;

        string readingsFile = readingsPath;
        var text = string.Concat(added.Select(r => Serialize(r) + Environment.NewLine));
        await fileLock.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(readingsFile, text);
        }
        finally
        {
            fileLock.Release();
        }

        return added.Select(r => r.Ssid).Distinct().ToList();
    }

    public IReadOnlyList<SignalReading> PendingBatch(int max) => pending.Take(max).ToList();

    public async Task MarkSyncedAsync(int userId, IEnumerable<Guid> clientIds)
    {
        if (UserId != userId || pendingPath == null)
            return;

        var done = clientIds.ToHashSet();
        if (pending.RemoveAll(r => done.Contains(r.ClientId)) == 0)
            return;

        await RewriteAsync(pendingPath, pending);
    }

    public async Task RemoveNetworkAsync(int userId, string ssid)
    {
        if (UserId != userId || readingsPath == null || pendingPath == null)
            return;

        foreach (var reading in readings.Where(r => r.Ssid == ssid))
            known.Remove(reading.ClientId);
        readings.RemoveAll(r => r.Ssid == ssid);
        pending.RemoveAll(r => r.Ssid == ssid);
        Version++;

        await RewriteAsync(readingsPath, readings);
        await RewriteAsync(pendingPath, pending);
    }

    async Task RewriteAsync(string path, List<SignalReading> items)
    {
        await fileLock.WaitAsync();
        try
        {
            var lines = items.Select(Serialize).ToList();
            await File.WriteAllLinesAsync(path, lines);
        }
        finally
        {
            fileLock.Release();
        }
    }

    static async Task<List<SignalReading>> ReadAsync(string path)
    {
        var list = new List<SignalReading>();
        if (!File.Exists(path))
            return list;

        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                if (JsonSerializer.Deserialize(line, ApiJson.Default.SignalReading) is { } reading)
                    list.Add(reading);
            }
            catch (JsonException)
            {
            }
        }
        return list;
    }

    static string Serialize(SignalReading reading) =>
        JsonSerializer.Serialize(reading, ApiJson.Default.SignalReading);
}
