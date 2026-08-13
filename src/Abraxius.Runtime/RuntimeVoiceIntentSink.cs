using System.Runtime.CompilerServices;
using Abraxius.Core;
using Abraxius.Agents;
using Abraxius.Protocol;
using Abraxius.Scheduler;
using Abraxius.Voice;

namespace Abraxius.Runtime;

/// <summary>Bridges finalized speech into the same intent/scheduler path used by typed commands.</summary>
public sealed class RuntimeVoiceIntentSink(AbraxiusRuntimeHost runtime) : IVoiceIntentSink
{
    public ValueTask<VoiceResponse?> SubmitAsync(string transcript, VoiceTurnId turnId, CancellationToken cancellationToken = default)
    {
        var mission = runtime.RunMissionAsync(new Intent(transcript, CorrelationId.New()), cancellationToken: cancellationToken);
        return ValueTask.FromResult<VoiceResponse?>(new VoiceResponse(
            "The mission is running.",
            SummarizeWhenCompleteAsync(mission, cancellationToken)));
    }

    private static async IAsyncEnumerable<string> SummarizeWhenCompleteAsync(
        Task<MissionResult> execution,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await execution.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var status = result.Mission.State == MissionState.Cancelled ? "cancelled" : result.Succeeded ? "completed successfully" : "completed with failures";
        yield return $"The mission {status}. The workstation has the detailed execution evidence.";
    }
}
