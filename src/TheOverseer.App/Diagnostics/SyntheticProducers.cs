using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using TheOverseer.App.Monitoring;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;

namespace TheOverseer.App.Diagnostics;

/// <summary>
/// Invented producer files for the finite production-path checks.
/// </summary>
/// <remarks>
/// These write the same documents the shipped producers write - a Codex rollout, an
/// append-only bridge <c>events.jsonl</c> and an atomically replaced
/// <c>current.json</c> - so a check drives the real readers instead of a mock. Every
/// label is synthetic and every path lives under a caller-owned directory, so no real
/// rollout, spool, credential or harness profile is read or written.
/// </remarks>
internal static class SyntheticProducers
{
    public const string ProducerVersion = "overseer-integration-check/1.0.0";

    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string Iso(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>
    /// One invented Codex rollout. Only structural fields the parser may read are
    /// written, and the labels are synthetic.
    /// </summary>
    internal sealed class SyntheticCodexProducer
    {
        private const string ConversationId = "01a0c900-0000-0000-0000-0000000000c9";
        private string _path;

        public SyntheticCodexProducer(string codexHome)
        {
            var sessions = Path.Combine(codexHome, "sessions", "2026", "01", "02");
            Directory.CreateDirectory(sessions);
            _path = Path.Combine(sessions, "rollout-2026-01-02T03-04-05-" + ConversationId + ".jsonl");
            var now = DateTimeOffset.UtcNow;
            File.WriteAllText(_path, JsonSerializer.Serialize(new
            {
                type = "session_meta",
                payload = new
                {
                    id = ConversationId,
                    session_id = ConversationId,
                    timestamp = Iso(now),
                    cwd = @"C:\synthetic\codex-project",
                    originator = "Codex Desktop",
                    source = "vscode",
                    history_mode = "paginated",
                    cli_version = "0.155.1",
                    model_provider = "openai",
                    thread_source = "user",
                },
            }) + "\n", Utf8NoBom);
        }

        public void WriteWorkingTask()
        {
            var now = DateTimeOffset.UtcNow;
            Append(JsonSerializer.Serialize(new
            {
                type = "event_msg",
                payload = new
                {
                    type = "task_started",
                    turn_id = "turn-1",
                    started_at = now.ToUnixTimeSeconds(),
                    collaboration_mode_kind = "default",
                },
            }));
        }

        /// <summary>
        /// The asynchronous input tool call the desktop client persists: attention
        /// arrives while the turn itself keeps working.
        /// </summary>
        public void RequestAsyncInput(string callId, int questionCount = 1) =>
            Append(ResponseItem("function_call", callId, "request_user_input_async", questionCount));

        public void AcknowledgeAsyncInput(string callId) => Append(ResponseItem("function_call_output", callId, null));

        public void AnswerAsyncInput(string callId, int questionIndex) => Append(JsonSerializer.Serialize(new
        {
            type = "response_item",
            timestamp = Iso(DateTimeOffset.UtcNow),
            payload = new
            {
                type = "message",
                role = "user",
                content = new[]
                {
                    new
                    {
                        type = "input_text",
                        text = "<send_user_message_question_reply>\n" + JsonSerializer.Serialize(new[]
                        {
                            new
                            {
                                questionItemId = JsonSerializer.Serialize(new object[] { "request_user_input_async", callId, questionIndex }),
                                question = "Synthetic question",
                                answer = "Synthetic answer",
                            },
                        }) + "\n</send_user_message_question_reply>",
                    },
                },
            },
        }));

        /// <summary>
        /// Replaces the rollout with unreadable content. A real truncated or partially
        /// written rollout is exactly this shape, and the monitor must report it
        /// instead of keeping the last good state silently.
        /// </summary>
        public void BreakFile() => File.WriteAllText(_path, "{\"type\": \"event_msg\", \"payload\": {", Utf8NoBom);

        /// <summary>Removes the rollout: the session is gone from the source's view.</summary>
        public void RemoveFile()
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }

        /// <summary>
        /// Writes a clean rollout under a new file name - what a producer does when it
        /// rotates - so the source can be observed fresh without depending on how a
        /// reader resumes a file that was rewritten underneath it.
        /// </summary>
        public void RestoreFile()
        {
            var now = DateTimeOffset.UtcNow;
            var suffix = now.ToString("HHmmss", CultureInfo.InvariantCulture);
            _path = Path.Combine(Path.GetDirectoryName(_path)!, "rollout-" + suffix + "-" + ConversationId + "_recovered.jsonl");
            var lines = new List<string>
            {
                JsonSerializer.Serialize(new
                {
                    type = "session_meta",
                    payload = new
                    {
                        id = ConversationId,
                        session_id = ConversationId,
                        timestamp = Iso(now),
                        cwd = @"C:\synthetic\codex-project",
                        originator = "Codex Desktop",
                        source = "vscode",
                        history_mode = "paginated",
                        cli_version = "0.155.1",
                        model_provider = "openai",
                        thread_source = "user",
                    },
                }),
                JsonSerializer.Serialize(new
                {
                    type = "event_msg",
                    payload = new
                    {
                        type = "task_started",
                        turn_id = "turn-2",
                        started_at = now.ToUnixTimeSeconds(),
                        collaboration_mode_kind = "default",
                    },
                }),
            };
            File.WriteAllLines(_path, lines, Utf8NoBom);
        }

        public void Complete()
        {
            var now = DateTimeOffset.UtcNow;
            Append(JsonSerializer.Serialize(new
            {
                type = "event_msg",
                payload = new
                {
                    type = "task_complete",
                    turn_id = "turn-1",
                    last_agent_message = "synthetic",
                    started_at = now.AddSeconds(-2).ToUnixTimeSeconds(),
                    completed_at = now.ToUnixTimeSeconds(),
                    duration_ms = 2000,
                },
            }));
        }

        private static string ResponseItem(string type, string callId, string? name, int questionCount = 1) =>
            JsonSerializer.Serialize(new
            {
                type = "response_item",
                timestamp = Iso(DateTimeOffset.UtcNow),
                payload = new
                {
                    type,
                    name,
                    call_id = callId,
                    arguments = type == "function_call" ? JsonSerializer.Serialize(new
                    {
                        questions = Enumerable.Range(0, questionCount).Select(static index => new { title = "Synthetic question " + index }),
                    }) : null,
                    output = type == "function_call_output" ? "synthetic response accepted" : null,
                },
            });

        private void Append(string line)
        {
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, Utf8NoBom);
            writer.WriteLine(line);
        }
    }

    /// <summary>
    /// One invented bridge producer. It publishes the same two documents as the
    /// shipped observers - an append-only <c>events.jsonl</c> and an atomically
    /// replaced <c>current.json</c> - so the consumer's resynchronization path is
    /// exercised rather than bypassed.
    /// </summary>
    internal sealed class SyntheticBridgeProducer
    {
        private sealed class SessionState
        {
            public string ProjectRoot = string.Empty;
            public string OpenTurnId = string.Empty;
            public string PendingApprovalRequestId = string.Empty;
            public string Category = "idle";
            public long Sequence;
            public long Records;
            public DateTimeOffset CreatedAtUtc;
            public DateTimeOffset FirstSeenUtc;
            public DateTimeOffset LastObservedUtc;
        }

        private readonly string _directory;
        private readonly string _harnessId;
        private readonly string _instanceId;
        private readonly string _epoch;
        private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);

        public SyntheticBridgeProducer(string dataDirectory, string harnessId, string instanceId)
        {
            _harnessId = HarnessIds.Normalize(harnessId);
            _instanceId = instanceId;
            _epoch = instanceId + "-epoch-1";
            var root = Path.Combine(dataDirectory, _harnessId == HarnessIds.Claude ? "spool" : "bridges", _harnessId);
            _directory = Path.Combine(root, instanceId);
            Directory.CreateDirectory(_directory);
            File.WriteAllText(EventsPath, string.Empty, Utf8NoBom);
            WriteHealth();
        }

        /// <summary>The spool directory this producer publishes into.</summary>
        public string SpoolDirectory => _directory;

        /// <summary>Backdate invented evidence without changing the production clock.</summary>
        public DateTimeOffset? Timestamp { get; set; }

        private string EventsPath => Path.Combine(_directory, "events.jsonl");

        private string CurrentPath => Path.Combine(_directory, "current.json");

        private string HealthPath => Path.Combine(_directory, "health.json");

        public void Start(string sessionId, string projectRoot)
        {
            var now = Timestamp ?? DateTimeOffset.UtcNow;
            _sessions[sessionId] = new SessionState
            {
                ProjectRoot = projectRoot,
                Category = "idle",
                CreatedAtUtc = now,
                FirstSeenUtc = now,
                LastObservedUtc = now,
            };
            Publish(sessionId, "session/start");
            Publish(sessionId, "turn/start", turnId: "turn-1");
        }

        /// <summary>A host heartbeat: keeps the source fresh without changing task state.</summary>
        public void Heartbeat() => Publish(FirstSession(), "heartbeat");

        public void ApprovalAsked(string requestId) =>
            Publish(FirstSession(), "approval/asked", requestId: requestId, turnId: "turn-1", category: "waiting");

        public void ApprovalDecided(string requestId) =>
            Publish(FirstSession(), "approval/decided", requestId: requestId, turnId: "turn-1", outcome: "ok", category: "working");

        private string FirstSession() => _sessions.Keys.OrderBy(static key => key, StringComparer.Ordinal).First();

        private void Publish(string sessionId, string kind, string? turnId = null, string? requestId = null,
            string? outcome = null, string? category = null)
        {
            var state = _sessions[sessionId];
            var now = Timestamp ?? DateTimeOffset.UtcNow;
            state.Sequence++;
            state.Records++;
            state.LastObservedUtc = now;
            if (category is not null)
            {
                state.Category = category;
            }

            if (!string.IsNullOrEmpty(turnId))
            {
                state.OpenTurnId = turnId!;
            }

            if (kind == "turn/end")
            {
                state.OpenTurnId = string.Empty;
            }

            if (kind == "approval/asked")
            {
                state.PendingApprovalRequestId = requestId ?? string.Empty;
            }

            if (kind == "approval/decided")
            {
                state.PendingApprovalRequestId = string.Empty;
            }

            var record = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                harnessId = _harnessId,
                sourceInstanceId = _instanceId,
                sourceEpoch = _epoch,
                sessionId,
                sequence = state.Sequence,
                eventId = _harnessId + ":" + sessionId + ":" + state.Sequence.ToString(CultureInfo.InvariantCulture),
                observedAtUtc = Iso(now),
                eventAtUtc = Iso(now),
                kind,
                turnId,
                callId = (string?)null,
                requestId,
                projectRoot = state.ProjectRoot,
                category = state.Category,
                evidence = "explicit",
                outcome,
                hydration = false,
                producerVersion = ProducerVersion,
            });
            using (var stream = new FileStream(EventsPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var writer = new StreamWriter(stream, Utf8NoBom))
            {
                writer.WriteLine(record);
            }

            WriteCurrent();
            WriteHealth();
        }

        private void WriteCurrent()
        {
            var now = Timestamp ?? DateTimeOffset.UtcNow;
            var document = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                harnessId = _harnessId,
                sourceInstanceId = _instanceId,
                sourceEpoch = _epoch,
                producerVersion = ProducerVersion,
                updatedAtUtc = Iso(now),
                evictedSessions = 0,
                sessions = _sessions.Select(pair => new
                {
                    sessionId = pair.Key,
                    parentSessionId = (string?)null,
                    projectRoot = pair.Value.ProjectRoot,
                    createdAtUtc = Iso(pair.Value.CreatedAtUtc),
                    firstSeenUtc = Iso(pair.Value.FirstSeenUtc),
                    lastObservedUtc = Iso(pair.Value.LastObservedUtc),
                    openTurnId = pair.Value.OpenTurnId.Length == 0 ? null : pair.Value.OpenTurnId,
                    planMode = false,
                    pendingApprovalRequestId = pair.Value.PendingApprovalRequestId.Length == 0 ? null : pair.Value.PendingApprovalRequestId,
                    category = pair.Value.Category,
                    disposed = false,
                    lastSequence = pair.Value.Sequence,
                    lastEventId = _harnessId + ":" + pair.Key + ":" + pair.Value.Sequence.ToString(CultureInfo.InvariantCulture),
                    records = pair.Value.Records,
                }).ToArray(),
            });
            // The shipped producers publish atomically; the consumer may read at any
            // moment, so a torn document must never be observable.
            var pending = CurrentPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.WriteAllText(pending, document, Utf8NoBom);
            File.Move(pending, CurrentPath, overwrite: true);
        }

        private void WriteHealth()
        {
            var now = Timestamp ?? DateTimeOffset.UtcNow;
            var document = JsonSerializer.Serialize(new
            {
                version = 1,
                producerVersion = ProducerVersion,
                harnessId = _harnessId,
                sourceInstanceId = _instanceId,
                sourceEpoch = _epoch,
                startedAtUtc = Iso(now),
                lastWrite = Iso(now),
                queueDepth = 0,
                dropped = 0,
                unavailable = false,
                counters = new
                {
                    enqueued = _sessions.Values.Sum(static state => state.Records),
                    written = _sessions.Values.Sum(static state => state.Records),
                    dropped = 0,
                    droppedOldest = 0,
                    droppedOversize = 0,
                    droppedError = 0,
                    writeErrors = 0,
                },
                lastError = (string?)null,
            });
            File.WriteAllText(HealthPath, document, Utf8NoBom);
        }
    }
}
