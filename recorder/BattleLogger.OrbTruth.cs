using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;

namespace STS2CombatRecorder;

public static partial class BattleLogger
{
    private sealed class TrackedOrbState
    {
        public required OrbModel Model { get; init; }
        public required string OrbInstanceId { get; init; }
        public required string OrbId { get; init; }
        public required string OrbName { get; init; }
        public required string OwnerEntityId { get; init; }
        public decimal? Passive { get; set; }
        public decimal? Evoke { get; set; }
    }

    private static int _orbInstanceCounter;
    private static Dictionary<OrbModel, string> _orbModelToInstanceId = new();
    private static Dictionary<string, TrackedOrbState> _trackedOrbsById = new(StringComparer.Ordinal);
    private static List<string> _trackedOrbOrder = new();
    private static int _trackedOrbSlots;

    private static void ResetOrbTruthState()
    {
        _orbInstanceCounter = 0;
        _orbModelToInstanceId = new Dictionary<OrbModel, string>();
        _trackedOrbsById = new Dictionary<string, TrackedOrbState>(StringComparer.Ordinal);
        _trackedOrbOrder = new List<string>();
        _trackedOrbSlots = 0;
    }

    public static void SyncInitialOrbState(Player player)
    {
        ResetOrbTruthState();

        try
        {
            _trackedOrbSlots = GameStateReader.GetOrbSlots(player);
            foreach (var orb in GameStateReader.ReadOrbs(player).OrderBy(info => info.SlotIndex))
            {
                var orbInstanceId = GetOrbInstanceId(orb.Instance);
                _trackedOrbsById[orbInstanceId] = BuildTrackedOrbState(orb.Instance);
                _trackedOrbOrder.Add(orbInstanceId);
            }
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".SyncInitialOrbState", ex);
        }
    }

    public static int GetTrackedOrbSlots()
    {
        return _trackedOrbSlots;
    }

    public static List<Dictionary<string, object?>> BuildTrackedOrbStatePayloads()
    {
        var result = new List<Dictionary<string, object?>>();

        for (int slotIndex = 0; slotIndex < _trackedOrbOrder.Count; slotIndex++)
        {
            if (_trackedOrbsById.TryGetValue(_trackedOrbOrder[slotIndex], out var orbState))
            {
                result.Add(BuildOrbPayload(orbState, slotIndex));
            }
        }

        return result;
    }

    public static void OnOrbSlotsChanged(Player player, int oldSlots, int newSlots, string reason)
    {
        if (!_active || !_initDone || oldSlots == newSlots)
        {
            _trackedOrbSlots = newSlots;
            return;
        }

        try
        {
            var triggerRef = ResolveCurrentTruthTriggerRef(includeEnemyMove: true);
            var eventContext = ResolveCurrentTruthAttributionContext();
            var payload = new Dictionary<string, object?>
            {
                ["entity_id"] = _playerEntityId,
                ["old_slots"] = oldSlots,
                ["new_slots"] = newSlots,
                ["delta"] = newSlots - oldSlots,
                ["reason"] = reason,
            };

            AppendTriggerField(payload, triggerRef);
            EmitEvent("orb_slots_changed", _phase, eventContext, payload, dispatchMode: EventDispatchMode.PublicAndShadowHook);
            _trackedOrbSlots = newSlots;
            MarkPendingSnapshotRelevantChange("orb_slots_changed");
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnOrbSlotsChanged", ex);
        }
    }

    public static void OnOrbInserted(Player player, OrbModel orb, string reason, int? slotIndex = null)
    {
        if (!_active || !_initDone)
        {
            return;
        }

        try
        {
            var orbState = BuildTrackedOrbState(orb);
            var resolvedSlotIndex = slotIndex ?? ResolveLiveOrbSlotIndex(player, orb) ?? _trackedOrbOrder.Count;
            TrackOrbInserted(orbState, resolvedSlotIndex);
            var eventContext = ResolveCurrentTruthAttributionContext();

            var payload = BuildOrbPayload(orbState, resolvedSlotIndex);
            payload["reason"] = reason;
            AppendTriggerField(payload, ResolveCurrentTruthTriggerRef(includeEnemyMove: true));
            EmitEvent("orb_inserted", _phase, eventContext, payload, dispatchMode: EventDispatchMode.PublicAndShadowHook);
            MarkPendingSnapshotRelevantChange("orb_inserted");
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnOrbInserted", ex);
        }
    }

    public static void OnOrbEvoked(OrbModel orb, bool dequeued, IEnumerable<string>? targetEntityIds, int? slotIndex = null)
    {
        if (!_active || !_initDone)
        {
            return;
        }

        try
        {
            var orbState = EnsureTrackedOrbState(orb);
            var resolvedSlotIndex = slotIndex ?? ResolveTrackedOrbSlotIndex(orbState.OrbInstanceId) ?? 0;
            var eventContext = ResolveCurrentTruthAttributionContext();
            var payload = BuildOrbPayload(orbState, resolvedSlotIndex);
            payload["dequeued"] = dequeued;

            var targets = targetEntityIds?.ToList();
            if (targets != null && targets.Count > 0)
            {
                payload["target_entity_ids"] = targets;
            }

            AppendTriggerField(payload, ResolveCurrentTruthTriggerRef(includeEnemyMove: true));
            EmitEvent("orb_evoked", _phase, eventContext, payload, dispatchMode: EventDispatchMode.PublicAndShadowHook);
            MarkPendingSnapshotRelevantChange("orb_evoked");
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnOrbEvoked", ex);
        }
    }

    public static void OnOrbRemoved(OrbModel orb, string reason, Dictionary<string, object?>? triggerRef = null, string? phase = null)
    {
        if (!_active || !_initDone)
        {
            TrackOrbRemoved(orb);
            return;
        }

        try
        {
            var orbState = EnsureTrackedOrbState(orb);
            var slotIndex = ResolveTrackedOrbSlotIndex(orbState.OrbInstanceId) ?? 0;
            var eventContext = ResolveCurrentTruthAttributionContext();
            var payload = BuildOrbPayload(orbState, slotIndex);
            payload["reason"] = reason;
            AppendTriggerField(payload, triggerRef ?? ResolveCurrentTruthTriggerRef(includeEnemyMove: true));
            EmitEvent("orb_removed", phase ?? _phase, eventContext, payload, dispatchMode: EventDispatchMode.PublicAndShadowHook);
            TrackOrbRemoved(orb);
            MarkPendingSnapshotRelevantChange("orb_removed");
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnOrbRemoved", ex);
        }
    }

    public static void OnOrbPassiveTriggered(OrbModel orb, string timing)
    {
        if (!_active || !_initDone)
        {
            return;
        }

        try
        {
            var orbState = EnsureTrackedOrbState(orb);
            var slotIndex = ResolveTrackedOrbSlotIndex(orbState.OrbInstanceId) ?? 0;
            var eventContext = ResolveCurrentTruthAttributionContext();
            var payload = BuildOrbPayload(orbState, slotIndex);
            payload["timing"] = timing;
            AppendTriggerField(payload, ResolveCurrentTruthTriggerRef(includeEnemyMove: true));
            EmitEvent("orb_passive_triggered", _phase, eventContext, payload, dispatchMode: EventDispatchMode.PublicAndShadowHook);
            MarkPendingSnapshotRelevantChange("orb_passive_triggered");
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnOrbPassiveTriggered", ex);
        }
    }

    public static void OnOrbDisplayValuesPossiblyChanged(OrbModel orb, string reason)
    {
        OnOrbDisplayValuesPossiblyChangedCore(orb, reason, null, null, null);
    }

    private static void OnOrbDisplayValuesPossiblyChangedCore(
        OrbModel orb,
        string reason,
        AttributionContext? eventContext,
        Dictionary<string, object?>? triggerRef,
        string? phase)
    {
        if (!_active || !_initDone)
        {
            return;
        }

        try
        {
            var orbState = EnsureTrackedOrbState(orb);
            var currentPassive = ReadOrbPassiveValue(orb);
            var currentEvoke = ReadOrbEvokeValue(orb);
            var passiveChanged = orbState.Passive.HasValue && currentPassive.HasValue && orbState.Passive.Value != currentPassive.Value;
            var evokeChanged = orbState.Evoke.HasValue && currentEvoke.HasValue && orbState.Evoke.Value != currentEvoke.Value;

            if (!passiveChanged && !evokeChanged)
            {
                UpdateTrackedOrbDisplayValues(orbState, currentPassive, currentEvoke);
                return;
            }

            var slotIndex = ResolveTrackedOrbSlotIndex(orbState.OrbInstanceId) ?? 0;
            var oldPassive = orbState.Passive;
            var oldEvoke = orbState.Evoke;
            UpdateTrackedOrbDisplayValues(orbState, currentPassive, currentEvoke);

            var payload = BuildOrbPayload(orbState, slotIndex);
            payload["reason"] = reason;

            var changes = new Dictionary<string, object?>();
            if (passiveChanged && oldPassive.HasValue && currentPassive.HasValue)
            {
                changes["passive"] = new Dictionary<string, object?>
                {
                    ["old"] = oldPassive.Value,
                    ["new"] = currentPassive.Value,
                };
            }

            if (evokeChanged && oldEvoke.HasValue && currentEvoke.HasValue)
            {
                changes["evoke"] = new Dictionary<string, object?>
                {
                    ["old"] = oldEvoke.Value,
                    ["new"] = currentEvoke.Value,
                };
            }

            if (changes.Count == 0)
            {
                return;
            }

            payload["changes"] = changes;
            AppendTriggerField(payload, triggerRef ?? ResolveCurrentTruthTriggerRef(includeEnemyMove: true));
            EmitEvent(
                "orb_modified",
                phase ?? _phase,
                eventContext ?? ResolveCurrentTruthAttributionContext(),
                payload,
                dispatchMode: EventDispatchMode.PublicAndShadowHook);
            MarkPendingSnapshotRelevantChange("orb_modified");
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnOrbDisplayValuesPossiblyChanged", ex);
        }
    }

    private static void OnTrackedOrbDisplayValuesPossiblyChangedByPower(
        PowerModel power,
        AttributionContext? eventContext,
        string? phase)
    {
        if (!_active || !_initDone || power.Owner?.IsPlayer != true || _trackedOrbOrder.Count == 0)
        {
            return;
        }

        try
        {
            var triggerRef = SanitizePowerTruthRef(DescribeSource(power));
            foreach (var orbId in _trackedOrbOrder.ToList())
            {
                if (!_trackedOrbsById.TryGetValue(orbId, out var orbState))
                {
                    continue;
                }

                OnOrbDisplayValuesPossiblyChangedCore(
                    orbState.Model,
                    "power_changed",
                    eventContext,
                    triggerRef,
                    phase);
            }
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnTrackedOrbDisplayValuesPossiblyChangedByPower", ex);
        }
    }

    public static void OnOrbReplace(Player player, OrbModel oldOrb, OrbModel newOrb)
    {
        if (!_active || !_initDone)
        {
            return;
        }

        try
        {
            var slotIndex = ResolveLiveOrbSlotIndex(player, newOrb) ?? ResolveTrackedOrbSlotIndex(GetOrbInstanceId(oldOrb)) ?? 0;
            OnOrbRemoved(oldOrb, "replace");
            OnOrbInserted(player, newOrb, "replace", slotIndex);
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnOrbReplace", ex);
        }
    }

    public static void EmitOrbCombatEndCleanup()
    {
        if (!_active || !_initDone || _trackedOrbOrder.Count == 0)
        {
            return;
        }

        var remainingOrbIds = _trackedOrbOrder.ToList();
        foreach (var orbId in remainingOrbIds)
        {
            if (!_trackedOrbsById.TryGetValue(orbId, out var orbState))
            {
                continue;
            }

            var payload = BuildOrbPayload(orbState, ResolveTrackedOrbSlotIndex(orbId) ?? 0);
            payload["reason"] = "combat_end_cleanup";
            EmitEvent("orb_removed", "battle_end", (AttributionContext?)null, payload, dispatchMode: EventDispatchMode.PublicAndShadowHook);
            TrackOrbRemoved(orbState.Model);
            MarkPendingSnapshotRelevantChange("orb_removed_cleanup");
        }
    }

    private static TrackedOrbState EnsureTrackedOrbState(OrbModel orb)
    {
        var orbInstanceId = GetOrbInstanceId(orb);
        if (_trackedOrbsById.TryGetValue(orbInstanceId, out var existing))
        {
            return existing;
        }

        var created = BuildTrackedOrbState(orb);
        _trackedOrbsById[created.OrbInstanceId] = created;
        if (!_trackedOrbOrder.Contains(created.OrbInstanceId))
        {
            _trackedOrbOrder.Add(created.OrbInstanceId);
        }

        return created;
    }

    private static TrackedOrbState BuildTrackedOrbState(OrbModel orb)
    {
        var ownerEntityId = ResolveEntityId(orb.Owner?.Creature) ?? _playerEntityId;
        var passive = ReadOrbPassiveValue(orb);
        var evoke = ReadOrbEvokeValue(orb);
        return new TrackedOrbState
        {
            Model = orb,
            OrbInstanceId = GetOrbInstanceId(orb),
            OrbId = GameStateReader.GetOrbId(orb),
            OrbName = GameStateReader.GetOrbName(orb),
            OwnerEntityId = ownerEntityId,
            Passive = passive,
            Evoke = evoke,
        };
    }

    private static void TrackOrbInserted(TrackedOrbState orbState, int slotIndex)
    {
        _trackedOrbsById[orbState.OrbInstanceId] = orbState;
        _trackedOrbOrder.Remove(orbState.OrbInstanceId);

        var clampedIndex = Math.Max(0, Math.Min(slotIndex, _trackedOrbOrder.Count));
        _trackedOrbOrder.Insert(clampedIndex, orbState.OrbInstanceId);
    }

    private static void TrackOrbRemoved(OrbModel orb)
    {
        var orbInstanceId = GetOrbInstanceId(orb);
        _trackedOrbOrder.Remove(orbInstanceId);
        _trackedOrbsById.Remove(orbInstanceId);
        _orbModelToInstanceId.Remove(orb);
    }

    private static int? ResolveLiveOrbSlotIndex(Player player, OrbModel orb)
    {
        try
        {
            var orbQueue = player.PlayerCombatState?.OrbQueue;
            if (orbQueue == null)
            {
                return null;
            }

            var index = orbQueue.Orbs.ToList().IndexOf(orb);
            return index >= 0 ? index : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? ResolveTrackedOrbSlotIndex(string orbInstanceId)
    {
        var index = _trackedOrbOrder.IndexOf(orbInstanceId);
        return index >= 0 ? index : null;
    }

    private static Dictionary<string, object?> BuildOrbPayload(TrackedOrbState orbState, int slotIndex)
    {
        var payload = new Dictionary<string, object?>
        {
            ["orb_instance_id"] = orbState.OrbInstanceId,
            ["orb_id"] = orbState.OrbId,
            ["orb_name"] = orbState.OrbName,
            ["owner_entity_id"] = orbState.OwnerEntityId,
            ["slot_index"] = slotIndex,
        };

        if (orbState.Passive.HasValue)
        {
            payload["passive"] = orbState.Passive.Value;
        }

        if (orbState.Evoke.HasValue)
        {
            payload["evoke"] = orbState.Evoke.Value;
        }

        return payload;
    }

    private static void UpdateTrackedOrbDisplayValues(TrackedOrbState orbState, decimal? passive, decimal? evoke)
    {
        orbState.Passive = passive;
        orbState.Evoke = evoke;
    }

    private static decimal? ReadOrbPassiveValue(OrbModel orb)
    {
        try
        {
            return orb.PassiveVal;
        }
        catch
        {
            return null;
        }
    }

    private static decimal? ReadOrbEvokeValue(OrbModel orb)
    {
        try
        {
            return orb.EvokeVal;
        }
        catch
        {
            return null;
        }
    }

    private static string GetOrbInstanceId(OrbModel orb)
    {
        if (_orbModelToInstanceId.TryGetValue(orb, out var existingId))
        {
            return existingId;
        }

        var nextId = $"orb:{++_orbInstanceCounter:D4}";
        _orbModelToInstanceId[orb] = nextId;
        return nextId;
    }

    private static void AppendOrbSignature(StringBuilder builder)
    {
        builder.Append("orb_slots=").Append(_trackedOrbSlots).Append('|');
        for (int slotIndex = 0; slotIndex < _trackedOrbOrder.Count; slotIndex++)
        {
            if (!_trackedOrbsById.TryGetValue(_trackedOrbOrder[slotIndex], out var orbState))
            {
                continue;
            }

            builder.Append("orb:")
                .Append(slotIndex)
                .Append('=')
                .Append(orbState.OrbInstanceId)
                .Append(':')
                .Append(orbState.OrbId)
                .Append('|');
        }
    }

    private static Dictionary<string, object?>? DescribeOrbSource(OrbModel orb)
    {
        var orbInstanceId = GetOrbInstanceId(orb);
        var payload = new Dictionary<string, object?>
        {
            ["kind"] = "orb_instance",
            ["ref"] = orbInstanceId,
            ["orb_instance_id"] = orbInstanceId,
            ["orb_id"] = GameStateReader.GetOrbId(orb),
            ["orb_name"] = GameStateReader.GetOrbName(orb),
            ["owner_entity_id"] = ResolveEntityId(orb.Owner?.Creature) ?? _playerEntityId,
        };

        var slotIndex = ResolveTrackedOrbSlotIndex(orbInstanceId);
        if (slotIndex.HasValue)
        {
            payload["slot_index"] = slotIndex.Value;
        }

        return payload;
    }

    public static string? ResolveTrackedEntityId(Creature? creature)
    {
        return ResolveEntityId(creature);
    }
}
