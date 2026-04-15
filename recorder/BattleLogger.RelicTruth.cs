using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace STS2CombatRecorder;

public static partial class BattleLogger
{
    private sealed class TrackedRelicState
    {
        public required RelicModel Model { get; init; }
        public required string RelicInstanceId { get; init; }
        public required string RelicId { get; init; }
        public required string RelicName { get; init; }
        public required string OwnerEntityId { get; init; }
        public int StackCount { get; set; }
        public required string Status { get; set; }
        public int? DisplayAmount { get; set; }
        public bool IsUsedUp { get; set; }
        public bool IsWax { get; set; }
        public bool IsMelted { get; set; }
    }

    private static int _relicInstanceCounter;
    private static Dictionary<RelicModel, string> _relicModelToInstanceId = new();
    private static Dictionary<string, TrackedRelicState> _trackedRelicsById = new(StringComparer.Ordinal);
    private static List<string> _trackedRelicOrder = new();
    private static Player? _subscribedRelicPlayer;
    private static HashSet<RelicModel> _subscribedRelics = new();
    private static Dictionary<RelicModel, Action> _relicDisplayAmountHandlers = new();
    private static Dictionary<RelicModel, Action> _relicStatusHandlers = new();
    private static HashSet<string> _emittedRelicTriggerSignatures = new(StringComparer.Ordinal);

    private static void ResetRelicTruthState()
    {
        UnsubscribeRelicTruthState();
        _relicInstanceCounter = 0;
        _relicModelToInstanceId = new Dictionary<RelicModel, string>();
        _trackedRelicsById = new Dictionary<string, TrackedRelicState>(StringComparer.Ordinal);
        _trackedRelicOrder = new List<string>();
        _relicDisplayAmountHandlers = new Dictionary<RelicModel, Action>();
        _relicStatusHandlers = new Dictionary<RelicModel, Action>();
        _emittedRelicTriggerSignatures = new HashSet<string>(StringComparer.Ordinal);
    }

