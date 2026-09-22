using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using TheOverseer.App.Voice;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.History;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;

namespace TheOverseer.App.Diagnostics;

/// <summary>
/// Finite Plan 3 voice smoke. Silent by default: it proves the pack, policy,
/// aggregation, mute and TTS contracts with synthetic tones and an owned
/// loopback server. With --audible it additionally plays through the real sound
/// device and reports what was actually heard separately from API success.
/// </summary>
internal static class VoiceSmokeRunner
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UtcNow;

    public static int Run(StartupArguments arguments)
    {
        var failures = 0;
        var dataDirectory = DataDirectoryResolver.Resolve(arguments.DataDirectory);
        var packDirectory = Path.Combine(dataDirectory, VoicePackProvider.PackFolderName, "example");
        Directory.CreateDirectory(packDirectory);

        Console.WriteLine("OVERSEER voice smoke (synthetic tones; no speech assets were supplied)");
        Console.WriteLine("data-directory: " + dataDirectory);
        Console.WriteLine("audible: " + (arguments.Audible ? "on (real sound device)" : "off (silent)"));

        var manifest = WriteSyntheticPack(packDirectory);
        Console.WriteLine("pack: " + manifest + " (synthetic PCM tones prove audio mechanics, not spoken voice)");

        failures += RunPackSmoke(dataDirectory, arguments.Audible);
        failures += RunTtsSmoke(dataDirectory, arguments.Audible);
        if (arguments.Audible) failures += RunNativeCancellationSmoke();

        Console.WriteLine(failures == 0 ? "voice smoke: PASS" : "voice smoke: FAIL (" + failures + ")");
        return failures == 0 ? 0 : 1;
    }

    private static int RunNativeCancellationSmoke()
    {
        var player = new WavPlaybackService();
        using var cancellation = new CancellationTokenSource();
        var clip = new VoiceClip(VoicePackValidator.CreateTone(440, 2), "synthetic", "cancellation");
        var playing = player.PlayAsync(clip, cancellation.Token).AsTask();
        Thread.Sleep(60);
        var wasPlaying = !playing.IsCompleted;
        var stopwatch = Stopwatch.StartNew();
        cancellation.Cancel();
        try { playing.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        var passed = wasPlaying && !player.DeviceUnavailable && stopwatch.ElapsedMilliseconds < 1000;
        Console.WriteLine((passed ? "ok   " : "FAIL ") + "native in-flight cancellation: " +
            stopwatch.ElapsedMilliseconds + "ms; acoustic stop not verified by ear");
        return passed ? 0 : 1;
    }

    private static string WriteSyntheticPack(string packDirectory)
    {
        var clips = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["permission_required"] = new[] { "permission-1.wav", "permission-2.wav" },
            ["input_required"] = new[] { "input.wav" },
            ["task_error"] = new[] { "error.wav" },
            ["task_complete"] = new[] { "complete.wav" },
            ["all_tasks_complete"] = new[] { "all.wav" },
        };

        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["permission-1.wav"] = 660,
            ["permission-2.wav"] = 720,
            ["input.wav"] = 600,
            ["error.wav"] = 320,
            ["complete.wav"] = 880,
            ["all.wav"] = 990,
        };

        foreach (var pair in clips)
        {
            foreach (var clip in pair.Value)
            {
                File.WriteAllBytes(Path.Combine(packDirectory, clip), VoicePackValidator.CreateTone(frequencies[clip], 0.12));
            }
        }

        var manifestJson = JsonSerializer.Serialize(new
        {
            id = "example",
            name = "Synthetic example cues",
            language = "en",
            version = 1,
            events = clips,
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(packDirectory, VoicePackProvider.ManifestFileName), manifestJson);
        return "example v1 with " + clips.Sum(m => m.Value.Length) + " synthetic clips";
    }

    private static VoiceCandidate Candidate(
        VoiceEventKind kind,
        int number = 1,
        DateTimeOffset? at = null,
        bool aggregate = false,
        int count = 1) => new(
        kind,
        new TaskKey("codex", "01a0f000-0000-0000-0000-00000000000" + number),
        number,
        aggregate ? "All tasks" : "Task " + number,
        "turn-1",
        at ?? DateTimeOffset.UtcNow,
        "smoke",
        aggregate,
        count,
        aggregate ? "episode:" + (at ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds() : string.Empty);

    private static int RunPackSmoke(string dataDirectory, bool audible)
    {
        Console.WriteLine(string.Empty);
        Console.WriteLine("--- pack mode ---");
        var failures = 0;
        var settings = new VoiceSettings { Muted = false, Provider = "pack", PackId = "example" };
        var provider = new VoicePackProvider(dataDirectory, () => settings);
        var policy = new VoiceEventPolicy();
        using var counter = new CountingPlayer(audible);
        var manager = new VoiceManager(
            provider,
            counter,
            VoiceManagerOptions.Default with { MinimumStartSpacing = TimeSpan.Zero },
            TimeProvider.System,
            revalidate: candidate => policy.IsRelevant(candidate, DateTimeOffset.UtcNow),
            muted: false);
        var history = new ActivityHistory();
        var coordinator = new NotificationCoordinator(history, policy, manager, TimeProvider.System, settings);
        coordinator.Start();

        var key = new TaskKey("codex", "01a0f000-0000-0000-0000-000000000001");
        coordinator.OnSnapshot(Snapshot(
            DateTimeOffset.UtcNow,
            new[] { Task(key, 1, AgentTaskState.NeedsApproval) },
            new[] { Attention(AttentionEventKind.PermissionRequired, key, 1, "turn-1") }));
        failures += Expect("permission cue played once", 1, () => counter.Played.Count, 4000);

        // Repeated replay of the same event must not speak again.
        coordinator.OnSnapshot(Snapshot(
            DateTimeOffset.UtcNow,
            new[] { Task(key, 1, AgentTaskState.NeedsApproval) },
            new[] { Attention(AttentionEventKind.PermissionRequired, key, 1, "turn-1") }));
        Thread.Sleep(250);
        failures += Expect("duplicate replay stays silent", 1, () => counter.Played.Count, 250);

        // Clearance then a fresh episode (cooldown default is 10s, so the cue is
        // eligible immediately because the first one already left the window).
        coordinator.OnSnapshot(Snapshot(DateTimeOffset.UtcNow, new[] { Task(key, 1, AgentTaskState.Working) }, Array.Empty<AttentionEvent>()));
        Thread.Sleep(11000);
        // A new turn with matching task state: the transition and the task agree,
        // exactly as one monitor cycle produces them.
        coordinator.OnSnapshot(Snapshot(
            DateTimeOffset.UtcNow,
            new[] { Task(key, 1, AgentTaskState.NeedsApproval, "turn-2") },
            new[] { Attention(AttentionEventKind.PermissionRequired, key, 1, "turn-2") }));
        failures += Expect("verified re-arm speaks once", 2, () => counter.Played.Count, 4000);

        // Completion aggregation: two completions, one cue.
        var key2 = new TaskKey("codex", "01a0f000-0000-0000-0000-000000000002");
        var key3 = new TaskKey("codex", "01a0f000-0000-0000-0000-000000000003");
        var done = new[] { Task(key2, 2, AgentTaskState.Done), Task(key3, 3, AgentTaskState.Done) };
        coordinator.OnSnapshot(Snapshot(
            DateTimeOffset.UtcNow,
            done,
            new[]
            {
                Attention(AttentionEventKind.TaskCompleted, key2, 2, "turn-2"),
                Attention(AttentionEventKind.TaskCompleted, key3, 3, "turn-3"),
            },
            working: CountValue.Partial(0),
            coverageComplete: false));
        Thread.Sleep(2600);
        coordinator.Pump();
        failures += Expect("completion group is one cue", 3, () => counter.Played.Count, 4000, coordinator.Pump);

        // Mute during playback stops current audio and clears the queue.
        var before = counter.Played.Count;
        coordinator.OnSnapshot(Snapshot(
            DateTimeOffset.UtcNow,
            new[] { Task(new TaskKey("codex", "01a0f000-0000-0000-0000-000000000004"), 4, AgentTaskState.Error, "turn-4") },
            new[] { Attention(AttentionEventKind.TaskError, new TaskKey("codex", "01a0f000-0000-0000-0000-000000000004"), 4, "turn-4") }));
        Thread.Sleep(400);
        Console.WriteLine("policy after error cue: " + coordinator.Describe());
        var beforeMute = counter.Played.Count;
        coordinator.SetMuted(true);
        Thread.Sleep(300);
        failures += Expect("mute stops playback", 1, () => counter.StopCount, 1000);

        // A condition raised while muted stays silent (no backlog on unmute).
        coordinator.OnSnapshot(Snapshot(
            DateTimeOffset.UtcNow,
            new[] { Task(new TaskKey("codex", "01a0f000-0000-0000-0000-00000000000a"), 10, AgentTaskState.Error, "turn-10") },
            new[] { Attention(AttentionEventKind.TaskError, new TaskKey("codex", "01a0f000-0000-0000-0000-00000000000a"), 10, "turn-10") }));
        Thread.Sleep(400);
        failures += Expect("mute prevents further cues", beforeMute, () => counter.Played.Count, 600);
        coordinator.SetMuted(false);
        Thread.Sleep(400);
        failures += Expect("unmute does not replay backlog", beforeMute, () => counter.Played.Count, 600);

        ShowPlayerReport(counter);
        coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return failures;
    }

    private static int RunTtsSmoke(string dataDirectory, bool audible)
    {
        Console.WriteLine(string.Empty);
        Console.WriteLine("--- tts mode (owned loopback endpoint) ---");
        var failures = 0;
        var tone = VoicePackValidator.CreateTone(520, 0.1);
        var corrupt = new byte[256];

        using var server = LoopbackTtsServer.Start((index, _) => index switch
        {
            0 => new LoopbackResponse(200, "audio/wav", tone, TimeSpan.Zero, false),
            1 => new LoopbackResponse(500, "text/plain", new byte[] { 1, 2, 3 }, TimeSpan.Zero, false),
            2 => new LoopbackResponse(200, "audio/wav", tone, TimeSpan.FromSeconds(5), false),
            3 => new LoopbackResponse(200, "text/html", tone, TimeSpan.Zero, false),
            _ => new LoopbackResponse(200, "audio/wav", corrupt, TimeSpan.Zero, false),
        });
        Console.WriteLine("loopback endpoint: " + server.Endpoint);

        var settings = new VoiceSettings
        {
            Muted = false,
            Provider = "tts",
            PackId = "example",
            Endpoint = server.Endpoint,
            TimeoutSeconds = 1,
        };
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var tts = new TtsEndpointProvider(client, () => settings);
        var pack = new VoicePackProvider(dataDirectory, () => settings);
        var policy = new VoiceEventPolicy();
        bool Relevant(VoiceCandidate candidate) => policy.IsRelevant(candidate, DateTimeOffset.UtcNow);
        var router = new FallbackVoiceProvider(tts, pack, allowFallback: true, Relevant);
        using var counter = new CountingPlayer(audible);
        var manager = new VoiceManager(
            router,
            counter,
            VoiceManagerOptions.Default with { MinimumStartSpacing = TimeSpan.Zero },
            TimeProvider.System,
            revalidate: Relevant,
            muted: false);
        var coordinator = new NotificationCoordinator(new ActivityHistory(), policy, manager, TimeProvider.System, settings);
        coordinator.Start();

        // 1. Successful contract.
        coordinator.OnSnapshot(SimpleSnapshot(VoiceEventKind.PermissionRequired, 5));
        failures += Expect("tts success plays", 1, () => counter.Played.Count, 4000);
        failures += Expect("one request reached the loopback endpoint", 1, () => server.RequestCount, 1000);
        failures += CheckPayloadContract(server);

        // 2. HTTP 500 falls back once to the local pack clip.
        coordinator.OnSnapshot(SimpleSnapshot(VoiceEventKind.TaskError, 6));
        failures += Expect("500 falls back to local audio", 2, () => counter.Played.Count, 4000);
        failures += Expect("fallback used the local pack source", 1, () => counter.Played.Count(clip => clip.SourceCode.StartsWith("pack:", StringComparison.Ordinal)), 1500);

        // 3. Timeout (endpoint delay > configured timeout) also falls back.
        coordinator.OnSnapshot(SimpleSnapshot(VoiceEventKind.InputRequired, 7));
        failures += Expect("timeout falls back to local audio", 3, () => counter.Played.Count, 6000);

        // 4. Wrong MIME type falls back.
        coordinator.OnSnapshot(SimpleSnapshot(VoiceEventKind.TaskCompleted, 8));
        failures += Expect("wrong content type falls back", 4, () => counter.Played.Count, 6000, coordinator.Pump);

        // 5. Corrupt audio falls back.
        coordinator.OnSnapshot(SimpleSnapshot(VoiceEventKind.AllTasksCompleted, 9, aggregate: true));
        failures += Expect("corrupt audio falls back", 5, () => counter.Played.Count, 6000, coordinator.Pump);

        Console.WriteLine("loopback requests served: " + server.RequestCount);
        Console.WriteLine("tts last code: " + tts.Status.LastCode + " · fallbacks: " + router.FallbackCount);
        ShowPlayerReport(counter);
        coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return failures;
    }

    private static int CheckPayloadContract(LoopbackTtsServer server)
    {
        var bodies = server.Bodies;
        if (bodies.Count == 0)
        {
            Console.WriteLine("FAIL payload: no request captured");
            return 1;
        }

        var body = bodies[0];
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var fields = root.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var expected = new[] { "event", "task", "text" };
        if (!fields.SequenceEqual(expected))
        {
            Console.WriteLine("FAIL payload: fields were " + string.Join(",", fields));
            return 1;
        }

        var text = root.GetProperty("text").GetString() ?? string.Empty;
        var eventCode = root.GetProperty("event").GetString() ?? string.Empty;
        var task = root.GetProperty("task").GetString() ?? string.Empty;
        if (text != "Task 5 needs your approval." || eventCode != "permission_required" || task != "Task 5")
        {
            Console.WriteLine("FAIL payload: unexpected values (text length " + text.Length + ", event " + eventCode + ")");
            return 1;
        }

        Console.WriteLine("payload contract ok: fields=event,task,text · text=\"" + text + "\" · event=" + eventCode + " · task=" + task);
        return 0;
    }

    private static MonitoringSnapshot SimpleSnapshot(VoiceEventKind kind, int number, bool aggregate = false)
    {
        var key = new TaskKey("codex", "01a0f000-0000-0000-0000-00000000000" + number);
        var attentionKind = kind switch
        {
            VoiceEventKind.PermissionRequired => AttentionEventKind.PermissionRequired,
            VoiceEventKind.InputRequired => AttentionEventKind.InputRequired,
            VoiceEventKind.TaskError => AttentionEventKind.TaskError,
            VoiceEventKind.TaskCompleted => AttentionEventKind.TaskCompleted,
            _ => AttentionEventKind.AllTasksCompleted,
        };
        var state = kind switch
        {
            VoiceEventKind.PermissionRequired => AgentTaskState.NeedsApproval,
            VoiceEventKind.InputRequired => AgentTaskState.NeedsInput,
            VoiceEventKind.TaskError => AgentTaskState.Error,
            VoiceEventKind.TaskCompleted => AgentTaskState.Done,
            _ => AgentTaskState.Done,
        };
        // An all-complete episode is only valid when the completed work is still
        // visible and no other work remains, exactly as the detector produces it.
        var tasks = new[] { Task(key, number, aggregate ? AgentTaskState.Done : state) };
        var working = aggregate ? CountValue.Known(0) : CountValue.Known(1);
        return Snapshot(DateTimeOffset.UtcNow, tasks, new[] { Attention(attentionKind, key, number, "turn-" + number) }, working: working);
    }

    private static int Expect(string label, int expected, Func<int> actual, int timeoutMs, Action? pump = null)
    {
        var started = Environment.TickCount64;
        var value = actual();
        while (value != expected && Environment.TickCount64 - started < timeoutMs)
        {
            // Completion aggregation only flushes on the pump, exactly like the
            // HUD timer does in production.
            pump?.Invoke();
            Thread.Sleep(25);
            value = actual();
        }

        if (value == expected)
        {
            Console.WriteLine("ok   " + label + " (" + value + ")");
            return 0;
        }

        Console.WriteLine("FAIL " + label + ": expected " + expected + ", saw " + value);
        return 1;
    }

    private static void ShowPlayerReport(CountingPlayer player)
    {
        Console.WriteLine("playback: API successes=" + player.Played.Count +
                          " device-unavailable=" + player.DeviceUnavailable +
                          " stop-calls=" + player.StopCount);
        if (player.Audible)
        {
            Console.WriteLine(player.DeviceUnavailable
                ? "audible observation: UNAVAILABLE (no usable sound device in this environment)"
                : "audible observation: clips were sent to the real sound device (not verified by ear)");
        }
        else
        {
            Console.WriteLine("audible observation: not requested (silent default); synthetic tones prove mechanics only");
        }
    }

    private static AgentTask Task(TaskKey key, int number, AgentTaskState state, string? turn = null) => new(
        key,
        number,
        key.ConversationId,
        key.ProfileId,
        ClientSurface.Desktop,
        "Task " + number,
        turn ?? "turn-" + number,
        state,
        new AgentAction(AgentActionKind.RunningTests, EvidenceKind.Explicit),
        EvidenceKind.Explicit,
        Start,
        Start,
        Start,
        null,
        state == AgentTaskState.Done ? Start : null,
        false,
        false,
        state is AgentTaskState.NeedsApproval or AgentTaskState.NeedsInput,
        false,
        false,
        null,
        1);

    private static AttentionEvent Attention(AttentionEventKind kind, TaskKey key, int number, string turn) => new(
        kind,
        key,
        number,
        "Task " + number,
        AgentTaskState.Working,
        AgentActionKind.RunningTests,
        turn,
        DateTimeOffset.UtcNow,
        "smoke");

    private static MonitoringSnapshot Snapshot(
        DateTimeOffset at,
        IReadOnlyList<AgentTask> tasks,
        IReadOnlyList<AttentionEvent> transitions,
        CountValue? working = null,
        bool coverageComplete = true) => new(
        at,
        new MonitoringHealth(MonitoringHealthLevel.Healthy, Array.Empty<string>(), coverageComplete, at, at),
        new MonitoringCounts(
            working ?? CountValue.Known(tasks.Count(item => item.State == AgentTaskState.Working)),
            CountValue.Known(tasks.Count(item => item.NeedsHuman)),
            CountValue.Known(tasks.Count(item => item.State == AgentTaskState.NeedsApproval)),
            CountValue.Known(tasks.Count(item => item.State == AgentTaskState.NeedsInput)),
            CountValue.Known(tasks.Count(item => item.State == AgentTaskState.Error)),
            CountValue.Known(tasks.Count(item => item.State == AgentTaskState.Done)),
            CountValue.Known(0),
            CountValue.Known(tasks.Count)),
        tasks,
        transitions,
        MonitorCapabilities.Initial,
        LiveObservationStats.Empty);

    /// <summary>Counts playback; forwards to the real device only when asked.</summary>
    private sealed class CountingPlayer : IVoicePlayer, IDisposable
    {
        private readonly WavPlaybackService? _inner;
        private readonly List<VoiceClip> _played = new();

        public CountingPlayer(bool audible)
        {
            Audible = audible;
            _inner = audible ? new WavPlaybackService() : null;
        }

        public bool Audible { get; }

        public bool DeviceUnavailable => _inner?.DeviceUnavailable ?? false;

        public int StopCount { get; private set; }

        public IReadOnlyList<VoiceClip> Played
        {
            get
            {
                lock (_played)
                {
                    return _played.ToArray();
                }
            }
        }

        public async ValueTask<bool> PlayAsync(VoiceClip clip, CancellationToken cancellationToken)
        {
            lock (_played)
            {
                _played.Add(clip);
            }

            if (_inner is not null)
            {
                return await _inner.PlayAsync(clip, cancellationToken).ConfigureAwait(false);
            }

            // Silent mode still honours cancellation so mute semantics are real.
            try
            {
                await System.Threading.Tasks.Task.Delay(120, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public void Stop()
        {
            StopCount++;
            _inner?.Stop();
        }

        public void Dispose()
        {
            // The playback service holds no unmanaged state between clips.
        }
    }
}
