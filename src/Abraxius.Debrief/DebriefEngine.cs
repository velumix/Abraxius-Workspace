using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Abraxius.Agents;
using Abraxius.Core;
using Abraxius.Models;
using Abraxius.Protocol;
using Abraxius.Voice;

namespace Abraxius.Debrief;

public sealed class DebriefEngine : IDebriefEngine
{
    private readonly IDebriefPlanner _planner;
    private readonly IDebriefDialogueComposer _composer;
    private readonly IDebriefGroundingPolicy _grounding;
    private readonly IDebriefAudioCache _audioCache;
    private readonly IDebriefSessionStore _sessions;
    private readonly AgentKernel? _agents;
    private readonly DebriefOptions _options;
    private readonly object _audioGate = new();
    private ITextToSpeechProvider? _tts;
    private IAudioPlaybackService? _playback;
    private CancellationTokenSource? _activePlayback;
    private VoiceGenerationId _activeVoiceGeneration;
    private int _disposed;

    public DebriefEngine(
        IDebriefPlanner planner,
        IDebriefDialogueComposer composer,
        IDebriefGroundingPolicy grounding,
        IDebriefAudioCache? audioCache = null,
        IDebriefSessionStore? sessions = null,
        AgentKernel? agents = null,
        DebriefOptions? options = null,
        DebriefEventHub? events = null)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _grounding = grounding ?? throw new ArgumentNullException(nameof(grounding));
        _audioCache = audioCache ?? new InMemoryDebriefAudioCache();
        _sessions = sessions ?? new InMemoryDebriefSessionStore();
        _agents = agents;
        _options = options ?? new DebriefOptions();
        Events = events ?? new DebriefEventHub();
    }

    public DebriefEventHub Events { get; }

    public void ConfigureAudio(ITextToSpeechProvider tts, IAudioPlaybackService? playback = null)
    {
        ArgumentNullException.ThrowIfNull(tts);
        lock (_audioGate)
        {
            _tts = tts;
            _playback = playback;
        }
    }

    public async ValueTask<DebriefSession> CreateAsync(DebriefRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        var plan = await _planner.PlanAsync(request, cancellationToken).ConfigureAwait(false);
        var session = new DebriefSession(DebriefId.New(), request, plan);
        Publish(new DebriefEvent(DebriefEventKind.Created, session.Id, DateTimeOffset.UtcNow, session.State, plan.Title, Plan: plan));
        Publish(new DebriefEvent(DebriefEventKind.PlanningCompleted, session.Id, DateTimeOffset.UtcNow, session.State, $"claims={plan.Claims.Count} chapters={plan.Chapters.Count}", Plan: plan));
        await SaveAsync(session, cancellationToken).ConfigureAwait(false);
        return session;
    }

    public async Task<DebriefResult> PlayAsync(DebriefSession session, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(session);
        var started = Stopwatch.GetTimestamp();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_audioGate)
        {
            _activePlayback?.Cancel();
            _activePlayback = linked;
            _activeVoiceGeneration = new VoiceGenerationId(Math.Max(1, session.AdvanceGeneration().Value));
        }

        var supported = 0;
        var rejected = 0;
        var audioSegments = 0;
        session.State = session.State == DebriefState.Paused || session.State == DebriefState.Interrupted ? DebriefState.Playing : DebriefState.Preparing;
        Publish(new DebriefEvent(DebriefEventKind.Resumed, session.Id, DateTimeOffset.UtcNow, session.State, "generation started"));
        try
        {
            var startOrdinal = session.CurrentChapter is { } current
                ? session.Plan.Chapters.FirstOrDefault(chapter => chapter.Id == current)?.Ordinal ?? 1
                : 1;
            for (var chapterIndex = 0; chapterIndex < session.Plan.Chapters.Count; chapterIndex++)
            {
                linked.Token.ThrowIfCancellationRequested();
                var chapter = session.Plan.Chapters[chapterIndex];
                if (chapter.Ordinal < startOrdinal) continue;
                if (session.Turns.Any(turn => turn.ChapterId == chapter.Id)) continue;

                session.CurrentChapter = chapter.Id;
                var drafted = await _composer.ComposeAsync(session.Plan, chapter, session.Turns, linked.Token).ConfigureAwait(false);
                var grounded = _grounding.Verify(session.Plan, chapter, drafted, out var rejectedClaims);
                rejected += rejectedClaims.Count;
                foreach (var claim in rejectedClaims)
                {
                    Publish(new DebriefEvent(DebriefEventKind.ClaimRejected, session.Id, DateTimeOffset.UtcNow, session.State, claim.ClaimId, Claim: claim));
                }
                foreach (var turn in grounded.Take(_options.MaxTurns - session.Turns.Count))
                {
                    linked.Token.ThrowIfCancellationRequested();
                    session.AddTurn(turn);
                    supported += turn.ClaimIds.Count;
                    Publish(new DebriefEvent(DebriefEventKind.TurnReady, session.Id, DateTimeOffset.UtcNow, session.State, turn.SpeakerName, Turn: turn));
                }
                Publish(new DebriefEvent(DebriefEventKind.ChapterReady, session.Id, DateTimeOffset.UtcNow, session.State, chapter.Title));
                session.State = DebriefState.Playing;

                foreach (var turn in grounded)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (!session.Turns.Contains(turn)) continue;
                    var cacheKey = CreateAudioCacheKey(turn, session.Request);
                    var cached = await _audioCache.GetAsync(cacheKey, linked.Token).ConfigureAwait(false);
                    if (await PlayAudioAsync(session, turn, cached, cacheKey, linked.Token).ConfigureAwait(false)) audioSegments++;
                    session.CurrentTurnIndex = session.Turns.ToList().IndexOf(turn);
                }
                await SaveAsync(session, linked.Token).ConfigureAwait(false);
            }

            session.State = DebriefState.Completed;
            session.CompletedAt = DateTimeOffset.UtcNow;
            Publish(new DebriefEvent(DebriefEventKind.Completed, session.Id, DateTimeOffset.UtcNow, session.State, $"turns={session.Turns.Count}"));
            await SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
            return new DebriefResult(session, true, "Debrief completed from a versioned source snapshot.", Stopwatch.GetElapsedTime(started), supported, rejected, audioSegments);
        }
        catch (OperationCanceledException) when (session.State is DebriefState.Paused or DebriefState.Interrupted or DebriefState.Cancelled)
        {
            return new DebriefResult(session, false, $"Debrief {session.State.ToString().ToLowerInvariant()}.", Stopwatch.GetElapsedTime(started), supported, rejected, audioSegments);
        }
        catch (Exception exception)
        {
            session.State = DebriefState.Failed;
            Publish(new DebriefEvent(DebriefEventKind.Failed, session.Id, DateTimeOffset.UtcNow, session.State, exception.GetType().Name));
            await SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
            return new DebriefResult(session, false, "Debrief generation failed safely before unverified content could be spoken.", Stopwatch.GetElapsedTime(started), supported, rejected, audioSegments);
        }
        finally
        {
            lock (_audioGate)
            {
                if (ReferenceEquals(_activePlayback, linked)) _activePlayback = null;
            }
        }
    }

    public async ValueTask PauseAsync(DebriefSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.State = DebriefState.Paused;
        CancelPlayback();
        await StopAudioAsync(cancellationToken).ConfigureAwait(false);
        Publish(new DebriefEvent(DebriefEventKind.Paused, session.Id, DateTimeOffset.UtcNow, session.State, "user paused playback"));
        await SaveAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ResumeAsync(DebriefSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.State is DebriefState.Completed or DebriefState.Cancelled) return;
        _ = await PlayAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SkipToChapterAsync(DebriefSession session, ChapterId chapterId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.Plan.Chapters.Any(chapter => chapter.Id == chapterId)) throw new ArgumentException("Chapter is not part of this Debrief.", nameof(chapterId));
        session.CurrentChapter = chapterId;
        session.State = DebriefState.Interrupted;
        CancelPlayback();
        await StopAudioAsync(cancellationToken).ConfigureAwait(false);
        Publish(new DebriefEvent(DebriefEventKind.Interrupted, session.Id, DateTimeOffset.UtcNow, session.State, $"skip:{chapterId}"));
        await SaveAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DebriefLiveAnswer> AskAsync(DebriefSession session, DebriefLiveQuestion question, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(question.Text);
        session.State = DebriefState.Interrupted;
        CancelPlayback();
        await StopAudioAsync(cancellationToken).ConfigureAwait(false);
        Publish(new DebriefEvent(DebriefEventKind.LiveQuestion, session.Id, DateTimeOffset.UtcNow, session.State, question.Text));

        var role = question.ExplicitSpeaker ?? InferSpeaker(question.Text);
        var answerText = "The current Debrief sources do not provide a verified answer to that question.";
        var evidence = session.Plan.SourceSnapshot.ResolvedEvidenceIds;
        var mutationRequest = role == SpecialistRole.Builder && (question.Text.Contains("implement", StringComparison.OrdinalIgnoreCase) || question.Text.Contains("change", StringComparison.OrdinalIgnoreCase) || question.Text.Contains("fix", StringComparison.OrdinalIgnoreCase) || question.Text.Contains("write", StringComparison.OrdinalIgnoreCase));
        if (_agents is not null && !mutationRequest && role is not SpecialistRole.Builder)
        {
            var mission = await _agents.RunMissionAsync(
                new Intent(question.Text, CorrelationId.New()),
                new MissionSuccessContract(question.Text, ["Return a grounded answer."], ["Answer cites current evidence."]),
                role,
                cancellationToken).ConfigureAwait(false);
            answerText = mission.Summary;
            evidence = mission.AssignmentResults.Values.SelectMany(static result => result.SafeEvidence).Concat(evidence).Distinct().ToArray();
        }
        else if (mutationRequest)
        {
            answerText = "That request would change the workspace. Debrief cannot mutate directly; I can turn it into a normal Daedalus mission for policy review.";
            evidence = [];
        }

        var chapter = session.CurrentChapter is { } current
            ? session.Plan.Chapters.FirstOrDefault(item => item.Id == current) ?? session.Plan.Chapters[0]
            : session.Plan.Chapters[0];
        var profile = role switch
        {
            SpecialistRole.Coordinator => ("Athena", DebriefSpeechStyle.Composed),
            SpecialistRole.Investigator => ("Orion", DebriefSpeechStyle.Investigative),
            SpecialistRole.Builder => ("Daedalus", DebriefSpeechStyle.Technical),
            SpecialistRole.Verifier => ("Argus", DebriefSpeechStyle.Analytical),
            _ => (role.ToString(), DebriefSpeechStyle.Technical)
        };
        var turn = new DialogueTurn(DialogueTurnId.New(), chapter.Id, role, profile.Item1, answerText, [], evidence, profile.Item2, TimeSpan.FromSeconds(Math.Max(2, answerText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 2.5)), SourceRefs: evidence.Select(static id => $"e:{id}").ToArray());
        session.AddTurn(turn);
        var cacheKey = CreateAudioCacheKey(turn, session.Request);
        await PlayAudioAsync(session, turn, await _audioCache.GetAsync(cacheKey, cancellationToken).ConfigureAwait(false), cacheKey, cancellationToken).ConfigureAwait(false);
        var resumed = false;
        if (question.ResumeAfterAnswer && session.State != DebriefState.Cancelled)
        {
            session.State = DebriefState.Paused;
            resumed = true;
            _ = Task.Run(() => PlayAsync(session, CancellationToken.None));
        }
        await SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
        return new DebriefLiveAnswer(turn, evidence, resumed, answerText);
    }

    public async ValueTask CancelAsync(DebriefSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.State = DebriefState.Cancelled;
        CancelPlayback();
        await StopAudioAsync(cancellationToken).ConfigureAwait(false);
        Publish(new DebriefEvent(DebriefEventKind.Cancelled, session.Id, DateTimeOffset.UtcNow, session.State, "user cancelled"));
        await SaveAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask SaveAsync(DebriefSession session, CancellationToken cancellationToken = default) =>
        _sessions.SaveAsync(new DebriefSessionSnapshot(session.Id, session.Request, session.Plan, session.State, session.CreatedAt, session.CompletedAt, session.CurrentChapter, session.CurrentTurnIndex, session.Turns.ToArray(), session.IsSourceStale), cancellationToken);

    public async ValueTask<DebriefSession?> RestoreAsync(DebriefId id, CancellationToken cancellationToken = default)
    {
        var snapshot = await _sessions.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return snapshot is null ? null : DebriefSession.Restore(snapshot);
    }

    public ValueTask<IReadOnlyList<DebriefSessionSnapshot>> ListAsync(CancellationToken cancellationToken = default) => _sessions.ListAsync(cancellationToken);

    public async ValueTask ExportTranscriptAsync(DebriefSession session, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(destination);
        await using var writer = new StreamWriter(destination, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync($"# {session.Plan.Title}").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync($"Mode: {session.Plan.Mode}").ConfigureAwait(false);
        await writer.WriteLineAsync($"Source snapshot: {session.Plan.SourceSnapshot.ContentHash}").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        foreach (var chapter in session.Plan.Chapters)
        {
            await writer.WriteLineAsync($"## {chapter.Ordinal}. {chapter.Title}").ConfigureAwait(false);
            foreach (var turn in session.Turns.Where(turn => turn.ChapterId == chapter.Id))
            {
                await writer.WriteLineAsync($"**{turn.SpeakerName}:** {turn.Text}").ConfigureAwait(false);
                if (turn.SafeSourceRefs.Count > 0) await writer.WriteLineAsync($"Sources: {string.Join(", ", turn.SafeSourceRefs)}").ConfigureAwait(false);
                await writer.WriteLineAsync().ConfigureAwait(false);
            }
        }
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ExportAudioAsync(DebriefSession session, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanSeek) throw new ArgumentException("WAV export requires a seekable destination stream.", nameof(destination));

        var segments = new List<CachedAudioSegment>();
        foreach (var turn in session.Turns)
        {
            var segment = await _audioCache.GetAsync(CreateAudioCacheKey(turn, session.Request), cancellationToken).ConfigureAwait(false);
            if (segment is not null) segments.Add(segment);
        }
        if (segments.Count == 0) throw new InvalidOperationException("No cached audio is available; play the Debrief before exporting audio.");

        var firstFrame = segments.SelectMany(static item => item.Frames).FirstOrDefault();
        if (firstFrame.Format is null) throw new InvalidOperationException("Cached audio has no format.");
        var format = firstFrame.Format;
        if (format.SampleType != AudioSampleType.Pcm16) throw new NotSupportedException("Only PCM16 WAV export is currently supported.");
        var header = new byte[44];
        WriteWavHeader(header, format, 0);
        await destination.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        long dataLength = 0;
        foreach (var segment in segments)
        {
            foreach (var frame in segment.Frames)
            {
                await destination.WriteAsync(frame.Data, cancellationToken).ConfigureAwait(false);
                dataLength += frame.Data.Length;
            }
        }
        destination.Position = 0;
        WriteWavHeader(header, format, checked((int)dataLength));
        await destination.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        destination.Position = destination.Length;
    }

    private static void WriteWavHeader(Span<byte> header, AudioFormat format, int dataLength)
    {
        header.Clear();
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], checked(36 + dataLength));
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], checked((short)format.Channels));
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], format.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], format.BytesPerSecond);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], checked((short)(format.Channels * format.BytesPerSample)));
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], dataLength);
    }

    private async ValueTask<bool> PlayAudioAsync(DebriefSession session, DialogueTurn turn, CachedAudioSegment? cached, string cacheKey, CancellationToken cancellationToken)
    {
        ITextToSpeechProvider? tts;
        IAudioPlaybackService? playback;
        lock (_audioGate) { tts = _tts; playback = _playback; }
        if (!session.Request.GenerateAudio || tts is null) return false;
        var generation = _activeVoiceGeneration;
        IReadOnlyList<AudioFrame> frames;
        if (cached is not null)
        {
            frames = cached.Frames;
            if (playback is not null)
            {
                Publish(new DebriefEvent(DebriefEventKind.PlaybackStarted, session.Id, DateTimeOffset.UtcNow, session.State, turn.SpeakerName, Turn: turn));
                await playback.PlayAsync(ToAsync(frames, cancellationToken), new AudioPlaybackOptions(cached.Format), generation, cancellationToken).ConfigureAwait(false);
                Publish(new DebriefEvent(DebriefEventKind.PlaybackStopped, session.Id, DateTimeOffset.UtcNow, session.State, "cache", Turn: turn));
            }
            return true;
        }

        var buffer = new List<AudioFrame>();
        async IAsyncEnumerable<AudioFrame> Synthesize([EnumeratorCancellation] CancellationToken token = default)
        {
            var request = new SpeechSynthesisRequest(
                turn.Text,
                session.Request.VoiceFor(turn.Speaker),
                session.Request.VoiceLanguage,
                Style: turn.SpeechStyle.ToString(),
                OutputFormat: AudioFormat.NormalizedSpeech,
                Context: new SpeechContext(Mission: session.Plan.Objective, Language: session.Request.Language, PrivateMode: session.Request.PrivateMode),
                RoutingMode: session.Request.PrivateMode ? SpeechRoutingMode.Private : SpeechRoutingMode.BalancedQuality);
            await foreach (var frame in tts.SynthesizeAsync(request, token).ConfigureAwait(false))
            {
                buffer.Add(frame);
                yield return frame;
            }
        }

        Publish(new DebriefEvent(DebriefEventKind.PlaybackStarted, session.Id, DateTimeOffset.UtcNow, session.State, turn.SpeakerName, Turn: turn));
        if (playback is not null)
        {
            await playback.PlayAsync(Synthesize(cancellationToken), new AudioPlaybackOptions(AudioFormat.NormalizedSpeech), generation, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await foreach (var _ in Synthesize(cancellationToken).ConfigureAwait(false)) { }
        }
        if (buffer.Count > 0)
        {
            var duration = buffer.Sum(static frame => frame.Duration.Ticks);
            await _audioCache.PutAsync(new CachedAudioSegment(AudioSegmentId.New(), turn.Id, cacheKey, buffer[0].Format, buffer.ToArray(), TimeSpan.FromTicks(duration), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        }
        Publish(new DebriefEvent(DebriefEventKind.PlaybackStopped, session.Id, DateTimeOffset.UtcNow, session.State, "completed", Turn: turn));
        return buffer.Count > 0;
    }

    private void CancelPlayback()
    {
        lock (_audioGate) _activePlayback?.Cancel();
    }

    private async ValueTask StopAudioAsync(CancellationToken cancellationToken)
    {
        IAudioPlaybackService? playback;
        VoiceGenerationId generation;
        lock (_audioGate) { playback = _playback; generation = _activeVoiceGeneration; }
        if (playback is not null) await playback.StopAsync(generation, cancellationToken).ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<AudioFrame> ToAsync(IReadOnlyList<AudioFrame> frames, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return frame;
            await Task.Yield();
        }
    }

    private static string CreateAudioCacheKey(DialogueTurn turn, DebriefRequest request) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{turn.Text}|{request.VoiceFor(turn.Speaker)}|{request.VoiceLanguage}|{request.Language}|{request.PrivateMode}"))).ToLowerInvariant();

    private static SpecialistRole InferSpeaker(string question) =>
        question.Contains("test", StringComparison.OrdinalIgnoreCase) || question.Contains("confident", StringComparison.OrdinalIgnoreCase)
            ? SpecialistRole.Verifier
            : question.Contains("where", StringComparison.OrdinalIgnoreCase) || question.Contains("evidence", StringComparison.OrdinalIgnoreCase)
                ? SpecialistRole.Investigator
                : question.Contains("implement", StringComparison.OrdinalIgnoreCase) || question.Contains("code", StringComparison.OrdinalIgnoreCase)
                    ? SpecialistRole.Builder
                    : SpecialistRole.Coordinator;

    private void Publish(DebriefEvent value) => Events.Publish(value);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CancelPlayback();
        await StopAudioAsync(CancellationToken.None).ConfigureAwait(false);
    }
}

public sealed class JsonDebriefSessionStore(string path) : IDebriefSessionStore, IDisposable
{
    private readonly string _path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<DebriefId, DebriefSessionSnapshot> _items = new();
    private int _loaded;

    public async ValueTask SaveAsync(DebriefSessionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        _items[snapshot.Id] = snapshot;
        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DebriefSessionSnapshot?> GetAsync(DebriefId id, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return _items.TryGetValue(id, out var value) ? value : null;
    }

    public async ValueTask<IReadOnlyList<DebriefSessionSnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return _items.Values.OrderByDescending(static item => item.CreatedAt).ToArray();
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _loaded, 1) != 0) return;
        if (!File.Exists(_path)) return;
        try
        {
            await using var stream = File.OpenRead(_path);
            var values = await JsonSerializer.DeserializeAsync<List<DebriefSessionSnapshot>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
            foreach (var value in values) _items[value.Id] = value;
        }
        catch (JsonException) { _items.Clear(); }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporary = _path + ".tmp";
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, _items.Values.OrderByDescending(static item => item.CreatedAt).Take(64).ToArray(), cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, _path, true);
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
