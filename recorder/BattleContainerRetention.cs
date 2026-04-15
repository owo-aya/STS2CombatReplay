using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace STS2CombatRecorder;

internal sealed record BattleContainerRetentionPolicy(
    int MaxBattleContainers,
    long? MaxTotalBytes)
{
    public static BattleContainerRetentionPolicy Default { get; } =
        new(MaxBattleContainers: 200, MaxTotalBytes: null);
}

internal sealed record BattleContainerRetentionRunResult(
    bool CleanupAttempted,
    int DeletedContainerCount,
    long BytesReclaimed,
    bool Failed,
    string? FailureMessage,
    IReadOnlyList<string> DeletedContainerPaths);

internal static class BattleContainerRetentionManager
{
    private sealed class ContainerCandidate
    {
        public required string Path { get; init; }
        public required string BattleId { get; init; }
        public required BattleContainerCompletionState CompletionState { get; init; }
        public required DateTimeOffset SortKey { get; init; }
        public required long SizeBytes { get; init; }
        public required bool IsProtected { get; init; }
    }

    public static BattleContainerRetentionRunResult Cleanup(
        string combatLogsRoot,
        BattleContainerRetentionPolicy policy,
        string? activeBattleDirectory = null,
        Func<string, long>? measureDirectoryBytes = null,
        Action<string>? deleteDirectory = null)
    {
        measureDirectoryBytes ??= MeasureDirectoryBytes;
        deleteDirectory ??= static directory => Directory.Delete(directory, recursive: true);

        if (string.IsNullOrWhiteSpace(combatLogsRoot) || !Directory.Exists(combatLogsRoot))
        {
            return new BattleContainerRetentionRunResult(
                CleanupAttempted: false,
                DeletedContainerCount: 0,
                BytesReclaimed: 0,
                Failed: false,
                FailureMessage: null,
                DeletedContainerPaths: Array.Empty<string>());
        }

        var candidates = Directory.EnumerateDirectories(combatLogsRoot)
            .Select(directory => ProbeContainer(directory, activeBattleDirectory, measureDirectoryBytes))
            .Where(candidate => candidate != null)
            .Cast<ContainerCandidate>()
            .ToList();

        var totalContainerCount = candidates.Count;
        var totalBytes = candidates.Sum(candidate => candidate.SizeBytes);
        var needsCountCleanup =
            policy.MaxBattleContainers > 0 && totalContainerCount > policy.MaxBattleContainers;
        var needsByteCleanup =
            policy.MaxTotalBytes.HasValue && totalBytes > policy.MaxTotalBytes.Value;

        if (!needsCountCleanup && !needsByteCleanup)
        {
            return new BattleContainerRetentionRunResult(
                CleanupAttempted: false,
                DeletedContainerCount: 0,
                BytesReclaimed: 0,
                Failed: false,
                FailureMessage: null,
                DeletedContainerPaths: Array.Empty<string>());
        }

        var deletedPaths = new List<string>();
        var failureMessages = new List<string>();
        var reclaimBytes = 0L;

        foreach (var candidate in candidates
                     .Where(candidate => !candidate.IsProtected &&
                                         candidate.CompletionState == BattleContainerCompletionState.Completed)
                     .OrderBy(candidate => candidate.SortKey)
                     .ThenBy(candidate => candidate.BattleId, StringComparer.Ordinal))
        {
            if (!ShouldContinueCleanup(totalContainerCount, totalBytes, policy))
                break;

            try
            {
                deleteDirectory(candidate.Path);
                deletedPaths.Add(candidate.Path);
                reclaimBytes += candidate.SizeBytes;
                totalContainerCount--;
                totalBytes -= candidate.SizeBytes;
            }
            catch (Exception ex)
            {
                failureMessages.Add(
                    $"Failed to delete battle container '{candidate.BattleId}': {ex.Message}");
            }
        }

        return new BattleContainerRetentionRunResult(
            CleanupAttempted: true,
            DeletedContainerCount: deletedPaths.Count,
            BytesReclaimed: reclaimBytes,
            Failed: failureMessages.Count > 0,
            FailureMessage: failureMessages.Count == 0 ? null : string.Join(" | ", failureMessages),
            DeletedContainerPaths: deletedPaths);
    }