    private static void UnsubscribeRelicTruthState()
    {
        if (_subscribedRelicPlayer != null)
        {
            _subscribedRelicPlayer.RelicObtained -= OnPlayerRelicObtained;
            _subscribedRelicPlayer.RelicRemoved -= OnPlayerRelicRemoved;
            _subscribedRelicPlayer = null;
        }

        foreach (var relic in _subscribedRelics.ToList())
        {
            try
            {
                relic.Flashed -= OnRelicFlashed;
                if (_relicDisplayAmountHandlers.TryGetValue(relic, out var displayHandler))
                {
                    relic.DisplayAmountChanged -= displayHandler;
                }
                if (_relicStatusHandlers.TryGetValue(relic, out var statusHandler))
                {
                    relic.StatusChanged -= statusHandler;
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        _subscribedRelics = new HashSet<RelicModel>();
        _relicDisplayAmountHandlers.Clear();
        _relicStatusHandlers.Clear();
    }

    public static void SyncInitialRelicState(Player player)
    {
        ResetRelicTruthState();

        try
        {
            SubscribePlayerRelicTruth(player);
            foreach (var relic in GameStateReader.ReadRelics(player))
            {
                var relicState = CaptureTrackedRelicState(relic.Instance);
                UpsertTrackedRelicState(relicState);
            }
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".SyncInitialRelicState", ex);
        }
    }

    public static List<Dictionary<string, object?>> BuildTrackedRelicStatePayloads()
    {
        return _trackedRelicOrder
            .Where(id => _trackedRelicsById.ContainsKey(id))
            .Select(id => BuildRelicPayload(_trackedRelicsById[id]))
            .ToList();
    }

    public static void EmitInitialRelics()
    {
        foreach (var relicPayload in BuildTrackedRelicStatePayloads())
        {
            EmitEvent("relic_initialized", relicPayload);
        }
    }

    public static void OnRelicStackCountModified(RelicModel relic, int oldStackCount, int newStackCount)
    {
        if (!_active || !_initDone || oldStackCount == newStackCount)
        {
            return;
        }

        try
        {
            var trackedState = EnsureTrackedRelicState(relic);
            var eventContext = ResolveCurrentTruthAttributionContext();
            var triggerRef = ResolveRelicTruthTriggerRef();
            var oldIsUsedUp = trackedState.IsUsedUp;
            var oldIsWax = trackedState.IsWax;
            var oldIsMelted = trackedState.IsMelted;

            RefreshTrackedRelicState(trackedState, relic);

            var payload = BuildRelicPayload(trackedState);
            payload["change_kind"] = "stack_count";
            payload["old_stack_count"] = oldStackCount;
            payload["new_stack_count"] = newStackCount;
            AppendTriggerField(payload, triggerRef);

            EmitEvent("relic_modified", _phase, eventContext, payload);
            MarkPendingSnapshotRelevantChange("relic_modified");
            EmitRelicDerivedFlagChanges(
                relic,
                trackedState,
                oldIsUsedUp,
                oldIsWax,
                oldIsMelted,
                eventContext,
                triggerRef);
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnRelicStackCountModified", ex);
        }
    }

    public static void OnRelicFlagModified(RelicModel relic, string flag, bool oldValue, bool newValue)
    {
        if (!_active || !_initDone || oldValue == newValue)
        {
            return;
        }

        try
        {
            var trackedState = EnsureTrackedRelicState(relic);
            var eventContext = ResolveCurrentTruthAttributionContext();
            var triggerRef = ResolveRelicTruthTriggerRef();
            RefreshTrackedRelicState(trackedState, relic);

            var payload = BuildRelicPayload(trackedState);
            payload["change_kind"] = "flag";
            payload["flag"] = flag;
            payload["old_value"] = oldValue;
            payload["new_value"] = newValue;
            AppendTriggerField(payload, triggerRef);

            EmitEvent("relic_modified", _phase, eventContext, payload);
            MarkPendingSnapshotRelevantChange("relic_modified");
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnRelicFlagModified", ex);
        }
    }

    public static void HandleSilentRelicObtained(Player player, RelicModel relic)
    {
        try
        {
            OnRelicObtained(player, relic);
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".HandleSilentRelicObtained", ex);
        }
    }

    public static void HandleSilentRelicRemoved(Player player, RelicModel relic)
    {
        try
        {
            OnRelicRemoved(player, relic);
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".HandleSilentRelicRemoved", ex);
        }
    }

    public static string GetRelicInstanceId(RelicModel relic)
    {
        if (_relicModelToInstanceId.TryGetValue(relic, out var existingId))
        {
            return existingId;
        }

        var nextId = $"relic:{++_relicInstanceCounter:D3}";
        _relicModelToInstanceId[relic] = nextId;
        return nextId;
    }

    private static void SubscribePlayerRelicTruth(Player player)
    {
        if (!ReferenceEquals(_subscribedRelicPlayer, player))
        {
            if (_subscribedRelicPlayer != null)
            {
                _subscribedRelicPlayer.RelicObtained -= OnPlayerRelicObtained;
                _subscribedRelicPlayer.RelicRemoved -= OnPlayerRelicRemoved;
            }

            _subscribedRelicPlayer = player;
            _subscribedRelicPlayer.RelicObtained += OnPlayerRelicObtained;
            _subscribedRelicPlayer.RelicRemoved += OnPlayerRelicRemoved;
        }

        foreach (var relic in player.Relics)
        {
            SubscribeRelicTruth(relic);
        }
    }

    private static void SubscribeRelicTruth(RelicModel relic)
    {
        if (!_subscribedRelics.Add(relic))
        {
            return;
        }

        Action displayHandler = () => OnRelicDisplayAmountChanged(relic);
        Action statusHandler = () => OnRelicStatusChanged(relic);
        _relicDisplayAmountHandlers[relic] = displayHandler;
        _relicStatusHandlers[relic] = statusHandler;
        relic.Flashed += OnRelicFlashed;
        relic.DisplayAmountChanged += displayHandler;
        relic.StatusChanged += statusHandler;
    }

