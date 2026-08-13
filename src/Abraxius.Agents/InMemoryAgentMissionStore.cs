namespace Abraxius.Agents;

public sealed class InMemoryAgentMissionStore : IAgentMissionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<MissionId, AgentMissionRecord> _records = [];

    public ValueTask<IReadOnlyList<AgentMissionRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) return ValueTask.FromResult<IReadOnlyList<AgentMissionRecord>>(_records.Values.OrderByDescending(static record => record.RecordedAt).ToArray());
    }

    public ValueTask SaveAsync(AgentMissionRecord record, CancellationToken cancellationToken = default)
    {
        lock (_gate) _records[record.Mission.Id] = record;
        return ValueTask.CompletedTask;
    }
}
