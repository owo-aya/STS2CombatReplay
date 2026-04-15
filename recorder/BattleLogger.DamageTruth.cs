using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace STS2CombatRecorder;

public static partial class BattleLogger
{
    private sealed class TruthResolutionSnapshot
    {
        public string? ResolutionId { get; init; }
        public string? ParentResolutionId { get; init; }
        public int? ResolutionDepth { get; init; }
        public Dictionary<string, object?>? RootSource { get; init; }
    }

    private sealed class AttackTruthGroupContext
    {
        public required AttackCommand Command { get; init; }
        public required string AttemptGroupId { get; init; }
        public TruthResolutionSnapshot? Resolution { get; init; }
        public Dictionary<string, object?>? ExecutorSource { get; init; }
    }

    private sealed class DamageStepRecord
    {
        public required string Stage { get; init; }
        public required string Operation { get; init; }
        public decimal? Before { get; init; }
        public decimal? After { get; init; }
        public decimal? Delta { get; init; }
        public Dictionary<string, object?>? ModifierRef { get; init; }
        public string? TargetEntityId { get; init; }
        public bool IsUnknown { get; init; }
        public string? UnknownReason { get; init; }
    }

    private sealed class DamageAttemptBuilder
    {
        public required string AttemptId { get; init; }
        public required string? ParentAttemptId { get; init; }
        public required Creature OriginalTarget { get; init; }
        public required string OriginalTargetEntityId { get; init; }
        public required int OriginalTargetHpBefore { get; init; }
        public required int OriginalTargetBlockBefore { get; init; }
        public required decimal BaseAmount { get; init; }
        public decimal DamageAfterDamageStage { get; set; }
        public decimal? PostBlockAmount { get; set; }
        public decimal? HpLostBeforeOstyAmount { get; set; }
        public decimal? HpLostAfterOstyAmount { get; set; }
        public decimal? RedirectCompanionHpLostAfterOstyAmount { get; set; }
        public Creature? RedirectTarget { get; set; }
        public string? RedirectTargetEntityId { get; set; }
        public int? RedirectTargetHpBefore { get; set; }
        public int? RedirectTargetBlockBefore { get; set; }
        public string? SettledTargetEntityId { get; set; }
        public int? SettledTargetHpBefore { get; set; }
        public int? SettledTargetHpAfter { get; set; }
        public int? SettledTargetBlockBefore { get; set; }
        public int? SettledTargetBlockAfter { get; set; }
        public bool WasTargetKilled { get; set; }
        public List<DamageStepRecord> DamageStageSteps { get; } = new();
        public List<DamageStepRecord> HpLostBeforeOstySteps { get; } = new();
        public List<DamageStepRecord> RedirectSteps { get; } = new();
        public List<DamageStepRecord> HpLostAfterOstySteps { get; } = new();
        public List<DamageStepRecord> RedirectCompanionHpLostAfterOstySteps { get; } = new();
        public HashSet<string> UnknownFlags { get; } = new(StringComparer.Ordinal);
    }

    private sealed class DamageModifyCandidate
    {
        public required Creature Target { get; init; }
        public required Creature? Dealer { get; init; }
        public required CardModel? CardSource { get; init; }
        public required ValueProp DamageProps { get; init; }
        public required decimal InputAmount { get; init; }
        public required decimal FinalAmount { get; init; }
        public List<DamageStepRecord> Steps { get; } = new();
    }

    private sealed class DamageTruthCallContext
    {
        public required string AttemptGroupId { get; init; }
        public required int TurnIndex { get; init; }
        public required string Phase { get; init; }
        public required string TimingKind { get; init; }
        public required string DeliveryKind { get; init; }
        public required string? ParentAttemptId { get; init; }
        public required string? ActorEntityId { get; init; }
        public required ValueProp DamageProps { get; init; }
        public required decimal RequestedAmount { get; init; }
        public TruthResolutionSnapshot? Resolution { get; init; }
        public Dictionary<string, object?>? ExecutorSource { get; init; }
        public Dictionary<string, object?>? TriggerSource { get; init; }
        public List<string> TrackedOriginalTargetEntityIds { get; } = new();
        public List<DamageModifyCandidate> PendingModifyDamageCandidates { get; } = new();
        public List<DamageAttemptBuilder> Attempts { get; } = new();
        public DamageAttemptBuilder? CurrentAttempt { get; set; }
    }

    private sealed class ExpectedDamageSettlement
    {
        public required string AttemptId { get; init; }
        public required int ExpectedHp { get; init; }
        public required int ExpectedBlock { get; init; }
    }

    private sealed class DeferredObservedStateEvent
    {
        public required string EntityId { get; init; }
        public required string EventType { get; init; }
        public required string Phase { get; init; }
        public required AttributionContext? AttributionContext { get; init; }
        public required Dictionary<string, object?> Payload { get; init; }
        public required EventDispatchMode DispatchMode { get; init; }
        public string? DeathSourceEntityId { get; init; }
        public string? DeathReason { get; init; }
    }

    private sealed class DeferredEntityDiedEvent
    {
        public required string EntityId { get; init; }
        public required string Phase { get; init; }
        public required AttributionContext? AttributionContext { get; init; }
        public string? SourceEntityId { get; init; }
        public required string Reason { get; init; }
    }

    private static readonly AsyncLocal<Stack<AttackTruthGroupContext>?> _attackTruthGroupStack = new();
    private static readonly AsyncLocal<Stack<DamageTruthCallContext>?> _damageTruthCallStack = new();
    private static readonly AsyncLocal<Stack<AbstractModel>?> _truthSourceScopeStack = new();
    private static readonly AsyncLocal<Stack<Dictionary<string, object?>?>?> _powerTruthTriggerScopeStack = new();
    private static readonly AsyncLocal<Stack<TruthResolutionSnapshot?>?> _retainedTruthResolutionScopeStack = new();

    private static int _damageAttemptCounter;
    private static int _damageAttemptGroupCounter;
    private static int _powerInstanceCounter;
    private static Dictionary<PowerModel, string> _powerModelToInstanceId = new();
    private static Dictionary<string, ExpectedDamageSettlement> _expectedDamageSettlements = new(StringComparer.Ordinal);
    private static Dictionary<string, int> _pendingDamageTruthEntityCounts = new(StringComparer.Ordinal);
    private static List<DeferredObservedStateEvent> _deferredObservedStateEvents = new();
    private static HashSet<string> _pendingKilledEntityIds = new(StringComparer.Ordinal);
    private static Dictionary<string, DeferredEntityDiedEvent> _deferredEntityDiedEvents = new(StringComparer.Ordinal);

    private static void ResetDamageTruthState()
    {
        _damageAttemptCounter = 0;
        _damageAttemptGroupCounter = 0;
        _powerInstanceCounter = 0;
        _powerModelToInstanceId = new Dictionary<PowerModel, string>();
        _expectedDamageSettlements = new Dictionary<string, ExpectedDamageSettlement>(StringComparer.Ordinal);
        _pendingDamageTruthEntityCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        _deferredObservedStateEvents = new List<DeferredObservedStateEvent>();
        _pendingKilledEntityIds = new HashSet<string>(StringComparer.Ordinal);
        _deferredEntityDiedEvents = new Dictionary<string, DeferredEntityDiedEvent>(StringComparer.Ordinal);
        _attackTruthGroupStack.Value = new Stack<AttackTruthGroupContext>();
        _damageTruthCallStack.Value = new Stack<DamageTruthCallContext>();
        _truthSourceScopeStack.Value = new Stack<AbstractModel>();
        _powerTruthTriggerScopeStack.Value = new Stack<Dictionary<string, object?>?>();
        _retainedTruthResolutionScopeStack.Value = new Stack<TruthResolutionSnapshot?>();
    }

    public static void OnAttackTruthStarted(CombatState combatState, AttackCommand command)
    {
        if (!_active || !_initDone)
            return;

        try
        {
            var stack = EnsureStack(_attackTruthGroupStack);
            if (stack.Any(existing => ReferenceEquals(existing.Command, command)))
            {
                return;
            }

            var resolution = GetActiveTruthResolutionSnapshot();
            var executorSource = ResolveAttackGroupExecutor(command);

            stack.Push(new AttackTruthGroupContext
            {
                Command = command,
                AttemptGroupId = $"grp_attack_{++_damageAttemptGroupCounter:D4}",
                Resolution = resolution,
                ExecutorSource = executorSource,
            });
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnAttackTruthStarted", ex);
        }
    }

    public static Task WrapAttackTruthCompletion(AttackCommand command, Task originalTask)
    {
        return WrapAttackTruthCompletionInternal(command, originalTask);
    }