    private static void UnsubscribeRelicTruth(RelicModel relic)
    {
        if (!_subscribedRelics.Remove(relic))
        {
            return;
        }

        relic.Flashed -= OnRelicFlashed;
        if (_relicDisplayAmountHandlers.TryGetValue(relic, out var displayHandler))
        {
            relic.DisplayAmountChanged -= displayHandler;
            _relicDisplayAmountHandlers.Remove(relic);
        }
        if (_relicStatusHandlers.TryGetValue(relic, out var statusHandler))
        {
            relic.StatusChanged -= statusHandler;
            _relicStatusHandlers.Remove(relic);
        }
    }

    private static void OnPlayerRelicObtained(RelicModel relic)
    {
        try
        {
            if (_subscribedRelicPlayer == null)
            {
                return;
            }

            OnRelicObtained(_subscribedRelicPlayer, relic);
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnPlayerRelicObtained", ex);
        }
    }

    private static void OnPlayerRelicRemoved(RelicModel relic)
    {
        try
        {
            if (_subscribedRelicPlayer == null)
            {
                return;
            }

            OnRelicRemoved(_subscribedRelicPlayer, relic);
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnPlayerRelicRemoved", ex);
        }
    }

    private static void OnRelicFlashed(RelicModel relic, IEnumerable<Creature> targets)
    {
        try
        {
            if (!_active || !_initDone)
            {
                return;
            }

            var trackedState = EnsureTrackedRelicState(relic);
            var oldIsUsedUp = trackedState.IsUsedUp;
            var oldIsWax = trackedState.IsWax;
            var oldIsMelted = trackedState.IsMelted;
            RefreshTrackedRelicState(trackedState, relic);

            var payload = new Dictionary<string, object?>
            {
                ["relic_instance_id"] = trackedState.RelicInstanceId,
                ["relic_id"] = trackedState.RelicId,
                ["relic_name"] = trackedState.RelicName,
                ["owner_entity_id"] = trackedState.OwnerEntityId,
            };

            var targetEntityIds = targets
                .Select(ResolveTrackedEntityId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (targetEntityIds.Count > 0)
            {
                payload["target_entity_ids"] = targetEntityIds;
            }

            var eventContext = ResolveCurrentTruthAttributionContext();
            var triggerRef = ResolveRelicTruthTriggerRef();
            var emissionSignature = BuildRelicTriggerEmissionSignature(
                trackedState,
                targetEntityIds,
                triggerRef,
                eventContext);
            if (!_emittedRelicTriggerSignatures.Add(emissionSignature))
            {
                return;
            }

            AppendTriggerField(payload, triggerRef);
            EmitEvent("relic_triggered", _phase, eventContext, payload);
            MarkPendingSnapshotRelevantChange("relic_triggered");
            EmitRelicDerivedFlagChanges(
                relic,
                trackedState,
                oldIsUsedUp,
                oldIsWax,
                oldIsMelted,
                eventContext,
                triggerRef);
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnRelicFlashed", ex);
        }
    }

    private static void OnRelicDisplayAmountChanged(RelicModel relic)
    {
        EmitRelicModifiedIfChanged(relic, "display_amount");
    }

    private static void OnRelicStatusChanged(RelicModel relic)
    {
        EmitRelicModifiedIfChanged(relic, "status");
    }

    private static void EmitRelicModifiedIfChanged(RelicModel relic, string changeKind)
    {
        if (!_active || !_initDone)
        {
            return;
        }

        try
        {
            var trackedState = EnsureTrackedRelicState(relic);
            var eventContext = ResolveCurrentTruthAttributionContext();
            var triggerRef = ResolveRelicTruthTriggerRef();
            var oldIsUsedUp = trackedState.IsUsedUp;
            var oldIsWax = trackedState.IsWax;
            var oldIsMelted = trackedState.IsMelted;

            if (string.Equals(changeKind, "display_amount", StringComparison.Ordinal))
            {
                var oldDisplay = trackedState.DisplayAmount;
                var currentDisplay = ReadRelicDisplayAmount(relic);
                if (oldDisplay == currentDisplay)
                {
                    trackedState.DisplayAmount = currentDisplay;
                    EmitRelicDerivedFlagChanges(
                        relic,
                        trackedState,
                        oldIsUsedUp,
                        oldIsWax,
                        oldIsMelted,
                        eventContext,
                        triggerRef);
                    return;
                }

                RefreshTrackedRelicState(trackedState, relic);
                var payload = BuildRelicPayload(trackedState);
                payload["change_kind"] = "display_amount";
                payload["old_display_amount"] = oldDisplay;
                payload["new_display_amount"] = currentDisplay;
                AppendTriggerField(payload, triggerRef);
                EmitEvent("relic_modified", _phase, eventContext, payload);
                MarkPendingSnapshotRelevantChange("relic_modified");
                EmitRelicDerivedFlagChanges(
                    relic,
                    trackedState,
                    oldIsUsedUp,
                    oldIsWax,
                    oldIsMelted,
                    eventContext,
                    triggerRef);
                return;
            }

            var oldStatus = trackedState.Status;
            var currentStatus = GameStateReader.GetRelicStatus(relic.Status);
            if (string.Equals(oldStatus, currentStatus, StringComparison.Ordinal))
            {
                trackedState.Status = currentStatus;
                EmitRelicDerivedFlagChanges(
                    relic,
                    trackedState,
                    oldIsUsedUp,
                    oldIsWax,
                    oldIsMelted,
                    eventContext,
                    triggerRef);
                return;
            }

            RefreshTrackedRelicState(trackedState, relic);
            var statusPayload = BuildRelicPayload(trackedState);
            statusPayload["change_kind"] = "status";
            statusPayload["old_status"] = oldStatus;
            statusPayload["new_status"] = currentStatus;
            AppendTriggerField(statusPayload, triggerRef);
            EmitEvent("relic_modified", _phase, eventContext, statusPayload);
            MarkPendingSnapshotRelevantChange("relic_modified");
            EmitRelicDerivedFlagChanges(
                relic,
                trackedState,
                oldIsUsedUp,
                oldIsWax,
                oldIsMelted,
                eventContext,
                triggerRef);
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".EmitRelicModifiedIfChanged", ex);
        }
    }

