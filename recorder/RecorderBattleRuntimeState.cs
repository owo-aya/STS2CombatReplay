using System;
using System.Collections.Generic;
using System.Linq;

namespace STS2CombatRecorder;

internal enum BattleContainerCompletionState
{
    Initialized,
    Active,
    Completed,
    Partial,
    FailedFinalize,
}

internal static class BattleContainerCompletionStateExtensions
{
    public static string ToWireValue(this BattleContainerCompletionState state)
    {
        return state switch
        {
            BattleContainerCompletionState.Initialized => "initialized",
            BattleContainerCompletionState.Active => "active",
            BattleContainerCompletionState.Completed => "completed",
            BattleContainerCompletionState.Partial => "partial",
            _ => "failed_finalize",
        };
    }

    public static BattleContainerCompletionState Parse(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "initialized" => BattleContainerCompletionState.Initialized,
            "active" => BattleContainerCompletionState.Active,
            "completed" => BattleContainerCompletionState.Completed,
            "partial" => BattleContainerCompletionState.Partial,
            "failed_finalize" => BattleContainerCompletionState.FailedFinalize,
            _ => BattleContainerCompletionState.Active,
        };
    }
}

internal sealed record RecorderBattleMetadataContext(
    string BattleId,
    string CharacterId,
    string? CharacterName,
    string EncounterId,
    string? EncounterName,
    string? Seed,
    string? StartedAt,
    string? EndedAt,
    string? Result);

internal static class RecorderBattleMetadataFactory
{
    public static Dictionary<string, object?> Build(
        string schemaName,
        string protocolVersion,
        string schemaVersion,
        string modVersion,
        string recorderName,
        string recorderVersion,
        RecorderBattleMetadataContext context,
        RecorderBattleRuntimeState runtimeState)
    {
        return new Dictionary<string, object?>
        {
            ["schema_name"] = schemaName,
            ["protocol_version"] = protocolVersion,
            ["schema_version"] = schemaVersion,
            ["mod_version"] = modVersion,
            ["battle_id"] = context.BattleId,
            ["game"] = runtimeState.BuildMetadataGameObject(),
            ["compat"] = runtimeState.BuildMetadataCompatObject(),
            ["recorder"] = new Dictionary<string, object?>
            {
                ["name"] = recorderName,
                ["version"] = recorderVersion,
            },
            ["container"] = runtimeState.BuildMetadataContainerObject(),
            ["battle"] = new Dictionary<string, object?>
            {
                ["character_id"] = context.CharacterId,
                ["character_name"] = context.CharacterName,
                ["encounter_id"] = context.EncounterId,
                ["encounter_name"] = context.EncounterName,
                ["seed"] = context.Seed,
                ["started_at"] = context.StartedAt,
                ["ended_at"] = context.EndedAt,
                ["result"] = context.Result,
            },
        };
    }
}

internal sealed class RecorderBattleRuntimeState
{
    private sealed class WarningRecord
    {
        public WarningRecord(string code, string message)
        {
            Code = code;
            Message = message;
            Count = 1;
        }

        public string Code { get; }
        public string Message { get; }
        public long Count { get; set; }
    }

    private readonly List<WarningRecord> _warningRecords = new();
    private readonly Dictionary<string, WarningRecord> _warningRecordIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _warningCodeHistogram = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, long>> _truthQuality = CreateTruthQualityMap();

    private long _warningCount;
    private bool _stickyPartialState;

    public RecorderBattleRuntimeState(RecorderCompatibilityAssessment compatibility)
    {
        Compatibility = compatibility;
        CompletionState = BattleContainerCompletionState.Initialized;

        for (var i = 0; i < compatibility.WarningCodes.Count; i++)
        {
            RecordWarning(compatibility.WarningCodes[i], compatibility.WarningMessages[i]);
        }
    }

