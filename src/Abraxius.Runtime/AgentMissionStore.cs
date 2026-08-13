using System.Text.Json;
using Abraxius.Agents;

namespace Abraxius.Runtime;

public sealed class JsonAgentMissionStore(string path) : IAgentMissionStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.General) { WriteIndented = true };

    public async ValueTask<IReadOnlyList<AgentMissionRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return Array.Empty<AgentMissionRecord>();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<List<AgentMissionRecord>>(stream, _options, cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (JsonException)
        {
            return Array.Empty<AgentMissionRecord>();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask SaveAsync(AgentMissionRecord record, CancellationToken cancellationToken = default)
    {
        record = record with { Mission = record.Mission with { Assignments = record.Mission.SafeAssignments, Evidence = record.Mission.SafeEvidence } };
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = (await LoadWithoutLockAsync(cancellationToken).ConfigureAwait(false)).Where(item => item.Mission.Id != record.Mission.Id).Append(record).OrderByDescending(static item => item.RecordedAt).Take(256).ToArray();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            await using (var stream = File.Create(temporary)) await JsonSerializer.SerializeAsync(stream, records, _options, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { _gate.Release(); }
    }

    private async ValueTask<IReadOnlyList<AgentMissionRecord>> LoadWithoutLockAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return Array.Empty<AgentMissionRecord>();
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<AgentMissionRecord>>(stream, _options, cancellationToken).ConfigureAwait(false) ?? [];
    }

    public void Dispose() => _gate.Dispose();
}