    private static void EmitRelicDerivedFlagChanges(
        RelicModel relic,
        TrackedRelicState trackedState,
        bool oldIsUsedUp,
        bool oldIsWax,
        bool oldIsMelted,
        AttributionContext? eventContext,
        Dictionary<string, object?>? triggerRef)
    {
        EmitDerivedRelicFlagChange(relic, trackedState, "is_used_up", oldIsUsedUp, relic.IsUsedUp, eventContext, triggerRef);
        EmitDerivedRelicFlagChange(relic, trackedState, "is_wax", oldIsWax, relic.IsWax, eventContext, triggerRef);
        EmitDerivedRelicFlagChange(relic, trackedState, "is_melted", oldIsMelted, relic.IsMelted, eventContext, triggerRef);
    }

    private static void EmitDerivedRelicFlagChange(
        RelicModel relic,
        TrackedRelicState trackedState,
        string flag,
        bool oldValue,
        bool newValue,
        AttributionContext? eventContext,
        Dictionary<string, object?>? triggerRef)
    {
        if (oldValue == newValue)
        {
            return;
        }

        RefreshTrackedRelicState(trackedState, relic);

        var payload = BuildRelicPayload(trackedState);
        payload["change_kind"] = "flag";
        payload["flag"] = flag;
        payload["old_value"] = oldValue;
        payload["new_value"] = newValue;
        AppendTriggerField(payload, triggerRef);
        EmitEvent("relic_modified", _phase, eventContext, payload);
        MarkPendingSnapshotRelevantChange("relic_modified");
    }