    private static bool ShouldContinueCleanup(
        int totalContainerCount,
        long totalBytes,
        BattleContainerRetentionPolicy policy)
    {
        var overContainerLimit =
            policy.MaxBattleContainers > 0 && totalContainerCount > policy.MaxBattleContainers;
        var overByteLimit =
            policy.MaxTotalBytes.HasValue && totalBytes > policy.MaxTotalBytes.Value;
        return overContainerLimit || overByteLimit;
    }

    private static ContainerCandidate? ProbeContainer(
        string directory,
        string? activeBattleDirectory,
        Func<string, long> measureDirectoryBytes)
    {
        try
        {
            var metadataPath = Path.Combine(directory, "metadata.json");
            var metadata = File.Exists(metadataPath)
                ? ReadMetadataProbe(metadataPath)
                : null;

            var completionState = metadata?.CompletionState ??
                                  InferLegacyCompletionState(metadata?.EndedAt, metadata?.Result);
            var isProtected = PathsEqual(directory, activeBattleDirectory) ||
                              completionState != BattleContainerCompletionState.Completed;

            return new ContainerCandidate
            {
                Path = directory,
                BattleId = metadata?.BattleId ?? Path.GetFileName(directory),
                CompletionState = completionState,
                SortKey = metadata?.SortKey ?? new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory)),
                SizeBytes = measureDirectoryBytes(directory),
                IsProtected = isProtected,
            };
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleContainerRetentionManager) + ".ProbeContainer", ex);
            return null;
        }
    }

    private static bool PathsEqual(string left, string? right)
    {
        if (string.IsNullOrWhiteSpace(right))
            return false;

        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private static BattleContainerCompletionState InferLegacyCompletionState(
        string? endedAt,
        string? result)
    {
        if (!string.IsNullOrWhiteSpace(endedAt) || !string.IsNullOrWhiteSpace(result))
            return BattleContainerCompletionState.Completed;

        return BattleContainerCompletionState.Active;
    }

    private sealed record MetadataProbe(
        string? BattleId,
        BattleContainerCompletionState? CompletionState,
        string? EndedAt,
        string? Result,
        DateTimeOffset? SortKey);

    private static MetadataProbe? ReadMetadataProbe(string metadataPath)
    {
        using var stream = File.OpenRead(metadataPath);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        string? battleId = root.TryGetProperty("battle_id", out var battleIdElement)
            ? battleIdElement.GetString()
            : null;

        string? completionStateText = null;
        if (root.TryGetProperty("container", out var containerElement) &&
            containerElement.ValueKind == JsonValueKind.Object &&
            containerElement.TryGetProperty("completion_state", out var completionElement))
        {
            completionStateText = completionElement.GetString();
        }

        string? endedAt = null;
        string? result = null;
        string? startedAt = null;
        if (root.TryGetProperty("battle", out var battleElement) &&
            battleElement.ValueKind == JsonValueKind.Object)
        {
            if (battleElement.TryGetProperty("ended_at", out var endedAtElement))
                endedAt = endedAtElement.GetString();
            if (battleElement.TryGetProperty("result", out var resultElement))
                result = resultElement.GetString();
            if (battleElement.TryGetProperty("started_at", out var startedAtElement))
                startedAt = startedAtElement.GetString();
        }

        return new MetadataProbe(
            BattleId: battleId,
            CompletionState: string.IsNullOrWhiteSpace(completionStateText)
                ? null
                : BattleContainerCompletionStateExtensions.Parse(completionStateText),
            EndedAt: endedAt,
            Result: result,
            SortKey: ParseSortKey(endedAt) ?? ParseSortKey(startedAt));
    }

    private static DateTimeOffset? ParseSortKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateTimeOffset.TryParse(value, out var timestamp)
            ? timestamp
            : null;
    }

    private static long MeasureDirectoryBytes(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length);
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleContainerRetentionManager) + ".MeasureDirectoryBytes", ex);
            return 0L;
        }
    }
}