    private static async Task WrapAttackTruthCompletionInternal(AttackCommand command, Task originalTask)
    {
        try
        {
            await originalTask;
        }
        finally
        {
            try
            {
                OnAttackTruthFinished(command);
            }
            catch (Exception ex)
            {
                DebugFileLogger.Error(nameof(BattleLogger) + ".WrapAttackTruthCompletionInternal", ex);
            }
        }
    }

    private static void OnAttackTruthFinished(AttackCommand command)
    {
        var stack = EnsureStack(_attackTruthGroupStack);
        if (stack.Count == 0)
        {
            return;
        }

        if (ReferenceEquals(stack.Peek().Command, command))
        {
            stack.Pop();
            return;
        }

        var preserved = new Stack<AttackTruthGroupContext>();
        while (stack.Count > 0 && !ReferenceEquals(stack.Peek().Command, command))
        {
            preserved.Push(stack.Pop());
        }

        if (stack.Count > 0)
        {
            stack.Pop();
        }

        while (preserved.Count > 0)
        {
            stack.Push(preserved.Pop());
        }
    }

    public static object? OnDamageCallStarted(
        PlayerChoiceContext choiceContext,
        IEnumerable<Creature> targets,
        decimal amount,
        ValueProp props,
        Creature? dealer,
        CardModel? cardSource)
    {
        if (!_active || !_initDone)
            return null;

        try
        {
            var parentAttempt = GetCurrentDamageAttempt();
            var activeAttackGroup = GetActiveAttackTruthGroup();
            var resolution = activeAttackGroup?.Resolution ?? GetActiveTruthResolutionSnapshot();
            var executorSource = ResolveDamageExecutor(choiceContext, dealer, cardSource, activeAttackGroup);
            var triggerSource = ResolveDamageTrigger(parentAttempt, resolution?.RootSource, executorSource);

            var context = new DamageTruthCallContext
            {
                AttemptGroupId = activeAttackGroup?.AttemptGroupId ?? $"grp_damage_{++_damageAttemptGroupCounter:D4}",
                TurnIndex = _turnIndex,
                Phase = _phase,
                TimingKind = _phase,
                DeliveryKind = activeAttackGroup != null ? "attack_root" : "effect_damage",
                ParentAttemptId = parentAttempt?.AttemptId,
                ActorEntityId = ResolveEntityId(dealer),
                DamageProps = props,
                RequestedAmount = amount,
                Resolution = resolution,
                ExecutorSource = executorSource,
                TriggerSource = triggerSource,
            };

            EnsureStack(_damageTruthCallStack).Push(context);
            return context;
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnDamageCallStarted", ex);
            return null;
        }
    }

    public static Task<IEnumerable<DamageResult>> WrapDamageCallTask(
        object? state,
        Task<IEnumerable<DamageResult>> originalTask)
    {
        return WrapDamageCallTaskInternal(state as DamageTruthCallContext, originalTask);
    }

    private static async Task<IEnumerable<DamageResult>> WrapDamageCallTaskInternal(
        DamageTruthCallContext? context,
        Task<IEnumerable<DamageResult>> originalTask)
    {
        try
        {
            var results = (await originalTask).ToList();
            if (context != null)
            {
                FinalizeDamageCall(context, results);
            }

            return results;
        }
        finally
        {
            if (context != null)
            {
                ReleaseDeferredObservedStateForContext(context);
                var stack = EnsureStack(_damageTruthCallStack);
                if (stack.Count > 0 && ReferenceEquals(stack.Peek(), context))
                {
                    stack.Pop();
                }
            }
        }
    }

    public static void OnDamageStageModifyDamage(
        IRunState runState,
        CombatState? combatState,
        Creature? target,
        Creature? dealer,
        decimal damage,
        ValueProp props,
        CardModel? cardSource,
        ModifyDamageHookType modifyDamageHookType,
        decimal finalAmount)
    {
        if (target == null)
            return;

        try
        {
            var context = GetCurrentDamageTruthCall();
            if (context == null)
                return;

            var candidate = new DamageModifyCandidate
            {
                Target = target,
                Dealer = dealer,
                CardSource = cardSource,
                DamageProps = props,
                InputAmount = damage,
                FinalAmount = finalAmount,
            };
            ReplayModifyDamageSteps(
                candidate.Steps,
                runState,
                combatState,
                target,
                dealer,
                damage,
                props,
                cardSource,
                modifyDamageHookType);
            var targetEntityId = ResolveEntityId(target) ?? BuildSyntheticEntityId(target);
            context.TrackedOriginalTargetEntityIds.Add(targetEntityId);
            TrackPendingDamageTruthEntity(targetEntityId);
            context.PendingModifyDamageCandidates.Add(candidate);
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnDamageStageModifyDamage", ex);
        }
    }

    public static void OnDamageStageBeforeDamageReceived(
        Creature target,
        decimal amount,
        ValueProp props,
        Creature? dealer,
        CardModel? cardSource)
    {
        try
        {
            var context = GetCurrentDamageTruthCall();
            if (context == null)
                return;

            var candidate = context.PendingModifyDamageCandidates.FirstOrDefault(c =>
                ReferenceEquals(c.Target, target) &&
                ReferenceEquals(c.Dealer, dealer) &&
                ReferenceEquals(c.CardSource, cardSource) &&
                c.DamageProps == props &&
                c.FinalAmount == amount);

            var attempt = BeginDamageAttempt(context, target, candidate?.InputAmount ?? amount);
            attempt.DamageStageSteps.Clear();

            if (candidate != null)
            {
                attempt.DamageStageSteps.AddRange(candidate.Steps);
                attempt.DamageAfterDamageStage = candidate.FinalAmount;
                context.PendingModifyDamageCandidates.Remove(candidate);
            }
            else
            {
                attempt.DamageStageSteps.Add(new DamageStepRecord
                {
                    Stage = "base",
                    Operation = "base",
                    Before = null,
                    After = amount,
                    Delta = null,
                });
                attempt.DamageAfterDamageStage = amount;
                attempt.UnknownFlags.Add("modify_damage_unmatched");
            }
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnDamageStageBeforeDamageReceived", ex);
        }
    }

    public static void OnDamageStageModifyHpLostBeforeOsty(
        IRunState runState,
        CombatState? combatState,
        Creature target,
        decimal amount,
        ValueProp props,
        Creature? dealer,
        CardModel? cardSource,
        decimal finalAmount)
    {
        try
        {
            var attempt = GetCurrentDamageAttempt(target);
            if (attempt == null)
                return;

            attempt.PostBlockAmount = amount;
            attempt.HpLostBeforeOstySteps.Clear();
            ReplayHpLostBeforeOstySteps(
                attempt.HpLostBeforeOstySteps,
                runState,
                combatState,
                target,
                amount,
                props,
                dealer,
                cardSource);
            attempt.HpLostBeforeOstyAmount = finalAmount;
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnDamageStageModifyHpLostBeforeOsty", ex);
        }
    }

    public static void OnDamageStageRedirect(
        CombatState combatState,
        Creature originalTarget,
        decimal amount,
        ValueProp props,
        Creature? dealer,
        Creature redirectedTarget)
    {
        try
        {
            if (ReferenceEquals(originalTarget, redirectedTarget))
                return;

            var attempt = GetCurrentDamageAttempt(originalTarget);
            if (attempt == null)
                return;

            var priorRedirectTargetEntityId = attempt.RedirectTargetEntityId;
            attempt.RedirectTarget = redirectedTarget;
            attempt.RedirectTargetEntityId = ResolveEntityId(redirectedTarget) ?? BuildSyntheticEntityId(redirectedTarget);
            attempt.RedirectTargetHpBefore = redirectedTarget.CurrentHp;
            attempt.RedirectTargetBlockBefore = redirectedTarget.Block;
            if (!string.Equals(priorRedirectTargetEntityId, attempt.RedirectTargetEntityId, StringComparison.Ordinal))
            {
                TrackPendingDamageTruthEntity(attempt.RedirectTargetEntityId);
            }
            attempt.RedirectSteps.Clear();
            ReplayRedirectSteps(
                attempt.RedirectSteps,
                combatState,
                originalTarget,
                amount,
                props,
                dealer);
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnDamageStageRedirect", ex);
        }
    }