    private static void OnRelicObtained(Player player, RelicModel relic)
    {
        SubscribePlayerRelicTruth(player);
        SubscribeRelicTruth(relic);

        var trackedState = CaptureTrackedRelicState(relic);
        UpsertTrackedRelicState(trackedState);

        if (!_active || !_initDone)
        {
            return;
        }

        try
        {
            var payload = BuildRelicPayload(trackedState);
            AppendTriggerField(payload, ResolveRelicTruthTriggerRef());
            EmitEvent("relic_obtained", _phase, ResolveCurrentTruthAttributionContext(), payload);
            MarkPendingSnapshotRelevantChange("relic_obtained");
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnRelicObtained", ex);
        }
    }

    private static void OnRelicRemoved(Player player, RelicModel relic)
    {
        try
        {
            var trackedState = EnsureTrackedRelicState(relic);
            if (_active && _initDone)
            {
                var payload = BuildRelicPayload(trackedState);
                AppendTriggerField(payload, ResolveRelicTruthTriggerRef());
                EmitEvent("relic_removed", _phase, ResolveCurrentTruthAttributionContext(), payload);
                MarkPendingSnapshotRelevantChange("relic_removed");
            }

            UnsubscribeRelicTruth(relic);
            RemoveTrackedRelicState(relic);
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnRelicRemoved", ex);
        }
    }

    private static TrackedRelicState CaptureTrackedRelicState(RelicModel relic)
    {
        var relicInstanceId = GetRelicInstanceId(relic);
        return new TrackedRelicState
        {
            Model = relic,
            RelicInstanceId = relicInstanceId,
            RelicId = GameStateReader.GetRelicId(relic),
            RelicName = GameStateReader.GetRelicName(relic),
            OwnerEntityId = ResolveEntityId(relic.Owner?.Creature) ?? _playerEntityId,
            StackCount = relic.StackCount,
            Status = GameStateReader.GetRelicStatus(relic.Status),
            DisplayAmount = ReadRelicDisplayAmount(relic),
            IsUsedUp = relic.IsUsedUp,
            IsWax = relic.IsWax,
            IsMelted = relic.IsMelted,
        };
    }

    private static TrackedRelicState EnsureTrackedRelicState(RelicModel relic)
    {
        var relicInstanceId = GetRelicInstanceId(relic);
        if (_trackedRelicsById.TryGetValue(relicInstanceId, out var existing))
        {
            return existing;
        }

        var trackedState = CaptureTrackedRelicState(relic);
        UpsertTrackedRelicState(trackedState);
        return trackedState;
    }

    private static void UpsertTrackedRelicState(TrackedRelicState trackedState)
    {
        _trackedRelicsById[trackedState.RelicInstanceId] = trackedState;
        if (!_trackedRelicOrder.Contains(trackedState.RelicInstanceId, StringComparer.Ordinal))
        {
            _trackedRelicOrder.Add(trackedState.RelicInstanceId);
        }
    }

    private static void RemoveTrackedRelicState(RelicModel relic)
    {
        var relicInstanceId = GetRelicInstanceId(relic);
        _trackedRelicsById.Remove(relicInstanceId);
        _trackedRelicOrder.RemoveAll(id => string.Equals(id, relicInstanceId, StringComparison.Ordinal));
    }

    private static void RefreshTrackedRelicState(TrackedRelicState trackedState, RelicModel relic)
    {
        trackedState.StackCount = relic.StackCount;
        trackedState.Status = GameStateReader.GetRelicStatus(relic.Status);
        trackedState.DisplayAmount = ReadRelicDisplayAmount(relic);
        trackedState.IsUsedUp = relic.IsUsedUp;
        trackedState.IsWax = relic.IsWax;
        trackedState.IsMelted = relic.IsMelted;
    }

    private static int? ReadRelicDisplayAmount(RelicModel relic)
    {
        return relic.ShowCounter ? relic.DisplayAmount : null;
    }

