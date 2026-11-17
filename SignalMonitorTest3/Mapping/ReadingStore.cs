using System.Text.Json;
using System.Text.Json.Serialization;

namespace SignalMonitorTest3.Mapping;

public sealed class ReadingStore
{
    readonly List<SignalReading> readings = new();
    readonly SemaphoreSlim fileLock = new(1, 1);

    public string FilePath { get; } = Path.Combine(FileSystem.AppDataDirectory, "signal-readings-v2.jsonl");

    public IEnumerable<string> Networks => readings
        .GroupBy(r => r.Ssid)
        .OrderByDescending(g => g.Max(r => r.RecordedAt))
        .Select(g => g.Key);

    public IEnumerable<SignalReading> ForNetwork(string ssid) => readings.Where(r => r.Ssid == ssid);

    public async Task LoadAsync()
    {
        readings.Clear();
        if (!File.Exists(FilePath))
            return;

        var seen = new HashSet<Guid>();
        foreach (var line in await File.ReadAllLinesAsync(FilePath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                if (JsonSerializer.Deserialize(line, ReadingJson.Default.SignalReading) is { } reading && seen.Add(reading.ClientId))
                    readings.Add(reading);
            }
            catch (JsonException)
            {
            }
        }
    }

    public async Task AddAsync(SignalReading reading)
    {
        readings.Add(reading);
        var line = Serialize(reading) + Environment.NewLine;

        await fileLock.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(FilePath, line);
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task RemoveNetworkAsync(string ssid)
    {
        readings.RemoveAll(r => r.Ssid == ssid);
        var lines = readings.Select(Serialize).ToList();

        await fileLock.WaitAsync();
        try
        {
            await File.WriteAllLinesAsync(FilePath, lines);
        }
        finally
        {
            fileLock.Release();
        }
    }

    static string Serialize(SignalReading reading) =>
        JsonSerializer.Serialize(reading, ReadingJson.Default.SignalReading);
}

[JsonSerializable(typeof(SignalReading))]
internal partial class ReadingJson : JsonSerializerContext
{
}
