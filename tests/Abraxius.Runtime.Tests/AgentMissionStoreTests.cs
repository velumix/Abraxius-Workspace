using Abraxius.Agents;
using Abraxius.Core;
using Abraxius.Protocol;
using Xunit;

namespace Abraxius.Runtime.Tests;

public sealed class AgentMissionStoreTests
{
    [Fact]
    public async Task JsonMissionStoreSurvivesReopen()
    {
        var directory = Path.Combine(Path.GetTempPath(), "abraxius-agent-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "missions.json");
        try
        {
            var mission = new Mission(
                MissionId.New(),
                new Intent("remember mission", CorrelationId.New()),
                new MissionSuccessContract("remember mission", ["record"], ["load"]),
                WorkPriority.Interactive,
                new CognitiveBudget(),
                new AutonomyBudget(),
                WorkspacePolicy.SharedReadOnly,
                MissionState.Succeeded,
                CreatedAt: DateTimeOffset.UtcNow,
                CompletedAt: DateTimeOffset.UtcNow);
            using (var store = new JsonAgentMissionStore(path))
            {
                await store.SaveAsync(new AgentMissionRecord(mission, "saved", DateTimeOffset.UtcNow, 0));
            }
            var reopened = new JsonAgentMissionStore(path);
            var records = await reopened.LoadAsync();
            reopened.Dispose();
            Assert.Single(records);
            Assert.Equal(mission.Id, records[0].Mission.Id);
            Assert.Equal("saved", records[0].Summary);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