    public static void OnDamageStageModifyHpLostAfterOsty(
        IRunState runState,
        CombatState? combatState,
        Creature target,
        decimal amount,
        ValueProp props,
        Creature? dealer,
        CardModel? cardSource,
        decimal finalAmount)
    {
        try
        {
            var attempt = GetCurrentDamageAttempt(target) ?? GetCurrentDamageAttempt();
            if (attempt == null)
                return;

            var isRedirectCompanion =
                attempt.RedirectTarget != null &&
                !ReferenceEquals(target, attempt.RedirectTarget) &&
                attempt.HpLostAfterOstyAmount.HasValue;

            var sink = isRedirectCompanion
                ? attempt.RedirectCompanionHpLostAfterOstySteps
                : attempt.HpLostAfterOstySteps;

            sink.Clear();
            ReplayHpLostAfterOstySteps(
                sink,
                runState,
                combatState,
                target,
                amount,
                props,
                dealer,
                cardSource);
            if (isRedirectCompanion)
            {
                attempt.RedirectCompanionHpLostAfterOstyAmount = finalAmount;
            }
            else
            {
                attempt.HpLostAfterOstyAmount = finalAmount;
            }
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(BattleLogger) + ".OnDamageStageModifyHpLostAfterOsty", ex);
        }
    }

    public static void PushTruthSourceScope(AbstractModel model)
    {
        EnsureStack(_truthSourceScopeStack).Push(model);
    }

    public static void PopTruthSourceScope()
    {
        var stack = EnsureStack(_truthSourceScopeStack);
        if (stack.Count > 0)
        {
            stack.Pop();
        }
    }

    public static void PushPowerTruthTriggerScope()
    {
        var stack = EnsureStack(_powerTruthTriggerScopeStack);
        var scopedSource = EnsureStack(_truthSourceScopeStack).Count > 0
            ? EnsureStack(_truthSourceScopeStack).Peek()
            : null;
        stack.Push(scopedSource != null ? SanitizePowerTruthRef(DescribeSource(scopedSource)) : null);
    }

    public static void PopPowerTruthTriggerScope()
    {
        var stack = EnsureStack(_powerTruthTriggerScopeStack);
        if (stack.Count > 0)
        {
            stack.Pop();
        }
    }

    public static void PushTruthResolutionScope()
    {
        EnsureStack(_retainedTruthResolutionScopeStack).Push(GetActiveTruthResolutionSnapshot());
    }

    public static void PopTruthResolutionScope()
    {
        var stack = EnsureStack(_retainedTruthResolutionScopeStack);
        if (stack.Count > 0)
        {
            stack.Pop();
        }
    }

    private static void FinalizeDamageCall(
        DamageTruthCallContext context,
        IReadOnlyList<DamageResult> results)
    {
        var resultIndex = 0;

        for (var attemptIndex = 0; attemptIndex < context.Attempts.Count; attemptIndex++)
        {
            var attempt = context.Attempts[attemptIndex];
            if (resultIndex >= results.Count)
            {
                attempt.UnknownFlags.Add("missing_damage_result");
                break;
            }

            var primaryResult = results[resultIndex++];
            EmitDamageAttempt(context, attempt, primaryResult, attempt.AttemptId, attempt.ParentAttemptId, useRedirectCompanionSteps: false);

            var hasRedirectCompanion =
                attempt.RedirectTarget != null &&
                !ReferenceEquals(primaryResult.Receiver, attempt.OriginalTarget) &&
                resultIndex < results.Count &&
                ReferenceEquals(results[resultIndex].Receiver, attempt.OriginalTarget);

            if (hasRedirectCompanion)
            {
                var redirectCompanion = results[resultIndex++];
                EmitDamageAttempt(
                    context,
                    attempt,
                    redirectCompanion,
                    $"atk_{++_damageAttemptCounter:D5}",
                    attempt.AttemptId,
                    useRedirectCompanionSteps: true);
            }
        }

        while (resultIndex < results.Count)
        {
            var fallbackResult = results[resultIndex++];
            var fallbackAttempt = BuildFallbackDamageAttempt(context, fallbackResult);
            if (fallbackAttempt == null)
            {
                DebugFileLogger.Log(
                    nameof(BattleLogger) + ".FinalizeDamageCall",
                    $"Unmatched damage result could not be synthesized. receiver={ResolveEntityId(fallbackResult.Receiver) ?? BuildSyntheticEntityId(fallbackResult.Receiver)}, group_id={context.AttemptGroupId}");
                continue;
            }

            context.Attempts.Add(fallbackAttempt);
            EmitDamageAttempt(
                context,
                fallbackAttempt,
                fallbackResult,
                fallbackAttempt.AttemptId,
                fallbackAttempt.ParentAttemptId,
                useRedirectCompanionSteps: false);
        }

        if (context.PendingModifyDamageCandidates.Count > 0)
        {
            DebugFileLogger.Log(
                nameof(BattleLogger) + ".FinalizeDamageCall",
                $"Pending modify-damage candidates remained after finalize. remaining={context.PendingModifyDamageCandidates.Count}, group_id={context.AttemptGroupId}");
        }
    }

    private static void EmitDamageAttempt(
        DamageTruthCallContext context,
        DamageAttemptBuilder attempt,
        DamageResult result,
        string attemptId,
        string? parentAttemptId,
        bool useRedirectCompanionSteps)
    {
        var settledTargetEntityId = ResolveEntityId(result.Receiver) ?? BuildSyntheticEntityId(result.Receiver);
        var settledHpBefore = useRedirectCompanionSteps
            ? attempt.OriginalTargetHpBefore
            : (ReferenceEquals(result.Receiver, attempt.RedirectTarget)
                ? attempt.RedirectTargetHpBefore ?? attempt.OriginalTargetHpBefore
                : attempt.OriginalTargetHpBefore);
        var settledBlockBefore = useRedirectCompanionSteps
            ? attempt.OriginalTargetBlockBefore
            : (ReferenceEquals(result.Receiver, attempt.RedirectTarget)
                ? attempt.RedirectTargetBlockBefore ?? attempt.OriginalTargetBlockBefore
                : attempt.OriginalTargetBlockBefore);

        var executorSource = context.ExecutorSource ?? BuildUnknownSource("no_formal_executor");
        var triggerSource = context.TriggerSource;
        var unknownFlags = attempt.UnknownFlags.ToList();
        if (context.ExecutorSource == null)
        {
            unknownFlags.Add("executor_unknown");
        }

        var damageSettledTargetHpAfter =
            result.WasTargetKilled && result.Receiver.CurrentHp > 0
                ? 0
                : result.Receiver.CurrentHp;

        var payload = new Dictionary<string, object?>
        {
            ["attempt_id"] = attemptId,
            ["attempt_group_id"] = context.AttemptGroupId,
            ["parent_attempt_id"] = parentAttemptId,
            ["timing_kind"] = context.TimingKind,
            ["delivery_kind"] = context.DeliveryKind,
            ["actor_entity_id"] = context.ActorEntityId,
            ["original_target_entity_id"] = attempt.OriginalTargetEntityId,
            ["settled_target_entity_id"] = settledTargetEntityId,
            ["base_amount"] = attempt.BaseAmount,
            ["damage_after_damage_stage"] = attempt.DamageAfterDamageStage,
            ["blocked_amount"] = result.BlockedDamage,
            ["hp_loss"] = result.UnblockedDamage,
            ["overkill_amount"] = result.OverkillDamage,
            ["final_settled_damage"] = result.UnblockedDamage + result.OverkillDamage,
            ["bypasses_block"] = context.DamageProps.HasFlag(ValueProp.Unblockable),
            ["is_powered_damage"] = !context.DamageProps.HasFlag(ValueProp.Unpowered),
            ["is_move_damage"] = context.DamageProps.HasFlag(ValueProp.Move),
            ["was_fully_blocked"] = result.WasFullyBlocked,
            ["was_block_broken"] = result.WasBlockBroken,
            ["target_died"] = result.WasTargetKilled,
            ["target_hp_before"] = settledHpBefore,
            ["target_hp_after"] = damageSettledTargetHpAfter,
            ["target_block_before"] = settledBlockBefore,
            ["target_block_after"] = result.Receiver.Block,
            ["executor"] = executorSource,
            ["steps"] = BuildDamageStepPayloads(attempt, result, settledTargetEntityId, useRedirectCompanionSteps),
        };

        if (triggerSource != null)
        {
            payload["trigger"] = triggerSource;
        }

        if (attempt.RedirectTarget != null)
        {
            payload["redirected"] = true;
            payload["original_target_hp_before"] = attempt.OriginalTargetHpBefore;
            payload["original_target_hp_after"] = attempt.OriginalTarget.CurrentHp;
            payload["original_target_block_before"] = attempt.OriginalTargetBlockBefore;
            payload["original_target_block_after"] = attempt.OriginalTarget.Block;
        }

        if (unknownFlags.Count > 0)
        {
            payload["unknown_flags"] = unknownFlags.Distinct(StringComparer.Ordinal).ToList();
        }

        EmitEvent(
            "damage_attempt",
            context.TurnIndex,
            context.Phase,
            context.Resolution?.ResolutionId,
            context.Resolution?.ParentResolutionId,
            context.Resolution?.ResolutionDepth,
            null,
            payload);

        attempt.SettledTargetEntityId = settledTargetEntityId;
        attempt.SettledTargetHpBefore = settledHpBefore;
        attempt.SettledTargetHpAfter = result.Receiver.CurrentHp;
        attempt.SettledTargetBlockBefore = settledBlockBefore;
        attempt.SettledTargetBlockAfter = result.Receiver.Block;
        attempt.WasTargetKilled = result.WasTargetKilled;

        RecordExpectedDamageSettlement(settledTargetEntityId, attemptId, result.Receiver.CurrentHp, result.Receiver.Block);

        var targetSurvivedAfterResolution = result.Receiver.CurrentHp > 0;

        if (result.WasTargetKilled && !targetSurvivedAfterResolution && !ShouldDeferObservedStateEvent(settledTargetEntityId))
        {
            EmitEntityDiedIfNeeded(
                settledTargetEntityId,
                context.Phase,
                BuildLegacyDamageAttributionContext(context, executorSource),
                "damage");
        }
        else if (result.WasTargetKilled && !targetSurvivedAfterResolution)
        {
            MarkPendingKilledEntity(settledTargetEntityId);
            DeferEntityDiedEvent(
                settledTargetEntityId,
                context.Phase,
                BuildLegacyDamageAttributionContext(context, executorSource),
                context.ActorEntityId,
                "damage");
        }
    }

    private static List<object?> BuildDamageStepPayloads(
        DamageAttemptBuilder attempt,
        DamageResult result,
        string settledTargetEntityId,
        bool useRedirectCompanionSteps)
    {
        var payloads = new List<object?>();
        payloads.AddRange(attempt.DamageStageSteps.Select(BuildDamageStepPayload));

        var postBlockAmount = attempt.PostBlockAmount ?? Math.Max(attempt.DamageAfterDamageStage - result.BlockedDamage, 0m);
        payloads.Add(BuildDamageStepPayload(new DamageStepRecord
        {
            Stage = "block_absorption",
            Operation = "absorb",
            Before = attempt.DamageAfterDamageStage,
            After = postBlockAmount,
            Delta = postBlockAmount - attempt.DamageAfterDamageStage,
            TargetEntityId = attempt.OriginalTargetEntityId,
        }));

        payloads.AddRange(attempt.HpLostBeforeOstySteps.Select(BuildDamageStepPayload));
        payloads.AddRange(attempt.RedirectSteps.Select(BuildDamageStepPayload));
        payloads.AddRange((useRedirectCompanionSteps
                ? attempt.RedirectCompanionHpLostAfterOstySteps
                : attempt.HpLostAfterOstySteps)
            .Select(BuildDamageStepPayload));

        var settledBefore = useRedirectCompanionSteps
            ? attempt.RedirectCompanionHpLostAfterOstyAmount ?? attempt.HpLostAfterOstyAmount ?? postBlockAmount
            : attempt.HpLostAfterOstyAmount ?? postBlockAmount;
        payloads.Add(new Dictionary<string, object?>
        {
            ["stage"] = "settled",
            ["operation"] = "settle",
            ["before"] = settledBefore,
            ["after"] = result.UnblockedDamage,
            ["delta"] = (decimal)result.UnblockedDamage - settledBefore,
            ["target_entity_id"] = settledTargetEntityId,
            ["overkill_amount"] = result.OverkillDamage,
        });

        return payloads;
    }

    private static Dictionary<string, object?> BuildDamageStepPayload(DamageStepRecord step)
    {
        return new Dictionary<string, object?>
        {
            ["stage"] = step.Stage,
            ["operation"] = step.Operation,
            ["before"] = step.Before,
            ["after"] = step.After,
            ["delta"] = step.Delta,
            ["modifier_ref"] = step.ModifierRef,
            ["target_entity_id"] = step.TargetEntityId,
            ["is_unknown"] = step.IsUnknown ? true : null,
            ["unknown_reason"] = step.UnknownReason,
        };
    }

    private static DamageAttemptBuilder BeginDamageAttempt(
        DamageTruthCallContext context,
        Creature target,
        decimal baseAmount)
    {
        if (context.CurrentAttempt != null && ReferenceEquals(context.CurrentAttempt.OriginalTarget, target))
        {
            return context.CurrentAttempt;
        }

        var targetEntityId = ResolveEntityId(target) ?? BuildSyntheticEntityId(target);
        var attempt = new DamageAttemptBuilder
        {
            AttemptId = $"atk_{++_damageAttemptCounter:D5}",
            ParentAttemptId = context.ParentAttemptId,
            OriginalTarget = target,
            OriginalTargetEntityId = targetEntityId,
            OriginalTargetHpBefore = target.CurrentHp,
            OriginalTargetBlockBefore = target.Block,
            BaseAmount = baseAmount,
            DamageAfterDamageStage = baseAmount,
        };

        context.Attempts.Add(attempt);
        context.CurrentAttempt = attempt;
        return attempt;
    }

    private static DamageAttemptBuilder? BuildFallbackDamageAttempt(
        DamageTruthCallContext context,
        DamageResult result)
    {
        var candidate = TakeFallbackModifyCandidate(context, result);
        var originalTarget = candidate?.Target ?? result.Receiver;
        var originalTargetEntityId = ResolveEntityId(originalTarget) ?? BuildSyntheticEntityId(originalTarget);
        var settledTargetEntityId = ResolveEntityId(result.Receiver) ?? BuildSyntheticEntityId(result.Receiver);
        var originalTargetIsReceiver = ReferenceEquals(originalTarget, result.Receiver);
        var baseAmount = candidate?.InputAmount ?? context.RequestedAmount;
        var damageAfterDamageStage = candidate?.FinalAmount ?? Math.Max(context.RequestedAmount, result.BlockedDamage + result.UnblockedDamage + result.OverkillDamage);

        var attempt = new DamageAttemptBuilder
        {
            AttemptId = $"atk_{++_damageAttemptCounter:D5}",
            ParentAttemptId = context.ParentAttemptId,
            OriginalTarget = originalTarget,
            OriginalTargetEntityId = originalTargetEntityId,
            OriginalTargetHpBefore = originalTargetIsReceiver
                ? result.Receiver.CurrentHp + result.UnblockedDamage
                : originalTarget.CurrentHp,
            OriginalTargetBlockBefore = originalTargetIsReceiver
                ? result.Receiver.Block + result.BlockedDamage
                : originalTarget.Block,
            BaseAmount = baseAmount,
            DamageAfterDamageStage = damageAfterDamageStage,
        };

        if (candidate != null)
        {
            attempt.DamageStageSteps.AddRange(candidate.Steps);
        }

        if (attempt.DamageStageSteps.Count == 0)
        {
            attempt.DamageStageSteps.Add(new DamageStepRecord
            {
                Stage = "base",
                Operation = "base",
                Before = null,
                After = baseAmount,
                Delta = null,
                TargetEntityId = originalTargetEntityId,
                IsUnknown = candidate == null,
                UnknownReason = candidate == null ? "result_only_fallback" : null,
            });
        }

        if (!originalTargetIsReceiver)
        {
            attempt.RedirectTarget = result.Receiver;
            attempt.RedirectTargetEntityId = settledTargetEntityId;
            attempt.RedirectTargetHpBefore = result.Receiver.CurrentHp + result.UnblockedDamage;
            attempt.RedirectTargetBlockBefore = result.Receiver.Block + result.BlockedDamage;
        }

        if (candidate == null)
        {
            attempt.UnknownFlags.Add("modify_damage_unmatched");
        }

        DebugFileLogger.Log(
            nameof(BattleLogger) + ".BuildFallbackDamageAttempt",
            $"Synthesized fallback damage attempt. receiver={settledTargetEntityId}, group_id={context.AttemptGroupId}, has_modify_candidate={candidate != null}");

        return attempt;
    }

    private static DamageModifyCandidate? TakeFallbackModifyCandidate(
        DamageTruthCallContext context,
        DamageResult result)
    {
        if (context.PendingModifyDamageCandidates.Count == 0)
            return null;

        var receiverEntityId = ResolveEntityId(result.Receiver) ?? BuildSyntheticEntityId(result.Receiver);
        var candidateIndex = context.PendingModifyDamageCandidates.FindIndex(candidate =>
            ReferenceEquals(candidate.Target, result.Receiver) ||
            string.Equals(
                ResolveEntityId(candidate.Target) ?? BuildSyntheticEntityId(candidate.Target),
                receiverEntityId,
                StringComparison.Ordinal));

        if (candidateIndex < 0)
        {
            candidateIndex = 0;
        }

        var candidate = context.PendingModifyDamageCandidates[candidateIndex];
        context.PendingModifyDamageCandidates.RemoveAt(candidateIndex);
        return candidate;
    }

    private static DamageTruthCallContext? GetCurrentDamageTruthCall()
    {
        var stack = EnsureStack(_damageTruthCallStack);
        return stack.Count > 0 ? stack.Peek() : null;
    }

    private static DamageAttemptBuilder? GetCurrentDamageAttempt()
    {
        return GetCurrentDamageTruthCall()?.CurrentAttempt;
    }

    private static DamageAttemptBuilder? GetCurrentDamageAttempt(Creature target)
    {
        var context = GetCurrentDamageTruthCall();
        if (context == null)
            return null;

        if (context.CurrentAttempt != null && ReferenceEquals(context.CurrentAttempt.OriginalTarget, target))
        {
            return context.CurrentAttempt;
        }

        return context.Attempts.LastOrDefault(attempt => ReferenceEquals(attempt.OriginalTarget, target));
    }

    private static AttackTruthGroupContext? GetActiveAttackTruthGroup()
    {
        var stack = EnsureStack(_attackTruthGroupStack);
        return stack.Count > 0 ? stack.Peek() : null;
    }

    private static Stack<T> EnsureStack<T>(AsyncLocal<Stack<T>?> slot)
    {
        return slot.Value ??= new Stack<T>();
    }

    private static TruthResolutionSnapshot? GetActiveTruthResolutionSnapshot()
    {
        var activePlay = GetActivePlayContext();
        if (activePlay != null)
        {
            return new TruthResolutionSnapshot
            {
                ResolutionId = activePlay.ResolutionId,
                ParentResolutionId = activePlay.ParentResolutionId,
                ResolutionDepth = activePlay.ResolutionDepth,
                RootSource = DescribePlayResolutionSource(activePlay),
            };
        }

        if (_pendingManualPlayContext != null)
        {
            return new TruthResolutionSnapshot
            {
                ResolutionId = _pendingManualPlayContext.ResolutionId,
                ParentResolutionId = _pendingManualPlayContext.ParentResolutionId,
                ResolutionDepth = _pendingManualPlayContext.ResolutionDepth,
                RootSource = DescribePlayResolutionSource(_pendingManualPlayContext),
            };
        }

        if (_activePotionContext != null && _phase == "player_action")
        {
            return new TruthResolutionSnapshot
            {
                ResolutionId = _activePotionContext.ResolutionId,
                RootSource = DescribePotionResolutionSource(_activePotionContext),
            };
        }

        if (_activeEnemyActionContext != null)
        {
            return new TruthResolutionSnapshot
            {
                ResolutionId = _activeEnemyActionContext.ResolutionId,
                RootSource = DescribeEnemyMoveSource(_activeEnemyActionContext),
            };
        }

        var retainedResolutionStack = EnsureStack(_retainedTruthResolutionScopeStack);
        if (retainedResolutionStack.Count > 0)
        {
            return retainedResolutionStack.Peek();
        }

        return null;
    }

    private static Dictionary<string, object?>? ResolveAttackGroupExecutor(AttackCommand command)
    {
        if (command.ModelSource is CardModel card)
        {
            return DescribeSource(card);
        }

        var attacker = command.Attacker;
        if (_activeEnemyActionContext != null &&
            attacker != null &&
            ReferenceEquals(_activeEnemyActionContext.ActorCreature, attacker))
        {
            return DescribeEnemyMoveSource(_activeEnemyActionContext);
        }

        return null;
    }

    private static Dictionary<string, object?>? ResolveDamageExecutor(
        PlayerChoiceContext choiceContext,
        Creature? dealer,
        CardModel? cardSource,
        AttackTruthGroupContext? activeAttackGroup)
    {
        if (cardSource != null)
        {
            return DescribeSource(cardSource);
        }

        var scopedSource = EnsureStack(_truthSourceScopeStack).Count > 0
            ? EnsureStack(_truthSourceScopeStack).Peek()
            : null;
        if (scopedSource != null)
        {
            return DescribeSource(scopedSource);
        }

        var involvedModel = choiceContext.LastInvolvedModel;
        if (involvedModel != null)
        {
            return DescribeSource(involvedModel);
        }

        if (activeAttackGroup?.ExecutorSource != null)
        {
            return activeAttackGroup.ExecutorSource;
        }

        if (_activeEnemyActionContext != null &&
            dealer != null &&
            ReferenceEquals(_activeEnemyActionContext.ActorCreature, dealer))
        {
            return DescribeEnemyMoveSource(_activeEnemyActionContext);
        }

        return null;
    }

    private static Dictionary<string, object?>? ResolveDamageTrigger(
        DamageAttemptBuilder? parentAttempt,
        Dictionary<string, object?>? resolutionRootSource,
        Dictionary<string, object?>? executorSource)
    {
        if (parentAttempt != null)
        {
            var parentSource = GetCurrentDamageTruthCall()?.ExecutorSource;
            if (parentSource != null && !SourcesEquivalent(parentSource, executorSource))
            {
                return parentSource;
            }
        }

        if (resolutionRootSource != null && !SourcesEquivalent(resolutionRootSource, executorSource))
        {
            return resolutionRootSource;
        }

        return null;
    }

    private static void ReplayModifyDamageSteps(
        List<DamageStepRecord> sink,
        IRunState runState,
        CombatState? combatState,
        Creature? target,
        Creature? dealer,
        decimal damage,
        ValueProp props,
        CardModel? cardSource,
        ModifyDamageHookType modifyDamageHookType)
    {
        sink.Add(new DamageStepRecord
        {
            Stage = "base",
            Operation = "base",
            After = damage,
        });

        var current = damage;
        if (cardSource?.Enchantment != null)
        {
            if (modifyDamageHookType.HasFlag(ModifyDamageHookType.Additive))
            {
                var delta = cardSource.Enchantment.EnchantDamageAdditive(current, props);
                if (delta != 0m)
                {
                    var after = current + delta;
                    sink.Add(new DamageStepRecord
                    {
                        Stage = "damage_additive",
                        Operation = "additive",
                        Before = current,
                        After = after,
                        Delta = after - current,
                        ModifierRef = DescribeSource(cardSource.Enchantment),
                    });
                    current = after;
                }
            }

            if (modifyDamageHookType.HasFlag(ModifyDamageHookType.Multiplicative))
            {
                var multiplier = cardSource.Enchantment.EnchantDamageMultiplicative(current, props);
                if (multiplier != 1m)
                {
                    var after = current * multiplier;
                    sink.Add(new DamageStepRecord
                    {
                        Stage = "damage_multiplicative",
                        Operation = "multiplicative",
                        Before = current,
                        After = after,
                        Delta = after - current,
                        ModifierRef = DescribeSource(cardSource.Enchantment),
                    });
                    current = after;
                }
            }
        }

        if (modifyDamageHookType.HasFlag(ModifyDamageHookType.Additive))
        {
            foreach (var listener in runState.IterateHookListeners(combatState))
            {
                var delta = listener.ModifyDamageAdditive(target, current, props, dealer, cardSource);
                if (delta == 0m)
                    continue;

                var after = current + delta;
                sink.Add(new DamageStepRecord
                {
                    Stage = "damage_additive",
                    Operation = "additive",
                    Before = current,
                    After = after,
                    Delta = after - current,
                    ModifierRef = DescribeSource(listener),
                });
                current = after;
            }
        }

        if (modifyDamageHookType.HasFlag(ModifyDamageHookType.Multiplicative))
        {
            foreach (var listener in runState.IterateHookListeners(combatState))
            {
                var multiplier = listener.ModifyDamageMultiplicative(target, current, props, dealer, cardSource);
                if (multiplier == 1m)
                    continue;

                var after = current * multiplier;
                sink.Add(new DamageStepRecord
                {
                    Stage = "damage_multiplicative",
                    Operation = "multiplicative",
                    Before = current,
                    After = after,
                    Delta = after - current,
                    ModifierRef = DescribeSource(listener),
                });
                current = after;
            }
        }

        var currentCap = decimal.MaxValue;
        foreach (var listener in runState.IterateHookListeners(combatState))
        {
            var candidateCap = listener.ModifyDamageCap(target, props, dealer, cardSource);
            if (candidateCap >= currentCap)
                continue;

            currentCap = candidateCap;
            if (current <= candidateCap)
                continue;

            sink.Add(new DamageStepRecord
            {
                Stage = "damage_cap",
                Operation = "cap",
                Before = current,
                After = candidateCap,
                Delta = candidateCap - current,
                ModifierRef = DescribeSource(listener),
            });
            current = candidateCap;
        }

        if (current < 0m)
        {
            sink.Add(new DamageStepRecord
            {
                Stage = "damage_floor",
                Operation = "floor",
                Before = current,
                After = 0m,
                Delta = -current,
            });
        }
    }

    private static void ReplayHpLostBeforeOstySteps(
        List<DamageStepRecord> sink,
        IRunState runState,
        CombatState? combatState,
        Creature target,
        decimal amount,
        ValueProp props,
        Creature? dealer,
        CardModel? cardSource)
    {
        ReplayHpLossModificationSteps(
            sink,
            "hp_loss_before_osty",
            runState,
            combatState,
            target,
            amount,
            props,
            dealer,
            cardSource,
            (listener, current) => listener.ModifyHpLostBeforeOsty(target, current, props, dealer, cardSource),
            (listener, current) => listener.ModifyHpLostBeforeOstyLate(target, current, props, dealer, cardSource));
    }

    private static void ReplayHpLostAfterOstySteps(
        List<DamageStepRecord> sink,
        IRunState runState,
        CombatState? combatState,
        Creature target,
        decimal amount,
        ValueProp props,
        Creature? dealer,
        CardModel? cardSource)
    {
        ReplayHpLossModificationSteps(
            sink,
            "hp_loss_after_osty",
            runState,
            combatState,
            target,
            amount,
            props,
            dealer,
            cardSource,
            (listener, current) => listener.ModifyHpLostAfterOsty(target, current, props, dealer, cardSource),
            (listener, current) => listener.ModifyHpLostAfterOstyLate(target, current, props, dealer, cardSource));
    }

    private static void ReplayHpLossModificationSteps(
        List<DamageStepRecord> sink,
        string stage,
        IRunState runState,
        CombatState? combatState,
        Creature target,
        decimal amount,
        ValueProp props,
        Creature? dealer,
        CardModel? cardSource,
        Func<AbstractModel, decimal, decimal> primaryTransform,
        Func<AbstractModel, decimal, decimal> lateTransform)
    {
        var current = amount;
        foreach (var listener in runState.IterateHookListeners(combatState))
        {
            var after = primaryTransform(listener, current);
            if (after != current)
            {
                sink.Add(new DamageStepRecord
                {
                    Stage = stage,
                    Operation = "adjust",
                    Before = current,
                    After = after,
                    Delta = after - current,
                    ModifierRef = DescribeSource(listener),
                    TargetEntityId = ResolveEntityId(target) ?? BuildSyntheticEntityId(target),
                });
                current = after;
            }
        }

        foreach (var listener in runState.IterateHookListeners(combatState))
        {
            var after = lateTransform(listener, current);
            if (after != current)
            {
                sink.Add(new DamageStepRecord
                {
                    Stage = stage,
                    Operation = "adjust_late",
                    Before = current,
                    After = after,
                    Delta = after - current,
                    ModifierRef = DescribeSource(listener),
                    TargetEntityId = ResolveEntityId(target) ?? BuildSyntheticEntityId(target),
                });
                current = after;
            }
        }
    }

    private static void ReplayRedirectSteps(
        List<DamageStepRecord> sink,
        CombatState combatState,
        Creature originalTarget,
        decimal amount,
        ValueProp props,
        Creature? dealer)
    {
        var currentTarget = originalTarget;
        foreach (var listener in combatState.IterateHookListeners())
        {
            var redirectedTarget = listener.ModifyUnblockedDamageTarget(currentTarget, amount, props, dealer);
            if (ReferenceEquals(redirectedTarget, currentTarget))
                continue;

            sink.Add(new DamageStepRecord
            {
                Stage = "target_redirect",
                Operation = "redirect",
                ModifierRef = DescribeSource(listener),
                TargetEntityId = ResolveEntityId(redirectedTarget) ?? BuildSyntheticEntityId(redirectedTarget),
            });
            currentTarget = redirectedTarget;
        }
    }

    private static Dictionary<string, object?>? DescribePlayResolutionSource(PlayContext context)
    {
        if (_cardModelToId.Values.Contains(context.CardId, StringComparer.Ordinal))
        {
            return new Dictionary<string, object?>
            {
                ["kind"] = "card_instance",
                ["ref"] = context.CardId,
                ["card_instance_id"] = context.CardId,
                ["owner_entity_id"] = _playerEntityId,
            };
        }

        return null;
    }

    private static Dictionary<string, object?> DescribePotionResolutionSource(PotionContext context)
    {
        return new Dictionary<string, object?>
        {
            ["kind"] = "potion_instance",
            ["ref"] = context.PotionId,
            ["potion_instance_id"] = context.PotionId,
            ["owner_entity_id"] = _playerEntityId,
        };
    }

    private static Dictionary<string, object?> DescribeEnemyMoveSource(EnemyActionContext context)
    {
        return new Dictionary<string, object?>
        {
            ["kind"] = "enemy_move",
            ["ref"] = $"move:{context.ActorEntityId}:{context.MoveId}",
            ["owner_entity_id"] = context.ActorEntityId,
            ["move_id"] = context.MoveId,
        };
    }

    private static Dictionary<string, object?>? DescribeSource(AbstractModel model)
    {
        return model switch
        {
            CardModel card => DescribeCardSource(card),
            PotionModel potion => DescribePotionSource(potion),
            OrbModel orb => DescribeOrbSource(orb),
            PowerModel power => DescribePowerSource(power),
            RelicModel relic => DescribeRelicSource(relic),
            MonsterModel monster => DescribeMonsterSource(monster),
            _ => DescribeGenericModelSource(model),
        };
    }

    private static Dictionary<string, object?>? DescribeCardSource(CardModel card)
    {
        if (_cardModelToId.TryGetValue(card, out var cardId))
        {
            return new Dictionary<string, object?>
            {
                ["kind"] = "card_instance",
                ["ref"] = cardId,
                ["card_instance_id"] = cardId,
                ["owner_entity_id"] = _playerEntityId,
                ["model_id"] = card.Id?.Entry ?? GameStateReader.ToSnakeCase(card.GetType().Name),
            };
        }

        return DescribeGenericModelSource(card);
    }

    private static Dictionary<string, object?>? DescribePotionSource(PotionModel potion)
    {
        if (_potionModelToId.TryGetValue(potion, out var potionId))
        {
            return new Dictionary<string, object?>
            {
                ["kind"] = "potion_instance",
                ["ref"] = potionId,
                ["potion_instance_id"] = potionId,
                ["owner_entity_id"] = _playerEntityId,
                ["model_id"] = potion.Id?.Entry ?? GameStateReader.ToSnakeCase(potion.GetType().Name),
            };
        }

        return DescribeGenericModelSource(potion);
    }

    private static Dictionary<string, object?> DescribePowerSource(PowerModel power)
    {
        var ownerEntityId = ResolveEntityId(power.Owner);
        var powerInstanceId = GetPowerInstanceId(power);
        var payload = new Dictionary<string, object?>
        {
            ["kind"] = "power_instance",
            ["ref"] = powerInstanceId,
            ["power_instance_id"] = powerInstanceId,
            ["owner_entity_id"] = ownerEntityId,
            ["power_id"] = GetPowerId(power),
            ["power_name"] = GetPowerName(power),
        };

        if (power.Applier != null)
        {
            payload["applier_entity_id"] = ResolveEntityId(power.Applier);
        }

        if (power is ITemporaryPower temporaryPower)
        {
            try
            {
                var origin = DescribeSource(temporaryPower.OriginModel);
                if (origin != null)
                {
                    payload["origin"] = origin;
                }
            }
            catch
            {
                // Some temporary powers point at canonical card/potion models whose
                // owner access is invalid at runtime; omit origin rather than
                // breaking truth emission for the power instance itself.
            }
        }

        return payload;
    }

    private static Dictionary<string, object?> DescribeRelicSource(RelicModel relic)
    {
        var ownerEntityId = ResolveEntityId(relic.Owner?.Creature);
        var relicId = GameStateReader.GetRelicId(relic);
        var relicInstanceId = GetRelicInstanceId(relic);
        return new Dictionary<string, object?>
        {
            ["kind"] = "relic",
            ["ref"] = relicInstanceId,
            ["relic_instance_id"] = relicInstanceId,
            ["owner_entity_id"] = ownerEntityId,
            ["relic_id"] = relicId,
            ["relic_name"] = GameStateReader.GetRelicName(relic),
        };
    }

    private static Dictionary<string, object?> DescribeMonsterSource(MonsterModel monster)
    {
        if (_activeEnemyActionContext != null &&
            ReferenceEquals(_activeEnemyActionContext.ActorCreature, monster.Creature))
        {
            return DescribeEnemyMoveSource(_activeEnemyActionContext);
        }

        var ownerEntityId = ResolveEntityId(monster.Creature) ?? BuildSyntheticEntityId(monster.Creature);
        return new Dictionary<string, object?>
        {
            ["kind"] = "enemy_move",
            ["ref"] = $"move:{ownerEntityId}:{monster.NextMove.Id}",
            ["owner_entity_id"] = ownerEntityId,
            ["move_id"] = monster.NextMove.Id,
            ["model_id"] = monster.Id?.Entry ?? GameStateReader.ToSnakeCase(monster.GetType().Name),
        };
    }

    private static Dictionary<string, object?> DescribeGenericModelSource(AbstractModel model)
    {
        return new Dictionary<string, object?>
        {
            ["kind"] = "generic_model",
            ["ref"] = $"model:{model.Id?.Entry ?? GameStateReader.ToSnakeCase(model.GetType().Name)}:{RuntimeHelpers.GetHashCode(model):x}",
            ["model_id"] = model.Id?.Entry ?? GameStateReader.ToSnakeCase(model.GetType().Name),
            ["model_type"] = model.GetType().Name,
            ["owner_entity_id"] = ResolveOwnerEntityId(model),
        };
    }

    private static string GetPowerInstanceId(PowerModel power)
    {
        if (_powerModelToInstanceId.TryGetValue(power, out var existingId))
        {
            return existingId;
        }

        var nextId = $"power:{++_powerInstanceCounter:D4}";
        _powerModelToInstanceId[power] = nextId;
        return nextId;
    }

    private static string? ResolveEntityId(Creature? creature)
    {
        if (creature != null && _entityIds.TryGetValue(creature, out var entityId))
        {
            return entityId;
        }

        return null;
    }

    private static string? ResolveOwnerEntityId(AbstractModel model)
    {
        return model switch
        {
            CardModel card => ResolveEntityId(card.Owner?.Creature),
            PotionModel potion => ResolveEntityId(potion.Owner?.Creature),
            OrbModel orb => ResolveEntityId(orb.Owner?.Creature),
            RelicModel relic => ResolveEntityId(relic.Owner?.Creature),
            PowerModel power => ResolveEntityId(power.Owner),
            MonsterModel monster => ResolveEntityId(monster.Creature),
            _ => null,
        };
    }

    private static string BuildSyntheticEntityId(Creature creature)
    {
        return $"unknown_entity:{RuntimeHelpers.GetHashCode(creature):x}";
    }

    private static Dictionary<string, object?> BuildUnknownSource(string reason)
    {
        return new Dictionary<string, object?>
        {
            ["kind"] = "unknown",
            ["ref"] = $"unknown:{reason}",
            ["unknown_reason"] = reason,
        };
    }

    private static bool SourcesEquivalent(
        Dictionary<string, object?>? left,
        Dictionary<string, object?>? right)
    {
        if (left == null || right == null)
            return false;

        var leftRef = TryGetSourceRef(left);
        var rightRef = TryGetSourceRef(right);
        return !string.IsNullOrEmpty(leftRef) &&
               string.Equals(leftRef, rightRef, StringComparison.Ordinal);
    }

    private static string? TryGetSourceRef(Dictionary<string, object?> source)
    {
        return source.TryGetValue("ref", out var value) ? value as string : null;
    }

    private static AttributionContext? BuildLegacyDamageAttributionContext(
        DamageTruthCallContext context,
        Dictionary<string, object?> executorSource)
    {
        var sourceKind = executorSource.TryGetValue("kind", out var kindValue) ? kindValue as string : null;
        return new AttributionContext
        {
            ResolutionId = context.Resolution?.ResolutionId ?? context.AttemptGroupId,
            SourceEntityId = context.ActorEntityId ?? _playerEntityId,
            SourceKind = sourceKind switch
            {
                "card_instance" => "card",
                "potion_instance" => "potion",
                "enemy_move" => "enemy_action",
                "power_instance" => "triggered_effect",
                "relic" => "triggered_effect",
                "generic_model" => "triggered_effect",
                _ => "unknown",
            },
            CardId = executorSource.TryGetValue("card_instance_id", out var cardId) ? cardId as string : null,
            PotionId = executorSource.TryGetValue("potion_instance_id", out var potionId) ? potionId as string : null,
            ParentResolutionId = context.Resolution?.ParentResolutionId,
            ResolutionDepth = context.Resolution?.ResolutionDepth,
        };
    }

    private static void RecordExpectedDamageSettlement(
        string entityId,
        string attemptId,
        int expectedHp,
        int expectedBlock)
    {
        _expectedDamageSettlements[entityId] = new ExpectedDamageSettlement
        {
            AttemptId = attemptId,
            ExpectedHp = expectedHp,
            ExpectedBlock = expectedBlock,
        };
    }

    private static void ObserveDamageTruthValidation(string entityId, int hp, int block)
    {
        if (!_expectedDamageSettlements.TryGetValue(entityId, out var expected))
        {
            return;
        }

        if (expected.ExpectedHp == hp && expected.ExpectedBlock == block)
        {
            _expectedDamageSettlements.Remove(entityId);
            return;
        }

        var hpChanged = _prevHp.TryGetValue(entityId, out var prevHp) && prevHp != hp;
        var blockChanged = _prevBlock.TryGetValue(entityId, out var prevBlock) && prevBlock != block;
        if (!hpChanged && !blockChanged)
        {
            return;
        }

        DebugFileLogger.Log(
            nameof(BattleLogger) + ".ObserveDamageTruthValidation",
            $"Damage truth drift detected. entity_id={entityId}, attempt_id={expected.AttemptId}, expected_hp={expected.ExpectedHp}, observed_hp={hp}, expected_block={expected.ExpectedBlock}, observed_block={block}");
        _expectedDamageSettlements.Remove(entityId);
    }

    private static bool ShouldDeferObservedStateEvent(string entityId)
    {
        return _pendingDamageTruthEntityCounts.TryGetValue(entityId, out var count) && count > 0;
    }

    private static void DeferObservedStateEvent(
        string entityId,
        string eventType,
        string phase,
        AttributionContext? attrContext,
        Dictionary<string, object?> payload,
        EventDispatchMode dispatchMode = EventDispatchMode.PublicOnly,
        string? deathSourceEntityId = null,
        string? deathReason = null,
        bool insertBeforeExistingEntityEvents = false)
    {
        var deferredEvent = new DeferredObservedStateEvent
        {
            EntityId = entityId,
            EventType = eventType,
            Phase = phase,
            AttributionContext = attrContext,
            Payload = new Dictionary<string, object?>(payload),
            DispatchMode = dispatchMode,
            DeathSourceEntityId = deathSourceEntityId,
            DeathReason = deathReason,
        };

        if (!insertBeforeExistingEntityEvents)
        {
            _deferredObservedStateEvents.Add(deferredEvent);
            return;
        }

        var insertIndex = _deferredObservedStateEvents.FindIndex(existing =>
            string.Equals(existing.EntityId, entityId, StringComparison.Ordinal));
        if (insertIndex >= 0)
        {
            _deferredObservedStateEvents.Insert(insertIndex, deferredEvent);
        }
        else
        {
            _deferredObservedStateEvents.Add(deferredEvent);
        }
    }

    private static void DeferEntityDiedEvent(
        string entityId,
        string phase,
        AttributionContext? attrContext,
        string? sourceEntityId,
        string reason)
    {
        _deferredEntityDiedEvents[entityId] = new DeferredEntityDiedEvent
        {
            EntityId = entityId,
            Phase = phase,
            AttributionContext = attrContext,
            SourceEntityId = sourceEntityId,
            Reason = reason,
        };
    }

    private static void MarkPendingKilledEntity(string entityId)
    {
        _pendingKilledEntityIds.Add(entityId);
    }

    private static bool ShouldSuppressEntityRemovedForPendingKill(string entityId)
    {
        return _pendingKilledEntityIds.Contains(entityId);
    }

    private static void TrackPendingDamageTruthEntity(string? entityId)
    {
        if (string.IsNullOrWhiteSpace(entityId))
            return;

        _pendingDamageTruthEntityCounts.TryGetValue(entityId, out var existingCount);
        _pendingDamageTruthEntityCounts[entityId] = existingCount + 1;
    }

    private static void ReleaseDeferredObservedStateForContext(DamageTruthCallContext context)
    {
        var releasedEntityIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trackedEntityId in context.TrackedOriginalTargetEntityIds)
        {
            ReleasePendingDamageTruthEntity(trackedEntityId, releasedEntityIds);
        }

        foreach (var attempt in context.Attempts)
        {
            ReleasePendingDamageTruthEntity(attempt.RedirectTargetEntityId, releasedEntityIds);
        }

        if (releasedEntityIds.Count == 0)
            return;

        EnqueueMissingObservedDamageSettlementFallbacks(context, releasedEntityIds);
        FlushDeferredObservedStateEvents(releasedEntityIds);
        FlushDeferredEntityDiedEvents(releasedEntityIds);
    }

    // Some settlements are never observed by polling before the damage-truth
    // scope releases, especially when retaliation kills the actor and battle
    // end starts immediately after the original hit still lands.
    private static void EnqueueMissingObservedDamageSettlementFallbacks(
        DamageTruthCallContext context,
        HashSet<string> releasedEntityIds)
    {
        if (releasedEntityIds.Count == 0)
            return;

        foreach (var attempt in context.Attempts)
        {
            if (string.IsNullOrWhiteSpace(attempt.SettledTargetEntityId) ||
                !releasedEntityIds.Contains(attempt.SettledTargetEntityId))
            {
                continue;
            }

            var entityId = attempt.SettledTargetEntityId;
            var attrContext = context.ExecutorSource != null
                ? BuildLegacyDamageAttributionContext(context, context.ExecutorSource)
                : null;
            var blockTriggerRef = context.ExecutorSource != null
                ? SanitizePowerTruthRef(context.ExecutorSource)
                : null;
            var hasDeferredHpChanged = _deferredObservedStateEvents.Any(existing =>
                existing.EventType == "hp_changed" &&
                string.Equals(existing.EntityId, entityId, StringComparison.Ordinal));
            var hasDeferredBlockChanged = _deferredObservedStateEvents.Any(existing =>
                existing.EventType == "block_changed" &&
                string.Equals(existing.EntityId, entityId, StringComparison.Ordinal));
            var hpChangedMissing =
                !hasDeferredHpChanged &&
                attempt.SettledTargetHpBefore.HasValue &&
                attempt.SettledTargetHpAfter.HasValue &&
                attempt.SettledTargetHpBefore.Value != attempt.SettledTargetHpAfter.Value;
            var blockChangedMissing =
                !hasDeferredBlockChanged &&
                attempt.SettledTargetBlockBefore.HasValue &&
                attempt.SettledTargetBlockAfter.HasValue &&
                attempt.SettledTargetBlockBefore.Value != attempt.SettledTargetBlockAfter.Value;
            var settledTargetHpBefore = attempt.SettledTargetHpBefore.GetValueOrDefault();
            var settledTargetHpAfter = attempt.SettledTargetHpAfter.GetValueOrDefault();
            var settledTargetBlockBefore = attempt.SettledTargetBlockBefore.GetValueOrDefault();
            var settledTargetBlockAfter = attempt.SettledTargetBlockAfter.GetValueOrDefault();
            var targetSurvivedAfterResolution = attempt.WasTargetKilled && settledTargetHpAfter > 0;

            if (blockChangedMissing)
            {
                var blockPayload = new Dictionary<string, object?>
                {
                    ["entity_id"] = entityId,
                    ["old"] = settledTargetBlockBefore,
                    ["new"] = settledTargetBlockAfter,
                    ["delta"] = settledTargetBlockAfter - settledTargetBlockBefore,
                    ["reason"] = settledTargetBlockAfter > settledTargetBlockBefore
                        ? "block_gain"
                        : "damage_absorption",
                };
                if (blockTriggerRef != null)
                {
                    blockPayload["trigger"] = blockTriggerRef;
                }

                DeferObservedStateEvent(
                    entityId,
                    "block_changed",
                    context.Phase,
                    attrContext,
                    blockPayload,
                    insertBeforeExistingEntityEvents: true);

                if (settledTargetBlockBefore > 0 && settledTargetBlockAfter <= 0)
                {
                    var blockBrokenPayload = new Dictionary<string, object?>
                    {
                        ["entity_id"] = entityId,
                        ["old_block"] = settledTargetBlockBefore,
                        ["new_block"] = settledTargetBlockAfter,
                    };
                    if (blockTriggerRef != null)
                    {
                        blockBrokenPayload["trigger"] = blockTriggerRef;
                    }

                    DeferObservedStateEvent(
                        entityId,
                        "block_broken",
                        context.Phase,
                        attrContext,
                        blockBrokenPayload,
                        insertBeforeExistingEntityEvents: true);
                }
            }

            if (hpChangedMissing && !targetSurvivedAfterResolution)
            {
                DeferObservedStateEvent(
                    entityId,
                    "hp_changed",
                    context.Phase,
                    attrContext,
                    new Dictionary<string, object?>
                    {
                        ["entity_id"] = entityId,
                        ["old"] = settledTargetHpBefore,
                        ["new"] = settledTargetHpAfter,
                        ["delta"] = settledTargetHpAfter - settledTargetHpBefore,
                        ["reason"] = "damage",
                    },
                    deathSourceEntityId: attrContext?.SourceEntityId,
                    deathReason: settledTargetHpAfter <= 0 ? "damage" : null,
                    insertBeforeExistingEntityEvents: true);
            }

            if (attempt.SettledTargetHpAfter.HasValue)
            {
                _prevHp[entityId] = attempt.SettledTargetHpAfter.Value;
            }

            if (attempt.SettledTargetBlockAfter.HasValue)
            {
                _prevBlock[entityId] = attempt.SettledTargetBlockAfter.Value;
            }
        }
    }

    private static void ReleasePendingDamageTruthEntity(string? entityId, HashSet<string> releasedEntityIds)
    {
        if (string.IsNullOrWhiteSpace(entityId) ||
            !_pendingDamageTruthEntityCounts.TryGetValue(entityId, out var count))
        {
            return;
        }

        if (count <= 1)
        {
            _pendingDamageTruthEntityCounts.Remove(entityId);
            releasedEntityIds.Add(entityId);
            return;
        }

        _pendingDamageTruthEntityCounts[entityId] = count - 1;
    }

    private static void FlushDeferredObservedStateEvents(HashSet<string> releasedEntityIds)
    {
        if (releasedEntityIds.Count == 0 || _deferredObservedStateEvents.Count == 0)
            return;

        var readyToFlush = new List<DeferredObservedStateEvent>();
        var retained = new List<DeferredObservedStateEvent>();

        foreach (var deferredEvent in _deferredObservedStateEvents)
        {
            if (releasedEntityIds.Contains(deferredEvent.EntityId) &&
                !ShouldDeferObservedStateEvent(deferredEvent.EntityId))
            {
                readyToFlush.Add(deferredEvent);
            }
            else
            {
                retained.Add(deferredEvent);
            }
        }

        _deferredObservedStateEvents = retained;

        foreach (var deferredEvent in readyToFlush)
        {
            EmitEvent(
                deferredEvent.EventType,
                deferredEvent.Phase,
                deferredEvent.AttributionContext,
                deferredEvent.Payload,
                dispatchMode: deferredEvent.DispatchMode);
            MarkPendingSnapshotRelevantChange(deferredEvent.EventType);

            if (deferredEvent.EventType == "hp_changed" &&
                deferredEvent.Payload.TryGetValue("new", out var newValue) &&
                Convert.ToInt32(newValue) <= 0 &&
                !string.IsNullOrEmpty(deferredEvent.DeathReason))
            {
                EmitEntityDiedIfNeeded(
                    deferredEvent.EntityId,
                    deferredEvent.Phase,
                    deferredEvent.AttributionContext,
                    deferredEvent.DeathReason!);
            }

            if (deferredEvent.EventType == "hp_changed" &&
                deferredEvent.Payload.TryGetValue("old", out var oldHpValue) &&
                deferredEvent.Payload.TryGetValue("new", out var healedHpValue) &&
                Convert.ToInt32(healedHpValue) > Convert.ToInt32(oldHpValue) &&
                _pendingEntityRevivedEvents.TryGetValue(deferredEvent.EntityId, out var pendingRevive))
            {
                EmitEntityRevivedEvent(pendingRevive);
            }
        }
    }

    private static void FlushDeferredEntityDiedEvents(HashSet<string> releasedEntityIds)
    {
        if (releasedEntityIds.Count == 0 || _deferredEntityDiedEvents.Count == 0)
            return;

        foreach (var entityId in releasedEntityIds)
        {
            if (!_deferredEntityDiedEvents.TryGetValue(entityId, out var deferredDeath))
                continue;

            EmitEntityDiedIfNeeded(
                deferredDeath.EntityId,
                deferredDeath.Phase,
                deferredDeath.AttributionContext,
                deferredDeath.Reason);
        }
    }
}
