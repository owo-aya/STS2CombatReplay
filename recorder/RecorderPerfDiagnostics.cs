using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STS2CombatRecorder;

internal sealed class RecorderPerfDiagnostics
{
    private static readonly JsonSerializerOptions SummaryOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Stopwatch _battleStopwatch = Stopwatch.StartNew();
    private readonly Dictionary<string, long> _eventCountByType = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _stageTicks = new(StringComparer.Ordinal);
    private readonly long _debugLogBaselineBytes;

    private long _observedFrameCount;
    private long _totalRecorderTicks;
    private long _maxRecorderTicks;
    private long _totalEventCount;
    private long _totalSnapshotCount;
    private long _eventBytesWritten;
    private long _snapshotBytesWritten;
    private long _metadataBytesWritten;
    private long _diagnosticsOutputBytes;

    public RecorderPerfDiagnostics(long debugLogBaselineBytes)
    {
        _debugLogBaselineBytes = debugLogBaselineBytes;
    }

    public void RecordObservedFrame(long elapsedTicks)
    {
        _observedFrameCount++;
        _totalRecorderTicks += elapsedTicks;
        if (elapsedTicks > _maxRecorderTicks)
        {
            _maxRecorderTicks = elapsedTicks;
        }

        RecordStage(StageNames.ProcessFramePollingTotal, elapsedTicks);
    }

    public void RecordStage(string stageName, long elapsedTicks)
    {
        if (_stageTicks.TryGetValue(stageName, out var existingTicks))
        {
            _stageTicks[stageName] = existingTicks + elapsedTicks;
            return;
        }

        _stageTicks[stageName] = elapsedTicks;
    }

    public void RecordEventWritten(string eventType, int bytesWritten)
    {
        _totalEventCount++;
        _eventBytesWritten += bytesWritten;

        if (_eventCountByType.TryGetValue(eventType, out var count))
        {
            _eventCountByType[eventType] = count + 1;
            return;
        }

        _eventCountByType[eventType] = 1;
    }

    public void RecordSnapshotWritten(int bytesWritten)
    {
        _totalSnapshotCount++;
        _snapshotBytesWritten += bytesWritten;
    }

    public void RecordMetadataWritten(int bytesWritten)
    {
        _metadataBytesWritten += bytesWritten;
    }

    public (string Json, int BytesWritten) BuildSummaryJson(
        string battleId,
        RecorderBattleRuntimeState runtimeState)
    {
        _battleStopwatch.Stop();

        var diagnosticsBytes = _diagnosticsOutputBytes;

        while (true)
        {
            var summary = BuildSummaryObject(battleId, diagnosticsBytes, runtimeState);
            var json = JsonSerializer.Serialize(summary, SummaryOpts);
            var bytesWritten = Encoding.UTF8.GetByteCount(json + "\n");
            if (bytesWritten == diagnosticsBytes)
            {
                _diagnosticsOutputBytes = bytesWritten;
                return (json, bytesWritten);
            }

            diagnosticsBytes = bytesWritten;
        }
    }

    private Dictionary<string, object?> BuildSummaryObject(
        string battleId,
        long diagnosticsBytes,
        RecorderBattleRuntimeState runtimeState)
    {
        return new Dictionary<string, object?>
        {
            ["battle_id"] = battleId,
            ["battle_duration_ms"] = RoundMilliseconds(_battleStopwatch.ElapsedTicks),
            ["observed_recorder_frame_count"] = _observedFrameCount,
            ["total_recorder_time_ms"] = RoundMilliseconds(_totalRecorderTicks),
            ["average_recorder_time_per_observed_frame_ms"] = _observedFrameCount > 0
                ? RoundMilliseconds(_totalRecorderTicks / (double)_observedFrameCount)
                : 0d,
            ["max_recorder_time_per_observed_frame_ms"] = RoundMilliseconds(_maxRecorderTicks),
            ["total_event_count"] = _totalEventCount,
            ["event_count_by_type"] = _eventCountByType
                .OrderBy(kvp => kvp.Key)
                .ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value),
            ["total_snapshot_count"] = _totalSnapshotCount,
            ["bytes_written"] = BuildBytesWrittenObject(diagnosticsBytes),
            ["compat"] = runtimeState.BuildSummaryCompatObject(),
            ["runtime_warnings"] = runtimeState.BuildSummaryRuntimeWarningsObject(),
            ["truth_quality"] = runtimeState.BuildSummaryTruthQualityObject(),
            ["output_integrity"] = runtimeState.BuildSummaryOutputIntegrityObject(),
            ["retention"] = runtimeState.BuildSummaryRetentionObject(),
            ["stage_timing_ms"] = _stageTicks
                .OrderBy(kvp => kvp.Key)
                .ToDictionary(kvp => kvp.Key, kvp => (object?)RoundMilliseconds(kvp.Value)),
        };
    }

    private Dictionary<string, object?> BuildBytesWrittenObject(long diagnosticsBytes)
    {
        return new Dictionary<string, object?>
        {
            ["events"] = _eventBytesWritten,
            ["snapshots"] = _snapshotBytesWritten,
            ["metadata"] = _metadataBytesWritten,
            ["diagnostics_output"] = diagnosticsBytes,
            ["battle_container_payload_total"] =
                _eventBytesWritten + _snapshotBytesWritten + _metadataBytesWritten + diagnosticsBytes,
            ["debug_log_bytes_written"] = Math.Max(0, DebugFileLogger.TotalBytesWritten - _debugLogBaselineBytes),
        };
    }

    private static double RoundMilliseconds(long elapsedTicks)
    {
        return RoundMilliseconds((double)elapsedTicks);
    }

    private static double RoundMilliseconds(double elapsedTicks)
    {
        return Math.Round(elapsedTicks * 1000d / Stopwatch.Frequency, 3);
    }

    internal static class StageNames
    {
        public const string ProcessFramePollingTotal = "process_frame_polling_total";
        public const string ZoneDiff = "zone_diff";
        public const string HpBlockDiff = "hp_block_diff";
        public const string PotionDiff = "potion_diff";
        public const string SnapshotStabilityCheck = "snapshot_stability_check";
        public const string EventSerializeWrite = "event_serialize_write";
        public const string SnapshotBuildSerializeWrite = "snapshot_build_serialize_write";
    }
}