    public RecorderCompatibilityAssessment Compatibility { get; }
    public BattleContainerCompletionState CompletionState { get; private set; }
    public long EventWriteFailureCount { get; private set; }
    public long SnapshotWriteFailureCount { get; private set; }
    public long MetadataFinalizeFailureCount { get; private set; }
    public long MetadataWriteFailureCount { get; private set; }
    public long DiagnosticsWriteFailureCount { get; private set; }
    public bool Truncated { get; private set; }
    public bool FlushFailure { get; private set; }
    public BattleContainerRetentionRunResult? LastRetentionResult { get; private set; }

    public bool HasOutputIntegrityFailures =>
        EventWriteFailureCount > 0 ||
        SnapshotWriteFailureCount > 0 ||
        MetadataFinalizeFailureCount > 0;

    public bool IsPartialState =>
        CompletionState is BattleContainerCompletionState.Partial or BattleContainerCompletionState.FailedFinalize;

    public bool ShouldRecordFinalizeFailureWarning()
    {
        return HasOutputIntegrityFailures;
    }

    public void SetCompletionState(BattleContainerCompletionState completionState)
    {
        CompletionState = completionState;
    }

    public BattleContainerCompletionState GetRecommendedFinalState()
    {
        if (MetadataFinalizeFailureCount > 0)
            return BattleContainerCompletionState.FailedFinalize;

        if (_stickyPartialState)
            return BattleContainerCompletionState.Partial;

        return HasOutputIntegrityFailures
            ? BattleContainerCompletionState.Partial
            : BattleContainerCompletionState.Completed;
    }

