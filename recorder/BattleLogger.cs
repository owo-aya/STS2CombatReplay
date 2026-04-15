using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace STS2CombatRecorder;

public static partial class BattleLogger
{
    private sealed class PlayContext
    {
        public required string ResolutionId { get; init; }
        public required string CardId { get; init; }
        public required string SourceKind { get; init; }
        public required List<string> TargetEntityIds { get; init; }
        public string? ParentResolutionId { get; init; }
        public int? ResolutionDepth { get; init; }
    }

    private sealed class StalePlayContext
    {
        public required string ResolutionId { get; init; }
        public required string CardId { get; init; }
        public required string SourceKind { get; init; }
        public string? ParentResolutionId { get; init; }
        public int? ResolutionDepth { get; init; }
    }

    private sealed class ReplayPlan
    {
        public required string CardId { get; init; }
        public int PendingExtraPlays { get; set; }
        public string? OriginResolutionId { get; set; }
        public int? OriginResolutionDepth { get; set; }
        public int OriginOrder { get; set; }
    }

    private sealed class PotionContext
    {
        public required string ResolutionId { get; init; }
        public required string PotionId { get; init; }
        public required int SlotIndex { get; init; }
        public required string TargetMode { get; init; }
        public required List<string> TargetEntityIds { get; init; }
    }

    private sealed class PendingPotionDiscardEvent
    {
        public required string PotionId { get; init; }
        public required int SlotIndex { get; init; }
        public required EventDispatchMode DispatchMode { get; init; }
    }

    private sealed class EnemyActionContext
    {
        public required string ResolutionId { get; init; }
        public required string ActorEntityId { get; init; }
        public required Creature ActorCreature { get; init; }
        public required string MoveId { get; init; }
        public bool EmittedTrackedOutcome { get; set; }
        public HashSet<AttackCommand> ProcessedAttackCommands { get; } = new();
        public Dictionary<AttackCommand, Dictionary<Creature, EnemyAttackTargetSample>> PendingAttackSamples { get; } = new();
        public HashSet<string> SuppressedPollingEntityIds { get; } = new();
    }

    private sealed class EnemyAttackTargetSample
    {
        public required string EntityId { get; init; }
        public required int OldHp { get; init; }
        public required int OldBlock { get; init; }
    }

    private sealed class BlockGainStepRecord
    {
        public required string Stage { get; init; }
        public required string Operation { get; init; }
        public decimal? Before { get; init; }
        public decimal? After { get; init; }
        public decimal? Delta { get; init; }
        public Dictionary<string, object?>? ModifierRef { get; init; }
        public bool IsUnknown { get; init; }
        public string? UnknownReason { get; init; }
    }

    private sealed class PendingBlockTruthSample
    {
        public required string EntityId { get; init; }
        public required int OldBlock { get; init; }
        public required string Phase { get; init; }
        public AttributionContext? EventContext { get; init; }
        public Dictionary<string, object?>? Trigger { get; init; }
        public decimal? BaseAmount { get; set; }
        public decimal? ModifiedAmount { get; set; }
        public bool HasCapturedModifierChain { get; set; }
        public List<BlockGainStepRecord> ModifierSteps { get; } = new();
    }

    private sealed class PendingBlockClearSample
    {
        public required string EntityId { get; init; }
        public required int OldBlock { get; init; }
        public required string Phase { get; init; }
    }

    private sealed class PendingEntityReviveEvent
    {
        public required string EntityId { get; init; }
        public required int TurnIndex { get; init; }
        public required string Phase { get; init; }
        public required AttributionContext? EventContext { get; init; }
        public required Dictionary<string, object?> Payload { get; init; }
    }

    private sealed class AttributionContext
    {
        public required string ResolutionId { get; init; }
        public required string SourceEntityId { get; init; }
        public required string SourceKind { get; init; }
        public string? MoveId { get; init; }
        public string? CardId { get; init; }
        public string? PotionId { get; init; }
        public string? ParentResolutionId { get; init; }
        public int? ResolutionDepth { get; init; }
    }

    private sealed class PotionStateRecord
    {
        public required string PotionId { get; init; }
        public required string DefId { get; set; }
        public required string Name { get; set; }
        public int SlotIndex { get; set; }
        public required string State { get; set; }
        public string? Origin { get; set; }
    }

    private sealed class TrackedPowerState
    {
        public required string PowerId { get; init; }
        public string? Name { get; set; }
        public int Stacks { get; set; }
    }

    private sealed class PendingPowerRemovalTruth
    {
        public required AttributionContext? EventContext { get; init; }
        public Dictionary<string, object?>? Applier { get; init; }
        public Dictionary<string, object?>? Trigger { get; init; }
    }

    private const string SchemaName = "sts2-combat-log";
    private const string SchemaVersion = "0.1.0";
    private const string RecorderName = "sts2-combat-recorder";
    private const string RecorderVersion = "0.1.0";
    private const string RecorderPerfSummaryFileName = "recorder_perf_summary.json";
    private const string HookFirstShadowSummaryFileName = "hook_first_shadow_summary.json";
    private static readonly BattleContainerRetentionPolicy RetentionPolicy = BattleContainerRetentionPolicy.Default;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions SnapshotOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly TimeSpan RecorderTimeOffset = TimeSpan.FromHours(8);

    private enum EventDispatchMode
    {
        PublicOnly,
        PublicAndShadowHook,
    }

    private enum MetadataWriteKind
    {
        Initial,
        Refresh,
        Finalize,
    }

    // ─── Battle state ──────────────────────────────────────────────────────

    private static string? _battleId;
    private static string? _battleStartedAt;
    private static string _encounterId = "unknown";
    private static string _encounterName = "Unknown Encounter";
    private static int _seq = -1;
    private static int _turnIndex;
    private static string _phase = "";
    private static string _activeSide = "player";
    private static string? _battleDir;
    private static string? _eventsPath;
    private static bool _active;
    private static bool _initDone;
    private static bool _awaitingOpeningDraw;
    private static CombatState? _lastObservedCombatState;
    private static Player? _lastObservedPlayer;
    private static RecorderPerfDiagnostics? _perfDiagnostics;
    private static HookFirstShadowComparison? _hookFirstShadowComparison;
    private static RecorderBattleRuntimeState? _runtimeState;

    // ─── ID counters ───────────────────────────────────────────────────────

    private static int _cardIdCounter;
    private static int _enemyIdCounter;
    private static int _potionIdCounter;
    private static int _resolutionCounter;

    // ─── Card tracking: CardModel reference → assigned instance id ─────────

    private static readonly string[] TrackedZoneNames =
    {
        "draw",
        "hand",
        "discard",
        "play",
        "exhaust",
        "removed",
        "unknown",
    };

    private static Dictionary<CardModel, string> _cardModelToId = new();
    private static Dictionary<string, string> _cardZones = new();
    private static Dictionary<string, int> _cardCosts = new();
    private static Dictionary<string, List<string>> _trackedZoneCards = new(StringComparer.Ordinal);
    private static List<string> _cardOrder = new();
    private static Dictionary<PotionModel, string> _potionModelToId = new();
    private static Dictionary<int, string> _potionSlotToId = new();
    private static Dictionary<string, PotionStateRecord> _potionsById = new();
    private static Dictionary<PotionModel, int> _pendingPotionUseSlotIndices = new();
    private static PendingPotionDiscardEvent? _pendingPotionDiscardEvent;

    // ─── Entity tracking ───────────────────────────────────────────────────

    private static Dictionary<Creature, string> _entityIds = new();
    private static string _playerEntityId = "player:0";
    private static Dictionary<string, GameStateReader.IntentInfo> _visibleIntentsByEntityId = new();
    private static Dictionary<string, Dictionary<string, TrackedPowerState>> _trackedPowersByEntityId = new();
    private static Dictionary<PowerModel, PendingPowerRemovalTruth> _pendingPowerRemovalTruthByPower = new();
    private static Dictionary<Creature, Stack<PendingBlockTruthSample>> _pendingBlockTruthByCreature = new();
    private static Dictionary<Creature, Stack<PendingBlockClearSample>> _pendingBlockClearByCreature = new();

    private const int EntityRemovalStableFrames = 3;
    private static HashSet<string> _removedEntityIds = new();
    private static Dictionary<string, int> _entityAbsenceFrames = new();

    // ─── Previous state for diff detection ─────────────────────────────────

    private static int _prevEnergy;
    private static int _prevStars;
    private static Dictionary<string, int> _prevHp = new();
    private static Dictionary<string, int> _prevBlock = new();
    private static HashSet<string> _deathRecordedEntityIds = new();
    private static Dictionary<string, PendingEntityReviveEvent> _pendingEntityRevivedEvents = new(StringComparer.Ordinal);
    private static Dictionary<Creature, Stack<Dictionary<string, object?>?>> _pendingPreventedDeathReviverRefsByCreature = new();
    private static int _prevRound = -1;
    private static bool _awaitingTurnStartEnergyReset;

    // ─── Zone diff scheduling ─────────────────────────────────────────────

    private const int ZoneDiffValidationFramesAfterSignal = 8;
    private static HashSet<string> _dirtyZoneDiffZones = new(StringComparer.Ordinal);
    private static int _zoneDiffValidationFramesRemaining;

    // ─── Opening draw settle tracking ────────────────────────────────────

    private const int OpeningDrawSettleFrames = 45;
    private static int _openingDrawSettleCount;
    private static int _openingDrawMaxHandSeen;

    // ─── Card play tracking ────────────────────────────────────────────────

    private static Stack<PlayContext> _playContextStack = new();
    private static PlayContext? _pendingManualPlayContext;
    private static Dictionary<CardModel, List<string>> _pendingManualPlayTargetIdsByCard = new();
    private static int _pendingEnergyCost;
    private static int _pendingEnergyOld;
    private static int _pendingEnergyNew;
    private static int _pendingStarsOld;
    private static int _pendingStarsNew;
    private static HashSet<string> _cardsInPlay = new();
    private static PotionContext? _activePotionContext;
    private static EnemyActionContext? _activeEnemyActionContext;
    private static Dictionary<string, int> _recentDiscardMoveSeqByCardId = new();
    private static Dictionary<string, ReplayPlan> _replayPlansByCardId = new();
    private static bool _pendingSnapshot;
    private static int _pendingSnapshotStableFrames;
    private static int _pendingSnapshotAgeFrames;
    private static string _pendingSnapshotSignature = "";
    private static bool _pendingSnapshotNeedsProbe;
    private static bool _pendingSnapshotRequiresChange;
    private static bool _pendingSnapshotSawChange;

    // Stale resolution cooldown: keeps resolution context alive for a few frames
    // after card_play_resolved so that late-arriving HP/block diffs can still
    // be attributed to the correct card play.
    private const int PendingSnapshotMinFrames = 15;
    private const int PendingSnapshotForceFrames = 240;
    private static AttributionContext? _staleAttributionContext;
    private static string? _staleAttributionPhase;

    public static bool IsActive => _active;
    public static int CurrentTurnIndex => _turnIndex;

    private static bool HasActivePlayContext => _playContextStack.Count > 0;

    private static PlayContext? GetActivePlayContext()
    {
        return _playContextStack.Count > 0 ? _playContextStack.Peek() : null;
    }

    private static PlayContext? GetActiveRootPlayContext()
    {
        return _playContextStack.LastOrDefault();
    }

    private static void BindReplayPlanOriginIfNeeded(string cardId, PlayContext? context)
    {
        if (context == null || context.CardId != cardId)
            return;

        if (!_replayPlansByCardId.TryGetValue(cardId, out var replayPlan))
            return;

        if (!string.IsNullOrEmpty(replayPlan.OriginResolutionId))
            return;

        replayPlan.OriginResolutionId = context.ResolutionId;
        replayPlan.OriginResolutionDepth = context.ResolutionDepth ?? 0;
        replayPlan.OriginOrder = _resolutionCounter;

        DebugFileLogger.Log(nameof(BattleLogger) + ".BindReplayPlanOriginIfNeeded",
            $"Replay origin bound. card_instance_id={cardId}, origin_resolution_id={replayPlan.OriginResolutionId}, origin_depth={replayPlan.OriginResolutionDepth}");
    }

    private static AttributionContext? GetAttributionContext()
    {
        var active = GetLiveAttributionContext();
        if (active != null)
        {
            return active;
        }

        if (_staleAttributionContext != null && _phase == _staleAttributionPhase)
        {
            return _staleAttributionContext;
        }

        var pendingReplay = GetPendingReplayAttributionContext();
        if (pendingReplay != null)
        {
            return pendingReplay;
        }

        return null;
    }

    private static AttributionContext? GetLiveAttributionContext()
    {
        var active = GetActivePlayContext();
        if (active != null)
        {
            return ToAttributionContext(active);
        }

        if (_pendingManualPlayContext != null)
        {
            return ToAttributionContext(_pendingManualPlayContext);
        }

        // Potion roots may remain alive until slot discard so that late
        // power removals can still recover potion attribution. Do not let that
        // long-lived context leak into ambient turn-start or battle-end events.
        if (_activePotionContext != null && _phase == "player_action")
        {
            return ToAttributionContext(_activePotionContext);
        }

        if (_activeEnemyActionContext != null)
        {
            return ToAttributionContext(_activeEnemyActionContext);
        }

        return null;
    }

    private static AttributionContext? GetPendingReplayAttributionContext()
    {
        if (_phase != "player_action")
            return null;

        var replayPlan = _replayPlansByCardId.Values
            .Where(plan => plan.PendingExtraPlays > 0 && !string.IsNullOrEmpty(plan.OriginResolutionId))
            .OrderByDescending(plan => plan.OriginOrder)
            .FirstOrDefault();
        if (replayPlan == null)
            return null;

        return new AttributionContext
        {
            ResolutionId = replayPlan.OriginResolutionId!,
            SourceEntityId = _playerEntityId,
            SourceKind = "replayed_effect",
            CardId = replayPlan.CardId,
            ParentResolutionId = null,
            ResolutionDepth = replayPlan.OriginResolutionDepth,
        };
    }

    private static AttributionContext ToAttributionContext(PlayContext context)
    {
        return new AttributionContext
        {
            ResolutionId = context.ResolutionId,
            SourceEntityId = _playerEntityId,
            SourceKind = context.SourceKind,
            CardId = context.CardId,
            ParentResolutionId = context.ParentResolutionId,
            ResolutionDepth = context.ResolutionDepth,
        };
    }

    private static AttributionContext ToAttributionContext(PotionContext context)
    {
        return new AttributionContext
        {
            ResolutionId = context.ResolutionId,
            SourceEntityId = _playerEntityId,
            SourceKind = "potion",
            PotionId = context.PotionId,
        };
    }

    private static AttributionContext ToAttributionContext(EnemyActionContext context)
    {
        return new AttributionContext
        {
            ResolutionId = context.ResolutionId,
            SourceEntityId = context.ActorEntityId,
            SourceKind = "enemy_action",
            MoveId = context.MoveId,
        };
    }

    private static AttributionContext? BuildAttributionContextFromTruthResolution(TruthResolutionSnapshot? resolution)
    {
        if (resolution == null || string.IsNullOrEmpty(resolution.ResolutionId))
        {
            return null;
        }

        var sourceKind = "triggered_effect";
        var sourceEntityId = _playerEntityId;
        string? moveId = null;
        string? cardId = null;
        string? potionId = null;

        if (resolution.RootSource != null &&
            resolution.RootSource.TryGetValue("kind", out var kindValue) &&
            kindValue is string kind)
        {
            if (resolution.RootSource.TryGetValue("owner_entity_id", out var ownerValue) &&
                ownerValue is string ownerEntityId &&
                !string.IsNullOrEmpty(ownerEntityId))
            {
                sourceEntityId = ownerEntityId;
            }

            switch (kind)
            {
                case "card_instance":
                    sourceKind = "card";
                    if (resolution.RootSource.TryGetValue("card_instance_id", out var cardIdValue) &&
                        cardIdValue is string resolvedCardId)
                    {
                        cardId = resolvedCardId;
                    }
                    break;
                case "potion_instance":
                    sourceKind = "potion";
                    if (resolution.RootSource.TryGetValue("potion_instance_id", out var potionIdValue) &&
                        potionIdValue is string resolvedPotionId)
                    {
                        potionId = resolvedPotionId;
                    }
                    break;
                case "enemy_move":
                    sourceKind = "enemy_action";
                    if (resolution.RootSource.TryGetValue("move_id", out var moveIdValue) &&
                        moveIdValue is string resolvedMoveId)
                    {
                        moveId = resolvedMoveId;
                    }
                    break;
            }
        }

        return new AttributionContext
        {
            ResolutionId = resolution.ResolutionId,
            SourceEntityId = sourceEntityId,
            SourceKind = sourceKind,
            MoveId = moveId,
            CardId = cardId,
            PotionId = potionId,
            ParentResolutionId = resolution.ParentResolutionId,
            ResolutionDepth = resolution.ResolutionDepth,
        };
    }

    private static AttributionContext? ResolveCurrentTruthAttributionContext()
    {
        var live = GetLiveAttributionContext();
        if (live != null)
        {
            return live;
        }

        return BuildAttributionContextFromTruthResolution(GetActiveTruthResolutionSnapshot());
    }

    // ─── Lifecycle ─────────────────────────────────────────────────────────

    public static void OnRecorderInitialized()
    {
        RecorderRuntimeEnvironment.GetCurrentAssessment(refresh: true, logOutcome: true);
        RunRetentionCleanup(activeBattleDirectory: null, runtimeState: null, context: "startup");
    }