    private static Dictionary<string, object?> BuildRelicPayload(TrackedRelicState trackedState)
    {
        return new Dictionary<string, object?>
        {
            ["relic_instance_id"] = trackedState.RelicInstanceId,
            ["relic_id"] = trackedState.RelicId,
            ["relic_name"] = trackedState.RelicName,
            ["owner_entity_id"] = trackedState.OwnerEntityId,
            ["stack_count"] = trackedState.StackCount,
            ["status"] = trackedState.Status,
            ["display_amount"] = trackedState.DisplayAmount,
            ["is_used_up"] = trackedState.IsUsedUp,
            ["is_wax"] = trackedState.IsWax,
            ["is_melted"] = trackedState.IsMelted,
        };
    }

    private static Dictionary<string, object?>? ResolveRelicTruthTriggerRef()
    {
        var resolution = GetActiveTruthResolutionSnapshot();
        if (resolution?.RootSource != null)
        {
            var rootRef = SanitizePowerTruthRef(resolution.RootSource);
            if (!IsRelicRef(rootRef))
            {
                return rootRef;
            }
        }

        var liveAttribution = ResolveAttributionContextTriggerRef(GetLiveAttributionContext(), includeEnemyMove: true);
        if (liveAttribution != null)
        {
            return liveAttribution;
        }

        var scopedSources = EnsureStack(_truthSourceScopeStack);
        foreach (var scopedSource in scopedSources.Skip(1))
        {
            var source = SanitizePowerTruthRef(DescribeSource(scopedSource));
            if (source == null)
            {
                continue;
            }

            if (source.TryGetValue("kind", out var kindValue) &&
                kindValue is string kind &&
                string.Equals(kind, "relic", StringComparison.Ordinal))
            {
                continue;
            }

            return source;
        }

        return null;
    }

    private static bool IsRelicRef(Dictionary<string, object?>? source)
    {
        if (source == null)
        {
            return false;
        }

        return source.TryGetValue("kind", out var kindValue) &&
            kindValue is string kind &&
            string.Equals(kind, "relic", StringComparison.Ordinal);
    }

    private static string BuildRelicTriggerEmissionSignature(
        TrackedRelicState trackedState,
        IReadOnlyList<string> targetEntityIds,
        Dictionary<string, object?>? triggerRef,
        AttributionContext? eventContext)
    {
        var builder = new StringBuilder();
        builder.Append("turn=").Append(_turnIndex)
            .Append("|phase=").Append(_phase)
            .Append("|resolution=").Append(eventContext?.ResolutionId ?? "")
            .Append("|relic=").Append(trackedState.RelicInstanceId)
            .Append("|targets=");

        foreach (var targetId in targetEntityIds)
        {
            builder.Append(targetId).Append(',');
        }

        builder.Append("|trigger=");
        AppendRelicTriggerRefSignature(builder, triggerRef);
        return builder.ToString();
    }

    private static void AppendRelicTriggerRefSignature(StringBuilder builder, Dictionary<string, object?>? triggerRef)
    {
        if (triggerRef == null)
        {
            builder.Append("null");
            return;
        }

        foreach (var entry in triggerRef.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            builder.Append(entry.Key).Append('=');
            if (entry.Value is IEnumerable<string> stringEnumerable && entry.Value is not string)
            {
                foreach (var item in stringEnumerable)
                {
                    builder.Append(item).Append(',');
                }
            }
            else
            {
                builder.Append(entry.Value?.ToString() ?? "null");
            }
            builder.Append(';');
        }
    }

    private static void AppendRelicSignature(StringBuilder builder)
    {
        foreach (var relicId in _trackedRelicOrder)
        {
            if (!_trackedRelicsById.TryGetValue(relicId, out var relic))
            {
                continue;
            }

            builder.Append("relic:")
                .Append(relic.RelicInstanceId)
                .Append('=')
                .Append(relic.RelicId)
                .Append(':')
                .Append(relic.Status)
                .Append(':')
                .Append(relic.DisplayAmount?.ToString() ?? "")
                .Append(':')
                .Append(relic.StackCount)
                .Append(':')
                .Append(relic.IsUsedUp ? '1' : '0')
                .Append(':')
                .Append(relic.IsWax ? '1' : '0')
                .Append(':')
                .Append(relic.IsMelted ? '1' : '0')
                .Append('|');
        }
    }
}