    public void RecordWarning(string code, string message)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(message))
            return;

        _warningCount++;

        if (_warningCodeHistogram.TryGetValue(code, out var existingCodeCount))
        {
            _warningCodeHistogram[code] = existingCodeCount + 1;
        }
        else
        {
            _warningCodeHistogram[code] = 1;
        }

        var key = $"{code}\n{message}";
        if (_warningRecordIndex.TryGetValue(key, out var record))
        {
            record.Count++;
            return;
        }

        var newRecord = new WarningRecord(code, message);
        _warningRecordIndex[key] = newRecord;
        _warningRecords.Add(newRecord);
    }

    public void RecordPartialBattleContainer(string reason)
    {
        _stickyPartialState = true;

        if (CompletionState == BattleContainerCompletionState.Completed)
        {
            CompletionState = BattleContainerCompletionState.Partial;
        }
        else if (CompletionState != BattleContainerCompletionState.FailedFinalize)
        {
            CompletionState = BattleContainerCompletionState.Partial;
        }

        RecordWarning("partial_battle_container", reason);
    }

    public void RecordEventWriteFailure(string message)
    {
        EventWriteFailureCount++;
        Truncated = true;
        RecordWarning("event_write_failed", message);
    }

    public void RecordSnapshotWriteFailure(string message)
    {
        SnapshotWriteFailureCount++;
        Truncated = true;
        RecordWarning("snapshot_write_failed", message);
    }

    public void RecordMetadataWriteFailure(string message)
    {
        MetadataWriteFailureCount++;
        RecordWarning("metadata_write_failed", message);
    }

    public void RecordMetadataFinalizeFailure(string message)
    {
        MetadataFinalizeFailureCount++;
        CompletionState = BattleContainerCompletionState.FailedFinalize;
        RecordWarning("metadata_finalize_failed", message);
    }

    public void RecordDiagnosticsWriteFailure(string message)
    {
        DiagnosticsWriteFailureCount++;
        RecordWarning("diagnostics_write_failed", message);
    }

    public void RecordRetentionResult(BattleContainerRetentionRunResult result)
    {
        LastRetentionResult = result;
        if (!result.Failed || string.IsNullOrWhiteSpace(result.FailureMessage))
            return;

        RecordWarning("retention_cleanup_failed", result.FailureMessage);
    }

    public void RecordCardModifiedDriftValidatorHit()
    {
        IncrementTruthQuality("card", "card_modified_drift_validator_hits");
    }

    public void InspectWrittenEvent(string eventType, string? resolutionId, Dictionary<string, object?> payload)
    {
        switch (eventType)
        {
            case "damage_attempt":
                InspectDamageAttempt(payload);
                break;
            case "power_applied":
            case "power_stacks_changed":
            case "power_removed":
                InspectPowerEvent(eventType, payload);
                break;
            case "block_changed":
            case "block_broken":
            case "block_cleared":
            case "block_clear_prevented":
                if (!HasDictionary(payload, "trigger"))
                {
                    IncrementTruthQuality("block", "blank_trigger_count");
                }
                break;
            case "card_created":
                if (!HasDictionary(payload, "trigger") && string.IsNullOrWhiteSpace(resolutionId))
                {
                    IncrementTruthQuality("card", "blank_trigger_rootless_create_count");
                }
                break;
            case "entity_revived":
                if (!HasDictionary(payload, "trigger"))
                {
                    IncrementTruthQuality("entity", "blank_revive_trigger_count");
                }
                break;
            case "entity_removed":
                if (!HasDictionary(payload, "trigger") &&
                    string.Equals(GetString(payload, "reason"), "roster_absent", StringComparison.Ordinal))
                {
                    IncrementTruthQuality("entity", "cleanup_blank_trigger_removal_count");
                }
                break;
            case "energy_changed":
            case "resource_changed":
                InspectTriggerBoundSlice("resource", payload);
                break;
            case "orb_slots_changed":
            case "orb_inserted":
            case "orb_evoked":
            case "orb_removed":
            case "orb_passive_triggered":
            case "orb_modified":
                InspectOrbEvent(payload);
                break;
            case "relic_obtained":
            case "relic_removed":
            case "relic_triggered":
            case "relic_modified":
                InspectRelicEvent(payload);
                break;
        }
    }

    public Dictionary<string, object?> BuildMetadataGameObject()
    {
        return new Dictionary<string, object?>
        {
            ["title"] = "Slay the Spire 2",
            ["channel"] = string.IsNullOrWhiteSpace(Compatibility.Game.Channel)
                ? "unknown"
                : Compatibility.Game.Channel,
            ["version"] = Compatibility.Game.Version,
            ["build"] = Compatibility.Game.Build,
            ["sts2_dll_hash"] = Compatibility.Game.Sts2DllHash,
        };
    }

    public Dictionary<string, object?> BuildMetadataCompatObject()
    {
        return new Dictionary<string, object?>
        {
            ["status"] = Compatibility.Status.ToWireValue(),
            ["warning_codes"] = Compatibility.WarningCodes.ToArray(),
            ["warning_messages"] = Compatibility.WarningMessages.ToArray(),
        };
    }

    public Dictionary<string, object?> BuildMetadataContainerObject()
    {
        return new Dictionary<string, object?>
        {
            ["completion_state"] = CompletionState.ToWireValue(),
        };
    }

    public Dictionary<string, object?> BuildSummaryCompatObject()
    {
        return new Dictionary<string, object?>
        {
            ["channel"] = string.IsNullOrWhiteSpace(Compatibility.Game.Channel)
                ? "unknown"
                : Compatibility.Game.Channel,
            ["version"] = Compatibility.Game.Version,
            ["build"] = Compatibility.Game.Build,
            ["sts2_dll_hash"] = Compatibility.Game.Sts2DllHash,
            ["status"] = Compatibility.Status.ToWireValue(),
            ["warnings"] = Compatibility.WarningCodes.Count == 0
                ? Array.Empty<object>()
                : Compatibility.WarningCodes
                    .Select((code, index) => new Dictionary<string, object?>
                    {
                        ["code"] = code,
                        ["message"] = Compatibility.WarningMessages[index],
                    })
                    .Cast<object>()
                    .ToArray(),
        };
    }

    public Dictionary<string, object?> BuildSummaryRuntimeWarningsObject()
    {
        return new Dictionary<string, object?>
        {
            ["warning_count"] = _warningCount,
            ["warning_list"] = _warningRecords
                .Select(record => new Dictionary<string, object?>
                {
                    ["code"] = record.Code,
                    ["message"] = record.Message,
                    ["count"] = record.Count,
                })
                .Cast<object>()
                .ToArray(),
            ["warning_code_histogram"] = _warningCodeHistogram
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value),
        };
    }

    public Dictionary<string, object?> BuildSummaryTruthQualityObject()
    {
        return _truthQuality
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .ToDictionary(
                kvp => kvp.Key,
                kvp => (object?)kvp.Value
                    .OrderBy(metric => metric.Key, StringComparer.Ordinal)
                    .ToDictionary(metric => metric.Key, metric => (object?)metric.Value));
    }

    public Dictionary<string, object?> BuildSummaryOutputIntegrityObject()
    {
        return new Dictionary<string, object?>
        {
            ["completion_state"] = CompletionState.ToWireValue(),
            ["is_partial"] = IsPartialState,
            ["is_truncated"] = Truncated,
            ["flush_failed"] = FlushFailure,
            ["event_write_failed"] = EventWriteFailureCount > 0,
            ["event_write_failure_count"] = EventWriteFailureCount,
            ["snapshot_write_failed"] = SnapshotWriteFailureCount > 0,
            ["snapshot_write_failure_count"] = SnapshotWriteFailureCount,
            ["metadata_finalize_failed"] = MetadataFinalizeFailureCount > 0,
            ["metadata_finalize_failure_count"] = MetadataFinalizeFailureCount,
            ["diagnostics_write_failed"] = DiagnosticsWriteFailureCount > 0,
            ["diagnostics_write_failure_count"] = DiagnosticsWriteFailureCount,
        };
    }

    public Dictionary<string, object?> BuildSummaryRetentionObject()
    {
        return new Dictionary<string, object?>
        {
            ["cleanup_happened"] = LastRetentionResult?.CleanupAttempted ?? false,
            ["containers_deleted"] = LastRetentionResult?.DeletedContainerCount ?? 0,
            ["bytes_reclaimed"] = LastRetentionResult?.BytesReclaimed ?? 0L,
            ["cleanup_failed"] = LastRetentionResult?.Failed ?? false,
            ["failure_message"] = LastRetentionResult?.FailureMessage,
        };
    }

    private void InspectDamageAttempt(Dictionary<string, object?> payload)
    {
        if (string.Equals(GetNestedString(payload, "executor", "kind"), "unknown", StringComparison.Ordinal) ||
            ContainsString(payload, "unknown_flags", "executor_unknown"))
        {
            IncrementTruthQuality("damage", "executor_unknown_count");
        }

        if (!HasDictionary(payload, "trigger"))
        {
            IncrementTruthQuality("damage", "trigger_blank_count");
        }

        if (ContainsString(payload, "unknown_flags", "modify_damage_unmatched") ||
            StepsContainUnknownReason(payload, "result_only_fallback"))
        {
            IncrementTruthQuality("damage", "fallback_closeout_count");
        }
    }

    private void InspectPowerEvent(string eventType, Dictionary<string, object?> payload)
    {
        if (!HasDictionary(payload, "applier"))
        {
            IncrementTruthQuality("power", "applier_blank_count");
        }

        if (!HasDictionary(payload, "trigger"))
        {
            IncrementTruthQuality("power", "trigger_blank_count");
        }

        if (eventType == "power_removed" &&
            !HasDictionary(payload, "applier") &&
            !HasDictionary(payload, "trigger"))
        {
            IncrementTruthQuality("power", "removal_blank_count");
        }
    }

    private void InspectTriggerBoundSlice(string slice, Dictionary<string, object?> payload)
    {
        if (!HasDictionary(payload, "trigger"))
        {
            IncrementTruthQuality(slice, "blank_trigger_count");
        }

        if (string.Equals(GetString(payload, "reason"), "unknown", StringComparison.Ordinal) ||
            string.Equals(GetNestedString(payload, "trigger", "kind"), "unknown", StringComparison.Ordinal))
        {
            IncrementTruthQuality(slice, "fallback_count");
        }
    }

    private void InspectOrbEvent(Dictionary<string, object?> payload)
    {
        if (!HasDictionary(payload, "trigger"))
        {
            IncrementTruthQuality("orb", "blank_trigger_count");
        }

        if (string.Equals(GetString(payload, "reason"), "combat_end_cleanup", StringComparison.Ordinal) ||
            string.Equals(GetNestedString(payload, "trigger", "kind"), "unknown", StringComparison.Ordinal))
        {
            IncrementTruthQuality("orb", "fallback_count");
        }
    }

    private void InspectRelicEvent(Dictionary<string, object?> payload)
    {
        if (!HasDictionary(payload, "trigger"))
        {
            IncrementTruthQuality("relic", "blank_trigger_count");
        }

        if (string.Equals(GetNestedString(payload, "trigger", "kind"), "unknown", StringComparison.Ordinal))
        {
            IncrementTruthQuality("relic", "fallback_count");
        }
    }

    private void IncrementTruthQuality(string slice, string metric, long delta = 1)
    {
        if (!_truthQuality.TryGetValue(slice, out var metrics))
            return;

        metrics[metric] = metrics.TryGetValue(metric, out var existingValue)
            ? existingValue + delta
            : delta;
    }

    private static Dictionary<string, Dictionary<string, long>> CreateTruthQualityMap()
    {
        return new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal)
        {
            ["damage"] = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["executor_unknown_count"] = 0,
                ["fallback_closeout_count"] = 0,
                ["trigger_blank_count"] = 0,
            },
            ["power"] = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["applier_blank_count"] = 0,
                ["removal_blank_count"] = 0,
                ["trigger_blank_count"] = 0,
            },
            ["block"] = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["blank_trigger_count"] = 0,
            },
            ["card"] = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["blank_trigger_rootless_create_count"] = 0,
                ["card_modified_drift_validator_hits"] = 0,
            },
            ["entity"] = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["blank_revive_trigger_count"] = 0,
                ["cleanup_blank_trigger_removal_count"] = 0,
            },
            ["resource"] = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["blank_trigger_count"] = 0,
                ["fallback_count"] = 0,
            },
            ["orb"] = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["blank_trigger_count"] = 0,
                ["fallback_count"] = 0,
            },
            ["relic"] = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["blank_trigger_count"] = 0,
                ["fallback_count"] = 0,
            },
        };
    }

    private static bool HasDictionary(Dictionary<string, object?> payload, string key)
    {
        return payload.TryGetValue(key, out var value) &&
               value is IReadOnlyDictionary<string, object?>;
    }

    private static string? GetString(Dictionary<string, object?> payload, string key)
    {
        return payload.TryGetValue(key, out var value) && value is string text
            ? text
            : null;
    }

    private static string? GetNestedString(
        Dictionary<string, object?> payload,
        string objectKey,
        string childKey)
    {
        if (!payload.TryGetValue(objectKey, out var nestedValue) ||
            nestedValue is not IReadOnlyDictionary<string, object?> nested)
        {
            return null;
        }

        return nested.TryGetValue(childKey, out var value) && value is string text
            ? text
            : null;
    }

    private static bool ContainsString(
        Dictionary<string, object?> payload,
        string key,
        string expectedValue)
    {
        if (!payload.TryGetValue(key, out var values) ||
            values is not IEnumerable<object?> enumerable)
        {
            return false;
        }

        return enumerable.Any(value =>
            string.Equals(value as string, expectedValue, StringComparison.Ordinal));
    }

    private static bool StepsContainUnknownReason(
        Dictionary<string, object?> payload,
        string expectedReason)
    {
        if (!payload.TryGetValue("steps", out var values) ||
            values is not IEnumerable<object?> steps)
        {
            return false;
        }

        foreach (var step in steps)
        {
            if (step is not IReadOnlyDictionary<string, object?> stepDict)
                continue;

            if (stepDict.TryGetValue("unknown_reason", out var value) &&
                string.Equals(value as string, expectedReason, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