    public static void StartBattle(CombatState combatState, Player player)
    {
        DebugFileLogger.Log(nameof(BattleLogger) + ".StartBattle",
            $"Entered. Active={_active}");
        if (_active) EndBattle();

        _seq = -1;
        _turnIndex = 1;
        _phase = "battle_start";
        _activeSide = "player";
        _cardIdCounter = 0;
        _enemyIdCounter = 0;
        _potionIdCounter = 0;
        _resolutionCounter = 0;
        _playContextStack = new Stack<PlayContext>();
        _pendingManualPlayContext = null;
        _pendingManualPlayTargetIdsByCard = new Dictionary<CardModel, List<string>>();
        _cardsInPlay.Clear();
        _recentDiscardMoveSeqByCardId = new Dictionary<string, int>();
        _replayPlansByCardId = new Dictionary<string, ReplayPlan>();
        _initDone = false;
        _awaitingOpeningDraw = false;
        _openingDrawSettleCount = 0;
        _openingDrawMaxHandSeen = 0;
        _pendingEnergyCost = 0;
        _pendingEnergyOld = 0;
        _pendingEnergyNew = 0;
        _pendingStarsOld = 0;
        _pendingStarsNew = 0;
        _activePotionContext = null;
        _activeEnemyActionContext = null;
        _staleAttributionContext = null;
        _staleAttributionPhase = null;
        _pendingSnapshot = false;
        _pendingSnapshotStableFrames = 0;
        _pendingSnapshotAgeFrames = 0;
        _pendingSnapshotSignature = "";
        _pendingSnapshotNeedsProbe = false;
        _pendingSnapshotRequiresChange = false;
        _pendingSnapshotSawChange = false;
        _lastObservedCombatState = combatState;
        _lastObservedPlayer = player;
        _perfDiagnostics = null;
        _hookFirstShadowComparison = null;
        _runtimeState = null;
        ResetDamageTruthState();
        ResetRelicTruthState();
        _cardModelToId = new Dictionary<CardModel, string>();
        _cardZones = new Dictionary<string, string>();
        _cardCosts = new Dictionary<string, int>();
        _trackedZoneCards = CreateEmptyTrackedZoneState();
        _cardOrder = new List<string>();
        _potionModelToId = new Dictionary<PotionModel, string>();
        _potionSlotToId = new Dictionary<int, string>();
        _potionsById = new Dictionary<string, PotionStateRecord>();
        _pendingPotionUseSlotIndices = new Dictionary<PotionModel, int>();
        _pendingPotionDiscardEvent = null;
        _entityIds = new Dictionary<Creature, string>();
        _visibleIntentsByEntityId = new Dictionary<string, GameStateReader.IntentInfo>();
        _trackedPowersByEntityId = new Dictionary<string, Dictionary<string, TrackedPowerState>>(StringComparer.Ordinal);
        _pendingPowerRemovalTruthByPower = new Dictionary<PowerModel, PendingPowerRemovalTruth>();
        _pendingBlockTruthByCreature = new Dictionary<Creature, Stack<PendingBlockTruthSample>>();
        _pendingBlockClearByCreature = new Dictionary<Creature, Stack<PendingBlockClearSample>>();
        _removedEntityIds = new HashSet<string>();
        _entityAbsenceFrames = new Dictionary<string, int>();
        _prevHp = new Dictionary<string, int>();
        _prevBlock = new Dictionary<string, int>();
        _deathRecordedEntityIds = new HashSet<string>();
        _pendingEntityRevivedEvents = new Dictionary<string, PendingEntityReviveEvent>(StringComparer.Ordinal);
        _pendingPreventedDeathReviverRefsByCreature = new Dictionary<Creature, Stack<Dictionary<string, object?>?>>();
        _awaitingTurnStartEnergyReset = false;
        _prevStars = player.PlayerCombatState?.Stars ?? 0;
        _dirtyZoneDiffZones = new HashSet<string>(StringComparer.Ordinal);
        _zoneDiffValidationFramesRemaining = 0;

        var battleStartTime = GetRecorderNow();
        _battleStartedAt = FormatRecorderTimestamp(battleStartTime);
        _battleId = $"{FormatRecorderPathTimestamp(battleStartTime)}_{GameStateReader.GetCharacterId(player)}";
        _encounterId = "unknown";
        _encounterName = "Unknown Encounter";
        _runtimeState = new RecorderBattleRuntimeState(
            RecorderRuntimeEnvironment.GetCurrentAssessment(refresh: true));

        Directory.CreateDirectory(RecorderPaths.GetCombatLogsRoot());
        _battleDir = Path.Combine(RecorderPaths.GetCombatLogsRoot(), _battleId);
        Directory.CreateDirectory(_battleDir);
        Directory.CreateDirectory(Path.Combine(_battleDir, "snapshots"));
        _eventsPath = Path.Combine(_battleDir, "events.ndjson");
        if (DebugFileLogger.IsDebugBuild)
        {
            _perfDiagnostics = new RecorderPerfDiagnostics(DebugFileLogger.TotalBytesWritten);
            _hookFirstShadowComparison = new HookFirstShadowComparison();
        }
        DebugFileLogger.Log(nameof(BattleLogger) + ".StartBattle",
            $"Battle directory ready: {_battleDir}");

        _active = true;

        try
        {
            CacheEncounterIdentity(combatState);
            WriteMetadata(combatState, player, writeKind: MetadataWriteKind.Initial);
            SyncInitialOrbState(player);
            EmitBattleStarted(combatState, player);
            SyncInitialRelicState(player);
            EmitEntities(combatState, player);
            EmitInitialRelics();
            SyncInitialPowerStates(combatState, player);
            EmitCardCreated(player);
            EmitPotions(player);

            _awaitingOpeningDraw = true;

            Log.Info($"[STS2CombatRecorder] Battle started (awaiting opening draw): {_battleId}");
            DebugFileLogger.Log(nameof(BattleLogger) + ".StartBattle",
                $"Battle start initialized successfully. battle_id={_battleId}");
            if (_runtimeState.Compatibility.WarningCodes.Count > 0)
            {
                DebugFileLogger.Log(
                    nameof(BattleLogger) + ".StartBattle",
                    $"compat_status={_runtimeState.Compatibility.Status.ToWireValue()}, warnings={string.Join(",", _runtimeState.Compatibility.WarningCodes)}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] StartBattle failed: {ex.Message}\n{ex.StackTrace}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".StartBattle", ex);
            _runtimeState?.RecordPartialBattleContainer(
                "Battle container initialization failed before recorder reached active capture.");
            if (_runtimeState != null)
            {
                _runtimeState.SetCompletionState(BattleContainerCompletionState.Partial);
                WriteMetadata(combatState, player, writeKind: MetadataWriteKind.Refresh);
            }
            _active = false;
        }
    }

    public static void EndBattle(CombatState? combatState = null, Player? player = null)
    {
        if (!_active) return;
        string? endedAt = null;
        string? result = null;

        try
        {
            combatState ??= _lastObservedCombatState ?? CombatManager.Instance?.DebugOnlyGetState();
            player ??= _lastObservedPlayer ?? GameStateReader.GetPlayer(combatState);

            if (combatState != null && player != null && _runtimeState != null)
            {
                endedAt = FormatRecorderTimestamp(GetRecorderNow());
                result = InferBattleResult(combatState, player);

                _runtimeState.SetCompletionState(BattleContainerCompletionState.Partial);
                WriteMetadata(
                    combatState,
                    player,
                    endedAt,
                    result,
                    MetadataWriteKind.Refresh);

                if (_initDone)
                {
                    _phase = "battle_end";
                    DetectHpAndBlockChanges(combatState, player);
                    EmitMissingEntityDeathsAtBattleEnd(combatState, player);
                    DetectPotionChanges(player);
                    EmitOrbCombatEndCleanup();

                    EmitEvent("battle_ended", new Dictionary<string, object?>
                    {
                        ["result"] = result,
                        ["winning_side"] = result != null ? GetWinningSide(result) : null,
                        ["reason"] = "combat_manager_not_in_progress",
                    });

                    WriteSnapshot(combatState, player, result);
                    DebugFileLogger.Log(nameof(BattleLogger) + ".EndBattle",
                        $"battle_ended emitted. result={result}, seq={_seq}");
                }
                else
                {
                    _runtimeState.RecordPartialBattleContainer(
                        "Battle ended before recorder reached the active battle state.");
                }

                RunRetentionCleanup(_battleDir, _runtimeState, "battle_finalize");

                _runtimeState.SetCompletionState(_runtimeState.GetRecommendedFinalState());

                WriteMetadata(
                    combatState,
                    player,
                    endedAt,
                    result,
                    MetadataWriteKind.Finalize);
                if (_runtimeState.ShouldRecordFinalizeFailureWarning())
                {
                    _runtimeState.RecordPartialBattleContainer(
                        "Battle container finalized with one or more recorder write or finalize failures.");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] EndBattle failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".EndBattle", ex);
            if (_runtimeState != null)
            {
                _runtimeState.SetCompletionState(BattleContainerCompletionState.Partial);
                _runtimeState.RecordPartialBattleContainer(
                    $"Battle finalization aborted by recorder exception: {ex.Message}");

                if (combatState != null && player != null)
                {
                    WriteMetadata(
                        combatState,
                        player,
                        endedAt,
                        result,
                        MetadataWriteKind.Refresh);
                }
            }
        }
        finally
        {
            try
            {
                WriteHookFirstShadowSummary();
            }
            catch (Exception ex)
            {
                DebugFileLogger.Error(nameof(BattleLogger) + ".WriteHookFirstShadowSummary", ex);
            }

            try
            {
                var diagnosticsWritten = WriteRecorderPerformanceSummary();
                if (!diagnosticsWritten && combatState != null && player != null)
                {
                    WriteMetadata(
                        combatState,
                        player,
                        endedAt,
                        result,
                        MetadataWriteKind.Refresh);
                }
            }
            catch (Exception ex)
            {
                DebugFileLogger.Error(nameof(BattleLogger) + ".WriteRecorderPerformanceSummary", ex);
            }

            _perfDiagnostics = null;
            _hookFirstShadowComparison = null;
            _runtimeState = null;
            ResetRelicTruthState();
            _active = false;
            Log.Info($"[STS2CombatRecorder] Battle ended: {_battleId}, seq={_seq}");
        }
    }

    // ─── Battle start sub-sequences ────────────────────────────────────────

    private static void EmitBattleStarted(CombatState combatState, Player player)
    {
        var enemies = GameStateReader.GetAliveEnemies(combatState);
        var enemyIds = new List<string>();
        _playerEntityId = "player:0";
        _entityIds[player.Creature] = _playerEntityId;

        foreach (var enemy in enemies)
        {
            _enemyIdCounter++;
            var eid = $"enemy:{_enemyIdCounter}";
            _entityIds[enemy] = eid;
            enemyIds.Add(eid);
        }

        EmitEvent("battle_started", new Dictionary<string, object?>
        {
            ["encounter_id"] = _encounterId,
            ["player_entity_id"] = _playerEntityId,
            ["enemy_entity_ids"] = enemyIds,
            ["encounter_name"] = _encounterName,
        });
    }

    private static void EmitEntities(CombatState combatState, Player player)
    {
        var playerInfo = GameStateReader.GetEntityInfo(player.Creature, "player");
        EmitEvent("entity_spawned", new Dictionary<string, object?>
        {
            ["entity_id"] = _playerEntityId,
            ["side"] = "player",
            ["reason"] = "battle_start",
            ["entity_def_id"] = GameStateReader.GetCharacterId(player),
            ["name"] = GameStateReader.GetCharacterName(player),
            ["current_hp"] = playerInfo.CurrentHp,
            ["max_hp"] = playerInfo.MaxHp,
            ["block"] = playerInfo.Block,
            ["energy"] = player.PlayerCombatState?.Energy ?? 0,
            ["resources"] = new Dictionary<string, object?>
            {
                ["stars"] = player.PlayerCombatState?.Stars ?? 0,
            },
            ["orb_slots"] = GetTrackedOrbSlots(),
            ["orbs"] = BuildTrackedOrbStatePayloads(),
        });
        _prevHp[_playerEntityId] = playerInfo.CurrentHp;
        _prevBlock[_playerEntityId] = playerInfo.Block;

        foreach (var enemy in GameStateReader.GetAliveEnemies(combatState))
        {
            if (!_entityIds.TryGetValue(enemy, out var eid)) continue;
            var info = GameStateReader.GetEntityInfo(enemy, "enemy");
            EmitEvent("entity_spawned", new Dictionary<string, object?>
            {
                ["entity_id"] = eid,
                ["side"] = "enemy",
                ["reason"] = "battle_start",
                ["entity_def_id"] = info.DefId,
                ["name"] = info.Name,
                ["current_hp"] = info.CurrentHp,
                ["max_hp"] = info.MaxHp,
                ["block"] = info.Block,
            });
            _prevHp[eid] = info.CurrentHp;
            _prevBlock[eid] = info.Block;
        }
    }

    private static void SyncInitialPowerStates(CombatState combatState, Player player)
    {
        SyncInitialPowerState(player.Creature);

        foreach (var enemy in GameStateReader.GetAllEnemies(combatState))
        {
            SyncInitialPowerState(enemy);
        }
    }

    private static void SyncInitialPowerState(Creature creature)
    {
        if (!_entityIds.TryGetValue(creature, out var entityId))
        {
            return;
        }

        var currentState = CaptureVisiblePowerState(creature);
        if (currentState.Count == 0)
        {
            return;
        }

        var trackedState = GetTrackedPowerStateMap(entityId);
        foreach (var powerState in currentState.Values.OrderBy(power => power.PowerId, StringComparer.Ordinal))
        {
            trackedState[powerState.PowerId] = CloneTrackedPowerState(powerState);
            EmitPowerAppliedEvent(entityId, powerState, null, null, null);
        }
    }

    private static void EmitCardCreated(Player player)
    {
        var pcs = player.PlayerCombatState;
        if (pcs == null) return;

        var allCards = GameStateReader.ReadAllCards(pcs);
        foreach (var ci in allCards)
        {
            _cardIdCounter++;
            var cid = $"card:{_cardIdCounter:D3}";
            _cardModelToId[ci.Model] = cid;
            TrackCardCreation(cid, ci.Zone);
            _cardOrder.Add(cid);

            var createPayload = new Dictionary<string, object?>
            {
                ["card_instance_id"] = cid,
                ["card_def_id"] = ci.DefId,
                ["card_name"] = ci.Name,
                ["owner_entity_id"] = _playerEntityId,
                ["initial_zone"] = ci.Zone,
                ["cost"] = ci.Cost,
                ["current_upgrade_level"] = ci.Model.CurrentUpgradeLevel,
                ["created_this_combat"] = false,
                ["temporary"] = false,
            };
            AppendCardVisibleStatePayload(createPayload, CaptureCardTruthState(ci.Model), includeCost: false, includeUpgradeLevel: false);
            EmitEvent("card_created", createPayload);
            _cardCosts[cid] = ci.Cost;
        }
    }

    private static void EmitPotions(Player player)
    {
        var potions = GameStateReader.ReadPotions(player);
        foreach (var pi in potions)
        {
            var pid = TrackPotion(pi, "prebattle");
            EmitEvent("potion_initialized", new Dictionary<string, object?>
            {
                ["potion_instance_id"] = pid,
                ["potion_def_id"] = pi.DefId,
                ["potion_name"] = pi.Name,
                ["slot_index"] = pi.SlotIndex,
                ["origin"] = "prebattle",
            });
        }
    }

    // ─── Mid-battle card creation ──────────────────────────────────────────

    public static void OnCardGeneratedForCombat(CombatState combatState, CardModel card, bool addedByPlayer)
    {
        if (!_active || !_initDone) return;
        try
        {
            // Dedup: if this CardModel is already tracked, skip
            if (_cardModelToId.ContainsKey(card)) return;

            RegisterMidBattleCard(card);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnCardGeneratedForCombat failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnCardGeneratedForCombat", ex);
        }
    }

    public static void OnCardEnteredCombat(CombatState combatState, CardModel card)
    {
        if (!_active || !_initDone) return;
        try
        {
            if (_cardModelToId.ContainsKey(card)) return;

            RegisterMidBattleCard(card);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnCardEnteredCombat failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnCardEnteredCombat", ex);
        }
    }

    internal static CardTruthStateSnapshot CaptureCardTruthState(CardModel card)
    {
        return CardTruthStateSnapshot.Capture(card);
    }

    internal static void OnCardStateModified(
        CardModel card,
        CardTruthStateSnapshot oldState,
        string reason,
        CardTruthDiffFields allowedFields = CardTruthDiffFields.All)
    {
        if (!_active || !_initDone)
        {
            return;
        }

        try
        {
            if (!_cardModelToId.TryGetValue(card, out var cardId))
            {
                DebugFileLogger.Log(
                    nameof(BattleLogger) + ".OnCardStateModified",
                    $"Skipped untracked card mutation. card_model={card.Id?.Entry ?? card.GetType().Name}, reason={reason}");
                return;
            }

            var newState = CaptureCardTruthState(card);
            var changes = BuildCardModifiedChanges(oldState, newState, allowedFields);
            if (changes.Count == 0)
            {
                return;
            }

            _cardCosts[cardId] = newState.Cost;

            var payload = new Dictionary<string, object?>
            {
                ["card_instance_id"] = cardId,
                ["changes"] = changes,
                ["reason"] = reason,
            };

            if (!string.Equals(oldState.CardName, newState.CardName, StringComparison.Ordinal))
            {
                payload["card_name"] = newState.CardName;
            }

            if (allowedFields.HasFlag(CardTruthDiffFields.Upgrade) ||
                oldState.CurrentUpgradeLevel != newState.CurrentUpgradeLevel)
            {
                payload["current_upgrade_level"] = newState.CurrentUpgradeLevel;
            }

            var attrContext = GetAttributionContext();
            AppendTriggerField(payload, ResolveCardModifiedTriggerRef());
            EmitEvent("card_modified", _phase, attrContext, payload, dispatchMode: EventDispatchMode.PublicAndShadowHook);
            MarkPendingSnapshotRelevantChange("card_modified");
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnCardStateModified failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnCardStateModified", ex);
        }
    }

    public static void OnCardCostModified(CardModel card, int oldCost, int newCost, string reason)
    {
        if (!_active || !_initDone)
        {
            return;
        }

        try
        {
            if (!_cardModelToId.TryGetValue(card, out var cardId))
            {
                DebugFileLogger.Log(
                    nameof(BattleLogger) + ".OnCardCostModified",
                    $"Skipped untracked card cost mutation. card_model={card.Id?.Entry ?? card.GetType().Name}, old={oldCost}, new={newCost}, reason={reason}");
                return;
            }

            _cardCosts[cardId] = newCost;
            if (oldCost == newCost)
            {
                return;
            }

            var attrContext = GetAttributionContext();
            var payload = new Dictionary<string, object?>
            {
                ["card_instance_id"] = cardId,
                ["changes"] = new Dictionary<string, object?>
                {
                    ["cost"] = new Dictionary<string, object?>
                    {
                        ["old"] = oldCost,
                        ["new"] = newCost,
                    },
                },
                ["reason"] = reason,
            };

            AppendTriggerField(payload, ResolveCardModifiedTriggerRef());
            EmitEvent("card_modified", _phase, attrContext, payload, dispatchMode: EventDispatchMode.PublicAndShadowHook);
            MarkPendingSnapshotRelevantChange("card_modified");
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnCardCostModified failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnCardCostModified", ex);
        }
    }

    public static void OnCardUpgraded(
        CardModel card,
        int oldUpgradeLevel,
        int newUpgradeLevel,
        bool oldUpgraded,
        bool newUpgraded)
    {
        if (!_active || !_initDone)
        {
            return;
        }

        try
        {
            if (!_cardModelToId.TryGetValue(card, out var cardId))
            {
                DebugFileLogger.Log(
                    nameof(BattleLogger) + ".OnCardUpgraded",
                    $"Skipped untracked card upgrade. card_model={card.Id?.Entry ?? card.GetType().Name}, old_level={oldUpgradeLevel}, new_level={newUpgradeLevel}");
                return;
            }

            if (oldUpgradeLevel == newUpgradeLevel && oldUpgraded == newUpgraded)
            {
                return;
            }

            var attrContext = GetAttributionContext();
            var payload = new Dictionary<string, object?>
            {
                ["card_instance_id"] = cardId,
                ["card_name"] = card.Title?.ToString() ?? card.GetType().Name,
                ["current_upgrade_level"] = newUpgradeLevel,
                ["changes"] = new Dictionary<string, object?>
                {
                    ["upgraded"] = new Dictionary<string, object?>
                    {
                        ["old"] = oldUpgraded,
                        ["new"] = newUpgraded,
                    },
                    ["upgrade_level"] = new Dictionary<string, object?>
                    {
                        ["old"] = oldUpgradeLevel,
                        ["new"] = newUpgradeLevel,
                    },
                },
                ["reason"] = "upgrade",
            };

            AppendTriggerField(payload, ResolveCardModifiedTriggerRef());
            EmitEvent("card_modified", _phase, attrContext, payload, dispatchMode: EventDispatchMode.PublicAndShadowHook);
            MarkPendingSnapshotRelevantChange("card_modified");
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnCardUpgraded failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnCardUpgraded", ex);
        }
    }

    public static void OnEntityRevived(Creature creature)
    {
        if (!_active || !_initDone)
            return;

        try
        {
            if (!_entityIds.TryGetValue(creature, out var entityId))
                return;

            if (_removedEntityIds.Contains(entityId))
                return;

            var attrContext = GetLiveAttributionContext();
            var payload = new Dictionary<string, object?>
            {
                ["entity_id"] = entityId,
                ["current_hp"] = creature.CurrentHp,
                ["reason"] = "revive",
            };
            AppendTriggerField(payload, ResolveEntityReviveTriggerRef(creature, attrContext));
            var previousHp = _prevHp.TryGetValue(entityId, out var trackedHp) ? trackedHp : creature.CurrentHp;

            if (previousHp > 0 && creature.CurrentHp > previousHp)
            {
                EmitEvent("hp_changed", _turnIndex, _phase, attrContext, new Dictionary<string, object?>
                {
                    ["entity_id"] = entityId,
                    ["old"] = previousHp,
                    ["new"] = 0,
                    ["delta"] = -previousHp,
                    ["reason"] = "damage",
                });
                MarkPendingSnapshotRelevantChange("hp_changed");
                EmitEntityDiedIfNeeded(entityId, _phase, attrContext, "damage");

                EmitEvent("hp_changed", _turnIndex, _phase, attrContext, new Dictionary<string, object?>
                {
                    ["entity_id"] = entityId,
                    ["old"] = 0,
                    ["new"] = creature.CurrentHp,
                    ["delta"] = creature.CurrentHp,
                    ["reason"] = "heal",
                });
                MarkPendingSnapshotRelevantChange("hp_changed");

                EmitEntityRevivedEvent(new PendingEntityReviveEvent
                {
                    EntityId = entityId,
                    TurnIndex = _turnIndex,
                    Phase = _phase,
                    EventContext = attrContext,
                    Payload = payload,
                });
                FlushPendingPotionDiscard(potionId: attrContext?.SourceKind == "potion" ? _activePotionContext?.PotionId : null);
                _prevHp[entityId] = creature.CurrentHp;
                return;
            }

            if (previousHp < creature.CurrentHp)
            {
                _pendingEntityRevivedEvents[entityId] = new PendingEntityReviveEvent
                {
                    EntityId = entityId,
                    TurnIndex = _turnIndex,
                    Phase = _phase,
                    EventContext = attrContext,
                    Payload = payload,
                };
                return;
            }

            EmitEntityRevivedEvent(new PendingEntityReviveEvent
            {
                EntityId = entityId,
                TurnIndex = _turnIndex,
                Phase = _phase,
                EventContext = attrContext,
                Payload = payload,
            });
            FlushPendingPotionDiscard(potionId: attrContext?.SourceKind == "potion" ? _activePotionContext?.PotionId : null);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnEntityRevived failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnEntityRevived", ex);
        }
    }

    private static void EmitEntityRevivedEvent(PendingEntityReviveEvent pendingEvent)
    {
        EmitEvent(
            "entity_revived",
            pendingEvent.TurnIndex,
            pendingEvent.Phase,
            pendingEvent.EventContext,
            pendingEvent.Payload,
            dispatchMode: EventDispatchMode.PublicAndShadowHook);

        _deathRecordedEntityIds.Remove(pendingEvent.EntityId);
        _pendingKilledEntityIds.Remove(pendingEvent.EntityId);
        _deferredEntityDiedEvents.Remove(pendingEvent.EntityId);
        _pendingEntityRevivedEvents.Remove(pendingEvent.EntityId);
        MarkPendingSnapshotRelevantChange("entity_revived");
        FlushPendingPotionDiscard(potionId: pendingEvent.EventContext?.SourceKind == "potion" ? _activePotionContext?.PotionId : null);

        DebugFileLogger.Log(
            nameof(BattleLogger) + ".EmitEntityRevivedEvent",
            $"entity_revived emitted. entity_id={pendingEvent.EntityId}, current_hp={pendingEvent.Payload["current_hp"]}");
    }

    private static void RegisterMidBattleCard(CardModel card)
    {
        var player = _lastObservedPlayer;
        var pcs = player?.PlayerCombatState;
        if (pcs == null) return;

        var zone = FindCardZone(pcs, card) ?? "unknown";

        // Before adding the new card, detect tracked cards that vanished from
        // the same zone. A card generation via transform (e.g., Guards replacing
        // hand cards) removes old CardModels before the hook fires for the new one.
        var ctx = GetLiveAttributionContext();
        EmitVanishedCardsInZone(pcs, zone, ctx);

        _cardIdCounter++;
        var cid = $"card:{_cardIdCounter:D3}";

        _cardModelToId[card] = cid;
        TrackCardCreation(cid, zone);
        _cardOrder.Add(cid);

        var initialCost = GameStateReader.GetEnergyCost(card);
        _cardCosts[cid] = initialCost;

        var createPayload = new Dictionary<string, object?>
        {
            ["card_instance_id"] = cid,
            ["card_def_id"] = card.Id?.Entry ?? GameStateReader.ToSnakeCase(card.GetType().Name),
            ["card_name"] = card.Title?.ToString() ?? card.GetType().Name,
            ["owner_entity_id"] = _playerEntityId,
            ["initial_zone"] = zone,
            ["cost"] = initialCost,
            ["current_upgrade_level"] = card.CurrentUpgradeLevel,
            ["created_this_combat"] = true,
            ["temporary"] = false,
        };
        AppendCardVisibleStatePayload(createPayload, CaptureCardTruthState(card), includeCost: false, includeUpgradeLevel: false);
        AppendTriggerField(createPayload, ResolveCardTruthTriggerRef());
        EmitEvent("card_created", _turnIndex, _phase, ctx, createPayload, dispatchMode: EventDispatchMode.PublicAndShadowHook);

        MarkZoneDiffDirty();
        MarkPendingSnapshotRelevantChange("card_created");

        DebugFileLogger.Log(nameof(BattleLogger) + ".RegisterMidBattleCard",
            $"Mid-battle card registered. card_instance_id={cid}, card_def_id={card.Id?.Entry ?? "?"}, zone={zone}");
    }

    private static void EmitVanishedCardsInZone(PlayerCombatState pcs, string zone, AttributionContext? ctx)
    {
        var activePlayCardIds = _playContextStack.Select(c => c.CardId).ToHashSet();
        var liveDefIds = new HashSet<string>();
        var zoneCards = GameStateReader.GetCardsInZone(pcs, zone);
        foreach (var ci in zoneCards)
        {
            liveDefIds.Add(ci.DefId);
        }

        var trackedInZone = _cardZones
            .Where(kvp => kvp.Value == zone)
            .Select(kvp => kvp.Key)
            .Where(id => !activePlayCardIds.Contains(id))
            .ToList();

        foreach (var cardId in trackedInZone)
        {
            var defId = GetCardDefId(cardId);
            if (defId == null) continue;
            if (liveDefIds.Contains(defId)) continue;

            var movePayload = new Dictionary<string, object?>
            {
                ["card_instance_id"] = cardId,
                ["from_zone"] = zone,
                ["to_zone"] = "removed",
                ["reason"] = "transform",
                ["from_index"] = TryGetCardIndexInTrackedZone(zone, cardId),
            };
            AppendTriggerField(movePayload, ResolveCardTruthTriggerRef());
            EmitEvent("card_moved", _turnIndex, _phase, ctx, movePayload);

            MoveTrackedCard(cardId, zone, "removed",
                fromIndex: TryGetCardIndexInTrackedZone(zone, cardId));

            DebugFileLogger.Log(nameof(BattleLogger) + ".EmitVanishedCardsInZone",
                $"Transform removal. card_instance_id={cardId} {zone}->removed");
        }
    }

    private static string? FindCardZone(PlayerCombatState pcs, CardModel card)
    {
        foreach (var zone in new[] { "hand", "draw", "discard", "exhaust", "play" })
        {
            var zoneCards = GameStateReader.GetCardsInZone(pcs, zone);
            foreach (var ci in zoneCards)
            {
                if (ReferenceEquals(ci.Model, card))
                    return zone;
            }
        }
        return null;
    }

    private static string NextPotionId()
    {
        _potionIdCounter++;
        return $"potion:{_potionIdCounter:D3}";
    }

    private static int? ResolvePotionSlotIndex(Player player, PotionModel potion)
    {
        if (_pendingPotionUseSlotIndices.TryGetValue(potion, out var pendingSlotIndex))
        {
            return pendingSlotIndex;
        }

        var directSlotIndex = GameStateReader.FindPotionSlotIndex(player, potion);
        if (directSlotIndex.HasValue)
        {
            return directSlotIndex;
        }

        var potionDefId = potion.Id?.Entry ?? GameStateReader.ToSnakeCase(potion.GetType().Name);
        var matchingSlots = _potionSlotToId
            .Where(kvp =>
            {
                if (!_potionsById.TryGetValue(kvp.Value, out var record))
                    return false;

                return record.State != "discarded" && record.DefId == potionDefId;
            })
            .Select(kvp => kvp.Key)
            .Distinct()
            .OrderBy(slot => slot)
            .ToList();

        if (matchingSlots.Count == 1)
        {
            DebugFileLogger.Log(nameof(BattleLogger) + ".ResolvePotionSlotIndex",
                $"Resolved potion slot by tracked def-id fallback. potion_def_id={potionDefId}, slot_index={matchingSlots[0]}");
            return matchingSlots[0];
        }

        if (matchingSlots.Count > 1)
        {
            DebugFileLogger.Log(nameof(BattleLogger) + ".ResolvePotionSlotIndex",
                $"Ambiguous tracked potion slot fallback. potion_def_id={potionDefId}, slots={string.Join(",", matchingSlots)}");
        }

        return null;
    }

    private static GameStateReader.PotionInfo? TryReadPotionInfo(Player player, PotionModel potion)
    {
        var slotIndex = ResolvePotionSlotIndex(player, potion);
        if (!slotIndex.HasValue)
        {
            return null;
        }

        var potionInfo = GameStateReader.ReadPotions(player)
            .FirstOrDefault(info => ReferenceEquals(info.Instance, potion));
        if (ReferenceEquals(potionInfo.Instance, potion))
        {
            return potionInfo;
        }

        return new GameStateReader.PotionInfo
        {
            Instance = potion,
            DefId = potion.Id?.Entry ?? GameStateReader.ToSnakeCase(potion.GetType().Name),
            Name = potion.GetType().Name,
            SlotIndex = slotIndex.Value,
        };
    }

    private static string ResolvePotionIdForUse(
        PotionModel potion,
        GameStateReader.PotionInfo potionInfo,
        int slotIndex,
        out bool shouldEmitCreated)
    {
        shouldEmitCreated = false;

        if (_potionModelToId.TryGetValue(potion, out var knownPotionId))
        {
            UpdatePotionRecord(knownPotionId, potionInfo, _potionsById[knownPotionId].State);
            return knownPotionId;
        }

        if (_potionSlotToId.TryGetValue(slotIndex, out var slotPotionId) &&
            _potionsById.TryGetValue(slotPotionId, out var slotRecord) &&
            slotRecord.State != "discarded")
        {
            _potionModelToId[potion] = slotPotionId;
            UpdatePotionRecord(slotPotionId, potionInfo, slotRecord.State);
            return slotPotionId;
        }

        var potionId = TrackPotion(potionInfo, "generated_in_battle");
        shouldEmitCreated = true;
        return potionId;
    }

    private static string? ResolvePotionIdForDiscard(Player player, PotionModel potion)
    {
        if (_potionModelToId.TryGetValue(potion, out var knownPotionId))
        {
            return knownPotionId;
        }

        var slotIndex = GameStateReader.FindPotionSlotIndex(player, potion);
        if (slotIndex.HasValue && _potionSlotToId.TryGetValue(slotIndex.Value, out var slotPotionId))
        {
            return slotPotionId;
        }

        var potionDefId = potion.Id?.Entry ?? GameStateReader.ToSnakeCase(potion.GetType().Name);
        var matches = _potionsById.Values
            .Where(record => record.State != "discarded" && record.DefId == potionDefId)
            .Select(record => record.PotionId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static string TrackPotion(GameStateReader.PotionInfo info, string origin)
    {
        if (_potionModelToId.TryGetValue(info.Instance, out var existingId))
        {
            UpdatePotionRecord(existingId, info, "available");
            return existingId;
        }

        var potionId = NextPotionId();
        _potionModelToId[info.Instance] = potionId;
        _potionsById[potionId] = new PotionStateRecord
        {
            PotionId = potionId,
            DefId = info.DefId,
            Name = info.Name,
            SlotIndex = info.SlotIndex,
            State = "available",
            Origin = origin,
        };
        _potionSlotToId[info.SlotIndex] = potionId;
        return potionId;
    }

    private static void UpdatePotionRecord(string potionId, GameStateReader.PotionInfo info, string state)
    {
        if (!_potionsById.TryGetValue(potionId, out var record))
            return;

        record.DefId = info.DefId;
        record.Name = info.Name;
        record.SlotIndex = info.SlotIndex;
        record.State = state;
        _potionSlotToId[info.SlotIndex] = potionId;
    }

    private static void EmitOpeningDrawCardMoves(CombatState combatState)
    {
        var player = GameStateReader.GetPlayer(combatState);
        if (player == null) return;
        var pcs = player.PlayerCombatState;
        if (pcs == null) return;

        var handCards = GameStateReader.GetCardsInZone(pcs, "hand");

        for (int i = 0; i < handCards.Count; i++)
        {
            var ci = handCards[i];
            if (!_cardModelToId.TryGetValue(ci.Model, out var cid)) continue;
            if (_cardZones.TryGetValue(cid, out var zone) && zone != "draw") continue;

            MoveTrackedCard(cid, "draw", "hand", fromIndex: 0, toIndex: i);

            EmitEvent("card_moved", new Dictionary<string, object?>
            {
                ["card_instance_id"] = cid,
                ["from_zone"] = "draw",
                ["to_zone"] = "hand",
                ["reason"] = "draw",
                ["from_index"] = 0,
                ["to_index"] = i,
            });
        }
    }

    private static void FinalizeOpeningDraw(CombatState combatState)
    {
        var player = GameStateReader.GetPlayer(combatState);
        if (player == null) return;
        var pcs = player.PlayerCombatState;
        if (pcs == null) return;

        _awaitingOpeningDraw = false;
        _openingDrawSettleCount = 0;
        _initDone = true;
        var openingEnergy = pcs.Energy;
        var previousEnergy = _prevEnergy;
        _prevRound = combatState.RoundNumber;

        _turnIndex = _prevRound;
        _phase = "turn_start";
        _activeSide = "player";
        _staleAttributionPhase = null;

        if (previousEnergy != openingEnergy)
        {
            EmitEvent("energy_changed", new Dictionary<string, object?>
            {
                ["entity_id"] = _playerEntityId,
                ["old"] = previousEnergy,
                ["new"] = openingEnergy,
                ["delta"] = openingEnergy - previousEnergy,
                ["reason"] = "turn_start",
            });
        }

        _prevEnergy = openingEnergy;
        _prevStars = pcs.Stars;
        _awaitingTurnStartEnergyReset = false;

        EmitEvent("turn_started", new Dictionary<string, object?>
        {
            ["turn_index"] = _turnIndex,
            ["active_side"] = "player",
            ["phase"] = "turn_start",
        });

        SyncAllVisibleEnemyIntents(combatState);
        RefreshEntityState(combatState, player);

        _runtimeState?.SetCompletionState(BattleContainerCompletionState.Active);
        WriteSnapshot();
        WriteMetadata(combatState, player, writeKind: MetadataWriteKind.Refresh);

        var handCards = GameStateReader.GetCardsInZone(pcs, "hand");
        Log.Info($"[STS2CombatRecorder] Opening draw complete ({handCards.Count} cards), init done: {_battleId}, seq={_seq}");
        DebugFileLogger.Log(nameof(BattleLogger) + ".FinalizeOpeningDraw",
            $"Opening draw complete. hand_count={handCards.Count}, seq={_seq}");
    }

    // ─── Turn start ────────────────────────────────────────────────────────

    public static void OnRoundChanged(CombatState combatState)
    {
        if (!_active || !_initDone) return;
        try
        {
            FlushPendingPotionDiscard();
            var round = combatState.RoundNumber;
            if (round <= _prevRound) return;

            if (HasActivePlayContext)
            {
                var p = GameStateReader.GetPlayer(combatState);
                ForceResolveAllActivePlayContexts(p?.PlayerCombatState, "round_changed");
            }

            _turnIndex = round;
            _phase = "turn_start";
            _activeSide = "player";
            _staleAttributionContext = null;
            _staleAttributionPhase = null;
            _awaitingTurnStartEnergyReset = true;

            EmitEvent("turn_started", new Dictionary<string, object?>
            {
                ["turn_index"] = _turnIndex,
                ["active_side"] = "player",
                ["phase"] = "turn_start",
            });

            var player = GameStateReader.GetPlayer(combatState);
            if (player != null)
            {
                RefreshEntityState(combatState, player);
            }

            _prevRound = round;
            _recentDiscardMoveSeqByCardId.Clear();
            RequestStabilizedSnapshot(requiresChange: true);
            Log.Info($"[STS2CombatRecorder] Turn started: round={round}, seq={_seq}");
            DebugFileLogger.Log(nameof(BattleLogger) + ".OnRoundChanged",
                $"turn_started emitted. round={round}, seq={_seq}");
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnRoundChanged failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnRoundChanged", ex);
        }
    }

    public static void OnEnemyTurnStarted(CombatState combatState)
    {
        if (!_active || !_initDone) return;

        try
        {
            FlushPendingPotionDiscard();
            _turnIndex = combatState.RoundNumber;
            _phase = "enemy_action";
            _activeSide = "enemy";
            _staleAttributionContext = null;
            _staleAttributionPhase = null;
            _awaitingTurnStartEnergyReset = false;

            EmitEvent("turn_started", new Dictionary<string, object?>
            {
                ["turn_index"] = _turnIndex,
                ["active_side"] = "enemy",
                ["phase"] = "enemy_action",
            });

            DebugFileLogger.Log(nameof(BattleLogger) + ".OnEnemyTurnStarted",
                $"enemy turn_started emitted. round={_turnIndex}, seq={_seq}");
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnEnemyTurnStarted failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnEnemyTurnStarted", ex);
        }
    }

    // ─── Energy changed ────────────────────────────────────────────────────

    public static void OnEnergyChanged(PlayerCombatState pcs)
    {
        if (!_active || (!_initDone && _phase != "battle_start")) return;
        try
        {
            var newEnergy = pcs.Energy;
            var oldEnergy = _prevEnergy;
            if (oldEnergy == newEnergy) return;

            var delta = newEnergy - oldEnergy;

            if (_awaitingTurnStartEnergyReset && _phase == "turn_start" && delta > 0)
            {
                MarkPendingSnapshotRelevantChange("energy_changed");
                return;
            }

            // If energy decreased and no card play is in progress, buffer it.
            // It will be emitted after the next confirmed manual card play starts.
            // Must check HasActivePlayContext (stack-based) rather than
            // GetAttributionContext, because _staleAttributionContext from the
            // previously resolved card would cause the energy to be incorrectly
            // attributed to the wrong card.
            if (delta < 0 && !HasActivePlayContext)
            {
                _pendingEnergyOld = oldEnergy;
                _pendingEnergyNew = newEnergy;
                _pendingEnergyCost = Math.Abs(delta);
                _prevEnergy = newEnergy; // Still update prev to avoid re-detecting
                MarkPendingSnapshotRelevantChange("energy_changed");
                return;
            }

            if (delta < 0)
                _pendingEnergyCost = Math.Abs(delta);

            var phase = _phase;
            var triggerRef = ResolveResourceTruthTriggerRef();
            var payload = new Dictionary<string, object?>
            {
                ["entity_id"] = _playerEntityId,
                ["old"] = oldEnergy,
                ["new"] = newEnergy,
                ["delta"] = delta,
                ["reason"] = ResolveResourceTruthReason(triggerRef),
            };
            AppendTriggerField(payload, triggerRef);

            EmitEvent("energy_changed", phase, ResolveCurrentTruthAttributionContext(), payload,
                dispatchMode: EventDispatchMode.PublicAndShadowHook);

            _prevEnergy = newEnergy;
            MarkPendingSnapshotRelevantChange("energy_changed");
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnEnergyChanged failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnEnergyChanged", ex);
        }
    }

    public static void OnStarsChanged(PlayerCombatState pcs)
    {
        if (!_active || (!_initDone && _phase != "battle_start")) return;
        try
        {
            var newStars = pcs.Stars;
            var oldStars = _prevStars;
            if (oldStars == newStars) return;

            var delta = newStars - oldStars;

            if (delta < 0 && !HasActivePlayContext)
            {
                _pendingStarsOld = oldStars;
                _pendingStarsNew = newStars;
                _prevStars = newStars;
                MarkPendingSnapshotRelevantChange("resource_changed");
                return;
            }

            var triggerRef = ResolveResourceTruthTriggerRef();
            var payload = new Dictionary<string, object?>
            {
                ["entity_id"] = _playerEntityId,
                ["resource_id"] = "stars",
                ["old"] = oldStars,
                ["new"] = newStars,
                ["delta"] = delta,
                ["reason"] = ResolveResourceTruthReason(triggerRef),
            };
            AppendTriggerField(payload, triggerRef);

            EmitEvent("resource_changed", _phase, ResolveCurrentTruthAttributionContext(), payload,
                dispatchMode: EventDispatchMode.PublicAndShadowHook);

            _prevStars = newStars;
            MarkPendingSnapshotRelevantChange("resource_changed");
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnStarsChanged failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnStarsChanged", ex);
        }
    }

    public static void OnAfterEnergyReset(CombatState combatState, Player player)
    {
        if (!_active || !_initDone) return;

        try
        {
            if (!_awaitingTurnStartEnergyReset || _phase != "turn_start")
                return;

            var pcs = player.PlayerCombatState;
            var newEnergy = pcs?.Energy ?? 0;
            if (_prevEnergy != newEnergy)
            {
                EmitEvent("energy_changed", new Dictionary<string, object?>
                {
                    ["entity_id"] = _playerEntityId,
                    ["old"] = _prevEnergy,
                    ["new"] = newEnergy,
                    ["delta"] = newEnergy - _prevEnergy,
                    ["reason"] = "turn_start",
                }, dispatchMode: EventDispatchMode.PublicAndShadowHook);
                MarkPendingSnapshotRelevantChange("energy_changed");
            }

            _prevEnergy = newEnergy;
            _awaitingTurnStartEnergyReset = false;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnAfterEnergyReset failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnAfterEnergyReset", ex);
        }
    }

    private static int GetEnergyCostPaid(CardPlay cardPlay)
    {
        try
        {
            return cardPlay.Resources.EnergySpent;
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".GetEnergyCostPaid",
                $"Failed to read EnergySpent, falling back to pending cost={_pendingEnergyCost}. Error: {ex.Message}", ex);
            return _pendingEnergyCost;
        }
    }

    private static bool TryBuildFollowUpReplayContext(
        CardPlay cardPlay,
        string playedCardId,
        List<string> targetEntityIds,
        out PlayContext? replayContext,
        out string fromZone)
    {
        replayContext = null;
        fromZone = "hand";

        if (_replayPlansByCardId.TryGetValue(playedCardId, out var replayPlan) &&
            replayPlan.PendingExtraPlays > 0 &&
            !string.IsNullOrEmpty(replayPlan.OriginResolutionId))
        {
            var trackedZone = _cardZones.TryGetValue(playedCardId, out var knownReplayZone)
                ? knownReplayZone
                : "discard";
            if (trackedZone != "hand" && trackedZone != "play")
            {
                _resolutionCounter++;
                replayPlan.PendingExtraPlays--;
                replayContext = new PlayContext
                {
                    ResolutionId = $"r_replay_{_resolutionCounter:D3}",
                    CardId = playedCardId,
                    SourceKind = "replayed_effect",
                    TargetEntityIds = targetEntityIds,
                    ParentResolutionId = replayPlan.OriginResolutionId,
                    ResolutionDepth = (replayPlan.OriginResolutionDepth ?? 0) + 1,
                };
                fromZone = trackedZone;
                return true;
            }
        }

        var trackedZoneForFallback = _cardZones.TryGetValue(playedCardId, out var knownZone)
            ? knownZone
            : "hand";
        if (trackedZoneForFallback == "hand" || trackedZoneForFallback == "play")
            return false;

        if (GetEnergyCostPaid(cardPlay) != 0)
            return false;

        if (_staleAttributionContext == null || _staleAttributionContext.CardId != playedCardId)
            return false;

        _resolutionCounter++;
        replayContext = new PlayContext
        {
            ResolutionId = $"r_replay_{_resolutionCounter:D3}",
            CardId = playedCardId,
            SourceKind = "replayed_effect",
            TargetEntityIds = targetEntityIds,
            ParentResolutionId = _staleAttributionContext.ResolutionId,
            ResolutionDepth = (_staleAttributionContext.ResolutionDepth ?? 0) + 1,
        };
        fromZone = trackedZoneForFallback;
        return true;
    }

    public static void OnCardPlayCountModified(CardModel card, int modifiedPlayCount)
    {
        if (!_active || !_initDone)
            return;

        try
        {
            if (modifiedPlayCount <= 1)
                return;

            if (!_cardModelToId.TryGetValue(card, out var cardId))
                return;

            if (_replayPlansByCardId.TryGetValue(cardId, out var existingPlan) &&
                existingPlan.PendingExtraPlays > 0)
            {
                BindReplayPlanOriginIfNeeded(cardId, GetActivePlayContext());
                return;
            }

            _replayPlansByCardId[cardId] = new ReplayPlan
            {
                CardId = cardId,
                PendingExtraPlays = modifiedPlayCount - 1,
            };

            BindReplayPlanOriginIfNeeded(cardId, GetActivePlayContext());

            DebugFileLogger.Log(nameof(BattleLogger) + ".OnCardPlayCountModified",
                $"Replay plan registered. card_instance_id={cardId}, total_play_count={modifiedPlayCount}, pending_extra_plays={modifiedPlayCount - 1}");
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnCardPlayCountModified failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnCardPlayCountModified", ex);
        }
    }

    public static void OnPendingManualPlayTargetCaptured(CardModel card, Creature? target)
    {
        if (!_active || !_initDone)
            return;

        try
        {
            _pendingManualPlayTargetIdsByCard[card] = BuildPendingCardPlayTargetEntityIds(target);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnPendingManualPlayTargetCaptured failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnPendingManualPlayTargetCaptured", ex);
        }
    }

    public static void OnManualPlaySpendResourcesStarted(CardModel card)
    {
        if (!_active || !_initDone)
            return;

        try
        {
            if (!_cardModelToId.TryGetValue(card, out var playedCardId))
            {
                return;
            }

            if (_pendingManualPlayContext != null && _pendingManualPlayContext.CardId == playedCardId)
            {
                return;
            }

            var targetEntityIds = _pendingManualPlayTargetIdsByCard.TryGetValue(card, out var capturedTargetIds)
                ? new List<string>(capturedTargetIds)
                : BuildPendingCardPlayTargetEntityIds(null);

            _pendingManualPlayContext = new PlayContext
            {
                ResolutionId = $"r_card_{++_resolutionCounter:D3}",
                CardId = playedCardId,
                SourceKind = "card",
                TargetEntityIds = targetEntityIds,
            };
            _phase = "player_action";

            DebugFileLogger.Log(nameof(BattleLogger) + ".OnManualPlaySpendResourcesStarted",
                $"pending manual play root prepared. card_instance_id={playedCardId}, resolution_id={_pendingManualPlayContext.ResolutionId}");
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnManualPlaySpendResourcesStarted failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnManualPlaySpendResourcesStarted", ex);
        }
    }

    private static PlayContext? ConsumePendingManualPlayContext(string playedCardId)
    {
        if (_pendingManualPlayContext == null || _pendingManualPlayContext.CardId != playedCardId)
        {
            return null;
        }

        var context = _pendingManualPlayContext;
        _pendingManualPlayContext = null;
        return context;
    }

    private static void EmitManualRootPlayStarted(
        PlayerCombatState pcs,
        string playedCardId,
        List<string> targetEntityIds,
        int energyCostPaid,
        PlayContext rootPlayContext)
    {
        _playContextStack.Push(rootPlayContext);
        _cardsInPlay.Add(playedCardId);
        _phase = "player_action";
        rootPlayContext.TargetEntityIds.Clear();
        rootPlayContext.TargetEntityIds.AddRange(targetEntityIds);
        BindReplayPlanOriginIfNeeded(playedCardId, rootPlayContext);

        EmitEvent("card_play_started", "player_action", rootPlayContext, new Dictionary<string, object?>
        {
            ["card_instance_id"] = playedCardId,
            ["actor_entity_id"] = _playerEntityId,
            ["source_entity_id"] = _playerEntityId,
            ["source_kind"] = "card",
            ["source_card_instance_id"] = playedCardId,
            ["target_entity_ids"] = targetEntityIds,
            ["card_def_id"] = GetCardDefId(playedCardId),
            ["energy_cost_paid"] = energyCostPaid,
        }, dispatchMode: EventDispatchMode.PublicAndShadowHook);

        if (_pendingEnergyOld != _pendingEnergyNew)
        {
            var energyPayload = new Dictionary<string, object?>
            {
                ["entity_id"] = _playerEntityId,
                ["old"] = _pendingEnergyOld,
                ["new"] = _pendingEnergyNew,
                ["delta"] = _pendingEnergyNew - _pendingEnergyOld,
                ["reason"] = "card_play",
            };
            AppendTriggerField(energyPayload, ResolveCardPlayTriggerRef(rootPlayContext));
            EmitEvent("energy_changed", "player_action", rootPlayContext, energyPayload,
                dispatchMode: EventDispatchMode.PublicAndShadowHook);
        }

        if (_pendingStarsOld != _pendingStarsNew)
        {
            var starsPayload = new Dictionary<string, object?>
            {
                ["entity_id"] = _playerEntityId,
                ["resource_id"] = "stars",
                ["old"] = _pendingStarsOld,
                ["new"] = _pendingStarsNew,
                ["delta"] = _pendingStarsNew - _pendingStarsOld,
                ["reason"] = "card_play",
            };
            AppendTriggerField(starsPayload, ResolveCardPlayTriggerRef(rootPlayContext));
            EmitEvent("resource_changed", "player_action", rootPlayContext, starsPayload,
                dispatchMode: EventDispatchMode.PublicAndShadowHook);
        }

        _pendingEnergyCost = 0;
        _pendingEnergyOld = 0;
        _pendingEnergyNew = 0;
        _pendingStarsOld = 0;
        _pendingStarsNew = 0;

        var movePayload = new Dictionary<string, object?>
        {
            ["card_instance_id"] = playedCardId,
            ["from_zone"] = "hand",
            ["to_zone"] = "play",
            ["reason"] = "manual_play",
            ["to_index"] = 0,
        };

        var handIndex = TryGetCardIndexInZone(pcs, "hand", playedCardId);
        if (handIndex.HasValue)
        {
            movePayload["from_index"] = handIndex.Value;
        }

        MoveTrackedCard(playedCardId, "hand", "play", handIndex, 0);
        AppendTriggerField(movePayload, ResolveCardPlayTriggerRef(rootPlayContext));
        EmitEvent("card_moved", "player_action", rootPlayContext, movePayload);
        DebugFileLogger.Log(nameof(BattleLogger) + ".EmitManualRootPlayStarted",
            $"card_play_started emitted. card_instance_id={playedCardId}, resolution_id={rootPlayContext.ResolutionId}");
    }

    public static void OnBeforeCardPlayed(CombatState combatState, CardPlay cardPlay)
    {
        if (!_active || !_initDone)
        {
            DebugFileLogger.Log(nameof(BattleLogger) + ".OnBeforeCardPlayed",
                $"Early return: recorder not ready. active={_active}, initDone={_initDone}");
            return;
        }

        try
        {
            FlushPendingPotionDiscard();
            var player = GameStateReader.GetPlayer(combatState);
            var pcs = player?.PlayerCombatState;
            if (player == null || pcs == null)
            {
                DebugFileLogger.Log(nameof(BattleLogger) + ".OnBeforeCardPlayed",
                    "Early return: player or PlayerCombatState unavailable.");
                return;
            }

            if (_pendingSnapshot && _phase == "turn_start" && IsTurnStartSnapshotReady(pcs))
            {
                FlushPendingSnapshotNow(combatState, player);
            }

            if (!cardPlay.IsAutoPlay && HasActivePlayContext)
            {
                ForceResolveAllActivePlayContexts(pcs, "preemptive_new_play");
            }

            if (!_cardModelToId.TryGetValue(cardPlay.Card, out var playedCardId))
            {
                DebugFileLogger.Log(nameof(BattleLogger) + ".OnBeforeCardPlayed",
                    "Early return: played card did not have a tracked card_instance_id.");
                return;
            }

            _pendingManualPlayTargetIdsByCard.Remove(cardPlay.Card);

            var targetEntityIds = BuildTargetEntityIds(cardPlay);
            var energyCostPaid = GetEnergyCostPaid(cardPlay);

            if (cardPlay.IsAutoPlay)
            {
                EmitTrackedZoneDiffs(pcs);

                var parentContext = GetActivePlayContext();
                if (parentContext != null &&
                    _recentDiscardMoveSeqByCardId.TryGetValue(playedCardId, out var discardCauseSeq))
                {
                    _recentDiscardMoveSeqByCardId.Remove(playedCardId);

                    _resolutionCounter++;
                    var childContext = new PlayContext
                    {
                        ResolutionId = $"r_trigger_{_resolutionCounter:D3}",
                        CardId = playedCardId,
                        SourceKind = "triggered_effect",
                        TargetEntityIds = targetEntityIds,
                        ParentResolutionId = parentContext.ResolutionId,
                        ResolutionDepth = (parentContext.ResolutionDepth ?? 0) + 1,
                    };

                    BindReplayPlanOriginIfNeeded(playedCardId, childContext);

                    EmitEvent("trigger_fired", "player_action", parentContext, new Dictionary<string, object?>
                    {
                        ["trigger_type"] = "discard_triggered_autoplay",
                        ["source_resolution_id"] = parentContext.ResolutionId,
                        ["triggered_resolution_id"] = childContext.ResolutionId,
                        ["subject_card_instance_id"] = playedCardId,
                    }, discardCauseSeq, EventDispatchMode.PublicAndShadowHook);

                    _playContextStack.Push(childContext);
                    _cardsInPlay.Add(playedCardId);
                    var fromZone = _cardZones.TryGetValue(playedCardId, out var previousZone)
                        ? previousZone
                        : "discard";
                    var fromIndex = TryGetCardIndexInTrackedZone(fromZone, playedCardId);
                    MoveTrackedCard(playedCardId, fromZone, "play", fromIndex, 0);
                    _phase = "player_action";

                    EmitEvent("card_play_started", "player_action", childContext, new Dictionary<string, object?>
                    {
                        ["card_instance_id"] = playedCardId,
                        ["actor_entity_id"] = _playerEntityId,
                        ["source_entity_id"] = _playerEntityId,
                        ["source_kind"] = "triggered_effect",
                        ["source_card_instance_id"] = playedCardId,
                        ["target_entity_ids"] = targetEntityIds,
                        ["card_def_id"] = GetCardDefId(playedCardId),
                        ["energy_cost_paid"] = 0,
                    }, dispatchMode: EventDispatchMode.PublicAndShadowHook);

                    var childMovePayload = new Dictionary<string, object?>
                    {
                        ["card_instance_id"] = playedCardId,
                        ["from_zone"] = fromZone,
                        ["to_zone"] = "play",
                        ["reason"] = "auto_play",
                        ["from_index"] = fromIndex,
                        ["to_index"] = 0,
                    };
                    AppendTriggerField(childMovePayload, ResolveCardPlayTriggerRef(childContext));
                    EmitEvent("card_moved", "player_action", childContext, childMovePayload);

                    DebugFileLogger.Log(nameof(BattleLogger) + ".OnBeforeCardPlayed",
                        $"child card_play_started emitted. card_instance_id={playedCardId}, resolution_id={childContext.ResolutionId}, parent_resolution_id={childContext.ParentResolutionId}");
                    return;
                }

                if (TryBuildFollowUpReplayContext(
                        cardPlay,
                        playedCardId,
                        targetEntityIds,
                        out var autoplayReplayContext,
                        out var autoplayReplayFromZone) &&
                    autoplayReplayContext != null)
                {
                    _recentDiscardMoveSeqByCardId.Clear();

                    _playContextStack.Push(autoplayReplayContext);
                    _cardsInPlay.Add(playedCardId);
                    _phase = "player_action";
                    BindReplayPlanOriginIfNeeded(playedCardId, autoplayReplayContext);

                    EmitEvent("card_play_started", "player_action", autoplayReplayContext, new Dictionary<string, object?>
                    {
                        ["card_instance_id"] = playedCardId,
                        ["actor_entity_id"] = _playerEntityId,
                        ["source_entity_id"] = _playerEntityId,
                        ["source_kind"] = "replayed_effect",
                        ["source_card_instance_id"] = playedCardId,
                        ["target_entity_ids"] = targetEntityIds,
                        ["card_def_id"] = GetCardDefId(playedCardId),
                        ["energy_cost_paid"] = 0,
                    }, dispatchMode: EventDispatchMode.PublicAndShadowHook);

                    var replayMovePayload = new Dictionary<string, object?>
                    {
                        ["card_instance_id"] = playedCardId,
                        ["from_zone"] = autoplayReplayFromZone,
                        ["to_zone"] = "play",
                        ["reason"] = "replay_play",
                        ["to_index"] = 0,
                    };
                    var fromIndex = TryGetCardIndexInZone(pcs, autoplayReplayFromZone, playedCardId);
                    if (fromIndex.HasValue)
                    {
                        replayMovePayload["from_index"] = fromIndex.Value;
                    }

                    MoveTrackedCard(playedCardId, autoplayReplayFromZone, "play", fromIndex, 0);
                    AppendTriggerField(replayMovePayload, ResolveCardPlayTriggerRef(autoplayReplayContext));
                    EmitEvent("card_moved", "player_action", autoplayReplayContext, replayMovePayload);
                    DebugFileLogger.Log(nameof(BattleLogger) + ".OnBeforeCardPlayed",
                        $"autoplay follow-up replay detected. card_instance_id={playedCardId}, from_zone={autoplayReplayFromZone}, resolution_id={autoplayReplayContext.ResolutionId}, parent_resolution_id={autoplayReplayContext.ParentResolutionId}");
                    return;
                }

                DebugFileLogger.Log(nameof(BattleLogger) + ".OnBeforeCardPlayed",
                    parentContext == null
                        ? "Early return: autoplay card play without active parent resolution."
                        : $"Early return: autoplay card play missing discard trigger cause and no replay context. card_instance_id={playedCardId}");
                return;
            }

            if (TryBuildFollowUpReplayContext(
                    cardPlay,
                    playedCardId,
                    targetEntityIds,
                    out var replayContext,
                    out var replayFromZone) &&
                replayContext != null)
            {
                _recentDiscardMoveSeqByCardId.Clear();

                _playContextStack.Push(replayContext);
                _cardsInPlay.Add(playedCardId);
                _phase = "player_action";
                BindReplayPlanOriginIfNeeded(playedCardId, replayContext);

                EmitEvent("card_play_started", "player_action", replayContext, new Dictionary<string, object?>
                {
                    ["card_instance_id"] = playedCardId,
                    ["actor_entity_id"] = _playerEntityId,
                    ["source_entity_id"] = _playerEntityId,
                    ["source_kind"] = "replayed_effect",
                    ["source_card_instance_id"] = playedCardId,
                    ["target_entity_ids"] = targetEntityIds,
                    ["card_def_id"] = GetCardDefId(playedCardId),
                    ["energy_cost_paid"] = 0,
                }, dispatchMode: EventDispatchMode.PublicAndShadowHook);

                var replayMovePayload = new Dictionary<string, object?>
                {
                    ["card_instance_id"] = playedCardId,
                    ["from_zone"] = replayFromZone,
                    ["to_zone"] = "play",
                    ["reason"] = "replay_play",
                    ["to_index"] = 0,
                };
                var fromIndex = TryGetCardIndexInZone(pcs, replayFromZone, playedCardId);
                if (fromIndex.HasValue)
                {
                    replayMovePayload["from_index"] = fromIndex.Value;
                }

                MoveTrackedCard(playedCardId, replayFromZone, "play", fromIndex, 0);
                AppendTriggerField(replayMovePayload, ResolveCardPlayTriggerRef(replayContext));
                EmitEvent("card_moved", "player_action", replayContext, replayMovePayload);
                DebugFileLogger.Log(nameof(BattleLogger) + ".OnBeforeCardPlayed",
                    $"follow-up replay detected. card_instance_id={playedCardId}, from_zone={replayFromZone}, resolution_id={replayContext.ResolutionId}, parent_resolution_id={replayContext.ParentResolutionId}");
                return;
            }

            _recentDiscardMoveSeqByCardId.Clear();
            var rootPlayContext = ConsumePendingManualPlayContext(playedCardId) ?? new PlayContext
            {
                ResolutionId = $"r_card_{++_resolutionCounter:D3}",
                CardId = playedCardId,
                SourceKind = "card",
                TargetEntityIds = new List<string>(),
            };

            EmitManualRootPlayStarted(pcs, playedCardId, targetEntityIds, energyCostPaid, rootPlayContext);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnBeforeCardPlayed failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnBeforeCardPlayed", ex);
        }
    }

    public static void OnAfterCardPlayed(CombatState combatState, CardPlay cardPlay)
    {
        if (!_active || !_initDone) return;

        try
        {
            var activeContext = GetActivePlayContext();
            if (activeContext == null)
            {
                DebugFileLogger.Log(nameof(BattleLogger) + ".OnAfterCardPlayed",
                    "Early return: no in-progress card play to resolve.");
                return;
            }

            if (_cardModelToId.TryGetValue(cardPlay.Card, out var playedCardId) &&
                playedCardId != activeContext.CardId)
            {
                DebugFileLogger.Log(nameof(BattleLogger) + ".OnAfterCardPlayed",
                    $"Top play context/card mismatch. top_card_instance_id={activeContext.CardId}, played_card_instance_id={playedCardId}");
            }

            var player = GameStateReader.GetPlayer(combatState);
            var pcs = player?.PlayerCombatState;

            // PileType.None means the card is removed from combat entirely
            // (Power cards, duplicated cards). The game calls RemoveFromCombat
            // AFTER Hook.AfterCardPlayed, so live zone lookup would still find
            // the card in the play pile. Override to "removed" directly.
            //
            // For all other ResultPile values, use ResultPile directly as the
            // authoritative destination. At AfterCardPlayed time the card is
            // still transiently in the play pile, so live-state zone lookups
            // would incorrectly return "play".
            string finalZone;
            int? finalIndex;
            if (cardPlay.ResultPile == PileType.None)
            {
                finalZone = "removed";
                finalIndex = null;
            }
            else
            {
                finalZone = MapPileTypeToZone(cardPlay.ResultPile) ?? "discard";
                finalIndex = null;
            }

            CompletePlayContext(activeContext, finalZone, finalIndex);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnAfterCardPlayed failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnAfterCardPlayed", ex);
        }
    }

    public static void OnPotionEnqueuedForUse(PotionModel potion, Creature? target)
    {
        if (!_active || !_initDone)
            return;

        try
        {
            FlushPendingPotionDiscard();
            var player = potion.Owner;
            var combatState = player?.Creature?.CombatState ?? CombatManager.Instance?.DebugOnlyGetState();
            if (player == null || combatState == null)
            {
                DebugFileLogger.Log(nameof(BattleLogger) + ".OnPotionEnqueuedForUse",
                    "Early return: player or combat state unavailable.");
                return;
            }

            var slotIndex = ResolvePotionSlotIndex(player, potion);
            if (!slotIndex.HasValue)
            {
                DebugFileLogger.Log(nameof(BattleLogger) + ".OnPotionEnqueuedForUse",
                    "Early return: could not resolve potion slot index.");
                return;
            }

            var potionInfo = GameStateReader.ReadPotions(player)
                .FirstOrDefault(info => ReferenceEquals(info.Instance, potion));
            if (!ReferenceEquals(potionInfo.Instance, potion))
            {
                potionInfo = new GameStateReader.PotionInfo
                {
                    Instance = potion,
                    DefId = potion.Id?.Entry ?? GameStateReader.ToSnakeCase(potion.GetType().Name),
                    Name = potion.GetType().Name,
                    SlotIndex = slotIndex.Value,
                };
            }

            var potionId = ResolvePotionIdForUse(potion, potionInfo, slotIndex.Value, out var shouldEmitCreated);
            if (shouldEmitCreated)
            {
                EmitPotionCreatedEvent(
                    potionId,
                    potionInfo,
                    "generated_in_battle",
                    EventDispatchMode.PublicAndShadowHook);
            }

            if (!_potionsById.TryGetValue(potionId, out var record))
                return;

            _resolutionCounter++;
            var targetType = SafeTargetTypeName(potion);
            var targetEntityIds = BuildPotionTargetEntityIds(target, targetType);
            var targetMode = MapPotionTargetMode(targetType, targetEntityIds);
            var context = new PotionContext
            {
                ResolutionId = $"r_potion_{_resolutionCounter:D3}",
                PotionId = potionId,
                SlotIndex = slotIndex.Value,
                TargetMode = targetMode,
                TargetEntityIds = targetEntityIds,
            };

            _activePotionContext = context;
            record.State = "used";
            record.SlotIndex = slotIndex.Value;
            _phase = "player_action";

            EmitEvent("potion_used", "player_action", context, new Dictionary<string, object?>
            {
                ["potion_instance_id"] = potionId,
                ["potion_def_id"] = record.DefId,
                ["potion_name"] = record.Name,
                ["actor_entity_id"] = _playerEntityId,
                ["source_entity_id"] = _playerEntityId,
                ["source_kind"] = "potion",
                ["source_potion_instance_id"] = potionId,
                ["slot_index"] = slotIndex.Value,
                ["target_mode"] = targetMode,
                ["target_entity_ids"] = targetEntityIds,
            }, dispatchMode: EventDispatchMode.PublicAndShadowHook);
            MarkPendingSnapshotRelevantChange("potion_used");

            DebugFileLogger.Log(nameof(BattleLogger) + ".OnPotionEnqueuedForUse",
                $"potion_used emitted. potion_instance_id={potionId}, resolution_id={context.ResolutionId}, slot_index={slotIndex.Value}");
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnPotionEnqueuedForUse failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnPotionEnqueuedForUse", ex);
        }
    }

    public static void OnPotionUseRemovalStarted(Player player, PotionModel potion)
    {
        if (!_active || !_initDone)
            return;

        try
        {
            var slotIndex = GameStateReader.FindPotionSlotIndex(player, potion);
            if (!slotIndex.HasValue)
            {
                DebugFileLogger.Log(nameof(BattleLogger) + ".OnPotionUseRemovalStarted",
                    "Early return: could not resolve slot index before RemoveUsedPotionInternal.");
                return;
            }

            _pendingPotionUseSlotIndices[potion] = slotIndex.Value;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnPotionUseRemovalStarted failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnPotionUseRemovalStarted", ex);
        }
    }

    public static void OnPotionUseFinished(Player player, PotionModel potion)
    {
        if (!_active || !_initDone)
            return;

        try
        {
            var potionId = ResolvePotionIdForDiscard(player, potion);
            if (string.IsNullOrEmpty(potionId))
            {
                DetectPotionChanges(player);
                return;
            }

            if (!_potionsById.TryGetValue(potionId, out var record))
            {
                DetectPotionChanges(player);
                return;
            }

            var slotIndex = record.SlotIndex;
            if (slotIndex < 0 &&
                _pendingPotionUseSlotIndices.TryGetValue(potion, out var pendingSlotIndex))
            {
                slotIndex = pendingSlotIndex;
                record.SlotIndex = pendingSlotIndex;
            }

            if (slotIndex < 0)
            {
                DetectPotionChanges(player);
                return;
            }

            if (record.State != "discarded")
            {
                _pendingPotionDiscardEvent = new PendingPotionDiscardEvent
                {
                    PotionId = potionId,
                    SlotIndex = slotIndex,
                    DispatchMode = EventDispatchMode.PublicAndShadowHook,
                };
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnPotionUseFinished failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnPotionUseFinished", ex);
        }
        finally
        {
            _pendingPotionUseSlotIndices.Remove(potion);
        }
    }

    public static void OnPotionAdded(Player player, PotionModel potion)
    {
        if (!_active || !_initDone)
            return;

        try
        {
            var potionInfo = TryReadPotionInfo(player, potion);
            if (!potionInfo.HasValue)
            {
                DetectPotionChanges(player);
                return;
            }

            var info = potionInfo.Value;
            if (_potionModelToId.TryGetValue(potion, out var knownPotionId))
            {
                var previousState = _potionsById.TryGetValue(knownPotionId, out var existingRecord)
                    ? existingRecord.State
                    : "available";
                UpdatePotionRecord(knownPotionId, info, "available");
                if (previousState == "discarded")
                {
                    EmitPotionCreatedEvent(
                        knownPotionId,
                        info,
                        _potionsById[knownPotionId].Origin ?? "generated_in_battle",
                        EventDispatchMode.PublicAndShadowHook);
                }

                return;
            }

            if (_potionSlotToId.TryGetValue(info.SlotIndex, out var slotPotionId) &&
                _potionsById.TryGetValue(slotPotionId, out var slotRecord) &&
                slotRecord.State != "discarded")
            {
                _potionModelToId[potion] = slotPotionId;
                UpdatePotionRecord(slotPotionId, info, "available");
                return;
            }

            var potionId = TrackPotion(info, "generated_in_battle");
            EmitPotionCreatedEvent(
                potionId,
                info,
                _potionsById[potionId].Origin ?? "generated_in_battle",
                EventDispatchMode.PublicAndShadowHook);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnPotionAdded failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnPotionAdded", ex);
        }
    }

    public static void OnPotionDiscarded(Player player, PotionModel potion)
    {
        if (!_active || !_initDone)
            return;

        try
        {
            var potionId = ResolvePotionIdForDiscard(player, potion);
            if (string.IsNullOrEmpty(potionId))
            {
                DetectPotionChanges(player);
                return;
            }

            var slotIndex = _potionsById.TryGetValue(potionId, out var record)
                ? record.SlotIndex
                : -1;
            if (slotIndex < 0)
            {
                DetectPotionChanges(player);
                return;
            }

            EmitPotionDiscardedEvent(
                slotIndex,
                potionId,
                EventDispatchMode.PublicAndShadowHook);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnPotionDiscarded failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnPotionDiscarded", ex);
        }
    }

    // ─── Card pile changed ─────────────────────────────────────────────────

    public static void OnCardPileChanged(CombatState combatState, CardPile? changedPile = null)
    {
        if (!_active) return;

        if (_awaitingOpeningDraw)
        {
            try
            {
                EmitOpeningDrawCardMoves(combatState);
            }
            catch (Exception ex)
            {
                Log.Error($"[STS2CombatRecorder] EmitOpeningDrawCardMoves failed: {ex.Message}");
                DebugFileLogger.Error(nameof(BattleLogger) + ".OnCardPileChanged", "EmitOpeningDrawCardMoves failed", ex);
            }
            return;
        }

        if (!_initDone) return;
        try
        {
            var player = GameStateReader.GetPlayer(combatState);
            if (player == null) return;
            _lastObservedCombatState = combatState;
            _lastObservedPlayer = player;
            var pcs = player.PlayerCombatState;
            if (pcs == null) return;

            MarkZoneDiffDirty(changedPile?.Type);

            // Some discard-triggered autoplay chains enqueue the child play before
            // the next process-frame diff pass. Emit zone diffs here so the
            // discard move and its seq are available as trigger cause metadata.
            EmitTrackedZoneDiffsIfNeeded(pcs);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnCardPileChanged failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnCardPileChanged", ex);
        }
    }

    // ─── ProcessFrame state diff ───────────────────────────────────────────

    public static void OnProcessFrame(CombatState? combatState)
    {
        if (!_active || combatState == null) return;

        var observedFrameStart = StartDiagnosticsTimer();
        try
        {
            var player = GameStateReader.GetPlayer(combatState);
            if (player == null) return;
            var pcs = player.PlayerCombatState;

            // Opening draw: use frame-based settle instead of pile-change count
            if (_awaitingOpeningDraw)
            {
                if (pcs != null)
                {
                    var handCards = GameStateReader.GetCardsInZone(pcs, "hand");
                    var currentCount = handCards.Count;

                    if (currentCount > _openingDrawMaxHandSeen)
                    {
                        _openingDrawMaxHandSeen = currentCount;
                        _openingDrawSettleCount = 0;
                    }
                    else if (currentCount > 0)
                    {
                        _openingDrawSettleCount++;
                    }

                    if (currentCount >= 1 && _openingDrawSettleCount >= OpeningDrawSettleFrames)
                    {
                        FinalizeOpeningDraw(combatState);
                    }
                }
                return;
            }

            if (!_initDone) return;

            if (pcs != null)
            {
                EmitTrackedZoneDiffsIfNeeded(pcs);
                DetectCardCostChanges();
            }

            DetectEntityRosterChanges(combatState);

            DetectHpAndBlockChanges(combatState, player);
            TryWritePendingSnapshot(combatState, player);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnProcessFrame failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnProcessFrame", ex);
        }
        finally
        {
            RecordObservedFrameTimer(observedFrameStart);
        }
    }

    // ─── Card play detection ───────────────────────────────────────────────

    private static void MarkZoneDiffDirty(PileType? changedPileType = null)
    {
        var zone = changedPileType.HasValue
            ? MapPileTypeToZone(changedPileType.Value)
            : null;

        _dirtyZoneDiffZones.Add(zone ?? "*");
        _zoneDiffValidationFramesRemaining = Math.Max(
            _zoneDiffValidationFramesRemaining,
            ZoneDiffValidationFramesAfterSignal);
        MarkPendingSnapshotRelevantChange(zone != null
            ? $"zone_signal:{zone}"
            : "zone_signal:*");
    }

    private static void EmitTrackedZoneDiffsIfNeeded(PlayerCombatState pcs)
    {
        if (_dirtyZoneDiffZones.Count == 0 && _zoneDiffValidationFramesRemaining <= 0)
            return;

        EmitTrackedZoneDiffs(pcs);
        _dirtyZoneDiffZones.Clear();

        if (_zoneDiffValidationFramesRemaining > 0)
        {
            _zoneDiffValidationFramesRemaining--;
        }
    }

    private static void EmitTrackedZoneDiffs(PlayerCombatState pcs)
    {
        var stageStart = StartDiagnosticsTimer();
        try
        {
            var actualZones = CaptureTrackedZones(pcs);
            var activePlayCardIds = _playContextStack.Select(context => context.CardId).ToHashSet();

            foreach (var cardId in _cardZones.Keys.ToList())
            {
                if (activePlayCardIds.Contains(cardId))
                    continue;
                if (!actualZones.TryGetValue(cardId, out var actualZone))
                    continue;

                var previousZone = _cardZones[cardId];
                if (previousZone == actualZone.Zone)
                    continue;
                if (ShouldSuppressAutoZoneDiff(previousZone, actualZone.Zone))
                    continue;

                var moveContext = GetAttributionContext();
                var moveSeq = EmitEvent("card_moved", _turnIndex, _phase, moveContext, new Dictionary<string, object?>
                {
                    ["card_instance_id"] = cardId,
                    ["from_zone"] = previousZone,
                    ["to_zone"] = actualZone.Zone,
                    ["reason"] = GetAutoMoveReason(previousZone, actualZone.Zone),
                    ["from_index"] = TryGetCardIndexInTrackedZone(previousZone, cardId),
                    ["to_index"] = actualZone.Index,
                });

                MoveTrackedCard(
                    cardId,
                    previousZone,
                    actualZone.Zone,
                    fromIndex: TryGetCardIndexInTrackedZone(previousZone, cardId),
                    toIndex: actualZone.Index);

                if (previousZone == "hand" && actualZone.Zone == "discard" && moveContext != null)
                {
                    _recentDiscardMoveSeqByCardId[cardId] = moveSeq;
                }
            }
        }
        finally
        {
            RecordDiagnosticsStage(RecorderPerfDiagnostics.StageNames.ZoneDiff, stageStart);
        }
    }

    private static bool ShouldSuppressAutoZoneDiff(string previousZone, string actualZone)
    {
        // In M1, cards entering play should only be recorded through the explicit
        // root manual-play lifecycle. Auto zone sync into play is transient noise.
        return previousZone != "play" && actualZone == "play";
    }

    private static Dictionary<string, (string Zone, int Index)> CaptureTrackedZones(PlayerCombatState pcs)
    {
        var result = new Dictionary<string, (string Zone, int Index)>();
        foreach (var zone in new[] { "draw", "hand", "play", "discard", "exhaust" })
        {
            var zoneCards = GameStateReader.GetCardsInZone(pcs, zone);
            for (int i = 0; i < zoneCards.Count; i++)
            {
                if (_cardModelToId.TryGetValue(zoneCards[i].Model, out var cid))
                {
                    result[cid] = (zone, i);
                }
            }
        }
        return result;
    }

    private static List<(CardModel Model, string Zone, int Index)> CaptureAllLiveCards(PlayerCombatState pcs)
    {
        var result = new List<(CardModel, string, int)>();
        foreach (var zone in new[] { "hand", "draw", "discard", "exhaust", "play" })
        {
            var zoneCards = GameStateReader.GetCardsInZone(pcs, zone);
            for (int i = 0; i < zoneCards.Count; i++)
            {
                result.Add((zoneCards[i].Model, zone, i));
            }
        }
        return result;
    }

    private static string GetAutoMoveReason(string fromZone, string toZone)
    {
        if (fromZone == "draw" && toZone == "hand")
            return "draw";
        if (fromZone == "hand" && toZone == "discard")
            return "discard";
        if (fromZone == "play" && toZone == "discard")
            return "resolve_play";
        if (fromZone == "play" && toZone == "exhaust")
            return "resolve_play";
        return "zone_sync";
    }

    private static int? TryGetCardIndexInZone(PlayerCombatState pcs, string zone, string cardId)
    {
        var zoneCards = GameStateReader.GetCardsInZone(pcs, zone);
        for (int i = 0; i < zoneCards.Count; i++)
        {
            if (_cardModelToId.TryGetValue(zoneCards[i].Model, out var cid) && cid == cardId)
            {
                return i;
            }
        }
        return null;
    }

    private static string? MapPileTypeToZone(PileType pileType)
    {
        return pileType switch
        {
            PileType.Draw => "draw",
            PileType.Hand => "hand",
            PileType.Discard => "discard",
            PileType.Exhaust => "exhaust",
            PileType.Play => "play",
            PileType.Deck => "draw",
            _ => null,
        };
    }

    private static void EmitMissingTargetOutcomeFallback(CombatState combatState, PlayContext context)
    {
        // Damage truth no longer falls back to polling-based target disappearance
        // inference. Final damage attribution must come from hook/patch capture.
    }

    private static void CompletePlayContext(PlayContext context, string finalZone, int? finalIndex = null)
    {
        if (_playContextStack.Count == 0)
            return;

        var activeContext = _playContextStack.Pop();
        if (activeContext.ResolutionId != context.ResolutionId)
        {
            DebugFileLogger.Log(nameof(BattleLogger) + ".CompletePlayContext",
                $"Unexpected play-context pop order. expected={context.ResolutionId}, actual={activeContext.ResolutionId}");
            context = activeContext;
        }

        _cardsInPlay.Remove(context.CardId);

        if (finalZone != "play")
        {
            MoveTrackedCard(context.CardId, "play", finalZone, fromIndex: 0, toIndex: finalIndex);

            var movePayload = new Dictionary<string, object?>
            {
                ["card_instance_id"] = context.CardId,
                ["from_zone"] = "play",
                ["to_zone"] = finalZone,
                ["reason"] = "resolve_play",
                ["from_index"] = 0,
            };
            if (finalIndex.HasValue)
            {
                movePayload["to_index"] = finalIndex.Value;
            }

            AppendTriggerField(movePayload, ResolveCardPlayTriggerRef(context));
            EmitEvent("card_moved", "player_action", context, movePayload);

            if (finalZone == "exhaust")
            {
                var exhaustedPayload = new Dictionary<string, object?>
                {
                    ["card_instance_id"] = context.CardId,
                    ["from_zone"] = "play",
                };
                AppendTriggerField(exhaustedPayload, ResolveCardPlayTriggerRef(context));
                EmitEvent("card_exhausted", "player_action", context, exhaustedPayload);
            }
        }

        var resolvedPayload = new Dictionary<string, object?>
        {
            ["card_instance_id"] = context.CardId,
        };
        if (finalZone != "play")
        {
            resolvedPayload["final_zone"] = finalZone;
        }
        EmitEvent("card_play_resolved", "player_action", context, resolvedPayload, dispatchMode: EventDispatchMode.PublicAndShadowHook);
        DebugFileLogger.Log(nameof(BattleLogger) + ".CompletePlayContext",
            $"card_play_resolved emitted. card_instance_id={context.CardId}, final_zone={finalZone}, resolution_id={context.ResolutionId}");

        // Keep the last resolved action chain as the owner of any trailing
        // player_action diffs until an explicit boundary such as turn start
        // or a new in-progress play takes over.
        _staleAttributionContext = ToAttributionContext(context);
        _staleAttributionPhase = "player_action";
        _phase = "player_action";
        if (_playContextStack.Count == 0)
        {
            _recentDiscardMoveSeqByCardId.Clear();
        }

    }

    private static void DetectPotionChanges(Player player)
    {
        var stageStart = StartDiagnosticsTimer();
        try
        {
            var currentPotions = GameStateReader.ReadPotions(player);
            var currentPotionsBySlot = currentPotions.ToDictionary(p => p.SlotIndex, p => p);
            var currentSlotAssignments = new Dictionary<int, string>();

            foreach (var (slotIndex, potionId) in _potionSlotToId.ToList())
            {
                if (!currentPotionsBySlot.TryGetValue(slotIndex, out var currentPotion))
                {
                    EmitPotionDiscardedEvent(slotIndex, potionId);
                    continue;
                }

                if (_potionModelToId.TryGetValue(currentPotion.Instance, out var currentPotionId) &&
                    currentPotionId == potionId)
                {
                    continue;
                }

                EmitPotionDiscardedEvent(slotIndex, potionId);
            }

            foreach (var potionInfo in currentPotions)
            {
                string potionId;
                if (_potionModelToId.TryGetValue(potionInfo.Instance, out var existingId))
                {
                    potionId = existingId;
                    UpdatePotionRecord(potionId, potionInfo, _potionsById[potionId].State == "discarded" ? "available" : _potionsById[potionId].State);
                }
                else
                {
                    potionId = TrackPotion(potionInfo, "generated_in_battle");
                    EmitPotionCreatedEvent(potionId, potionInfo, "generated_in_battle");
                }

                currentSlotAssignments[potionInfo.SlotIndex] = potionId;
            }

            _potionSlotToId = currentSlotAssignments;
        }
        finally
        {
            RecordDiagnosticsStage(RecorderPerfDiagnostics.StageNames.PotionDiff, stageStart);
        }
    }

    private static void EmitPotionCreatedEvent(
        string potionId,
        GameStateReader.PotionInfo potionInfo,
        string origin,
        EventDispatchMode dispatchMode = EventDispatchMode.PublicOnly)
    {
        EmitEvent("potion_created", new Dictionary<string, object?>
        {
            ["potion_instance_id"] = potionId,
            ["potion_def_id"] = potionInfo.DefId,
            ["potion_name"] = potionInfo.Name,
            ["slot_index"] = potionInfo.SlotIndex,
            ["origin"] = origin,
        }, dispatchMode: dispatchMode);
        MarkPendingSnapshotRelevantChange("potion_created");
    }

    private static void EmitPotionDiscardedEvent(
        int slotIndex,
        string potionId,
        EventDispatchMode dispatchMode = EventDispatchMode.PublicOnly)
    {
        if (!_potionsById.TryGetValue(potionId, out var record))
            return;

        var context = _activePotionContext != null && _activePotionContext.PotionId == potionId
            ? _activePotionContext
            : null;

        record.State = "discarded";
        record.SlotIndex = slotIndex;
        _potionSlotToId.Remove(slotIndex);

        EmitEvent("potion_discarded", "player_action", context, new Dictionary<string, object?>
        {
            ["potion_instance_id"] = potionId,
            ["potion_def_id"] = record.DefId,
            ["potion_name"] = record.Name,
            ["slot_index"] = slotIndex,
        }, dispatchMode: dispatchMode);
        MarkPendingSnapshotRelevantChange("potion_discarded");

        if (context != null)
        {
            CompletePotionContext(context);
        }
    }

    private static void FlushPendingPotionDiscard(string? potionId = null)
    {
        if (_pendingPotionDiscardEvent == null)
            return;

        if (!string.IsNullOrEmpty(potionId) &&
            !string.Equals(_pendingPotionDiscardEvent.PotionId, potionId, StringComparison.Ordinal))
        {
            return;
        }

        var pending = _pendingPotionDiscardEvent;
        _pendingPotionDiscardEvent = null;
        EmitPotionDiscardedEvent(pending.SlotIndex, pending.PotionId, pending.DispatchMode);
    }

    private static void CompletePotionContext(PotionContext context)
    {
        if (_activePotionContext == null || _activePotionContext.ResolutionId != context.ResolutionId)
            return;

        _activePotionContext = null;
        _staleAttributionContext = ToAttributionContext(context);
        _staleAttributionPhase = "player_action";
    }

    private static void ForceResolveAllActivePlayContexts(PlayerCombatState? pcs, string reason)
    {
        while (HasActivePlayContext)
        {
            ForceResolveTopPlayContext(pcs, reason);
        }
    }

    private static void ForceResolveTopPlayContext(PlayerCombatState? pcs, string reason)
    {
        var activeContext = GetActivePlayContext();
        if (activeContext == null) return;

        var (targetZone, targetIndex) = ResolveResolvedCardZone(pcs, activeContext.CardId, "discard");
        if (targetZone == "play") targetZone = "discard";
        CompletePlayContext(activeContext, targetZone, targetZone == "play" ? null : targetIndex);

        Log.Info($"[STS2CombatRecorder] Force-resolved card play {activeContext.CardId} ({reason}), resId={activeContext.ResolutionId}");
        DebugFileLogger.Log(nameof(BattleLogger) + ".ForceResolveTopPlayContext",
            $"Forced resolve. card_instance_id={activeContext.CardId}, reason={reason}, resolution_id={activeContext.ResolutionId}");
    }

    public static void OnEnemyActionStarted(MonsterModel monster)
    {
        if (!_active || !_initDone) return;

        try
        {
            FlushPendingPotionDiscard();
            if (!_entityIds.TryGetValue(monster.Creature, out var actorEntityId))
            {
                DebugFileLogger.Log(nameof(BattleLogger) + ".OnEnemyActionStarted",
                    $"Early return: enemy entity id unavailable for monster={monster.Id?.Entry ?? monster.GetType().Name}");
                return;
            }

            _phase = "enemy_action";
            _activeSide = "enemy";
            _staleAttributionContext = null;
            _staleAttributionPhase = null;

            _resolutionCounter++;
            _activeEnemyActionContext = new EnemyActionContext
            {
                ResolutionId = $"r_enemy_{_resolutionCounter:D3}",
                ActorEntityId = actorEntityId,
                ActorCreature = monster.Creature,
                MoveId = monster.NextMove.Id,
                EmittedTrackedOutcome = false,
            };

            EmitPendingEnemyActorPreActionBlockChange(_activeEnemyActionContext, monster.Creature);

            DebugFileLogger.Log(nameof(BattleLogger) + ".OnEnemyActionStarted",
                $"Enemy action started. entity_id={actorEntityId}, move_id={monster.NextMove.Id}, resolution_id={_activeEnemyActionContext.ResolutionId}");
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnEnemyActionStarted failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnEnemyActionStarted", ex);
        }
    }

    private static void EmitPendingEnemyActorPreActionBlockChange(EnemyActionContext context, Creature creature)
    {
        if (!_prevBlock.TryGetValue(context.ActorEntityId, out var oldBlock))
        {
            return;
        }

        var newBlock = creature.Block;
        if (oldBlock == newBlock)
        {
            return;
        }

        var attrContext = ToAttributionContext(context);
        EmitEvent("block_changed", "enemy_action", attrContext, new Dictionary<string, object?>
        {
            ["entity_id"] = context.ActorEntityId,
            ["old"] = oldBlock,
            ["new"] = newBlock,
            ["delta"] = newBlock - oldBlock,
            ["reason"] = newBlock > oldBlock ? "block_gain" : "block_loss",
        });

        _prevBlock[context.ActorEntityId] = newBlock;
        context.EmittedTrackedOutcome = true;
    }

    public static void OnEnemyActionFinished(MonsterModel monster)
    {
        if (!_active || !_initDone) return;

        try
        {
            var context = _activeEnemyActionContext;
            if (context == null || !ReferenceEquals(context.ActorCreature, monster.Creature))
            {
                return;
            }

            _activeEnemyActionContext = null;
            _phase = "enemy_action";
            _activeSide = "enemy";

            if (!context.EmittedTrackedOutcome)
            {
                _staleAttributionContext = null;
                _staleAttributionPhase = null;
                return;
            }

            _staleAttributionContext = ToAttributionContext(context);
            _staleAttributionPhase = "enemy_action";

            DebugFileLogger.Log(nameof(BattleLogger) + ".OnEnemyActionFinished",
                $"Enemy action finished. entity_id={context.ActorEntityId}, move_id={context.MoveId}, emitted_tracked_outcome={context.EmittedTrackedOutcome}, resolution_id={context.ResolutionId}");
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnEnemyActionFinished failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnEnemyActionFinished", ex);
        }
    }

    public static void OnEnemyAttackResolved(CombatState combatState, AttackCommand command)
    {
        // Damage truth now comes from CreatureCmd.Damage settlement capture.
        // This legacy AfterAttack-based direct result synthesis is intentionally
        // left inactive so observed-state polling cannot masquerade as truth.
    }

    public static void OnBlockGained(CombatState combatState, Creature creature, decimal amount, ValueProp props, CardModel? cardSource)
    {
        if (!_active || !_initDone) return;

        try
        {
            var sample = PopPendingBlockTruthSample(creature);

            if (amount <= 0m)
            {
                var skippedEntityId = ResolveEntityId(creature);
                if (_activeEnemyActionContext != null &&
                    !string.IsNullOrEmpty(skippedEntityId) &&
                    ReferenceEquals(_activeEnemyActionContext.ActorCreature, creature))
                {
                    _activeEnemyActionContext.SuppressedPollingEntityIds.Remove(skippedEntityId);
                }
                return;
            }

            var entityId = ResolveEntityId(creature);
            if (string.IsNullOrEmpty(entityId))
            {
                return;
            }

            var oldBlock = sample?.OldBlock ?? (_prevBlock.TryGetValue(entityId, out var trackedBlock) ? trackedBlock : creature.Block);
            var newBlock = creature.Block;
            if (newBlock == oldBlock)
            {
                if (_activeEnemyActionContext != null && ReferenceEquals(_activeEnemyActionContext.ActorCreature, creature))
                {
                    _activeEnemyActionContext.SuppressedPollingEntityIds.Remove(entityId);
                }
                return;
            }

            var finalGainAmount = (decimal)(newBlock - oldBlock);
            var attrContext = sample?.EventContext ?? ResolveCurrentTruthAttributionContext();
            var payload = new Dictionary<string, object?>
            {
                ["entity_id"] = entityId,
                ["old"] = oldBlock,
                ["new"] = newBlock,
                ["delta"] = newBlock - oldBlock,
                ["reason"] = "block_gain",
            };

            if (sample != null)
            {
                var baseAmount = sample.BaseAmount ?? sample.ModifiedAmount ?? finalGainAmount;
                var modifiedAmount = sample.ModifiedAmount ?? amount;
                payload["base_amount"] = baseAmount;
                payload["modified_amount"] = modifiedAmount;
                payload["final_gain_amount"] = finalGainAmount;
                payload["steps"] = BuildBlockGainStepPayloads(sample, baseAmount, modifiedAmount, finalGainAmount);
            }

            AppendTriggerField(payload, sample?.Trigger ?? ResolveBlockTriggerRef(creature, props, cardSource, attrContext));
            EmitEvent("block_changed", sample?.Phase ?? _phase, attrContext, payload);

            _prevBlock[entityId] = newBlock;
            if (_activeEnemyActionContext != null && ReferenceEquals(_activeEnemyActionContext.ActorCreature, creature))
            {
                _activeEnemyActionContext.SuppressedPollingEntityIds.Remove(entityId);
                _activeEnemyActionContext.EmittedTrackedOutcome = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnBlockGained failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnBlockGained", ex);
        }
    }

    public static void OnBlockStageModifyBlock(
        CombatState combatState,
        Creature creature,
        decimal amount,
        ValueProp props,
        CardModel? cardSource,
        CardPlay? cardPlay,
        decimal finalAmount)
    {
        if (!_active || !_initDone) return;

        try
        {
            var sample = PeekPendingBlockTruthSample(creature);
            if (sample == null)
            {
                return;
            }

            if (sample.HasCapturedModifierChain)
            {
                DebugFileLogger.Log(
                    nameof(BattleLogger) + ".OnBlockStageModifyBlock",
                    $"Ignored extra ModifyBlock capture while block sample was still pending. entity_id={sample.EntityId}, amount={amount}, final_amount={finalAmount}");
                return;
            }

            sample.BaseAmount = amount;
            sample.ModifiedAmount = finalAmount;
            sample.ModifierSteps.Clear();
            ReplayModifyBlockSteps(sample.ModifierSteps, combatState, creature, amount, props, cardSource, cardPlay);

            var replayedAmount = sample.ModifierSteps.LastOrDefault()?.After ?? amount;
            if (replayedAmount != finalAmount)
            {
                sample.ModifierSteps.Clear();
                sample.ModifierSteps.Add(new BlockGainStepRecord
                {
                    Stage = "base",
                    Operation = "base",
                    After = amount,
                });

                if (finalAmount != amount)
                {
                    sample.ModifierSteps.Add(new BlockGainStepRecord
                    {
                        Stage = "block_adjustment",
                        Operation = "adjust",
                        Before = amount,
                        After = finalAmount,
                        Delta = finalAmount - amount,
                        ModifierRef = BuildUnknownSource("modify_block_replay_mismatch"),
                        IsUnknown = true,
                        UnknownReason = "modify_block_replay_mismatch",
                    });
                }
            }

            sample.HasCapturedModifierChain = true;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnBlockStageModifyBlock failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnBlockStageModifyBlock", ex);
        }
    }

    public static void OnBlockLossSamplingStarted(Creature creature)
    {
        if (!_active || !_initDone) return;

        try
        {
            var entityId = ResolveEntityId(creature);
            if (string.IsNullOrEmpty(entityId))
            {
                return;
            }

            GetPendingBlockTruthStack(creature).Push(new PendingBlockTruthSample
            {
                EntityId = entityId,
                OldBlock = creature.Block,
                Phase = _phase,
                EventContext = ResolveCurrentTruthAttributionContext(),
                Trigger = ResolveCurrentTruthTriggerRef(includeEnemyMove: true),
            });
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnBlockLossSamplingStarted failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnBlockLossSamplingStarted", ex);
        }
    }

    public static void OnBlockLost(Creature creature)
    {
        if (!_active || !_initDone) return;

        try
        {
            var entityId = ResolveEntityId(creature);
            if (string.IsNullOrEmpty(entityId))
            {
                return;
            }

            var sample = PopPendingBlockTruthSample(creature);
            var oldBlock = sample?.OldBlock ?? (_prevBlock.TryGetValue(entityId, out var trackedBlock) ? trackedBlock : creature.Block);
            var newBlock = creature.Block;
            if (newBlock == oldBlock)
            {
                return;
            }

            var payload = new Dictionary<string, object?>
            {
                ["entity_id"] = entityId,
                ["old"] = oldBlock,
                ["new"] = newBlock,
                ["delta"] = newBlock - oldBlock,
                ["reason"] = "block_loss",
            };
            AppendTriggerField(payload, sample?.Trigger ?? ResolveCurrentTruthTriggerRef(includeEnemyMove: true));
            EmitEvent("block_changed", sample?.Phase ?? _phase, sample?.EventContext ?? ResolveCurrentTruthAttributionContext(), payload);

            if (oldBlock > 0 && newBlock <= 0)
            {
                EmitBlockBrokenEvent(
                    entityId,
                    oldBlock,
                    newBlock,
                    sample?.Phase ?? _phase,
                    sample?.EventContext ?? ResolveCurrentTruthAttributionContext(),
                    sample?.Trigger ?? ResolveCurrentTruthTriggerRef(includeEnemyMove: true));
            }

            _prevBlock[entityId] = newBlock;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnBlockLost failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnBlockLost", ex);
        }
    }

    public static void OnEnemyAttackSamplingStarted(CombatState combatState, AttackCommand command)
    {
        if (!_active || !_initDone) return;

        try
        {
            var context = _activeEnemyActionContext;
            var attacker = command.Attacker;
            if (context == null || attacker == null || !ReferenceEquals(context.ActorCreature, attacker) || !attacker.IsMonster)
            {
                return;
            }

            if (context.PendingAttackSamples.ContainsKey(command) || context.ProcessedAttackCommands.Contains(command))
            {
                return;
            }

            var samples = new Dictionary<Creature, EnemyAttackTargetSample>();
            foreach (var target in combatState.PlayerCreatures)
            {
                if (!_entityIds.TryGetValue(target, out var entityId))
                {
                    continue;
                }

                samples[target] = new EnemyAttackTargetSample
                {
                    EntityId = entityId,
                    OldHp = target.CurrentHp,
                    OldBlock = target.Block,
                };
                context.SuppressedPollingEntityIds.Add(entityId);
            }

            if (samples.Count > 0)
            {
                context.PendingAttackSamples[command] = samples;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnEnemyAttackSamplingStarted failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnEnemyAttackSamplingStarted", ex);
        }
    }

    public static void OnBlockGainSamplingStarted(Creature creature, decimal amount, ValueProp props, CardModel? cardSource)
    {
        if (!_active || !_initDone) return;

        try
        {
            if (amount <= 0m)
            {
                return;
            }

            var entityId = ResolveEntityId(creature);
            if (string.IsNullOrEmpty(entityId))
            {
                return;
            }

            var eventContext = ResolveCurrentTruthAttributionContext();
            var sampleStack = GetPendingBlockTruthStack(creature);
            sampleStack.Push(new PendingBlockTruthSample
            {
                EntityId = entityId,
                OldBlock = creature.Block,
                Phase = _phase,
                EventContext = eventContext,
                Trigger = ResolveBlockTriggerRef(creature, props, cardSource, eventContext),
                BaseAmount = amount,
            });

            if (_activeEnemyActionContext != null && ReferenceEquals(_activeEnemyActionContext.ActorCreature, creature))
            {
                _activeEnemyActionContext.SuppressedPollingEntityIds.Add(entityId);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnBlockGainSamplingStarted failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnBlockGainSamplingStarted", ex);
        }
    }

    public static void OnBlockClearSamplingStarted(Creature creature)
    {
        if (!_active || !_initDone) return;

        try
        {
            var entityId = ResolveEntityId(creature);
            if (string.IsNullOrEmpty(entityId) || creature.Block <= 0)
            {
                return;
            }

            GetPendingBlockClearStack(creature).Push(new PendingBlockClearSample
            {
                EntityId = entityId,
                OldBlock = creature.Block,
                Phase = _phase,
            });
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnBlockClearSamplingStarted failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnBlockClearSamplingStarted", ex);
        }
    }

    public static void OnBlockCleared(Creature creature)
    {
        if (!_active || !_initDone) return;

        try
        {
            var sample = PopPendingBlockClearSample(creature);
            if (sample == null)
            {
                return;
            }

            var newBlock = creature.Block;
            if (sample.OldBlock == newBlock)
            {
                _prevBlock[sample.EntityId] = newBlock;
                return;
            }

            EmitEvent("block_changed", sample.Phase, (AttributionContext?)null, new Dictionary<string, object?>
            {
                ["entity_id"] = sample.EntityId,
                ["old"] = sample.OldBlock,
                ["new"] = newBlock,
                ["delta"] = newBlock - sample.OldBlock,
                ["reason"] = "block_clear",
            });

            EmitBlockClearedEvent(sample.EntityId, sample.OldBlock, newBlock, sample.Phase, null, null);
            _prevBlock[sample.EntityId] = newBlock;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnBlockCleared failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnBlockCleared", ex);
        }
    }

    public static void OnBlockClearPrevented(AbstractModel preventer, Creature creature)
    {
        if (!_active || !_initDone) return;

        try
        {
            var sample = PopPendingBlockClearSample(creature);
            var entityId = sample?.EntityId ?? ResolveEntityId(creature);
            if (string.IsNullOrEmpty(entityId))
            {
                return;
            }

            var retainedBlock = creature.Block;
            var preventerRef = SanitizePowerTruthRef(DescribeSource(preventer));
            if (preventerRef == null)
            {
                return;
            }

            EmitBlockClearPreventedEvent(
                entityId,
                retainedBlock,
                sample?.Phase ?? _phase,
                null,
                preventerRef);

            _prevBlock[entityId] = retainedBlock;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnBlockClearPrevented failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnBlockClearPrevented", ex);
        }
    }

    public static void OnPowerAmountChanged(CombatState combatState, PowerModel power, decimal amount, Creature? applier, CardModel? cardSource)
    {
        if (!_active)
        {
            return;
        }

        try
        {
            var owner = power.Owner;
            if (!_entityIds.TryGetValue(owner, out var entityId))
            {
                return;
            }

            var powerId = GetPowerId(power);
            var trackedState = GetTrackedPowerStateMap(entityId);
            var eventContext = GetAttributionContext();
            var applierRef = BuildPowerApplierRef(applier);
            var triggerRef = ResolvePowerTriggerRef(cardSource);

            if (power.ShouldRemoveDueToAmount())
            {
                _pendingPowerRemovalTruthByPower[power] = new PendingPowerRemovalTruth
                {
                    EventContext = eventContext,
                    Applier = applierRef,
                    Trigger = triggerRef,
                };
                return;
            }

            var currentState = CaptureVisiblePowerState(owner);
            var currentPowerState = CaptureCurrentPowerState(power, currentState);

            if (!trackedState.TryGetValue(powerId, out var previousPowerState))
            {
                EmitPowerAppliedEvent(
                    entityId,
                    currentPowerState,
                    eventContext,
                    applierRef,
                    triggerRef,
                    EventDispatchMode.PublicAndShadowHook);
            }
            else if (previousPowerState.Stacks != currentPowerState.Stacks ||
                     !string.Equals(previousPowerState.Name, currentPowerState.Name, StringComparison.Ordinal))
            {
                EmitPowerStacksChangedEvent(
                    entityId,
                    previousPowerState,
                    currentPowerState,
                    eventContext,
                    applierRef,
                    triggerRef,
                    EventDispatchMode.PublicAndShadowHook);
            }

            trackedState[powerId] = CloneTrackedPowerState(currentPowerState);
            OnTrackedOrbDisplayValuesPossiblyChangedByPower(power, eventContext, _phase);
            MarkPendingSnapshotRelevantChange("power_state");
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnPowerAmountChanged failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnPowerAmountChanged", ex);
        }
    }

    public static void OnPowerRemoved(Creature creature, PowerModel power)
    {
        if (!_active)
        {
            return;
        }

        try
        {
            if (!_entityIds.TryGetValue(creature, out var entityId))
            {
                return;
            }

            var powerId = GetPowerId(power);
            var trackedState = GetTrackedPowerStateMap(entityId);
            if (!trackedState.TryGetValue(powerId, out var removedPowerState))
            {
                var powerName = GetPowerName(power);
                if (string.IsNullOrWhiteSpace(powerName) && !power.IsVisible)
                {
                    return;
                }

                removedPowerState = new TrackedPowerState
                {
                    PowerId = powerId,
                    Name = powerName,
                    Stacks = power.Amount,
                };
            }

            var pendingTruth = ResolvePendingPowerRemovalTruth(power);
            var eventContext = pendingTruth?.EventContext ?? GetReliablePowerRemovalContext();
            var applierRef = pendingTruth?.Applier;
            var triggerRef = pendingTruth?.Trigger ?? ResolvePowerRemovalTriggerRef();
            if (ShouldDeferObservedStateEvent(entityId))
            {
                var payload = new Dictionary<string, object?>
                {
                    ["target_entity_id"] = entityId,
                    ["power_id"] = removedPowerState.PowerId,
                    ["power_name"] = removedPowerState.Name,
                    ["stacks"] = removedPowerState.Stacks,
                };

                AppendPowerTruthFields(payload, applierRef, triggerRef);
                DeferObservedStateEvent(
                    entityId,
                    "power_removed",
                    _phase,
                    eventContext,
                    payload,
                    dispatchMode: EventDispatchMode.PublicAndShadowHook);
            }
            else
            {
                EmitPowerRemovedEvent(
                    entityId,
                    removedPowerState,
                    eventContext,
                    applierRef,
                    triggerRef,
                    EventDispatchMode.PublicAndShadowHook);
            }

            trackedState.Remove(powerId);
            _pendingPowerRemovalTruthByPower.Remove(power);
            OnTrackedOrbDisplayValuesPossiblyChangedByPower(power, eventContext, _phase);
            MarkPendingSnapshotRelevantChange("power_state");
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnPowerRemoved failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnPowerRemoved", ex);
        }
    }

    // ─── Card cost validator ─────────────────────────────────────────────

    private static void DetectCardCostChanges()
    {
        foreach (var (model, cardId) in _cardModelToId)
        {
            var currentCost = GameStateReader.GetEnergyCost(model);
            if (!_cardCosts.TryGetValue(cardId, out var trackedCost))
            {
                _cardCosts[cardId] = currentCost;
                continue;
            }

            if (trackedCost == currentCost) continue;

            _cardCosts[cardId] = currentCost;
            _runtimeState?.RecordCardModifiedDriftValidatorHit();
            MarkPendingSnapshotRelevantChange("card_modified_validator");
            DebugFileLogger.Log(
                nameof(BattleLogger) + ".DetectCardCostChanges",
                $"Observed card cost drift without formal CardEnergyCost lane. card_instance_id={cardId}, old={trackedCost}, new={currentCost}");
        }
    }

    // ─── HP / Block diff ───────────────────────────────────────────────────

    private static void DetectHpAndBlockChanges(CombatState combatState, Player player)
    {
        var stageStart = StartDiagnosticsTimer();
        try
        {
            var creatures = new List<(Creature creature, string entityId, string side)>();

            if (_entityIds.TryGetValue(player.Creature, out var pid))
                creatures.Add((player.Creature, pid, "player"));

            foreach (var enemy in GameStateReader.GetAllEnemies(combatState))
            {
                if (_entityIds.TryGetValue(enemy, out var eid))
                    creatures.Add((enemy, eid, "enemy"));
            }

            foreach (var (creature, entityId, side) in creatures)
            {
                try
                {
                    if (_removedEntityIds.Contains(entityId))
                    {
                        continue;
                    }

                    if (_activeEnemyActionContext?.SuppressedPollingEntityIds.Contains(entityId) == true)
                    {
                        continue;
                    }

                    var hp = creature.CurrentHp;
                    var block = creature.Block;
                    ObserveDamageTruthValidation(entityId, hp, block);

                    if (_prevHp.TryGetValue(entityId, out var prevHp) && prevHp != hp)
                    {
                        var delta = hp - prevHp;
                        var attrContext = GetAttributionContext();
                        var hasAttribution = attrContext != null;
                        var phase = _phase;
                        var hpPayload = new Dictionary<string, object?>
                        {
                            ["entity_id"] = entityId,
                            ["old"] = prevHp,
                            ["new"] = hp,
                            ["delta"] = delta,
                            ["reason"] = delta < 0 ? "damage" : "heal",
                        };

                        if (ShouldDeferObservedStateEvent(entityId))
                        {
                            DeferObservedStateEvent(
                                entityId,
                                "hp_changed",
                                phase,
                                attrContext,
                                hpPayload,
                                deathSourceEntityId: hasAttribution ? attrContext!.SourceEntityId : null,
                                deathReason: delta < 0 ? "damage" : "unknown");
                        }
                        else
                        {
                            EmitEvent("hp_changed", phase, attrContext, hpPayload);

                            if (hp <= 0)
                            {
                                EmitEntityDiedIfNeeded(
                                    entityId,
                                    phase,
                                    attrContext,
                                    delta < 0 ? "damage" : "unknown");
                            }

                            MarkPendingSnapshotRelevantChange("hp_changed");

                            if (delta > 0 &&
                                _pendingEntityRevivedEvents.TryGetValue(entityId, out var pendingRevive))
                            {
                                EmitEntityRevivedEvent(pendingRevive);
                            }
                        }

                        _prevHp[entityId] = hp;
                    }

                    if (_prevBlock.TryGetValue(entityId, out var prevBlock) && prevBlock != block)
                    {
                        if (_phase == "enemy_action" &&
                            _activeEnemyActionContext == null &&
                            side == "enemy")
                        {
                            continue;
                        }

                        var blkAttrContext = GetAttributionContext();
                        var blkPhase = _phase;
                        var blockPayload = new Dictionary<string, object?>
                        {
                            ["entity_id"] = entityId,
                            ["old"] = prevBlock,
                            ["new"] = block,
                            ["delta"] = block - prevBlock,
                            ["reason"] = block > prevBlock ? "block_gain" : "block_loss",
                        };

                        if (block < prevBlock && ShouldDeferObservedStateEvent(entityId))
                        {
                            _prevBlock[entityId] = block;
                            continue;
                        }

                        if (block > prevBlock && HasPendingBlockTruthSample(creature))
                        {
                            continue;
                        }

                        if (ShouldDeferObservedStateEvent(entityId))
                        {
                            DeferObservedStateEvent(
                                entityId,
                                "block_changed",
                                blkPhase,
                                blkAttrContext,
                                blockPayload);
                        }
                        else
                        {
                            EmitEvent("block_changed", blkPhase, blkAttrContext, blockPayload);
                            MarkPendingSnapshotRelevantChange("block_changed");
                        }

                        _prevBlock[entityId] = block;
                    }
                }
                catch (Exception ex)
                {
                    DebugFileLogger.Error(nameof(BattleLogger) + ".DetectHpAndBlockChanges",
                        $"Failed to detect HP/block diff for entity. entity_id={entityId}, side={side}", ex);
                }
            }
        }
        finally
        {
            RecordDiagnosticsStage(RecorderPerfDiagnostics.StageNames.HpBlockDiff, stageStart);
        }
    }

    private static void EmitEntityDiedIfNeeded(
        string entityId,
        string phase,
        AttributionContext? attrContext,
        string reason)
    {
        if (_deathRecordedEntityIds.Contains(entityId))
            return;

        var payload = new Dictionary<string, object?>
        {
            ["entity_id"] = entityId,
            ["reason"] = reason,
        };
        AppendTriggerField(payload, ResolveEntityTruthTriggerRef(attrContext));
        EmitEvent("entity_died", phase, attrContext, payload);

        _deathRecordedEntityIds.Add(entityId);
        _pendingKilledEntityIds.Remove(entityId);
        _deferredEntityDiedEvents.Remove(entityId);
    }

    private static void EmitMissingEntityDeathsAtBattleEnd(CombatState combatState, Player player)
    {
        var result = InferBattleResult(combatState, player);

        if (result == "defeat" &&
            _entityIds.TryGetValue(player.Creature, out var playerEntityId) &&
            player.Creature.CurrentHp <= 0)
        {
            EmitEntityDiedIfNeeded(
                playerEntityId,
                "battle_end",
                null,
                "damage");
        }

        if (result != "victory")
            return;

        var aliveEnemyIds = new HashSet<string>(
            GameStateReader.GetAliveEnemies(combatState)
                .Where(enemy => _entityIds.TryGetValue(enemy, out _))
                .Select(enemy => _entityIds[enemy]));

        foreach (var (creature, entityId) in _entityIds)
        {
            if (ReferenceEquals(creature, player.Creature))
                continue;
            if (_deathRecordedEntityIds.Contains(entityId))
                continue;
            if (_removedEntityIds.Contains(entityId))
                continue;
            if (aliveEnemyIds.Contains(entityId))
                continue;

            EmitEntityDiedIfNeeded(
                entityId,
                "battle_end",
                null,
                "battle_end_cleanup");
        }
    }

    private static void RefreshEntityState(CombatState combatState, Player player)
    {
        if (_entityIds.TryGetValue(player.Creature, out var pid))
        {
            try
            {
                _prevHp[pid] = player.Creature.CurrentHp;
                _prevBlock[pid] = player.Creature.Block;
            }
            catch (Exception ex)
            {
                DebugFileLogger.Error(nameof(BattleLogger) + ".RefreshEntityState",
                    $"Failed to refresh player state. entity_id={pid}", ex);
            }
        }

        foreach (var enemy in GameStateReader.GetAllEnemies(combatState))
        {
            if (!_entityIds.TryGetValue(enemy, out var eid))
                continue;

            try
            {
                _prevHp[eid] = enemy.CurrentHp;
                _prevBlock[eid] = enemy.Block;
            }
            catch (Exception ex)
            {
                DebugFileLogger.Error(nameof(BattleLogger) + ".RefreshEntityState",
                    $"Failed to refresh enemy state. entity_id={eid}", ex);
            }
        }
    }

    private static void DetectEntityRosterChanges(CombatState combatState)
    {
        var currentEnemies = GameStateReader.GetAllEnemies(combatState);
        var currentCreatures = new HashSet<Creature>();

        foreach (var enemy in currentEnemies)
        {
            currentCreatures.Add(enemy);

            if (_entityIds.ContainsKey(enemy))
                continue;

            _enemyIdCounter++;
            var newEntityId = $"enemy:{_enemyIdCounter}";
            _entityIds[enemy] = newEntityId;

            var info = GameStateReader.GetEntityInfo(enemy, "enemy");
            var attrContext = GetLiveAttributionContext();
            var spawnPayload = new Dictionary<string, object?>
            {
                ["entity_id"] = newEntityId,
                ["side"] = "enemy",
                ["reason"] = "mid_battle_roster_add",
                ["entity_def_id"] = info.DefId,
                ["name"] = info.Name,
                ["current_hp"] = info.CurrentHp,
                ["max_hp"] = info.MaxHp,
                ["block"] = info.Block,
            };
            AppendTriggerField(spawnPayload, ResolveEntityTruthTriggerRef(attrContext));
            EmitEvent("entity_spawned", _turnIndex, _phase, attrContext, spawnPayload);

            _prevHp[newEntityId] = info.CurrentHp;
            _prevBlock[newEntityId] = info.Block;

            SyncInitialPowerState(enemy);
            SyncVisibleIntent(enemy);

            MarkPendingSnapshotRelevantChange("entity_spawned");

            DebugFileLogger.Log(nameof(BattleLogger) + ".DetectEntityRosterChanges",
                $"Mid-battle entity spawned. entity_id={newEntityId}, def_id={info.DefId}");
        }

        foreach (var (creature, entityId) in _entityIds.ToList())
        {
            if (entityId == _playerEntityId)
                continue;
            if (_removedEntityIds.Contains(entityId))
                continue;
            if (_deathRecordedEntityIds.Contains(entityId))
                continue;
            if (ShouldDeferObservedStateEvent(entityId) || ShouldSuppressEntityRemovedForPendingKill(entityId))
                continue;
            if (currentCreatures.Contains(creature))
            {
                _entityAbsenceFrames.Remove(entityId);
                continue;
            }

            var frames = _entityAbsenceFrames.TryGetValue(entityId, out var f) ? f + 1 : 1;
            _entityAbsenceFrames[entityId] = frames;

            if (frames < EntityRemovalStableFrames)
                continue;

            var attrContext = GetLiveAttributionContext();
            var removedPayload = new Dictionary<string, object?>
            {
                ["entity_id"] = entityId,
                ["reason"] = "roster_absent",
            };
            AppendTriggerField(removedPayload, ResolveEntityTruthTriggerRef(attrContext));
            EmitEvent("entity_removed", _turnIndex, _phase, attrContext, removedPayload);

            _removedEntityIds.Add(entityId);
            _entityAbsenceFrames.Remove(entityId);

            _visibleIntentsByEntityId.Remove(entityId);
            _trackedPowersByEntityId.Remove(entityId);
            _prevHp.Remove(entityId);
            _prevBlock.Remove(entityId);

            MarkPendingSnapshotRelevantChange("entity_removed");

            DebugFileLogger.Log(nameof(BattleLogger) + ".DetectEntityRosterChanges",
                $"Entity removed from roster. entity_id={entityId}, absence_frames={frames}");
        }
    }

    private static Dictionary<string, TrackedPowerState> GetTrackedPowerStateMap(string entityId)
    {
        if (!_trackedPowersByEntityId.TryGetValue(entityId, out var trackedState))
        {
            trackedState = new Dictionary<string, TrackedPowerState>(StringComparer.Ordinal);
            _trackedPowersByEntityId[entityId] = trackedState;
        }

        return trackedState;
    }

    private static Dictionary<string, TrackedPowerState> CaptureVisiblePowerState(Creature creature)
    {
        var result = new Dictionary<string, TrackedPowerState>(StringComparer.Ordinal);
        foreach (var power in GameStateReader.ReadPowers(creature))
        {
            result[power.PowerId] = new TrackedPowerState
            {
                PowerId = power.PowerId,
                Name = string.IsNullOrWhiteSpace(power.Name) ? null : power.Name,
                Stacks = power.Stacks,
            };
        }

        return result;
    }

    private static TrackedPowerState CaptureCurrentPowerState(
        PowerModel power,
        Dictionary<string, TrackedPowerState> visibleState)
    {
        var powerId = GetPowerId(power);
        if (visibleState.TryGetValue(powerId, out var visiblePowerState))
        {
            return CloneTrackedPowerState(visiblePowerState);
        }

        return new TrackedPowerState
        {
            PowerId = powerId,
            Name = GetPowerName(power),
            Stacks = power.Amount,
        };
    }

    private static TrackedPowerState CloneTrackedPowerState(TrackedPowerState powerState)
    {
        return new TrackedPowerState
        {
            PowerId = powerState.PowerId,
            Name = powerState.Name,
            Stacks = powerState.Stacks,
        };
    }

    private static void EmitPowerAppliedEvent(
        string entityId,
        TrackedPowerState powerState,
        AttributionContext? attributionContext,
        Dictionary<string, object?>? applierRef,
        Dictionary<string, object?>? triggerRef,
        EventDispatchMode dispatchMode = EventDispatchMode.PublicOnly)
    {
        var payload = new Dictionary<string, object?>
        {
            ["target_entity_id"] = entityId,
            ["power_id"] = powerState.PowerId,
            ["power_name"] = powerState.Name,
            ["stacks"] = powerState.Stacks,
        };

        AppendPowerTruthFields(payload, applierRef, triggerRef);
        EmitEvent("power_applied", _phase, attributionContext, payload, dispatchMode: dispatchMode);
    }

    private static void EmitPowerStacksChangedEvent(
        string entityId,
        TrackedPowerState previousPowerState,
        TrackedPowerState currentPowerState,
        AttributionContext? attributionContext,
        Dictionary<string, object?>? applierRef,
        Dictionary<string, object?>? triggerRef,
        EventDispatchMode dispatchMode = EventDispatchMode.PublicOnly)
    {
        var payload = new Dictionary<string, object?>
        {
            ["target_entity_id"] = entityId,
            ["power_id"] = currentPowerState.PowerId,
            ["power_name"] = currentPowerState.Name ?? previousPowerState.Name,
            ["old_stacks"] = previousPowerState.Stacks,
            ["new_stacks"] = currentPowerState.Stacks,
            ["delta"] = currentPowerState.Stacks - previousPowerState.Stacks,
        };

        AppendPowerTruthFields(payload, applierRef, triggerRef);
        EmitEvent("power_stacks_changed", _phase, attributionContext, payload, dispatchMode: dispatchMode);
    }

    private static void EmitPowerRemovedEvent(
        string entityId,
        TrackedPowerState powerState,
        AttributionContext? attributionContext,
        Dictionary<string, object?>? applierRef,
        Dictionary<string, object?>? triggerRef,
        EventDispatchMode dispatchMode = EventDispatchMode.PublicOnly)
    {
        var payload = new Dictionary<string, object?>
        {
            ["target_entity_id"] = entityId,
            ["power_id"] = powerState.PowerId,
            ["power_name"] = powerState.Name,
            ["stacks"] = powerState.Stacks,
        };

        AppendPowerTruthFields(payload, applierRef, triggerRef);
        EmitEvent("power_removed", _phase, attributionContext, payload, dispatchMode: dispatchMode);
    }

    private static void EmitBlockBrokenEvent(
        string entityId,
        int oldBlock,
        int newBlock,
        string phase,
        AttributionContext? attributionContext,
        Dictionary<string, object?>? triggerRef,
        EventDispatchMode dispatchMode = EventDispatchMode.PublicOnly)
    {
        var payload = new Dictionary<string, object?>
        {
            ["entity_id"] = entityId,
            ["old_block"] = oldBlock,
            ["new_block"] = newBlock,
        };

        AppendTriggerField(payload, triggerRef);
        EmitEvent("block_broken", phase, attributionContext, payload, dispatchMode: dispatchMode);
    }

    private static void EmitBlockClearedEvent(
        string entityId,
        int oldBlock,
        int newBlock,
        string phase,
        AttributionContext? attributionContext,
        Dictionary<string, object?>? triggerRef,
        EventDispatchMode dispatchMode = EventDispatchMode.PublicOnly)
    {
        var payload = new Dictionary<string, object?>
        {
            ["entity_id"] = entityId,
            ["old_block"] = oldBlock,
            ["new_block"] = newBlock,
        };

        AppendTriggerField(payload, triggerRef);
        EmitEvent("block_cleared", phase, attributionContext, payload, dispatchMode: dispatchMode);
    }

    private static void EmitBlockClearPreventedEvent(
        string entityId,
        int retainedBlock,
        string phase,
        AttributionContext? attributionContext,
        Dictionary<string, object?> preventerRef,
        EventDispatchMode dispatchMode = EventDispatchMode.PublicOnly)
    {
        var payload = new Dictionary<string, object?>
        {
            ["entity_id"] = entityId,
            ["retained_block"] = retainedBlock,
            ["preventer"] = preventerRef,
        };

        EmitEvent("block_clear_prevented", phase, attributionContext, payload, dispatchMode: dispatchMode);
    }

    private static AttributionContext? GetReliablePowerRemovalContext()
    {
        // Removal closeout is only allowed to inherit explicit card/potion roots.
        // Do not treat ambient enemy_action as formal removal provenance.
        var activePlay = GetActivePlayContext();
        if (activePlay != null)
        {
            return ToAttributionContext(activePlay);
        }

        if (_activePotionContext != null && _phase == "player_action")
        {
            return ToAttributionContext(_activePotionContext);
        }

        return null;
    }

    private static void AppendPowerTruthFields(
        Dictionary<string, object?> payload,
        Dictionary<string, object?>? applierRef,
        Dictionary<string, object?>? triggerRef)
    {
        if (applierRef != null)
        {
            payload["applier"] = applierRef;
        }

        if (triggerRef != null)
        {
            payload["trigger"] = triggerRef;
        }
    }

    private static void AppendTriggerField(
        Dictionary<string, object?> payload,
        Dictionary<string, object?>? triggerRef)
    {
        if (triggerRef != null)
        {
            payload["trigger"] = triggerRef;
        }
    }

    private static PendingPowerRemovalTruth? ResolvePendingPowerRemovalTruth(PowerModel power)
    {
        return _pendingPowerRemovalTruthByPower.TryGetValue(power, out var pendingTruth)
            ? pendingTruth
            : null;
    }

    private static Dictionary<string, object?>? BuildPowerApplierRef(Creature? applier)
    {
        var applierId = ResolveEntityId(applier);
        if (string.IsNullOrEmpty(applierId))
        {
            return null;
        }

        return new Dictionary<string, object?>
        {
            ["kind"] = "entity",
            ["ref"] = applierId,
            ["entity_id"] = applierId,
        };
    }

    private static Dictionary<string, object?>? ResolvePowerTriggerRef(CardModel? cardSource)
    {
        var retainedScopedSource = EnsureStack(_powerTruthTriggerScopeStack).Count > 0
            ? EnsureStack(_powerTruthTriggerScopeStack).Peek()
            : null;
        if (retainedScopedSource != null)
        {
            return SanitizePowerTruthRef(retainedScopedSource);
        }

        var scopedSource = EnsureStack(_truthSourceScopeStack).Count > 0
            ? EnsureStack(_truthSourceScopeStack).Peek()
            : null;
        if (scopedSource != null)
        {
            return SanitizePowerTruthRef(DescribeSource(scopedSource));
        }

        if (cardSource != null)
        {
            return SanitizePowerTruthRef(DescribeSource(cardSource));
        }

        var resolution = GetActiveTruthResolutionSnapshot();
        if (resolution?.RootSource != null)
        {
            return SanitizePowerTruthRef(resolution.RootSource);
        }

        return ResolveAttributionContextTriggerRef(GetAttributionContext(), includeEnemyMove: false);
    }

    private static Dictionary<string, object?>? ResolvePowerRemovalTriggerRef()
    {
        var scopedSource = EnsureStack(_truthSourceScopeStack).Count > 0
            ? EnsureStack(_truthSourceScopeStack).Peek()
            : null;
        if (scopedSource != null)
        {
            return SanitizePowerTruthRef(DescribeSource(scopedSource));
        }

        var activePlay = GetActivePlayContext();
        if (activePlay != null)
        {
            return SanitizePowerTruthRef(DescribePlayResolutionSource(activePlay));
        }

        if (_activePotionContext != null && _phase == "player_action")
        {
            return SanitizePowerTruthRef(DescribePotionResolutionSource(_activePotionContext));
        }

        return null;
    }

    private static Stack<PendingBlockTruthSample> GetPendingBlockTruthStack(Creature creature)
    {
        if (!_pendingBlockTruthByCreature.TryGetValue(creature, out var sampleStack))
        {
            sampleStack = new Stack<PendingBlockTruthSample>();
            _pendingBlockTruthByCreature[creature] = sampleStack;
        }

        return sampleStack;
    }

    private static bool HasPendingBlockTruthSample(Creature creature)
    {
        return _pendingBlockTruthByCreature.TryGetValue(creature, out var sampleStack) &&
               sampleStack.Count > 0;
    }

    private static PendingBlockTruthSample? PeekPendingBlockTruthSample(Creature creature)
    {
        if (!_pendingBlockTruthByCreature.TryGetValue(creature, out var sampleStack) || sampleStack.Count == 0)
        {
            return null;
        }

        return sampleStack.Peek();
    }

    private static PendingBlockTruthSample? PopPendingBlockTruthSample(Creature creature)
    {
        if (!_pendingBlockTruthByCreature.TryGetValue(creature, out var sampleStack) || sampleStack.Count == 0)
        {
            return null;
        }

        var sample = sampleStack.Pop();
        if (sampleStack.Count == 0)
        {
            _pendingBlockTruthByCreature.Remove(creature);
        }

        return sample;
    }

    private static List<object?> BuildBlockGainStepPayloads(
        PendingBlockTruthSample sample,
        decimal baseAmount,
        decimal modifiedAmount,
        decimal finalGainAmount)
    {
        var payloads = new List<object?>();
        if (sample.ModifierSteps.Count > 0)
        {
            payloads.AddRange(sample.ModifierSteps.Select(BuildBlockGainStepPayload));
        }
        else
        {
            payloads.Add(BuildBlockGainStepPayload(new BlockGainStepRecord
            {
                Stage = "base",
                Operation = "base",
                After = baseAmount,
            }));

            if (modifiedAmount != baseAmount)
            {
                payloads.Add(BuildBlockGainStepPayload(new BlockGainStepRecord
                {
                    Stage = "block_adjustment",
                    Operation = "adjust",
                    Before = baseAmount,
                    After = modifiedAmount,
                    Delta = modifiedAmount - baseAmount,
                    ModifierRef = BuildUnknownSource("modify_block_steps_missing"),
                    IsUnknown = true,
                    UnknownReason = "modify_block_steps_missing",
                }));
            }
        }

        payloads.Add(BuildBlockGainStepPayload(new BlockGainStepRecord
        {
            Stage = "settled",
            Operation = "settle",
            Before = modifiedAmount,
            After = finalGainAmount,
            Delta = finalGainAmount - modifiedAmount,
        }));

        return payloads;
    }

    private static Dictionary<string, object?> BuildBlockGainStepPayload(BlockGainStepRecord step)
    {
        return new Dictionary<string, object?>
        {
            ["stage"] = step.Stage,
            ["operation"] = step.Operation,
            ["before"] = step.Before,
            ["after"] = step.After,
            ["delta"] = step.Delta,
            ["modifier_ref"] = step.ModifierRef,
            ["is_unknown"] = step.IsUnknown ? true : null,
            ["unknown_reason"] = step.UnknownReason,
        };
    }

    private static void ReplayModifyBlockSteps(
        List<BlockGainStepRecord> sink,
        CombatState combatState,
        Creature target,
        decimal block,
        ValueProp props,
        CardModel? cardSource,
        CardPlay? cardPlay)
    {
        sink.Add(new BlockGainStepRecord
        {
            Stage = "base",
            Operation = "base",
            After = block,
        });

        var current = block;
        if (cardSource?.Enchantment != null)
        {
            var additiveDelta = cardSource.Enchantment.EnchantBlockAdditive(current, props);
            if (additiveDelta != 0m)
            {
                var after = current + additiveDelta;
                sink.Add(new BlockGainStepRecord
                {
                    Stage = "block_additive",
                    Operation = "additive",
                    Before = current,
                    After = after,
                    Delta = after - current,
                    ModifierRef = SanitizePowerTruthRef(DescribeSource(cardSource.Enchantment)),
                });
                current = after;
            }

            var multiplier = cardSource.Enchantment.EnchantBlockMultiplicative(current, props);
            if (multiplier != 1m)
            {
                var after = current * multiplier;
                sink.Add(new BlockGainStepRecord
                {
                    Stage = "block_multiplicative",
                    Operation = "multiplicative",
                    Before = current,
                    After = after,
                    Delta = after - current,
                    ModifierRef = SanitizePowerTruthRef(DescribeSource(cardSource.Enchantment)),
                });
                current = after;
            }
        }

        foreach (var listener in combatState.IterateHookListeners())
        {
            var delta = listener.ModifyBlockAdditive(target, current, props, cardSource, cardPlay);
            if (delta == 0m)
            {
                continue;
            }

            var after = current + delta;
            sink.Add(new BlockGainStepRecord
            {
                Stage = "block_additive",
                Operation = "additive",
                Before = current,
                After = after,
                Delta = after - current,
                ModifierRef = SanitizePowerTruthRef(DescribeSource(listener)),
            });
            current = after;
        }

        foreach (var listener in combatState.IterateHookListeners())
        {
            var multiplier = listener.ModifyBlockMultiplicative(target, current, props, cardSource, cardPlay);
            if (multiplier == 1m)
            {
                continue;
            }

            var after = current * multiplier;
            sink.Add(new BlockGainStepRecord
            {
                Stage = "block_multiplicative",
                Operation = "multiplicative",
                Before = current,
                After = after,
                Delta = after - current,
                ModifierRef = SanitizePowerTruthRef(DescribeSource(listener)),
            });
            current = after;
        }

        if (current < 0m)
        {
            sink.Add(new BlockGainStepRecord
            {
                Stage = "block_floor",
                Operation = "floor",
                Before = current,
                After = 0m,
                Delta = -current,
            });
        }
    }

    private static Stack<PendingBlockClearSample> GetPendingBlockClearStack(Creature creature)
    {
        if (!_pendingBlockClearByCreature.TryGetValue(creature, out var sampleStack))
        {
            sampleStack = new Stack<PendingBlockClearSample>();
            _pendingBlockClearByCreature[creature] = sampleStack;
        }

        return sampleStack;
    }

    private static PendingBlockClearSample? PopPendingBlockClearSample(Creature creature)
    {
        if (!_pendingBlockClearByCreature.TryGetValue(creature, out var sampleStack) || sampleStack.Count == 0)
        {
            return null;
        }

        var sample = sampleStack.Pop();
        if (sampleStack.Count == 0)
        {
            _pendingBlockClearByCreature.Remove(creature);
        }

        return sample;
    }

    private static Dictionary<string, object?>? ResolveBlockTriggerRef(
        Creature creature,
        ValueProp props,
        CardModel? cardSource,
        AttributionContext? eventContext)
    {
        var scopedSource = EnsureStack(_truthSourceScopeStack).Count > 0
            ? EnsureStack(_truthSourceScopeStack).Peek()
            : null;
        if (scopedSource != null)
        {
            return SanitizePowerTruthRef(DescribeSource(scopedSource));
        }

        if (cardSource != null)
        {
            return SanitizePowerTruthRef(DescribeSource(cardSource));
        }

        if (_activeEnemyActionContext != null &&
            ReferenceEquals(_activeEnemyActionContext.ActorCreature, creature) &&
            props.HasFlag(ValueProp.Move))
        {
            return SanitizePowerTruthRef(DescribeEnemyMoveSource(_activeEnemyActionContext));
        }

        var resolution = GetActiveTruthResolutionSnapshot();
        if (resolution?.RootSource != null)
        {
            return SanitizePowerTruthRef(resolution.RootSource);
        }

        return ResolveAttributionContextTriggerRef(eventContext, includeEnemyMove: true);
    }

    private static Dictionary<string, object?>? ResolveCardTruthTriggerRef()
    {
        var scopedSource = EnsureStack(_truthSourceScopeStack).Count > 0
            ? EnsureStack(_truthSourceScopeStack).Peek()
            : null;
        if (scopedSource != null)
        {
            return SanitizePowerTruthRef(DescribeSource(scopedSource));
        }

        var resolution = GetActiveTruthResolutionSnapshot();
        if (resolution?.RootSource != null)
        {
            return SanitizePowerTruthRef(resolution.RootSource);
        }

        return ResolveAttributionContextTriggerRef(GetLiveAttributionContext(), includeEnemyMove: true);
    }

    private static Dictionary<string, object?>? ResolveCardModifiedTriggerRef()
    {
        var scopedSource = EnsureStack(_truthSourceScopeStack).Count > 0
            ? EnsureStack(_truthSourceScopeStack).Peek()
            : null;
        if (scopedSource != null)
        {
            return SanitizePowerTruthRef(DescribeSource(scopedSource));
        }

        var resolution = GetActiveTruthResolutionSnapshot();
        if (resolution?.RootSource != null)
        {
            return SanitizePowerTruthRef(resolution.RootSource);
        }

        return ResolveAttributionContextTriggerRef(GetAttributionContext(), includeEnemyMove: true);
    }

    private static Dictionary<string, object?>? ResolveCurrentTruthTriggerRef(bool includeEnemyMove = true)
    {
        var scopedSource = EnsureStack(_truthSourceScopeStack).Count > 0
            ? EnsureStack(_truthSourceScopeStack).Peek()
            : null;
        if (scopedSource != null)
        {
            return SanitizePowerTruthRef(DescribeSource(scopedSource));
        }

        var resolution = GetActiveTruthResolutionSnapshot();
        if (resolution?.RootSource != null)
        {
            return SanitizePowerTruthRef(resolution.RootSource);
        }

        return ResolveAttributionContextTriggerRef(GetLiveAttributionContext(), includeEnemyMove);
    }

    private static Dictionary<string, object?>? ResolveResourceTruthTriggerRef()
    {
        return ResolveCurrentTruthTriggerRef(includeEnemyMove: true);
    }

    private static string ResolveResourceTruthReason(Dictionary<string, object?>? triggerRef)
    {
        if (triggerRef == null || !triggerRef.TryGetValue("kind", out var kindValue) || kindValue is not string kind)
        {
            return "unknown";
        }

        return kind switch
        {
            "card_instance" => "card_play",
            "potion_instance" => "potion_use",
            "enemy_move" => "enemy_action",
            "orb_instance" => "triggered_effect",
            "power_instance" => "triggered_effect",
            "relic" => "triggered_effect",
            "generic_model" => "triggered_effect",
            _ => "unknown",
        };
    }

    private static Dictionary<string, object?>? ResolveCardPlayTriggerRef(PlayContext context)
    {
        return SanitizePowerTruthRef(DescribePlayResolutionSource(context));
    }

    private static Dictionary<string, object?>? ResolveEntityTruthTriggerRef(AttributionContext? attributionContext)
    {
        return ResolveAttributionContextTriggerRef(attributionContext, includeEnemyMove: true);
    }

    private static Dictionary<string, object?>? ResolveEntityReviveTriggerRef(Creature creature, AttributionContext? attributionContext)
    {
        if (_pendingPreventedDeathReviverRefsByCreature.TryGetValue(creature, out var pendingReviverStack) &&
            pendingReviverStack.Count > 0)
        {
            return pendingReviverStack.Peek();
        }

        var scopedSources = EnsureStack(_truthSourceScopeStack);
        foreach (var scopedSource in scopedSources)
        {
            var source = SanitizePowerTruthRef(DescribeSource(scopedSource));
            if (IsDirectReviverRef(source))
            {
                return source;
            }
        }

        return ResolveEntityTruthTriggerRef(attributionContext);
    }

    public static bool PushPreventedDeathReviver(Creature creature, AbstractModel preventer)
    {
        var preventerRef = SanitizePowerTruthRef(DescribeSource(preventer));
        if (preventerRef == null)
        {
            return false;
        }

        if (!_pendingPreventedDeathReviverRefsByCreature.TryGetValue(creature, out var reviverStack))
        {
            reviverStack = new Stack<Dictionary<string, object?>?>();
            _pendingPreventedDeathReviverRefsByCreature[creature] = reviverStack;
        }

        reviverStack.Push(preventerRef);
        return true;
    }

    public static void PopPreventedDeathReviver(Creature creature)
    {
        if (!_pendingPreventedDeathReviverRefsByCreature.TryGetValue(creature, out var reviverStack) ||
            reviverStack.Count == 0)
        {
            return;
        }

        reviverStack.Pop();
        if (reviverStack.Count == 0)
        {
            _pendingPreventedDeathReviverRefsByCreature.Remove(creature);
        }
    }

    private static bool IsDirectReviverRef(Dictionary<string, object?>? source)
    {
        if (source == null ||
            !source.TryGetValue("kind", out var kindValue) ||
            kindValue is not string kind)
        {
            return false;
        }

        return string.Equals(kind, "relic", StringComparison.Ordinal) ||
               string.Equals(kind, "potion_instance", StringComparison.Ordinal) ||
               string.Equals(kind, "power_instance", StringComparison.Ordinal) ||
               string.Equals(kind, "card_instance", StringComparison.Ordinal) ||
               string.Equals(kind, "generic_model", StringComparison.Ordinal);
    }

    private static Dictionary<string, object?> BuildCardModifiedChanges(
        CardTruthStateSnapshot oldState,
        CardTruthStateSnapshot newState,
        CardTruthDiffFields allowedFields)
    {
        var changes = new Dictionary<string, object?>();

        if (allowedFields.HasFlag(CardTruthDiffFields.Cost) && oldState.Cost != newState.Cost)
        {
            changes["cost"] = new Dictionary<string, object?>
            {
                ["old"] = oldState.Cost,
                ["new"] = newState.Cost,
            };
        }

        if (allowedFields.HasFlag(CardTruthDiffFields.StarCost) && oldState.StarCost != newState.StarCost)
        {
            changes["star_cost"] = new Dictionary<string, object?>
            {
                ["old"] = oldState.StarCost,
                ["new"] = newState.StarCost,
            };
        }

        if (allowedFields.HasFlag(CardTruthDiffFields.Upgrade) &&
            (oldState.CurrentUpgradeLevel != newState.CurrentUpgradeLevel))
        {
            changes["upgraded"] = new Dictionary<string, object?>
            {
                ["old"] = oldState.CurrentUpgradeLevel > 0,
                ["new"] = newState.CurrentUpgradeLevel > 0,
            };
            changes["upgrade_level"] = new Dictionary<string, object?>
            {
                ["old"] = oldState.CurrentUpgradeLevel,
                ["new"] = newState.CurrentUpgradeLevel,
            };
        }

        if (allowedFields.HasFlag(CardTruthDiffFields.ReplayCount) &&
            oldState.ReplayCount != newState.ReplayCount)
        {
            changes["replay_count"] = new Dictionary<string, object?>
            {
                ["old"] = oldState.ReplayCount,
                ["new"] = newState.ReplayCount,
            };
        }

        if (allowedFields.HasFlag(CardTruthDiffFields.Keywords) &&
            !oldState.Keywords.SequenceEqual(newState.Keywords, StringComparer.Ordinal))
        {
            changes["keywords"] = new Dictionary<string, object?>
            {
                ["old"] = new List<string>(oldState.Keywords),
                ["new"] = new List<string>(newState.Keywords),
            };
        }

        if (allowedFields.HasFlag(CardTruthDiffFields.VisibleFlags) &&
            !VisibleFlagsEqual(oldState.VisibleFlags, newState.VisibleFlags))
        {
            changes["visible_flags"] = new Dictionary<string, object?>
            {
                ["old"] = BuildVisibleFlagsPayload(oldState.VisibleFlags, includeFalseValues: true),
                ["new"] = BuildVisibleFlagsPayload(newState.VisibleFlags, includeFalseValues: true),
            };
        }

        if (allowedFields.HasFlag(CardTruthDiffFields.Enchantment) &&
            !EnchantmentSnapshotsEqual(oldState.Enchantment, newState.Enchantment))
        {
            changes["enchantment"] = new Dictionary<string, object?>
            {
                ["old"] = BuildEnchantmentPayload(oldState.Enchantment),
                ["new"] = BuildEnchantmentPayload(newState.Enchantment),
            };
        }

        if (allowedFields.HasFlag(CardTruthDiffFields.Affliction) &&
            !AfflictionSnapshotsEqual(oldState.Affliction, newState.Affliction))
        {
            changes["affliction"] = new Dictionary<string, object?>
            {
                ["old"] = BuildAfflictionPayload(oldState.Affliction),
                ["new"] = BuildAfflictionPayload(newState.Affliction),
            };
        }

        if (allowedFields.HasFlag(CardTruthDiffFields.DynamicValues) &&
            !DynamicValuesEqual(oldState.DynamicValues, newState.DynamicValues))
        {
            changes["dynamic_values"] = new Dictionary<string, object?>
            {
                ["old"] = BuildDynamicValuesPayload(oldState.DynamicValues),
                ["new"] = BuildDynamicValuesPayload(newState.DynamicValues),
            };
        }

        return changes;
    }

    private static void AppendCardVisibleStatePayload(
        Dictionary<string, object?> payload,
        CardTruthStateSnapshot state,
        bool includeCost = true,
        bool includeUpgradeLevel = true)
    {
        if (includeCost)
        {
            payload["cost"] = state.Cost;
        }

        if (includeUpgradeLevel)
        {
            payload["current_upgrade_level"] = state.CurrentUpgradeLevel;
        }

        if (state.StarCost.HasValue)
        {
            payload["star_cost"] = state.StarCost.Value;
        }

        if (state.ReplayCount > 0)
        {
            payload["replay_count"] = state.ReplayCount;
        }

        if (state.Keywords.Count > 0)
        {
            payload["keywords"] = new List<string>(state.Keywords);
        }

        var visibleFlags = BuildVisibleFlagsPayload(state.VisibleFlags, includeFalseValues: false);
        if (visibleFlags.Count > 0)
        {
            payload["visible_flags"] = visibleFlags;
        }

        var enchantment = BuildEnchantmentPayload(state.Enchantment);
        if (enchantment != null)
        {
            payload["enchantment"] = enchantment;
        }

        var affliction = BuildAfflictionPayload(state.Affliction);
        if (affliction != null)
        {
            payload["affliction"] = affliction;
        }

        if (state.DynamicValues.Count > 0)
        {
            payload["dynamic_values"] = BuildDynamicValuesPayload(state.DynamicValues);
        }
    }

    private static Dictionary<string, object?> BuildVisibleFlagsPayload(
        CardVisibleFlagsSnapshot flags,
        bool includeFalseValues)
    {
        var payload = new Dictionary<string, object?>();
        if (includeFalseValues || flags.RetainThisTurn)
        {
            payload["retain_this_turn"] = flags.RetainThisTurn;
        }

        if (includeFalseValues || flags.SlyThisTurn)
        {
            payload["sly_this_turn"] = flags.SlyThisTurn;
        }

        return payload;
    }

    private static Dictionary<string, object?>? BuildEnchantmentPayload(CardEnchantmentSnapshot? snapshot)
    {
        if (snapshot == null)
        {
            return null;
        }

        return new Dictionary<string, object?>
        {
            ["enchantment_id"] = snapshot.EnchantmentId,
            ["name"] = snapshot.Name,
            ["amount"] = snapshot.Amount,
            ["status"] = snapshot.Status,
            ["display_amount"] = snapshot.DisplayAmount,
            ["show_amount"] = snapshot.ShowAmount,
        };
    }

    private static Dictionary<string, object?>? BuildAfflictionPayload(CardAfflictionSnapshot? snapshot)
    {
        if (snapshot == null)
        {
            return null;
        }

        return new Dictionary<string, object?>
        {
            ["affliction_id"] = snapshot.AfflictionId,
            ["name"] = snapshot.Name,
            ["amount"] = snapshot.Amount,
        };
    }

    private static Dictionary<string, object?> BuildDynamicValuesPayload(IReadOnlyDictionary<string, int> values)
    {
        return values.ToDictionary(
            entry => entry.Key,
            entry => (object?)entry.Value,
            StringComparer.Ordinal);
    }

    private static bool VisibleFlagsEqual(
        CardVisibleFlagsSnapshot left,
        CardVisibleFlagsSnapshot right)
    {
        return left.RetainThisTurn == right.RetainThisTurn &&
               left.SlyThisTurn == right.SlyThisTurn;
    }

    private static bool EnchantmentSnapshotsEqual(
        CardEnchantmentSnapshot? left,
        CardEnchantmentSnapshot? right)
    {
        if (left == null || right == null)
        {
            return left == right;
        }

        return string.Equals(left.EnchantmentId, right.EnchantmentId, StringComparison.Ordinal) &&
               string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
               left.Amount == right.Amount &&
               string.Equals(left.Status, right.Status, StringComparison.Ordinal) &&
               left.DisplayAmount == right.DisplayAmount &&
               left.ShowAmount == right.ShowAmount;
    }

    private static bool AfflictionSnapshotsEqual(
        CardAfflictionSnapshot? left,
        CardAfflictionSnapshot? right)
    {
        if (left == null || right == null)
        {
            return left == right;
        }

        return string.Equals(left.AfflictionId, right.AfflictionId, StringComparison.Ordinal) &&
               string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
               left.Amount == right.Amount;
    }

    private static bool DynamicValuesEqual(
        IReadOnlyDictionary<string, int> left,
        IReadOnlyDictionary<string, int> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (key, leftValue) in left)
        {
            if (!right.TryGetValue(key, out var rightValue) || leftValue != rightValue)
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, object?>? ResolveAttributionContextTriggerRef(
        AttributionContext? attributionContext,
        bool includeEnemyMove)
    {
        if (attributionContext == null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(attributionContext.CardId))
        {
            return new Dictionary<string, object?>
            {
                ["kind"] = "card_instance",
                ["ref"] = attributionContext.CardId,
                ["card_instance_id"] = attributionContext.CardId,
                ["owner_entity_id"] = attributionContext.SourceEntityId,
            };
        }

        if (!string.IsNullOrEmpty(attributionContext.PotionId))
        {
            return new Dictionary<string, object?>
            {
                ["kind"] = "potion_instance",
                ["ref"] = attributionContext.PotionId,
                ["potion_instance_id"] = attributionContext.PotionId,
                ["owner_entity_id"] = attributionContext.SourceEntityId,
            };
        }

        if (includeEnemyMove && string.Equals(attributionContext.SourceKind, "enemy_action", StringComparison.Ordinal))
        {
            var moveId = attributionContext.MoveId ?? _activeEnemyActionContext?.MoveId ?? "unknown";
            return new Dictionary<string, object?>
            {
                ["kind"] = "enemy_move",
                ["ref"] = $"move:{attributionContext.SourceEntityId}:{moveId}",
                ["owner_entity_id"] = attributionContext.SourceEntityId,
                ["move_id"] = moveId,
            };
        }

        return null;
    }

    private static Dictionary<string, object?>? SanitizePowerTruthRef(Dictionary<string, object?>? source)
    {
        if (source == null)
        {
            return null;
        }

        var sanitized = new Dictionary<string, object?>(source, StringComparer.Ordinal);
        sanitized.Remove("origin");
        return sanitized;
    }

    private static List<object?> BuildSnapshotPowers(Creature creature)
    {
        return GameStateReader.ReadPowers(creature)
            .OrderBy(power => power.PowerId, StringComparer.Ordinal)
            .Select(power => (object?)new Dictionary<string, object?>
            {
                ["power_id"] = power.PowerId,
                ["power_name"] = string.IsNullOrWhiteSpace(power.Name) ? null : power.Name,
                ["stacks"] = power.Stacks,
            })
            .ToList();
    }

    private static void AppendPowerSignature(StringBuilder builder, Creature creature)
    {
        foreach (var power in GameStateReader.ReadPowers(creature).OrderBy(power => power.PowerId, StringComparer.Ordinal))
        {
            builder.Append("power:")
                .Append(power.PowerId)
                .Append('=')
                .Append(power.Stacks)
                .Append('|');
        }
    }

    private static string GetPowerId(PowerModel power)
    {
        var powerId = power.Id?.Entry;
        if (!string.IsNullOrEmpty(powerId))
        {
            return powerId;
        }

        var typeName = power.GetType().Name;
        if (typeName.EndsWith("Power", StringComparison.Ordinal))
        {
            typeName = typeName[..^"Power".Length];
        }

        return GameStateReader.ToSnakeCase(typeName);
    }

    private static string GetPowerName(PowerModel power)
    {
        try
        {
            var formatted = power.Title?.GetFormattedText();
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                return formatted;
            }
        }
        catch
        {
            // Fallback below.
        }

        var typeName = power.GetType().Name;
        return typeName.EndsWith("Power", StringComparison.Ordinal)
            ? typeName[..^"Power".Length]
            : typeName;
    }

    // ─── Event emission ────────────────────────────────────────────────────

    private static void EmitEvent(
        string eventType,
        Dictionary<string, object?> payload,
        EventDispatchMode dispatchMode = EventDispatchMode.PublicOnly)
    {
        EmitEvent(eventType, _turnIndex, _phase, (PlayContext?)null, payload, dispatchMode: dispatchMode);
    }

    private static int EmitEvent(
        string eventType,
        string phase,
        PlayContext? context,
        Dictionary<string, object?> payload,
        int? causeEventSeq = null,
        EventDispatchMode dispatchMode = EventDispatchMode.PublicOnly)
    {
        return EmitEvent(eventType, _turnIndex, phase, context, payload, causeEventSeq, dispatchMode);
    }

    private static int EmitEvent(
        string eventType,
        string phase,
        PotionContext? context,
        Dictionary<string, object?> payload,
        int? causeEventSeq = null,
        EventDispatchMode dispatchMode = EventDispatchMode.PublicOnly)
    {
        return EmitEvent(eventType, _turnIndex, phase, context, payload, causeEventSeq, dispatchMode);
    }

    private static int EmitEvent(
        string eventType,
        string phase,
        AttributionContext? context,
        Dictionary<string, object?> payload,
        int? causeEventSeq = null,
        EventDispatchMode dispatchMode = EventDispatchMode.PublicOnly)
    {
        return EmitEvent(eventType, _turnIndex, phase, context, payload, causeEventSeq, dispatchMode);
    }

    private static int EmitEvent(
        string eventType,
        int turnIndex,
        string phase,
        PlayContext? context,
        Dictionary<string, object?> payload,
        int? causeEventSeq = null,
        EventDispatchMode dispatchMode = EventDispatchMode.PublicOnly)
    {
        return EmitEvent(
            eventType,
            turnIndex,
            phase,
            context?.ResolutionId,
            context?.ParentResolutionId,
            context?.ResolutionDepth,
            causeEventSeq,
            payload,
            dispatchMode);
    }

    private static int EmitEvent(
        string eventType,
        int turnIndex,
        string phase,
        PotionContext? context,
        Dictionary<string, object?> payload,
        int? causeEventSeq = null,
        EventDispatchMode dispatchMode = EventDispatchMode.PublicOnly)
    {
        return EmitEvent(
            eventType,
            turnIndex,
            phase,
            context?.ResolutionId,
            null,
            null,
            causeEventSeq,
            payload,
            dispatchMode);
    }

    private static int EmitEvent(
        string eventType,
        int turnIndex,
        string phase,
        AttributionContext? context,
        Dictionary<string, object?> payload,
        int? causeEventSeq = null,
        EventDispatchMode dispatchMode = EventDispatchMode.PublicOnly)
    {
        return EmitEvent(
            eventType,
            turnIndex,
            phase,
            context?.ResolutionId,
            context?.ParentResolutionId,
            context?.ResolutionDepth,
            causeEventSeq,
            payload,
            dispatchMode);
    }

    private static int EmitEvent(
        string eventType,
        int turnIndex,
        string phase,
        string? resolutionId,
        string? parentResolutionId,
        int? resolutionDepth,
        int? causeEventSeq,
        Dictionary<string, object?> payload,
        EventDispatchMode dispatchMode = EventDispatchMode.PublicOnly)
    {
        _seq++;
        var evt = BuildEventEnvelope(
            _seq,
            eventType,
            turnIndex,
            phase,
            resolutionId,
            parentResolutionId,
            resolutionDepth,
            causeEventSeq,
            payload);

        var stageStart = StartDiagnosticsTimer();
        var json = JsonSerializer.Serialize(evt, JsonOpts);
        var eventLine = json + "\n";
        var writeSucceeded = AppendEventLine(eventLine);
        if (writeSucceeded)
        {
            _perfDiagnostics?.RecordEventWritten(eventType, Encoding.UTF8.GetByteCount(eventLine));
            _runtimeState?.InspectWrittenEvent(eventType, resolutionId, payload);
        }

        if (dispatchMode == EventDispatchMode.PublicAndShadowHook && _hookFirstShadowComparison != null)
        {
            if (writeSucceeded)
            {
                _hookFirstShadowComparison.RecordPublic(evt);
            }

            _hookFirstShadowComparison.RecordShadowCandidate(evt);
        }

        RecordDiagnosticsStage(RecorderPerfDiagnostics.StageNames.EventSerializeWrite, stageStart);
        return _seq;
    }

    private static Dictionary<string, object?> BuildEventEnvelope(
        int seq,
        string eventType,
        int turnIndex,
        string phase,
        string? resolutionId,
        string? parentResolutionId,
        int? resolutionDepth,
        int? causeEventSeq,
        Dictionary<string, object?> payload)
    {
        return new Dictionary<string, object?>
        {
            ["seq"] = seq,
            ["event_type"] = eventType,
            ["turn_index"] = turnIndex,
            ["phase"] = phase,
            ["cause_event_seq"] = causeEventSeq,
            ["resolution_id"] = resolutionId,
            ["parent_resolution_id"] = parentResolutionId,
            ["resolution_depth"] = resolutionDepth,
            ["payload"] = payload,
        };
    }

    // ─── File I/O ──────────────────────────────────────────────────────────

    private static bool AppendEventLine(string jsonLine)
    {
        try
        {
            File.AppendAllText(_eventsPath!, jsonLine);
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2CombatRecorder] Failed to write event: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".AppendEventLine", ex);
            _runtimeState?.RecordEventWriteFailure(
                $"Failed to append an event record to events.ndjson: {ex.Message}");
            return false;
        }
    }

    public static void OnVisibleIntentUpdated(Creature creature)
    {
        if (!_active || !_initDone || !creature.IsMonster)
            return;

        try
        {
            SyncVisibleIntent(creature, EventDispatchMode.PublicAndShadowHook);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OnVisibleIntentUpdated failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnVisibleIntentUpdated", ex);
        }
    }

    private static void SyncAllVisibleEnemyIntents(CombatState combatState)
    {
        foreach (var enemy in GameStateReader.GetAllEnemies(combatState))
        {
            SyncVisibleIntent(enemy);
        }
    }

    private static void SyncVisibleIntent(
        Creature creature,
        EventDispatchMode dispatchMode = EventDispatchMode.PublicOnly)
    {
        if (!_entityIds.TryGetValue(creature, out var entityId))
            return;

        if (!GameStateReader.TryGetVisibleIntentInfo(creature, out var intent))
            return;

        if (_visibleIntentsByEntityId.TryGetValue(entityId, out var previousIntent) &&
            VisibleIntentEquals(previousIntent, intent))
        {
            return;
        }

        _visibleIntentsByEntityId[entityId] = intent;

        var payload = new Dictionary<string, object?>
        {
            ["entity_id"] = entityId,
            ["intent_id"] = intent.IntentId,
            ["intent_name"] = intent.IntentName,
        };

        if (intent.ProjectedDamage.HasValue)
        {
            payload["projected_damage"] = intent.ProjectedDamage.Value;
        }

        if (intent.ProjectedHits.HasValue)
        {
            payload["projected_hits"] = intent.ProjectedHits.Value;
        }

        EmitEvent("intent_changed", _phase, GetAttributionContext(), payload, dispatchMode: dispatchMode);
        MarkPendingSnapshotRelevantChange("intent_changed");
    }

    private static bool VisibleIntentEquals(GameStateReader.IntentInfo left, GameStateReader.IntentInfo right)
    {
        return left.IntentId == right.IntentId &&
               left.IntentName == right.IntentName &&
               left.ProjectedDamage == right.ProjectedDamage &&
               left.ProjectedHits == right.ProjectedHits;
    }

    private static Dictionary<string, object?> BuildIntentState(GameStateReader.IntentInfo intent)
    {
        var state = new Dictionary<string, object?>
        {
            ["intent_id"] = intent.IntentId,
            ["intent_name"] = intent.IntentName,
        };

        if (intent.ProjectedDamage.HasValue)
        {
            state["projected_damage"] = intent.ProjectedDamage.Value;
        }

        if (intent.ProjectedHits.HasValue)
        {
            state["projected_hits"] = intent.ProjectedHits.Value;
        }

        return state;
    }

    private static void RequestStabilizedSnapshot(bool requiresChange)
    {
        _pendingSnapshot = true;
        _pendingSnapshotStableFrames = 0;
        _pendingSnapshotAgeFrames = 0;
        _pendingSnapshotSignature = "";
        _pendingSnapshotNeedsProbe = !requiresChange;
        _pendingSnapshotRequiresChange = requiresChange;
        _pendingSnapshotSawChange = false;
    }

    private static void ResetPendingSnapshotState()
    {
        _pendingSnapshot = false;
        _pendingSnapshotStableFrames = 0;
        _pendingSnapshotAgeFrames = 0;
        _pendingSnapshotSignature = "";
        _pendingSnapshotNeedsProbe = false;
        _pendingSnapshotRequiresChange = false;
        _pendingSnapshotSawChange = false;
    }

    private static void MarkPendingSnapshotRelevantChange(string signalName)
    {
        if (!_pendingSnapshot)
            return;

        _pendingSnapshotSawChange = true;
        _pendingSnapshotNeedsProbe = true;
        _pendingSnapshotStableFrames = 0;

        DebugFileLogger.Log(nameof(BattleLogger) + ".MarkPendingSnapshotRelevantChange",
            $"Pending snapshot marked dirty. signal={signalName}, phase={_phase}, age_frames={_pendingSnapshotAgeFrames}");
    }

    private static void TryWritePendingSnapshot(CombatState combatState, Player player)
    {
        var stageStart = StartDiagnosticsTimer();
        var recordedStage = false;
        try
        {
            if (!_pendingSnapshot) return;

            _pendingSnapshotAgeFrames++;

            if (_pendingSnapshotRequiresChange && _phase != "turn_start")
            {
                ResetPendingSnapshotState();
                return;
            }

            if (_pendingSnapshotAgeFrames < PendingSnapshotMinFrames)
            {
                return;
            }

            var turnStartReady = false;
            if (_pendingSnapshotRequiresChange)
            {
                turnStartReady = IsTurnStartSnapshotReady(player.PlayerCombatState);
                if (!_pendingSnapshotSawChange && turnStartReady)
                {
                    // This preserves the existing turn-start fallback even if a
                    // supporting hook misses a signal on the settling path.
                    _pendingSnapshotSawChange = true;
                    _pendingSnapshotNeedsProbe = true;
                }
            }

            if (_pendingSnapshotRequiresChange && !_pendingSnapshotSawChange && _pendingSnapshotAgeFrames < PendingSnapshotForceFrames)
                return;
            if (_pendingSnapshotRequiresChange && !turnStartReady)
                return;
            if (!_pendingSnapshotNeedsProbe)
                return;

            var signature = BuildSnapshotStabilitySignature(combatState, player);
            if (signature == _pendingSnapshotSignature)
            {
                _pendingSnapshotStableFrames++;
            }
            else
            {
                _pendingSnapshotSignature = signature;
                _pendingSnapshotStableFrames = 0;
            }

            if (_pendingSnapshotStableFrames < 2)
                return;

            RecordDiagnosticsStage(RecorderPerfDiagnostics.StageNames.SnapshotStabilityCheck, stageStart);
            recordedStage = true;
            RefreshEntityState(combatState, player);
            WriteSnapshot();
            ResetPendingSnapshotState();
        }
        finally
        {
            if (!recordedStage)
            {
                RecordDiagnosticsStage(RecorderPerfDiagnostics.StageNames.SnapshotStabilityCheck, stageStart);
            }
        }
    }

    private static void FlushPendingSnapshotNow(CombatState combatState, Player player)
    {
        if (!_pendingSnapshot) return;

        RefreshEntityState(combatState, player);
        WriteSnapshot();
        ResetPendingSnapshotState();
    }

    private static string BuildSnapshotStabilitySignature(CombatState combatState, Player player)
    {
        var builder = new StringBuilder();
        builder.Append(_turnIndex).Append('|').Append(_phase).Append('|');
        builder.Append(player.PlayerCombatState?.Energy ?? 0).Append('|');
        builder.Append(player.PlayerCombatState?.Stars ?? 0).Append('|');
        builder.Append(player.Creature.CurrentHp).Append(':').Append(player.Creature.Block).Append('|');
        AppendOrbSignature(builder);
        AppendRelicSignature(builder);
        AppendPowerSignature(builder, player.Creature);

        foreach (var enemy in GameStateReader.GetAllEnemies(combatState))
        {
            if (_entityIds.TryGetValue(enemy, out var entityId))
            {
                builder.Append(entityId)
                    .Append(':')
                    .Append(enemy.CurrentHp)
                    .Append(':')
                    .Append(enemy.Block)
                    .Append('|');

                if (_visibleIntentsByEntityId.TryGetValue(entityId, out var intent))
                {
                    builder.Append("intent:")
                        .Append(intent.IntentId)
                        .Append(':')
                        .Append(intent.ProjectedDamage?.ToString() ?? "")
                        .Append(':')
                        .Append(intent.ProjectedHits?.ToString() ?? "")
                        .Append('|');
                }

                AppendPowerSignature(builder, enemy);
            }
        }

        var pcs = player.PlayerCombatState;
        if (pcs != null)
        {
            foreach (var zone in new[] { "draw", "hand", "play", "discard", "exhaust" })
            {
                builder.Append(zone).Append('=');
                foreach (var cardId in GetTrackedZoneCards(zone))
                {
                    builder.Append(cardId).Append(',');
                }
                builder.Append('|');
            }

            foreach (var (model, cardId) in _cardModelToId.OrderBy(entry => entry.Value, StringComparer.Ordinal))
            {
                var cardState = CaptureCardTruthState(model);
                builder.Append("card:")
                    .Append(cardId)
                    .Append(':')
                    .Append(cardState.Cost)
                    .Append(':')
                    .Append(cardState.StarCost?.ToString() ?? "null")
                    .Append(':')
                    .Append(cardState.CurrentUpgradeLevel)
                    .Append(':')
                    .Append(cardState.ReplayCount)
                    .Append(':')
                    .Append(string.Join(",", cardState.Keywords))
                    .Append(':')
                    .Append(cardState.VisibleFlags.RetainThisTurn ? '1' : '0')
                    .Append(cardState.VisibleFlags.SlyThisTurn ? '1' : '0')
                    .Append(':')
                    .Append(cardState.Enchantment?.EnchantmentId ?? "null")
                    .Append(':')
                    .Append(cardState.Enchantment?.Amount.ToString() ?? "null")
                    .Append(':')
                    .Append(cardState.Enchantment?.Status ?? "null")
                    .Append(':')
                    .Append(cardState.Affliction?.AfflictionId ?? "null")
                    .Append(':')
                    .Append(cardState.Affliction?.Amount.ToString() ?? "null");

                foreach (var dynamicValue in cardState.DynamicValues.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    builder.Append(':')
                        .Append(dynamicValue.Key)
                        .Append('=')
                        .Append(dynamicValue.Value);
                }

                builder.Append('|');
            }
        }

        foreach (var potion in GameStateReader.ReadPotions(player).OrderBy(p => p.SlotIndex))
        {
            var potionId = _potionModelToId.TryGetValue(potion.Instance, out var knownId)
                ? knownId
                : "?";
            builder.Append("potion:")
                .Append(potion.SlotIndex)
                .Append('=')
                .Append(potionId)
                .Append(':')
                .Append(potion.DefId)
                .Append('|');
        }

        return builder.ToString();
    }

    private static bool IsTurnStartSnapshotReady(PlayerCombatState? pcs)
    {
        if (pcs == null) return false;

        var handCount = GameStateReader.GetCardsInZone(pcs, "hand").Count;
        var drawCount = GameStateReader.GetCardsInZone(pcs, "draw").Count;
        var discardCount = GameStateReader.GetCardsInZone(pcs, "discard").Count;
        var playCount = GameStateReader.GetCardsInZone(pcs, "play").Count;
        var expectedHandCount = Math.Min(5, handCount + drawCount + discardCount);

        return playCount == 0 && handCount >= expectedHandCount;
    }

    private static string InferBattleResult(CombatState combatState, Player player)
    {
        var playerDefeated = player.Creature.CurrentHp <= 0;
        var hadEnemy = _entityIds.Values.Any(entityId => entityId != _playerEntityId);
        var aliveEnemyCount = hadEnemy
            ? GameStateReader.GetAliveEnemies(combatState).Count
            : -1;

        return DetermineBattleResult(playerDefeated, hadEnemy, aliveEnemyCount);
    }

    internal static string DetermineBattleResult(
        bool playerDefeated,
        bool hadEnemy,
        int aliveEnemyCount)
    {
        if (playerDefeated)
            return "defeat";

        if (hadEnemy && aliveEnemyCount == 0)
            return "victory";

        return "unknown";
    }

    private static string? GetWinningSide(string result)
    {
        return result switch
        {
            "victory" => "player",
            "defeat" => "enemy",
            _ => null,
        };
    }

    private static void WriteSnapshot()
    {
        if (_seq < 0 || _battleDir == null) return;
        try
        {
            var combatState = CombatManager.Instance?.DebugOnlyGetState();
            var player = GameStateReader.GetPlayer(combatState);
            if (combatState == null || player == null) return;
            WriteSnapshot(combatState, player);
        }
        catch (Exception ex)
        {
            _runtimeState?.RecordSnapshotWriteFailure(
                $"Failed to build a snapshot from live combat state: {ex.Message}");
            GD.PrintErr($"[STS2CombatRecorder] WriteSnapshot failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".WriteSnapshot", ex);
        }
    }

    private static bool WriteSnapshot(CombatState combatState, Player player, string? battleResult = null)
    {
        if (_seq < 0 || _battleDir == null) return false;

        try
        {
            var stageStart = StartDiagnosticsTimer();
            var snapshot = new Dictionary<string, object?>
            {
                ["schema_name"] = SchemaName,
                ["schema_version"] = SchemaVersion,
                ["seq"] = _seq,
                ["turn_index"] = _turnIndex,
                ["phase"] = _phase,
                ["battle_state"] = new Dictionary<string, object?> { ["active_side"] = GetSnapshotActiveSide(combatState) },
                ["entities"] = BuildSnapshotEntities(player),
                ["zones"] = BuildSnapshotZones(),
                ["cards"] = BuildSnapshotCards(),
                ["potions"] = BuildSnapshotPotions(),
            };

            if (snapshot["battle_state"] is Dictionary<string, object?> battleState && battleResult != null)
            {
                battleState["result"] = battleResult;
                battleState["winning_side"] = GetWinningSide(battleResult);
            }

            var fileName = $"{_seq:D6}.json";
            var path = Path.Combine(_battleDir, "snapshots", fileName);
            var json = JsonSerializer.Serialize(snapshot, SnapshotOpts);
            var snapshotText = json + "\n";
            File.WriteAllText(path, snapshotText);
            _perfDiagnostics?.RecordSnapshotWritten(Encoding.UTF8.GetByteCount(snapshotText));
            RecordDiagnosticsStage(RecorderPerfDiagnostics.StageNames.SnapshotBuildSerializeWrite, stageStart);
            return true;
        }
        catch (Exception ex)
        {
            _runtimeState?.RecordSnapshotWriteFailure(
                $"Failed to write snapshot for seq={_seq}: {ex.Message}");
            GD.PrintErr($"[STS2CombatRecorder] WriteSnapshot failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".WriteSnapshot", ex);
            return false;
        }
    }

    private static bool WriteMetadata(
        CombatState combatState,
        Player player,
        string? endedAt = null,
        string? result = null,
        MetadataWriteKind writeKind = MetadataWriteKind.Refresh)
    {
        try
        {
            if (_battleDir == null || string.IsNullOrWhiteSpace(_battleId) || _runtimeState == null)
                return false;

            var encounterId = _encounterId;
            var encounterName = _encounterName;
            if (encounterId == "unknown" || encounterName == "Unknown Encounter")
            {
                CacheEncounterIdentity(combatState);
                encounterId = _encounterId;
                encounterName = _encounterName;
            }

            var metadata = RecorderBattleMetadataFactory.Build(
                schemaName: SchemaName,
                protocolVersion: RecorderProtocol.Version,
                schemaVersion: SchemaVersion,
                modVersion: RecorderVersion,
                recorderName: RecorderName,
                recorderVersion: RecorderVersion,
                context: new RecorderBattleMetadataContext(
                    BattleId: _battleId,
                    CharacterId: GameStateReader.GetCharacterId(player),
                    CharacterName: GameStateReader.GetCharacterName(player),
                    EncounterId: encounterId,
                    EncounterName: encounterName,
                    Seed: null,
                    StartedAt: _battleStartedAt,
                    EndedAt: endedAt,
                    Result: result),
                runtimeState: _runtimeState);
            var json = JsonSerializer.Serialize(metadata, SnapshotOpts);
            var path = Path.Combine(_battleDir!, "metadata.json");
            var metadataText = json + "\n";
            File.WriteAllText(path, metadataText);
            _perfDiagnostics?.RecordMetadataWritten(Encoding.UTF8.GetByteCount(metadataText));
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2CombatRecorder] WriteMetadata failed: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".WriteMetadata", ex);
            if (writeKind == MetadataWriteKind.Finalize)
            {
                _runtimeState?.RecordMetadataFinalizeFailure(
                    $"Failed to finalize metadata.json: {ex.Message}");
            }
            else
            {
                _runtimeState?.RecordMetadataWriteFailure(
                    $"Failed to refresh metadata.json: {ex.Message}");
            }

            return false;
        }
    }

    private static void CacheEncounterIdentity(CombatState combatState)
    {
        var encounterId = GameStateReader.GetEncounterId(combatState);
        var encounterName = GameStateReader.GetEncounterName(combatState);

        if (!string.IsNullOrWhiteSpace(encounterId) && encounterId != "unknown")
        {
            _encounterId = encounterId;
        }

        if (!string.IsNullOrWhiteSpace(encounterName) && encounterName != "Unknown Encounter")
        {
            _encounterName = encounterName;
        }
    }

    private static bool WriteRecorderPerformanceSummary()
    {
        if (_perfDiagnostics == null || _battleDir == null || string.IsNullOrEmpty(_battleId) || _runtimeState == null)
            return true;

        try
        {
            var path = Path.Combine(_battleDir, RecorderPerfSummaryFileName);
            var (json, _) = _perfDiagnostics.BuildSummaryJson(_battleId, _runtimeState);
            File.WriteAllText(path, json + "\n");
            return true;
        }
        catch (Exception ex)
        {
            _runtimeState.RecordDiagnosticsWriteFailure(
                $"Failed to write recorder_perf_summary.json: {ex.Message}");
            DebugFileLogger.Error(nameof(BattleLogger) + ".WriteRecorderPerformanceSummary", ex);
            return false;
        }
    }

    private static BattleContainerRetentionRunResult RunRetentionCleanup(
        string? activeBattleDirectory,
        RecorderBattleRuntimeState? runtimeState,
        string context)
    {
        try
        {
            var result = BattleContainerRetentionManager.Cleanup(
                RecorderPaths.GetCombatLogsRoot(),
                RetentionPolicy,
                activeBattleDirectory);

            runtimeState?.RecordRetentionResult(result);

            if (result.CleanupAttempted || result.Failed)
            {
                DebugFileLogger.Log(
                    nameof(BattleLogger) + ".RunRetentionCleanup",
                    $"{context}: attempted={result.CleanupAttempted}, deleted={result.DeletedContainerCount}, reclaimed_bytes={result.BytesReclaimed}, failed={result.Failed}");
            }

            if (result.Failed && runtimeState == null && !string.IsNullOrWhiteSpace(result.FailureMessage))
            {
                Log.Info($"[STS2CombatRecorder] Retention cleanup warning: {result.FailureMessage}");
            }

            return result;
        }
        catch (Exception ex)
        {
            var failureResult = new BattleContainerRetentionRunResult(
                CleanupAttempted: true,
                DeletedContainerCount: 0,
                BytesReclaimed: 0,
                Failed: true,
                FailureMessage: $"Retention cleanup crashed: {ex.Message}",
                DeletedContainerPaths: Array.Empty<string>());
            runtimeState?.RecordRetentionResult(failureResult);
            DebugFileLogger.Error(nameof(BattleLogger) + ".RunRetentionCleanup", ex);
            return failureResult;
        }
    }

    private static void WriteHookFirstShadowSummary()
    {
        if (_hookFirstShadowComparison == null || _battleDir == null || string.IsNullOrEmpty(_battleId))
            return;

        var path = Path.Combine(_battleDir, HookFirstShadowSummaryFileName);
        var json = _hookFirstShadowComparison.BuildSummaryJson(_battleId);
        File.WriteAllText(path, json + "\n");
    }

    private static long StartDiagnosticsTimer()
    {
        return _perfDiagnostics != null ? Stopwatch.GetTimestamp() : 0L;
    }

    private static void RecordObservedFrameTimer(long startedAt)
    {
        if (_perfDiagnostics == null || startedAt == 0L)
            return;

        _perfDiagnostics.RecordObservedFrame(Stopwatch.GetTimestamp() - startedAt);
    }

    private static void RecordDiagnosticsStage(string stageName, long startedAt)
    {
        if (_perfDiagnostics == null || startedAt == 0L)
            return;

        _perfDiagnostics.RecordStage(stageName, Stopwatch.GetTimestamp() - startedAt);
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private static DateTimeOffset GetRecorderNow()
    {
        return DateTimeOffset.UtcNow.ToOffset(RecorderTimeOffset);
    }

    private static string GetSnapshotActiveSide(CombatState? combatState)
    {
        try
        {
            return combatState?.CurrentSide switch
            {
                CombatSide.Player => "player",
                CombatSide.Enemy => "enemy",
                CombatSide.None => "neutral",
                _ => _activeSide,
            };
        }
        catch
        {
            return _activeSide;
        }
    }

    private static string FormatRecorderTimestamp(DateTimeOffset timestamp)
    {
        return timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz");
    }

    private static string FormatRecorderPathTimestamp(DateTimeOffset timestamp)
    {
        return timestamp.ToString("yyyy-MM-dd'T'HH-mm-ss.fffzzz").Replace(":", "-");
    }

    private static Dictionary<string, List<string>> CreateEmptyTrackedZoneState()
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var zoneName in TrackedZoneNames)
        {
            result[zoneName] = new List<string>();
        }
        return result;
    }

    private static List<string> GetTrackedZoneCards(string zoneName)
    {
        if (_trackedZoneCards.TryGetValue(zoneName, out var cards))
        {
            return cards;
        }

        cards = new List<string>();
        _trackedZoneCards[zoneName] = cards;
        return cards;
    }

    private static void RemoveTrackedCardFromZone(List<string> zoneCards, string cardId, int? index = null)
    {
        if (index.HasValue &&
            index.Value >= 0 &&
            index.Value < zoneCards.Count &&
            zoneCards[index.Value] == cardId)
        {
            zoneCards.RemoveAt(index.Value);
            return;
        }

        var actualIndex = zoneCards.IndexOf(cardId);
        if (actualIndex >= 0)
        {
            zoneCards.RemoveAt(actualIndex);
        }
    }

    private static void InsertTrackedCardIntoZone(List<string> zoneCards, string cardId, int? index = null)
    {
        if (index.HasValue && index.Value >= 0 && index.Value <= zoneCards.Count)
        {
            zoneCards.Insert(index.Value, cardId);
            return;
        }

        zoneCards.Add(cardId);
    }

    private static int? TryGetCardIndexInTrackedZone(string zoneName, string cardId)
    {
        var index = GetTrackedZoneCards(zoneName).IndexOf(cardId);
        return index >= 0 ? index : null;
    }

    private static void TrackCardCreation(string cardId, string zoneName)
    {
        _cardZones[cardId] = zoneName;
        InsertTrackedCardIntoZone(GetTrackedZoneCards(zoneName), cardId);
    }

    private static void MoveTrackedCard(
        string cardId,
        string fromZone,
        string toZone,
        int? fromIndex = null,
        int? toIndex = null)
    {
        RemoveTrackedCardFromZone(GetTrackedZoneCards(fromZone), cardId, fromIndex);
        InsertTrackedCardIntoZone(GetTrackedZoneCards(toZone), cardId, toIndex);
        _cardZones[cardId] = toZone;
    }

    private static (string Zone, int? Index) ResolveResolvedCardZone(
        PlayerCombatState? pcs,
        string cardId,
        string fallbackZone)
    {
        if (pcs != null)
        {
            var liveZones = CaptureTrackedZones(pcs);
            if (liveZones.TryGetValue(cardId, out var actualZone))
            {
                return (actualZone.Zone, actualZone.Index);
            }
        }

        return fallbackZone switch
        {
            "discard" or "exhaust" or "play" => ("removed", null),
            _ => (fallbackZone, null),
        };
    }

    private static List<string> BuildTargetEntityIds(CardPlay cardPlay)
    {
        var targetEntityIds = new List<string>();
        if (cardPlay.Target != null && _entityIds.TryGetValue(cardPlay.Target, out var targetEntityId))
        {
            targetEntityIds.Add(targetEntityId);
        }
        else
        {
            targetEntityIds.AddRange(GetCurrentEnemyIds());
        }

        return targetEntityIds;
    }

    private static List<string> BuildPendingCardPlayTargetEntityIds(Creature? target)
    {
        var targetEntityIds = new List<string>();
        if (target != null && _entityIds.TryGetValue(target, out var targetEntityId))
        {
            targetEntityIds.Add(targetEntityId);
        }
        else
        {
            targetEntityIds.AddRange(GetCurrentEnemyIds());
        }

        return targetEntityIds;
    }

    private static List<string> BuildPotionTargetEntityIds(Creature? target, string targetType)
    {
        var targetEntityIds = new List<string>();

        if (target != null && _entityIds.TryGetValue(target, out var targetEntityId))
        {
            targetEntityIds.Add(targetEntityId);
            return targetEntityIds;
        }

        if (targetType is "Self" or "AnyPlayer" or "AnyAlly")
        {
            targetEntityIds.Add(_playerEntityId);
            return targetEntityIds;
        }

        if (targetType == "AnyEnemy")
        {
            targetEntityIds.AddRange(GetCurrentEnemyIds());
        }

        return targetEntityIds;
    }

    private static string MapPotionTargetMode(string targetType, List<string> targetEntityIds)
    {
        if (targetEntityIds.Count == 0)
        {
            return "none";
        }

        return targetType switch
        {
            "AnyEnemy" => "single_enemy",
            "Self" or "AnyPlayer" => "self",
            "AnyAlly" => "single_ally",
            _ => targetEntityIds.Count > 1 ? "unknown" : "single_entity",
        };
    }

    private static string SafeTargetTypeName(PotionModel potion)
    {
        try
        {
            return potion.TargetType.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static Dictionary<string, object?> BuildSnapshotPotions()
    {
        return _potionsById
            .OrderBy(kvp => kvp.Key)
            .ToDictionary(
                kvp => kvp.Key,
                kvp => (object?)new Dictionary<string, object?>
                {
                    ["potion_def_id"] = kvp.Value.DefId,
                    ["potion_name"] = kvp.Value.Name,
                    ["slot_index"] = kvp.Value.SlotIndex,
                    ["state"] = kvp.Value.State,
                });
    }

    private static List<Dictionary<string, object?>> BuildSnapshotEntities(Player player)
    {
        var entities = new List<Dictionary<string, object?>>();

        foreach (var (creature, entityId) in _entityIds.OrderBy(kvp => kvp.Value))
        {
            if (_removedEntityIds.Contains(entityId))
                continue;

            var isPlayer = entityId == _playerEntityId;
            var hp = _prevHp.TryGetValue(entityId, out var trackedHp)
                ? trackedHp
                : creature.CurrentHp;
            var block = _prevBlock.TryGetValue(entityId, out var trackedBlock)
                ? trackedBlock
                : creature.Block;
            var maxHp = creature.MaxHp;
            var name = isPlayer
                ? GameStateReader.GetCharacterName(player)
                : SafeSnapshotName(creature, "Enemy");
            var entity = new Dictionary<string, object?>
            {
                ["entity_id"] = entityId,
                ["kind"] = isPlayer ? "player" : "enemy",
                ["name"] = name,
                ["hp"] = hp,
                ["max_hp"] = maxHp,
                ["block"] = block,
                ["energy"] = isPlayer ? player.PlayerCombatState?.Energy ?? 0 : null,
                ["resources"] = isPlayer
                    ? new Dictionary<string, object?>
                    {
                        ["stars"] = player.PlayerCombatState?.Stars ?? 0,
                    }
                    : null,
                ["orb_slots"] = isPlayer ? GetTrackedOrbSlots() : null,
                ["orbs"] = isPlayer ? BuildTrackedOrbStatePayloads() : null,
                ["relics"] = isPlayer ? BuildTrackedRelicStatePayloads() : null,
                ["powers"] = BuildSnapshotPowers(creature),
            };

            if (!isPlayer && hp > 0 && _visibleIntentsByEntityId.TryGetValue(entityId, out var intent))
            {
                entity["intent"] = BuildIntentState(intent);
            }

            entities.Add(entity);
        }

        return entities;
    }

    private static Dictionary<string, object?> BuildSnapshotZones()
    {
        return _trackedZoneCards.ToDictionary(
            kvp => kvp.Key,
            kvp => (object?)new List<string>(kvp.Value));
    }

    private static Dictionary<string, object?> BuildSnapshotCards()
    {
        var cards = new Dictionary<string, object?>();

        foreach (var (model, cardId) in _cardModelToId.OrderBy(kvp => kvp.Value))
        {
            var zone = _cardZones.TryGetValue(cardId, out var trackedZone)
                ? trackedZone
                : "unknown";

            var cardState = CaptureCardTruthState(model);

            var payload = new Dictionary<string, object?>
            {
                ["card_def_id"] = model.Id?.Entry ?? GameStateReader.ToSnakeCase(model.GetType().Name),
                ["card_name"] = model.Title?.ToString() ?? model.GetType().Name,
                ["owner_entity_id"] = _playerEntityId,
                ["zone"] = zone,
            };
            AppendCardVisibleStatePayload(payload, cardState);
            cards[cardId] = payload;
        }

        return cards;
    }

    private static string SafeSnapshotName(Creature creature, string fallback)
    {
        try
        {
            var title = creature.Name?.ToString();
            if (!string.IsNullOrWhiteSpace(title) && !title.Contains("LocString"))
                return title;
        }
        catch
        {
        }

        return creature.Monster?.GetType().Name ?? fallback;
    }

    private static List<string> GetCurrentEnemyIds()
    {
        return _entityIds
            .Where(kvp => kvp.Value.StartsWith("enemy:") && !_removedEntityIds.Contains(kvp.Value))
            .Select(kvp => kvp.Value)
            .ToList();
    }

    private static string? GetCardDefId(string cardInstanceId)
    {
        foreach (var kvp in _cardModelToId)
        {
            if (kvp.Value == cardInstanceId)
                return kvp.Key.Id?.Entry ?? GameStateReader.ToSnakeCase(kvp.Key.GetType().Name);
        }
        return null;
    }
}
