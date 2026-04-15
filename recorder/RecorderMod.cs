using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;

namespace STS2CombatRecorder;

[ModInitializer("Initialize")]
public static class RecorderMod
{
    private readonly struct CardCostMutationState
    {
        public CardCostMutationState(CardModel? card, int oldCost)
        {
            Card = card;
            OldCost = oldCost;
        }

        public CardModel? Card { get; }
        public int OldCost { get; }
    }

    private readonly struct CardTruthMutationRecord
    {
        public CardTruthMutationRecord(CardModel? card, CardTruthStateSnapshot? oldState)
        {
            Card = card;
            OldState = oldState;
        }

        public CardModel? Card { get; }
        public CardTruthStateSnapshot? OldState { get; }
    }

    private readonly struct CardTruthMutationState
    {
        public CardTruthMutationState(IReadOnlyList<CardTruthMutationRecord> records)
        {
            Records = records;
        }

        public IReadOnlyList<CardTruthMutationRecord> Records { get; }
    }

    private readonly struct OrbSlotsMutationState
    {
        public OrbSlotsMutationState(Player? player, int oldSlots, string reason)
        {
            Player = player;
            OldSlots = oldSlots;
            Reason = reason;
        }

        public Player? Player { get; }
        public int OldSlots { get; }
        public string Reason { get; }
    }

    private readonly struct OrbReplaceState
    {
        public OrbReplaceState(Player? player, int oldIndex)
        {
            Player = player;
            OldIndex = oldIndex;
        }

        public Player? Player { get; }
        public int OldIndex { get; }
    }

    private readonly struct OrbRemoveSlotsState
    {
        public OrbRemoveSlotsState(Player? player, int oldSlots, IReadOnlyList<OrbModel> trimmedOrbs)
        {
            Player = player;
            OldSlots = oldSlots;
            TrimmedOrbs = trimmedOrbs;
        }

        public Player? Player { get; }
        public int OldSlots { get; }
        public IReadOnlyList<OrbModel> TrimmedOrbs { get; }
    }

    private readonly struct OrbEvokeState
    {
        public OrbEvokeState(Player? player, OrbModel? orb, int oldIndex, bool dequeue)
        {
            Player = player;
            Orb = orb;
            OldIndex = oldIndex;
            Dequeue = dequeue;
        }

        public Player? Player { get; }
        public OrbModel? Orb { get; }
        public int OldIndex { get; }
        public bool Dequeue { get; }
    }

    private readonly struct RelicIntMutationState
    {
        public RelicIntMutationState(RelicModel? relic, int oldValue)
        {
            Relic = relic;
            OldValue = oldValue;
        }

        public RelicModel? Relic { get; }
        public int OldValue { get; }
    }

    private readonly struct RelicBoolMutationState
    {
        public RelicBoolMutationState(RelicModel? relic, bool oldValue)
        {
            Relic = relic;
            OldValue = oldValue;
        }

        public RelicModel? Relic { get; }
        public bool OldValue { get; }
    }

    private static readonly FieldInfo? CardEnergyCostCardField = AccessTools.Field(typeof(CardEnergyCost), "_card");
    private static readonly FieldInfo? OrbQueueOwnerField = AccessTools.Field(typeof(OrbQueue), "_owner");
    private static readonly MethodInfo? OrbQueueSmallWaitMethod = AccessTools.Method(typeof(OrbQueue), "SmallWait");
    private static readonly MethodInfo? OrbModelBeforeTurnEndTriggerMethod = AccessTools.Method(typeof(OrbModel), nameof(OrbModel.BeforeTurnEndOrbTrigger));
    private static readonly MethodInfo? OrbModelAfterTurnStartTriggerMethod = AccessTools.Method(typeof(OrbModel), nameof(OrbModel.AfterTurnStartOrbTrigger));
    private static readonly AsyncLocal<Stack<bool>?> OrbAutoAddScopeStack = new();

    public static void Initialize()
    {
        DebugFileLogger.StartSession(nameof(RecorderMod) + ".Initialize",
            "Recorder mod initialization started.");
        try
        {
            var harmony = new Harmony("com.sts2combatrecorder");

            // Explicit patches (PatchAll silently fails in some environments)
            ApplyPatch(harmony,
                "Player.PopulateCombatState",
                AccessTools.Method(typeof(Player), nameof(Player.PopulateCombatState)),
                SymbolExtensions.GetMethodInfo(() => AfterPopulateCombatState(default!)));
            ApplyPatch(harmony,
                "CombatState.set_RoundNumber",
                AccessTools.Method(typeof(CombatState), "set_RoundNumber"),
                SymbolExtensions.GetMethodInfo(() => AfterRoundNumberChanged(default!)));
            ApplyPatch(harmony,
                "CombatManager.StartTurn",
                AccessTools.Method(typeof(CombatManager), "StartTurn", new[] { typeof(Func<Task>) }),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeStartTurn)),
                null);
            ApplyPatch(harmony,
                "PlayerCombatState.set_Energy",
                AccessTools.Method(typeof(PlayerCombatState), "set_Energy"),
                SymbolExtensions.GetMethodInfo(() => AfterEnergyChanged(default!)));
            ApplyPatch(harmony,
                "PlayerCombatState.set_Stars",
                AccessTools.Method(typeof(PlayerCombatState), "set_Stars"),
                SymbolExtensions.GetMethodInfo(() => AfterStarsChanged(default!)));
            ApplyPatch(harmony,
                "Hook.AfterEnergyReset",
                AccessTools.Method(typeof(Hook), nameof(Hook.AfterEnergyReset)),
                null,
                AccessTools.Method(typeof(RecorderMod), nameof(AfterAfterEnergyReset)));
            ApplyPatch(harmony,
                "Hook.AfterEnergySpent",
                AccessTools.Method(typeof(Hook), nameof(Hook.AfterEnergySpent)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeHookAfterEnergySpentWithTruthSourceScope)),
                null);
            ApplyPatch(harmony,
                "Hook.AfterStarsSpent",
                AccessTools.Method(typeof(Hook), nameof(Hook.AfterStarsSpent)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeHookAfterStarsSpentWithTruthSourceScope)),
                null);
            ApplyPatch(harmony,
                "Hook.AfterRoomEntered",
                AccessTools.Method(typeof(Hook), nameof(Hook.AfterRoomEntered)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeHookAfterRoomEnteredWithTruthSourceScope)),
                null);
            ApplyPatch(harmony,
                "Hook.BeforeCardPlayed",
                AccessTools.Method(typeof(Hook), nameof(Hook.BeforeCardPlayed)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeHookBeforeCardPlayedWithTruthSourceScope)),
                null);
            ApplyPatch(harmony,
                "PlayCardAction.ExecuteAction",
                AccessTools.Method(typeof(PlayCardAction), "ExecuteAction"),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforePlayCardActionExecuteAction)),
                null);
            ApplyPatch(harmony,
                "CardModel.SpendResources",
                AccessTools.Method(typeof(CardModel), nameof(CardModel.SpendResources)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeCardSpendResources)),
                null);
            ApplyPatch(harmony,
                "Hook.AfterCardPlayed",
                AccessTools.Method(typeof(Hook), nameof(Hook.AfterCardPlayed)),
                SymbolExtensions.GetMethodInfo(() => AfterAfterCardPlayed(default!, default!, default!)));
            ApplyPatch(harmony,
                "MonsterModel.PerformMove",
                AccessTools.Method(typeof(MonsterModel), nameof(MonsterModel.PerformMove)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeEnemyPerformMove)),
                null);
            ApplyPatch(harmony,
                "MonsterModel.PerformMove",
                AccessTools.Method(typeof(MonsterModel), nameof(MonsterModel.PerformMove)),
                null,
                AccessTools.Method(typeof(RecorderMod), nameof(AfterEnemyPerformMove)));
            ApplyPatch(harmony,
                "Hook.ModifyCardPlayCount",
                AccessTools.Method(typeof(Hook), nameof(Hook.ModifyCardPlayCount)),
                SymbolExtensions.GetMethodInfo(() => AfterModifyCardPlayCount(default!, default!, default, default!, default!, default!)));
            ApplyPatch(harmony,
                "CardPile.InvokeContentsChanged",
                AccessTools.Method(typeof(CardPile), nameof(CardPile.InvokeContentsChanged)),
                SymbolExtensions.GetMethodInfo(() => AfterCardPileChanged(default!)));
            ApplyPatch(harmony,
                "Hook.BeforePotionUsed",
                AccessTools.Method(typeof(Hook), nameof(Hook.BeforePotionUsed)),
                SymbolExtensions.GetMethodInfo(() => AfterBeforePotionUsed(default!, default!, default!, default!)));
            ApplyPatch(harmony,
                "Hook.AfterPotionUsed",
                AccessTools.Method(typeof(Hook), nameof(Hook.AfterPotionUsed)),
                SymbolExtensions.GetMethodInfo(() => AfterAfterPotionUsed(default!, default!, default!, default!)));
            ApplyPatch(harmony,
                "Hook.BeforeAttack",
                AccessTools.Method(typeof(Hook), nameof(Hook.BeforeAttack)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterBeforeAttack)));
            ApplyPatch(harmony,
                "Hook.AfterAttack",
                AccessTools.Method(typeof(Hook), nameof(Hook.AfterAttack)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterAfterAttack)));
            ApplyPatch(harmony,
                "CreatureCmd.Damage",
                AccessTools.Method(
                    typeof(CreatureCmd),
                    nameof(CreatureCmd.Damage),
                    new[]
                    {
                        typeof(PlayerChoiceContext),
                        typeof(IEnumerable<Creature>),
                        typeof(decimal),
                        typeof(ValueProp),
                        typeof(Creature),
                        typeof(CardModel),
                    }),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeCreatureDamage)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterCreatureDamage)));
            ApplyPatch(harmony,
                "Hook.ModifyDamage",
                AccessTools.Method(typeof(Hook), nameof(Hook.ModifyDamage)),
                null,
                AccessTools.Method(typeof(RecorderMod), nameof(AfterModifyDamage)));
            ApplyPatch(harmony,
                "Hook.BeforeDamageReceived",
                AccessTools.Method(typeof(Hook), nameof(Hook.BeforeDamageReceived)),
                null,
                AccessTools.Method(typeof(RecorderMod), nameof(AfterBeforeDamageReceived)));
            ApplyPatch(harmony,
                "Hook.ModifyHpLostBeforeOsty",
                AccessTools.Method(typeof(Hook), nameof(Hook.ModifyHpLostBeforeOsty)),
                null,
                AccessTools.Method(typeof(RecorderMod), nameof(AfterModifyHpLostBeforeOsty)));
            ApplyPatch(harmony,
                "Hook.ModifyUnblockedDamageTarget",
                AccessTools.Method(typeof(Hook), nameof(Hook.ModifyUnblockedDamageTarget)),
                null,
                AccessTools.Method(typeof(RecorderMod), nameof(AfterModifyUnblockedDamageTarget)));
            ApplyPatch(harmony,
                "Hook.ModifyHpLostAfterOsty",
                AccessTools.Method(typeof(Hook), nameof(Hook.ModifyHpLostAfterOsty)),
                null,
                AccessTools.Method(typeof(RecorderMod), nameof(AfterModifyHpLostAfterOsty)));
            ApplyPatch(harmony,
                "Hook.BeforeBlockGained",
                AccessTools.Method(typeof(Hook), nameof(Hook.BeforeBlockGained)),
                SymbolExtensions.GetMethodInfo(() => AfterBeforeBlockGained(default!, default!, default, default, default)));
            ApplyPatch(harmony,
                "Hook.ModifyBlock",
                AccessTools.Method(typeof(Hook), nameof(Hook.ModifyBlock)),
                null,
                AccessTools.Method(typeof(RecorderMod), nameof(AfterModifyBlock)));
            ApplyPatch(harmony,
                "Hook.AfterBlockGained",
                AccessTools.Method(typeof(Hook), nameof(Hook.AfterBlockGained)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeHookAfterBlockGainedWithTruthSourceScope)),
                null);
            ApplyPatch(harmony,
                "CreatureCmd.LoseBlock",
                AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.LoseBlock)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeCreatureLoseBlock)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterCreatureLoseBlock)));
            ApplyPatch(harmony,
                "Hook.AfterBlockBroken",
                AccessTools.Method(typeof(Hook), nameof(Hook.AfterBlockBroken)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeHookAfterBlockBrokenWithTruthSourceScope)),
                null);
            ApplyPatch(harmony,
                "Creature.ClearBlock",
                AccessTools.Method(typeof(Creature), "ClearBlock"),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeCreatureClearBlock)),
                null);
            ApplyPatch(harmony,
                "Hook.AfterPreventingBlockClear",
                AccessTools.Method(typeof(Hook), nameof(Hook.AfterPreventingBlockClear)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeHookAfterPreventingBlockClearWithTruthSourceScope)),
                null);
            ApplyPatch(harmony,
                "Hook.AfterPreventingDeath",
                AccessTools.Method(typeof(Hook), nameof(Hook.AfterPreventingDeath)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeHookAfterPreventingDeathWithTruthSourceScope)),
                null);
            ApplyPatch(harmony,
                "Hook.AfterBlockCleared",
                AccessTools.Method(typeof(Hook), nameof(Hook.AfterBlockCleared)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeHookAfterBlockClearedWithTruthSourceScope)),
                null);
            ApplyPatch(harmony,
                "Hook.AfterSideTurnStart",
                AccessTools.Method(typeof(Hook), nameof(Hook.AfterSideTurnStart)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeHookAfterSideTurnStartWithTruthSourceScope)),
                null);
            ApplyPatch(harmony,
                "Hook.AfterCurrentHpChanged",
                AccessTools.Method(typeof(Hook), nameof(Hook.AfterCurrentHpChanged)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeHookAfterCurrentHpChangedWithTruthSourceScope)),
                null);
            ApplyPatch(harmony,
                "Hook.AfterStarsGained",
                AccessTools.Method(typeof(Hook), nameof(Hook.AfterStarsGained)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeHookAfterStarsGainedWithTruthSourceScope)),
                null);
            ApplyPatch(harmony,
                "OrbCmd.AddSlots",
                AccessTools.Method(typeof(OrbCmd), nameof(OrbCmd.AddSlots)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeOrbAddSlots)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterOrbAddSlots)));
            ApplyPatch(harmony,
                "OrbCmd.RemoveSlots",
                AccessTools.Method(typeof(OrbCmd), nameof(OrbCmd.RemoveSlots)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeOrbRemoveSlots)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterOrbRemoveSlots)));
            ApplyPatch(harmony,
                "OrbCmd.Channel",
                AccessTools.Method(typeof(OrbCmd), nameof(OrbCmd.Channel), new[] { typeof(PlayerChoiceContext), typeof(OrbModel), typeof(Player) }),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeOrbChannel)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterOrbChannel)));
            ApplyPatch(harmony,
                "OrbCmd.Passive",
                AccessTools.Method(typeof(OrbCmd), nameof(OrbCmd.Passive)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeOrbPassive)),
                null);
            ApplyPatch(harmony,
                "OrbCmd.Evoke",
                AccessTools.Method(typeof(OrbCmd), "Evoke", new[] { typeof(PlayerChoiceContext), typeof(Player), typeof(OrbModel), typeof(bool) }),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeOrbEvokeWithTruthSourceScope)),
                null);
            ApplyPatch(harmony,
                "OrbCmd.Replace",
                AccessTools.Method(typeof(OrbCmd), nameof(OrbCmd.Replace)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeOrbReplace)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterOrbReplace)));
            ApplyPatch(harmony,
                "Hook.AfterModifyingOrbPassiveTriggerCount",
                AccessTools.Method(typeof(Hook), nameof(Hook.AfterModifyingOrbPassiveTriggerCount)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeHookAfterModifyingOrbPassiveTriggerCountWithTruthSourceScope)),
                null);
            ApplyPatch(harmony,
                "Hook.AfterOrbChanneled",
                AccessTools.Method(typeof(Hook), nameof(Hook.AfterOrbChanneled)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeHookAfterOrbChanneledWithTruthSourceScope)),
                null);
            ApplyPatch(harmony,
                "Hook.AfterOrbEvoked",
                AccessTools.Method(typeof(Hook), nameof(Hook.AfterOrbEvoked)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeHookAfterOrbEvokedWithTruthSourceScope)),
                null);
            ApplyPatch(harmony,
                "OrbQueue.BeforeTurnEnd",
                AccessTools.Method(typeof(OrbQueue), nameof(OrbQueue.BeforeTurnEnd)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeOrbQueueBeforeTurnEndWithTruthSourceScope)),
                null);
            ApplyPatch(harmony,
                "OrbQueue.AfterTurnStart",
                AccessTools.Method(typeof(OrbQueue), nameof(OrbQueue.AfterTurnStart)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeOrbQueueAfterTurnStartWithTruthSourceScope)),
                null);
            ApplyPatch(harmony,
                "Hook.AfterPowerAmountChanged",
                AccessTools.Method(typeof(Hook), nameof(Hook.AfterPowerAmountChanged)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeHookAfterPowerAmountChangedWithTruthSourceScope)),
                null);
            ApplyPatch(harmony,
                "Player.AddPotionInternal",
                AccessTools.Method(typeof(Player), nameof(Player.AddPotionInternal)),
                SymbolExtensions.GetMethodInfo(() => AfterAddPotionInternal(default!, default!)));
            ApplyPatch(harmony,
                "Player.DiscardPotionInternal",
                AccessTools.Method(typeof(Player), nameof(Player.DiscardPotionInternal)),
                SymbolExtensions.GetMethodInfo(() => AfterDiscardPotionInternal(default!, default!)));
            ApplyPatch(harmony,
                "Player.RemoveUsedPotionInternal",
                AccessTools.Method(typeof(Player), nameof(Player.RemoveUsedPotionInternal)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeRemoveUsedPotionInternal)),
                null);
            ApplyPatch(harmony,
                "Creature.RemovePowerInternal",
                AccessTools.Method(typeof(Creature), nameof(Creature.RemovePowerInternal)),
                SymbolExtensions.GetMethodInfo(() => AfterRemovePowerInternal(default!, default!)));
            ApplyPatch(harmony,
                "NCreature.UpdateIntent",
                AccessTools.Method(typeof(NCreature), nameof(NCreature.UpdateIntent)),
                SymbolExtensions.GetMethodInfo(() => AfterEnemyIntentUpdated(default!)));
            ApplyPatch(harmony,
                "Hook.AfterCardGeneratedForCombat",
                AccessTools.Method(typeof(Hook), nameof(Hook.AfterCardGeneratedForCombat)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeHookAfterCardGeneratedForCombatWithTruthSourceScope)),
                null);
            ApplyPatch(harmony,
                "Hook.AfterCardEnteredCombat",
                AccessTools.Method(typeof(Hook), nameof(Hook.AfterCardEnteredCombat)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeHookAfterCardEnteredCombatWithTruthSourceScope)),
                null);
            ApplyPatch(harmony,
                "CardCmd.Upgrade(IEnumerable<CardModel>, CardPreviewStyle)",
                AccessTools.Method(typeof(CardCmd), nameof(CardCmd.Upgrade), new[] { typeof(IEnumerable<CardModel>), typeof(CardPreviewStyle) }),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeCardUpgrade)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterCardUpgrade)));
            ApplyPatch(harmony,
                "CardCmd.Downgrade(CardModel)",
                AccessTools.Method(typeof(CardCmd), nameof(CardCmd.Downgrade), new[] { typeof(CardModel) }),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeCardDowngrade)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterCardDowngrade)));
            ApplyCardEnergyCostMutationPatch(harmony, nameof(CardEnergyCost.SetUntilPlayed));
            ApplyCardEnergyCostMutationPatch(harmony, nameof(CardEnergyCost.SetThisTurnOrUntilPlayed));
            ApplyCardEnergyCostMutationPatch(harmony, nameof(CardEnergyCost.SetThisTurn));
            ApplyCardEnergyCostMutationPatch(harmony, nameof(CardEnergyCost.SetThisCombat));
            ApplyCardEnergyCostMutationPatch(harmony, nameof(CardEnergyCost.AddUntilPlayed));
            ApplyCardEnergyCostMutationPatch(harmony, nameof(CardEnergyCost.AddThisTurnOrUntilPlayed));
            ApplyCardEnergyCostMutationPatch(harmony, nameof(CardEnergyCost.AddThisTurn));
            ApplyCardEnergyCostMutationPatch(harmony, nameof(CardEnergyCost.AddThisCombat));
            ApplyCardEnergyCostMutationPatch(harmony, nameof(CardEnergyCost.ResetForDowngrade));
            ApplyCardEnergyCostMutationPatch(harmony, nameof(CardEnergyCost.SetCustomBaseCost));
            ApplyCardEnergyCostCleanupPatch(harmony, nameof(CardEnergyCost.EndOfTurnCleanup));
            ApplyCardEnergyCostCleanupPatch(harmony, nameof(CardEnergyCost.AfterCardPlayedCleanup));
            ApplyPatch(harmony,
                "CardModel.SetStarCostUntilPlayed",
                AccessTools.Method(typeof(CardModel), nameof(CardModel.SetStarCostUntilPlayed)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeCardStarCostMutation)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterCardStarCostMutation)));
            ApplyPatch(harmony,
                "CardModel.SetStarCostThisTurn",
                AccessTools.Method(typeof(CardModel), nameof(CardModel.SetStarCostThisTurn)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeCardStarCostMutation)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterCardStarCostMutation)));
            ApplyPatch(harmony,
                "CardModel.SetStarCostThisCombat",
                AccessTools.Method(typeof(CardModel), nameof(CardModel.SetStarCostThisCombat)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeCardStarCostMutation)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterCardStarCostMutation)));
            ApplyPatch(harmony,
                "CardModel.UpgradeStarCostBy",
                AccessTools.Method(typeof(CardModel), "UpgradeStarCostBy"),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeCardStarCostMutation)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterCardStarCostMutation)));
            ApplyPatch(harmony,
                "CardModel.set_BaseReplayCount",
                AccessTools.PropertySetter(typeof(CardModel), nameof(CardModel.BaseReplayCount)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeReplayCountMutation)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterReplayCountMutation)));
            ApplyPatch(harmony,
                "CardModel.AddKeyword",
                AccessTools.Method(typeof(CardModel), nameof(CardModel.AddKeyword)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeKeywordMutation)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterKeywordMutation)));
            ApplyPatch(harmony,
                "CardModel.RemoveKeyword",
                AccessTools.Method(typeof(CardModel), nameof(CardModel.RemoveKeyword)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeKeywordMutation)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterKeywordMutation)));
            ApplyPatch(harmony,
                "CardModel.GiveSingleTurnRetain",
                AccessTools.Method(typeof(CardModel), nameof(CardModel.GiveSingleTurnRetain)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeSingleTurnRetainMutation)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterSingleTurnRetainMutation)));
            ApplyPatch(harmony,
                "CardModel.GiveSingleTurnSly",
                AccessTools.Method(typeof(CardModel), nameof(CardModel.GiveSingleTurnSly)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeSingleTurnSlyMutation)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterSingleTurnSlyMutation)));
            ApplyPatch(harmony,
                "CardModel.EndOfTurnCleanup",
                AccessTools.Method(typeof(CardModel), nameof(CardModel.EndOfTurnCleanup)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeCardEndOfTurnCleanup)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterCardEndOfTurnCleanup)));
            ApplyPatch(harmony,
                "CardCmd.Enchant(EnchantmentModel, CardModel, decimal)",
                AccessTools.Method(typeof(CardCmd), nameof(CardCmd.Enchant), new[] { typeof(EnchantmentModel), typeof(CardModel), typeof(decimal) }),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeCardEnchant)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterCardEnchant)));
            ApplyPatch(harmony,
                "CardCmd.ClearEnchantment(CardModel)",
                AccessTools.Method(typeof(CardCmd), nameof(CardCmd.ClearEnchantment)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeClearEnchantment)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterClearEnchantment)));
            ApplyPatch(harmony,
                "EnchantmentModel.set_Status",
                AccessTools.PropertySetter(typeof(EnchantmentModel), nameof(EnchantmentModel.Status)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeEnchantmentStatusMutation)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterEnchantmentStatusMutation)));
            ApplyPatch(harmony,
                "CardCmd.Afflict(AfflictionModel, CardModel, decimal)",
                AccessTools.Method(typeof(CardCmd), nameof(CardCmd.Afflict), new[] { typeof(AfflictionModel), typeof(CardModel), typeof(decimal) }),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeCardAfflict)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterCardAfflict)));
            ApplyPatch(harmony,
                "CardCmd.ClearAffliction(CardModel)",
                AccessTools.Method(typeof(CardCmd), nameof(CardCmd.ClearAffliction)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeClearAffliction)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterClearAffliction)));
            ApplyPatch(harmony,
                "ForgeCmd.IncreaseSovereignBladeDamage",
                AccessTools.Method(typeof(ForgeCmd), "IncreaseSovereignBladeDamage"),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeForgeSovereignBladeDamage)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterForgeSovereignBladeDamage)));
            ApplyPatch(harmony,
                "SovereignBlade.SetRepeats",
                AccessTools.Method(typeof(SovereignBlade), nameof(SovereignBlade.SetRepeats)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeSovereignBladeSetRepeats)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterSovereignBladeSetRepeats)));
            ApplyPatch(harmony,
                "RelicModel.IncrementStackCount",
                AccessTools.Method(typeof(RelicModel), nameof(RelicModel.IncrementStackCount)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeRelicIncrementStackCount)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterRelicIncrementStackCount)));
            ApplyPatch(harmony,
                "RelicModel.set_IsWax",
                AccessTools.PropertySetter(typeof(RelicModel), nameof(RelicModel.IsWax)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeRelicIsWaxMutated)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterRelicIsWaxMutated)));
            ApplyPatch(harmony,
                "RelicModel.set_IsMelted",
                AccessTools.PropertySetter(typeof(RelicModel), nameof(RelicModel.IsMelted)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeRelicIsMeltedMutated)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterRelicIsMeltedMutated)));
            ApplyPatch(harmony,
                "Player.AddRelicInternal",
                AccessTools.Method(typeof(Player), nameof(Player.AddRelicInternal)),
                null,
                AccessTools.Method(typeof(RecorderMod), nameof(AfterAddRelicInternal)));
            ApplyPatch(harmony,
                "Player.RemoveRelicInternal",
                AccessTools.Method(typeof(Player), nameof(Player.RemoveRelicInternal)),
                null,
                AccessTools.Method(typeof(RecorderMod), nameof(AfterRemoveRelicInternal)));
            ApplyPatch(harmony,
                "Creature.SetCurrentHpInternal",
                AccessTools.Method(typeof(Creature), nameof(Creature.SetCurrentHpInternal)),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeSetCurrentHpInternal)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterSetCurrentHpInternal)));
            ApplyPatch(harmony,
                "BlackHolePower.DealDamageToAllEnemies",
                AccessTools.Method(typeof(BlackHolePower), "DealDamageToAllEnemies"),
                AccessTools.Method(typeof(RecorderMod), nameof(BeforeBlackHoleDealDamageToAllEnemies)),
                AccessTools.Method(typeof(RecorderMod), nameof(AfterBlackHoleDealDamageToAllEnemies)));

            var tree = (SceneTree)Engine.GetMainLoop();
            tree.Connect(SceneTree.SignalName.ProcessFrame,
                Callable.From(OnProcessFrame));
            DebugFileLogger.Log(nameof(RecorderMod) + ".Initialize",
                "Connected OnProcessFrame to SceneTree.ProcessFrame.");
            BattleLogger.OnRecorderInitialized();

            Log.Info("[STS2CombatRecorder] Initialized. Harmony patches applied.");
            DebugFileLogger.Log(nameof(RecorderMod) + ".Initialize",
                "Recorder mod initialization succeeded.");
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] Init failed: {ex}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".Initialize", ex);
        }
    }

    // ─── Per-frame state diff ─────────────────────────────────────────────

    private static void OnProcessFrame()
    {
        if (!BattleLogger.IsActive) return;
        try
        {
            var combat = CombatManager.Instance;
            if (combat == null)
            {
                DebugFileLogger.Log(nameof(RecorderMod) + ".OnProcessFrame",
                    "CombatManager unavailable. Ending active recorder session.");
                BattleLogger.EndBattle();
                return;
            }

            var state = combat.DebugOnlyGetState();
            if (state == null)
            {
                state = GetCombatStateFromManager(combat);
            }
            var player = GameStateReader.GetPlayer(state);

            if (!combat.IsInProgress)
            {
                DebugFileLogger.Log(nameof(RecorderMod) + ".OnProcessFrame",
                    "Combat no longer in progress. Ending active recorder session.");
                BattleLogger.EndBattle(state, player);
                return;
            }

            if (state == null || player == null) return;

            BattleLogger.OnProcessFrame(state);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2CombatRecorder] ProcessFrame: {ex.Message}");
        }
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private static CombatState? GetCombatStateFromManager(CombatManager combat)
    {
        try
        {
            var field = typeof(CombatManager).GetField("_state",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            return field?.GetValue(combat) as CombatState;
        }
        catch { return null; }
    }

    // ─── Harmony patches ──────────────────────────────────────────────────

    [HarmonyPatch(typeof(Player), nameof(Player.PopulateCombatState))]
    [HarmonyPostfix]
    public static void AfterPopulateCombatState(Player __instance)
    {
        try
        {
            DebugFileLogger.Log(nameof(RecorderMod) + ".AfterPopulateCombatState",
                "Battle-start hook triggered.");
            if (BattleLogger.IsActive)
            {
                DebugFileLogger.Log(nameof(RecorderMod) + ".AfterPopulateCombatState",
                    "Early return: recorder already active.");
                return;
            }

            var combat = CombatManager.Instance;
            if (combat == null)
            {
                DebugFileLogger.Log(nameof(RecorderMod) + ".AfterPopulateCombatState",
                    "Early return: CombatManager.Instance was null.");
                return;
            }

            var state = combat.DebugOnlyGetState();
            if (state == null)
            {
                state = GetCombatStateFromManager(combat);
            }
            if (state == null)
            {
                DebugFileLogger.Log(nameof(RecorderMod) + ".AfterPopulateCombatState",
                    "Early return: combat state unavailable.");
                return;
            }

            BattleLogger.StartBattle(state, __instance);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] PopulateCombatState patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterPopulateCombatState", ex);
        }
    }

    [HarmonyPatch(typeof(CombatState), "set_RoundNumber")]
    [HarmonyPostfix]
    public static void AfterRoundNumberChanged(CombatState __instance)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            var player = GameStateReader.GetPlayer(__instance);
            if (player == null) return;

            BattleLogger.OnRoundChanged(__instance);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] RoundNumber patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterRoundNumberChanged", ex);
        }
    }

    [HarmonyPatch(typeof(CombatManager), "StartTurn")]
    [HarmonyPrefix]
    public static void BeforeStartTurn(CombatManager __instance)
    {
        try
        {
            if (!BattleLogger.IsActive) return;

            var state = __instance.DebugOnlyGetState();
            if (state == null)
            {
                state = GetCombatStateFromManager(__instance);
            }
            if (state?.CurrentSide != CombatSide.Enemy) return;

            BattleLogger.OnEnemyTurnStarted(state);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] StartTurn patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeStartTurn", ex);
        }
    }

    [HarmonyPatch(typeof(PlayerCombatState), "set_Energy")]
    [HarmonyPostfix]
    public static void AfterEnergyChanged(PlayerCombatState __instance)
    {
        try
        {
            if (!BattleLogger.IsActive) return;

            Player? player = null;
            var field = typeof(PlayerCombatState).GetField(
                "_player",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            if (field?.GetValue(__instance) is Player p)
                player = p;

            if (player != null)
                BattleLogger.OnEnergyChanged(__instance);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] Energy patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterEnergyChanged", ex);
        }
    }

    [HarmonyPatch(typeof(PlayerCombatState), "set_Stars")]
    [HarmonyPostfix]
    public static void AfterStarsChanged(PlayerCombatState __instance)
    {
        try
        {
            if (!BattleLogger.IsActive) return;

            Player? player = null;
            var field = typeof(PlayerCombatState).GetField(
                "_player",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            if (field?.GetValue(__instance) is Player p)
                player = p;

            if (player != null)
                BattleLogger.OnStarsChanged(__instance);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] Stars patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterStarsChanged", ex);
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterEnergyReset))]
    [HarmonyPostfix]
    public static void AfterAfterEnergyReset(CombatState combatState, Player player, ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            __result = WrapAfterEnergyReset(combatState, player, __result);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] AfterEnergyReset patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterAfterEnergyReset", ex);
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterEnergySpent))]
    [HarmonyPrefix]
    public static bool BeforeHookAfterEnergySpentWithTruthSourceScope(
        CombatState combatState,
        CardModel card,
        int amount,
        ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive) return true;
            __result = RunHookAfterEnergySpentWithTruthSourceScope(combatState, card, amount);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] AfterEnergySpent generic patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeHookAfterEnergySpentWithTruthSourceScope", ex);
            return true;
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterStarsSpent))]
    [HarmonyPrefix]
    public static bool BeforeHookAfterStarsSpentWithTruthSourceScope(
        CombatState combatState,
        int amount,
        Player spender,
        ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive) return true;
            __result = RunHookAfterStarsSpentWithTruthSourceScope(combatState, amount, spender);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] AfterStarsSpent generic patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeHookAfterStarsSpentWithTruthSourceScope", ex);
            return true;
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterRoomEntered))]
    [HarmonyPrefix]
    public static bool BeforeHookAfterRoomEnteredWithTruthSourceScope(
        IRunState runState,
        AbstractRoom room,
        ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive) return true;
            __result = RunHookAfterRoomEnteredWithTruthSourceScope(runState, room);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] AfterRoomEntered generic patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeHookAfterRoomEnteredWithTruthSourceScope", ex);
            return true;
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCardPlayed))]
    [HarmonyPrefix]
    public static bool BeforeHookBeforeCardPlayedWithTruthSourceScope(
        CombatState combatState,
        CardPlay cardPlay,
        ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive) return true;
            DebugFileLogger.Log(nameof(RecorderMod) + ".BeforeHookBeforeCardPlayedWithTruthSourceScope",
                $"Card play hook triggered. AutoPlay={cardPlay.IsAutoPlay}");
            BattleLogger.OnBeforeCardPlayed(combatState, cardPlay);
            __result = RunHookBeforeCardPlayedWithTruthSourceScope(combatState, cardPlay);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] BeforeCardPlayed patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeHookBeforeCardPlayedWithTruthSourceScope", ex);
            return true;
        }
    }

    [HarmonyPatch(typeof(PlayCardAction), "ExecuteAction")]
    [HarmonyPrefix]
    public static void BeforePlayCardActionExecuteAction(PlayCardAction __instance)
    {
        try
        {
            if (!BattleLogger.IsActive) return;

            var card = __instance.NetCombatCard.ToCardModelOrNull();
            if (card == null) return;

            BattleLogger.OnPendingManualPlayTargetCaptured(card, __instance.Target);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] PlayCardAction prefix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforePlayCardActionExecuteAction", ex);
        }
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.SpendResources))]
    [HarmonyPrefix]
    public static void BeforeCardSpendResources(CardModel __instance)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            BattleLogger.OnManualPlaySpendResourcesStarted(__instance);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] CardModel.SpendResources prefix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeCardSpendResources", ex);
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardPlayed))]
    [HarmonyPostfix]
    public static void AfterAfterCardPlayed(CombatState combatState, PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            BattleLogger.OnAfterCardPlayed(combatState, cardPlay);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] AfterCardPlayed patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterAfterCardPlayed", ex);
        }
    }

    [HarmonyPatch(typeof(MonsterModel), nameof(MonsterModel.PerformMove))]
    [HarmonyPrefix]
    public static void BeforeEnemyPerformMove(MonsterModel __instance)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            BattleLogger.OnEnemyActionStarted(__instance);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] Monster PerformMove prefix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeEnemyPerformMove", ex);
        }
    }

    [HarmonyPatch(typeof(MonsterModel), nameof(MonsterModel.PerformMove))]
    [HarmonyPostfix]
    public static void AfterEnemyPerformMove(MonsterModel __instance, ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            __result = WrapEnemyPerformMove(__instance, __result);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] Monster PerformMove postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterEnemyPerformMove", ex);
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyCardPlayCount))]
    [HarmonyPostfix]
    public static void AfterModifyCardPlayCount(
        CombatState combatState,
        CardModel card,
        int playCount,
        Creature target,
        List<AbstractModel> modifyingModels,
        int __result)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            BattleLogger.OnCardPlayCountModified(card, __result);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] ModifyCardPlayCount patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterModifyCardPlayCount", ex);
        }
    }

    [HarmonyPatch(typeof(CardPile), nameof(CardPile.InvokeContentsChanged))]
    [HarmonyPostfix]
    public static void AfterCardPileChanged(CardPile __instance)
    {
        try
        {
            if (!BattleLogger.IsActive) return;

            var combat = CombatManager.Instance;
            if (combat == null || !combat.IsInProgress) return;

            var state = combat.DebugOnlyGetState();
            var player = GameStateReader.GetPlayer(state);
            if (state == null || player == null) return;

            BattleLogger.OnCardPileChanged(state, __instance);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] CardPile patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterCardPileChanged", ex);
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforePotionUsed))]
    [HarmonyPostfix]
    public static void AfterBeforePotionUsed(IRunState runState, CombatState? combatState, PotionModel potion, Creature? target)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            BattleLogger.OnPotionEnqueuedForUse(potion, target);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] BeforePotionUsed patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterBeforePotionUsed", ex);
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterPotionUsed))]
    [HarmonyPostfix]
    public static void AfterAfterPotionUsed(IRunState runState, CombatState? combatState, PotionModel potion, Creature? target)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            if (potion.Owner == null) return;
            BattleLogger.OnPotionUseFinished(potion.Owner, potion);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] AfterPotionUsed patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterAfterPotionUsed", ex);
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeAttack))]
    [HarmonyPostfix]
    public static void AfterBeforeAttack(CombatState combatState, AttackCommand command)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            BattleLogger.OnAttackTruthStarted(combatState, command);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] BeforeAttack patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterBeforeAttack", ex);
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterAttack))]
    [HarmonyPostfix]
    public static void AfterAfterAttack(CombatState combatState, AttackCommand command, ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            __result = BattleLogger.WrapAttackTruthCompletion(command, __result);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] AfterAttack patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterAfterAttack", ex);
        }
    }

    [HarmonyPatch(
        typeof(CreatureCmd),
        nameof(CreatureCmd.Damage),
        new[]
        {
            typeof(PlayerChoiceContext),
            typeof(IEnumerable<Creature>),
            typeof(decimal),
            typeof(ValueProp),
            typeof(Creature),
            typeof(CardModel),
        })]
    [HarmonyPrefix]
    public static void BeforeCreatureDamage(
        PlayerChoiceContext choiceContext,
        IEnumerable<Creature> targets,
        decimal amount,
        ValueProp props,
        Creature? dealer,
        CardModel? cardSource,
        out object? __state)
    {
        __state = null;
        try
        {
            if (!BattleLogger.IsActive) return;
            __state = BattleLogger.OnDamageCallStarted(choiceContext, targets, amount, props, dealer, cardSource);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] CreatureCmd.Damage prefix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeCreatureDamage", ex);
        }
    }

    [HarmonyPatch(
        typeof(CreatureCmd),
        nameof(CreatureCmd.Damage),
        new[]
        {
            typeof(PlayerChoiceContext),
            typeof(IEnumerable<Creature>),
            typeof(decimal),
            typeof(ValueProp),
            typeof(Creature),
            typeof(CardModel),
        })]
    [HarmonyPostfix]
    public static void AfterCreatureDamage(ref Task<IEnumerable<DamageResult>> __result, object? __state)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            __result = BattleLogger.WrapDamageCallTask(__state, __result);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] CreatureCmd.Damage postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterCreatureDamage", ex);
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyDamage))]
    [HarmonyPostfix]
    public static void AfterModifyDamage(
        IRunState runState,
        CombatState? combatState,
        Creature? target,
        Creature? dealer,
        decimal damage,
        ValueProp props,
        CardModel? cardSource,
        ModifyDamageHookType modifyDamageHookType,
        CardPreviewMode previewMode,
        IEnumerable<AbstractModel> modifiers,
        decimal __result)
    {
        try
        {
            if (!BattleLogger.IsActive || previewMode != CardPreviewMode.None) return;
            BattleLogger.OnDamageStageModifyDamage(
                runState,
                combatState,
                target,
                dealer,
                damage,
                props,
                cardSource,
                modifyDamageHookType,
                __result);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] ModifyDamage postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterModifyDamage", ex);
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeDamageReceived))]
    [HarmonyPostfix]
    public static void AfterBeforeDamageReceived(
        PlayerChoiceContext choiceContext,
        IRunState runState,
        CombatState? combatState,
        Creature target,
        decimal amount,
        ValueProp props,
        Creature? dealer,
        CardModel? cardSource)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            BattleLogger.OnDamageStageBeforeDamageReceived(target, amount, props, dealer, cardSource);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] BeforeDamageReceived postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterBeforeDamageReceived", ex);
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyHpLostBeforeOsty))]
    [HarmonyPostfix]
    public static void AfterModifyHpLostBeforeOsty(
        IRunState runState,
        CombatState? combatState,
        Creature target,
        decimal amount,
        ValueProp props,
        Creature? dealer,
        CardModel? cardSource,
        IEnumerable<AbstractModel> modifiers,
        decimal __result)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            BattleLogger.OnDamageStageModifyHpLostBeforeOsty(
                runState,
                combatState,
                target,
                amount,
                props,
                dealer,
                cardSource,
                __result);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] ModifyHpLostBeforeOsty postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterModifyHpLostBeforeOsty", ex);
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyUnblockedDamageTarget))]
    [HarmonyPostfix]
    public static void AfterModifyUnblockedDamageTarget(
        CombatState combatState,
        Creature originalTarget,
        decimal amount,
        ValueProp props,
        Creature? dealer,
        Creature __result)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            BattleLogger.OnDamageStageRedirect(combatState, originalTarget, amount, props, dealer, __result);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] ModifyUnblockedDamageTarget postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterModifyUnblockedDamageTarget", ex);
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyHpLostAfterOsty))]
    [HarmonyPostfix]
    public static void AfterModifyHpLostAfterOsty(
        IRunState runState,
        CombatState? combatState,
        Creature target,
        decimal amount,
        ValueProp props,
        Creature? dealer,
        CardModel? cardSource,
        IEnumerable<AbstractModel> modifiers,
        decimal __result)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            BattleLogger.OnDamageStageModifyHpLostAfterOsty(
                runState,
                combatState,
                target,
                amount,
                props,
                dealer,
                cardSource,
                __result);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] ModifyHpLostAfterOsty postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterModifyHpLostAfterOsty", ex);
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeBlockGained))]
    [HarmonyPostfix]
    public static void AfterBeforeBlockGained(
        CombatState combatState,
        Creature creature,
        decimal amount,
        ValueProp props,
        CardModel? cardSource)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            BattleLogger.OnBlockGainSamplingStarted(creature, amount, props, cardSource);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] BeforeBlockGained patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterBeforeBlockGained", ex);
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyBlock))]
    [HarmonyPostfix]
    public static void AfterModifyBlock(
        CombatState combatState,
        Creature target,
        decimal block,
        ValueProp props,
        CardModel? cardSource,
        CardPlay? cardPlay,
        IEnumerable<AbstractModel> modifiers,
        decimal __result)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            BattleLogger.OnBlockStageModifyBlock(combatState, target, block, props, cardSource, cardPlay, __result);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] ModifyBlock postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterModifyBlock", ex);
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterBlockGained))]
    [HarmonyPrefix]
    public static bool BeforeHookAfterBlockGainedWithTruthSourceScope(
        CombatState combatState,
        Creature creature,
        decimal amount,
        ValueProp props,
        CardModel? cardSource,
        ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive) return true;
            __result = RunHookAfterBlockGainedWithTruthSourceScope(combatState, creature, amount, props, cardSource);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] AfterBlockGained generic patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeHookAfterBlockGainedWithTruthSourceScope", ex);
            return true;
        }
    }

    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.LoseBlock))]
    [HarmonyPrefix]
    public static void BeforeCreatureLoseBlock(Creature creature, decimal amount)
    {
        try
        {
            if (!BattleLogger.IsActive || amount <= 0m) return;
            BattleLogger.OnBlockLossSamplingStarted(creature);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] LoseBlock prefix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeCreatureLoseBlock", ex);
        }
    }

    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.LoseBlock))]
    [HarmonyPostfix]
    public static void AfterCreatureLoseBlock(Creature creature, ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            __result = WrapLoseBlockTruth(creature, __result);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] LoseBlock postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterCreatureLoseBlock", ex);
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterBlockBroken))]
    [HarmonyPrefix]
    public static bool BeforeHookAfterBlockBrokenWithTruthSourceScope(
        CombatState combatState,
        Creature creature,
        ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive) return true;
            __result = RunHookAfterBlockBrokenWithTruthSourceScope(combatState, creature);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] AfterBlockBroken generic patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeHookAfterBlockBrokenWithTruthSourceScope", ex);
            return true;
        }
    }

    [HarmonyPatch(typeof(Creature), "ClearBlock")]
    [HarmonyPrefix]
    public static void BeforeCreatureClearBlock(Creature __instance)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            BattleLogger.OnBlockClearSamplingStarted(__instance);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] ClearBlock prefix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeCreatureClearBlock", ex);
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterPreventingBlockClear))]
    [HarmonyPrefix]
    public static bool BeforeHookAfterPreventingBlockClearWithTruthSourceScope(
        CombatState combatState,
        AbstractModel preventer,
        Creature creature,
        ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive) return true;
            __result = RunHookAfterPreventingBlockClearWithTruthSourceScope(combatState, preventer, creature);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] AfterPreventingBlockClear generic patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeHookAfterPreventingBlockClearWithTruthSourceScope", ex);
            return true;
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterPreventingDeath))]
    [HarmonyPrefix]
    public static bool BeforeHookAfterPreventingDeathWithTruthSourceScope(
        IRunState runState,
        CombatState? combatState,
        AbstractModel preventer,
        Creature creature,
        ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive) return true;
            __result = RunHookAfterPreventingDeathWithTruthSourceScope(preventer, creature);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] AfterPreventingDeath generic patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeHookAfterPreventingDeathWithTruthSourceScope", ex);
            return true;
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterBlockCleared))]
    [HarmonyPrefix]
    public static bool BeforeHookAfterBlockClearedWithTruthSourceScope(
        CombatState combatState,
        Creature creature,
        ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive) return true;
            __result = RunHookAfterBlockClearedWithTruthSourceScope(combatState, creature);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] AfterBlockCleared generic patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeHookAfterBlockClearedWithTruthSourceScope", ex);
            return true;
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterSideTurnStart))]
    [HarmonyPrefix]
    public static bool BeforeHookAfterSideTurnStartWithTruthSourceScope(
        CombatState combatState,
        CombatSide side,
        ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive) return true;
            __result = RunHookAfterSideTurnStartWithTruthSourceScope(combatState, side);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] AfterSideTurnStart generic patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeHookAfterSideTurnStartWithTruthSourceScope", ex);
            return true;
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCurrentHpChanged))]
    [HarmonyPrefix]
    public static bool BeforeHookAfterCurrentHpChangedWithTruthSourceScope(
        IRunState runState,
        CombatState? combatState,
        Creature creature,
        decimal delta,
        ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive) return true;
            __result = RunHookAfterCurrentHpChangedWithTruthSourceScope(runState, combatState, creature, delta);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] AfterCurrentHpChanged generic patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeHookAfterCurrentHpChangedWithTruthSourceScope", ex);
            return true;
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterStarsGained))]
    [HarmonyPrefix]
    public static bool BeforeHookAfterStarsGainedWithTruthSourceScope(
        CombatState combatState,
        int amount,
        Player gainer,
        ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive) return true;
            __result = RunHookAfterStarsGainedWithTruthSourceScope(combatState, amount, gainer);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] AfterStarsGained generic patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeHookAfterStarsGainedWithTruthSourceScope", ex);
            return true;
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterPowerAmountChanged))]
    [HarmonyPrefix]
    public static bool BeforeHookAfterPowerAmountChangedWithTruthSourceScope(
        CombatState combatState,
        PowerModel power,
        decimal amount,
        Creature? applier,
        CardModel? cardSource,
        ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive) return true;
            __result = RunHookAfterPowerAmountChangedWithTruthSourceScope(combatState, power, amount, applier, cardSource);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] AfterPowerAmountChanged generic patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeHookAfterPowerAmountChangedWithTruthSourceScope", ex);
            return true;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.AddPotionInternal))]
    [HarmonyPostfix]
    public static void AfterAddPotionInternal(Player __instance, PotionModel potion)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            if (__instance.GetPotionSlotIndex(potion) < 0) return;

            BattleLogger.OnPotionAdded(__instance, potion);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] AddPotionInternal patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterAddPotionInternal", ex);
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.DiscardPotionInternal))]
    [HarmonyPostfix]
    public static void AfterDiscardPotionInternal(Player __instance, PotionModel potion)
    {
        try
        {
            if (!BattleLogger.IsActive) return;

            BattleLogger.OnPotionDiscarded(__instance, potion);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] DiscardPotionInternal patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterDiscardPotionInternal", ex);
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.RemoveUsedPotionInternal))]
    [HarmonyPrefix]
    public static void BeforeRemoveUsedPotionInternal(Player __instance, PotionModel potion)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            BattleLogger.OnPotionUseRemovalStarted(__instance, potion);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] RemoveUsedPotionInternal patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeRemoveUsedPotionInternal", ex);
        }
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.RemovePowerInternal))]
    [HarmonyPostfix]
    public static void AfterRemovePowerInternal(Creature __instance, PowerModel power)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            BattleLogger.OnPowerRemoved(__instance, power);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] RemovePowerInternal patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterRemovePowerInternal", ex);
        }
    }

    [HarmonyPatch(typeof(NCreature), nameof(NCreature.UpdateIntent))]
    [HarmonyPostfix]
    public static void AfterEnemyIntentUpdated(NCreature __instance)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            if (__instance.Entity == null || !__instance.Entity.IsMonster) return;

            BattleLogger.OnVisibleIntentUpdated(__instance.Entity);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] UpdateIntent patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterEnemyIntentUpdated", ex);
        }
    }

    private static async Task WrapEnemyPerformMove(MonsterModel monster, Task originalTask)
    {
        try
        {
            await originalTask;
        }
        finally
        {
            BattleLogger.OnEnemyActionFinished(monster);
        }
    }

    private static async Task WrapAfterEnergyReset(CombatState combatState, Player player, Task originalTask)
    {
        try
        {
            await originalTask;
        }
        finally
        {
            BattleLogger.OnAfterEnergyReset(combatState, player);
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardGeneratedForCombat))]
    [HarmonyPrefix]
    public static bool BeforeHookAfterCardGeneratedForCombatWithTruthSourceScope(
        CombatState combatState,
        CardModel card,
        bool addedByPlayer,
        ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive) return true;
            __result = RunHookAfterCardGeneratedForCombatWithTruthSourceScope(combatState, card, addedByPlayer);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] AfterCardGeneratedForCombat generic patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeHookAfterCardGeneratedForCombatWithTruthSourceScope", ex);
            return true;
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardEnteredCombat))]
    [HarmonyPrefix]
    public static bool BeforeHookAfterCardEnteredCombatWithTruthSourceScope(
        CombatState combatState,
        CardModel card,
        ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive) return true;
            __result = RunHookAfterCardEnteredCombatWithTruthSourceScope(combatState, card);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] AfterCardEnteredCombat generic patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeHookAfterCardEnteredCombatWithTruthSourceScope", ex);
            return true;
        }
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.SetCurrentHpInternal))]
    [HarmonyPrefix]
    public static void BeforeSetCurrentHpInternal(Creature __instance, out bool __state)
    {
        __state = __instance.IsDead;
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.SetCurrentHpInternal))]
    [HarmonyPostfix]
    public static void AfterSetCurrentHpInternal(Creature __instance, bool __state)
    {
        try
        {
            if (!BattleLogger.IsActive) return;
            if (!__state || __instance.IsDead) return;

            BattleLogger.OnEntityRevived(__instance);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] SetCurrentHpInternal patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterSetCurrentHpInternal", ex);
        }
    }

    [HarmonyPatch(typeof(BlackHolePower), "DealDamageToAllEnemies")]
    [HarmonyPrefix]
    public static void BeforeBlackHoleDealDamageToAllEnemies(
        BlackHolePower __instance,
        out bool __state)
    {
        __state = false;
        try
        {
            if (!BattleLogger.IsActive) return;
            BattleLogger.PushTruthSourceScope(__instance);
            __state = true;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] BlackHolePower prefix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeBlackHoleDealDamageToAllEnemies", ex);
        }
    }

    [HarmonyPatch(typeof(BlackHolePower), "DealDamageToAllEnemies")]
    [HarmonyPostfix]
    public static void AfterBlackHoleDealDamageToAllEnemies(bool __state, ref Task __result)
    {
        try
        {
            if (!__state) return;
            __result = WrapTruthSourceScope(__result);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] BlackHolePower postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterBlackHoleDealDamageToAllEnemies", ex);
        }
    }

    private static async Task RunHookAfterBlockGainedWithTruthSourceScope(
        CombatState combatState,
        Creature creature,
        decimal amount,
        ValueProp props,
        CardModel? cardSource)
    {
        try
        {
            foreach (var model in combatState.IterateHookListeners())
            {
                await ExecuteHookListenerWithTruthSourceScope(
                    model,
                    () => model.AfterBlockGained(creature, amount, props, cardSource));
            }
        }
        finally
        {
            BattleLogger.OnBlockGained(combatState, creature, amount, props, cardSource);
        }
    }

    private static async Task WrapLoseBlockTruth(Creature creature, Task originalTask)
    {
        try
        {
            await originalTask;
        }
        finally
        {
            BattleLogger.OnBlockLost(creature);
        }
    }

    private static async Task RunHookAfterBlockBrokenWithTruthSourceScope(
        CombatState combatState,
        Creature creature)
    {
        foreach (var model in combatState.IterateHookListeners())
        {
            await ExecuteHookListenerWithTruthSourceScope(
                model,
                () => model.AfterBlockBroken(creature));
        }
    }

    private static async Task RunHookAfterPreventingBlockClearWithTruthSourceScope(
        CombatState combatState,
        AbstractModel preventer,
        Creature creature)
    {
        BattleLogger.OnBlockClearPrevented(preventer, creature);

        if (!combatState.IterateHookListeners().Contains(preventer))
        {
            return;
        }

        await ExecuteHookListenerWithTruthSourceScope(
            preventer,
            () => preventer.AfterPreventingBlockClear(preventer, creature));
    }

    private static async Task RunHookAfterPreventingDeathWithTruthSourceScope(
        AbstractModel preventer,
        Creature creature)
    {
        var pushedDirectReviver = false;
        try
        {
            pushedDirectReviver = BattleLogger.PushPreventedDeathReviver(creature, preventer);
            await ExecuteHookListenerWithTruthSourceScope(
                preventer,
                () => preventer.AfterPreventingDeath(creature));
        }
        finally
        {
            if (pushedDirectReviver)
            {
                BattleLogger.PopPreventedDeathReviver(creature);
            }
        }
    }

    private static async Task RunHookAfterBlockClearedWithTruthSourceScope(
        CombatState combatState,
        Creature creature)
    {
        BattleLogger.OnBlockCleared(creature);

        foreach (var model in combatState.IterateHookListeners())
        {
            await ExecuteHookListenerWithTruthSourceScope(
                model,
                () => model.AfterBlockCleared(creature));
        }
    }

    private static async Task RunHookAfterSideTurnStartWithTruthSourceScope(
        CombatState combatState,
        CombatSide side)
    {
        foreach (var model in combatState.IterateHookListeners())
        {
            await ExecuteHookListenerWithTruthSourceScope(
                model,
                () => model.AfterSideTurnStart(side, combatState));
        }
    }

    private static async Task RunHookAfterCurrentHpChangedWithTruthSourceScope(
        IRunState runState,
        CombatState? combatState,
        Creature creature,
        decimal delta)
    {
        foreach (var model in runState.IterateHookListeners(combatState))
        {
            await ExecuteHookListenerWithTruthSourceScope(
                model,
                () => model.AfterCurrentHpChanged(creature, delta));
        }
    }

    private static async Task RunHookAfterEnergySpentWithTruthSourceScope(
        CombatState combatState,
        CardModel card,
        int amount)
    {
        foreach (var model in combatState.IterateHookListeners())
        {
            await ExecuteHookListenerWithTruthSourceScope(
                model,
                () => model.AfterEnergySpent(card, amount));
        }
    }

    private static async Task RunHookAfterStarsSpentWithTruthSourceScope(
        CombatState combatState,
        int amount,
        Player spender)
    {
        foreach (var model in combatState.IterateHookListeners())
        {
            await ExecuteHookListenerWithTruthSourceScope(
                model,
                () => model.AfterStarsSpent(amount, spender));
        }
    }

    private static async Task RunHookAfterStarsGainedWithTruthSourceScope(
        CombatState combatState,
        int amount,
        Player gainer)
    {
        foreach (var model in combatState.IterateHookListeners())
        {
            await ExecuteHookListenerWithTruthSourceScope(
                model,
                () => model.AfterStarsGained(amount, gainer));
        }
    }

    private static async Task RunHookAfterRoomEnteredWithTruthSourceScope(
        IRunState runState,
        AbstractRoom room)
    {
        foreach (var model in runState.IterateHookListeners(null))
        {
            await ExecuteHookListenerWithTruthSourceScope(
                model,
                () => model.AfterRoomEntered(room));
        }
    }

    private static async Task RunHookBeforeCardPlayedWithTruthSourceScope(
        CombatState combatState,
        CardPlay cardPlay)
    {
        foreach (var model in combatState.IterateHookListeners())
        {
            await ExecuteHookListenerWithTruthSourceScope(
                model,
                () => model.BeforeCardPlayed(cardPlay));
        }
    }

    private static async Task RunHookAfterPowerAmountChangedWithTruthSourceScope(
        CombatState combatState,
        PowerModel power,
        decimal amount,
        Creature? applier,
        CardModel? cardSource)
    {
        BattleLogger.PushPowerTruthTriggerScope();
        try
        {
            foreach (var model in combatState.IterateHookListeners())
            {
                await ExecuteHookListenerWithTruthSourceScope(
                    model,
                    () => model.AfterPowerAmountChanged(power, amount, applier, cardSource));
            }
        }
        finally
        {
            try
            {
                BattleLogger.OnPowerAmountChanged(combatState, power, amount, applier, cardSource);
            }
            finally
            {
                BattleLogger.PopPowerTruthTriggerScope();
            }
        }
    }

    private static async Task RunHookAfterCardGeneratedForCombatWithTruthSourceScope(
        CombatState combatState,
        CardModel card,
        bool addedByPlayer)
    {
        try
        {
            foreach (var model in combatState.IterateHookListeners())
            {
                await ExecuteHookListenerWithTruthSourceScope(
                    model,
                    () => model.AfterCardGeneratedForCombat(card, addedByPlayer));
            }
        }
        finally
        {
            BattleLogger.OnCardGeneratedForCombat(combatState, card, addedByPlayer);
        }
    }

    private static async Task RunHookAfterCardEnteredCombatWithTruthSourceScope(
        CombatState combatState,
        CardModel card)
    {
        try
        {
            foreach (var model in combatState.IterateHookListeners())
            {
                await ExecuteHookListenerWithTruthSourceScope(
                    model,
                    () => model.AfterCardEnteredCombat(card));
            }
        }
        finally
        {
            BattleLogger.OnCardEnteredCombat(combatState, card);
        }
    }

    private static void BeforeOrbAddSlots(Player player, int amount, out OrbSlotsMutationState __state)
    {
        TryGetPlayerOrbQueue(player, nameof(BeforeOrbAddSlots), out var orbQueue);
        var oldSlots = orbQueue?.Capacity ?? 0;
        var reason = IsOrbAutoAddScopeActive() ? "auto_add_for_channel" : "add_slots";
        __state = new OrbSlotsMutationState(player, oldSlots, reason);
    }

    private static void AfterOrbAddSlots(ref Task __result, OrbSlotsMutationState __state)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return;
            }

            __result = WrapOrbAddSlots(__state, __result);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OrbCmd.AddSlots postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterOrbAddSlots", ex);
        }
    }

    private static void BeforeOrbRemoveSlots(Player player, int amount, out OrbRemoveSlotsState __state)
    {
        TryGetPlayerOrbQueue(player, nameof(BeforeOrbRemoveSlots), out var orbQueue);
        var oldSlots = orbQueue?.Capacity ?? 0;
        var newSlots = Math.Max(0, oldSlots - Math.Max(0, amount));
        var trimmedOrbs = orbQueue?.Orbs.Skip(newSlots).ToList() ?? new List<OrbModel>();
        __state = new OrbRemoveSlotsState(player, oldSlots, trimmedOrbs);
    }

    private static void AfterOrbRemoveSlots(OrbRemoveSlotsState __state)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return;
            }

            var player = __state.Player;
            if (player == null)
            {
                return;
            }

            if (!TryGetPlayerOrbQueue(player, nameof(AfterOrbRemoveSlots), out var orbQueue))
            {
                return;
            }

            var newSlots = orbQueue.Capacity;
            BattleLogger.OnOrbSlotsChanged(player, __state.OldSlots, newSlots, "remove_slots");
            foreach (var trimmedOrb in __state.TrimmedOrbs)
            {
                BattleLogger.OnOrbRemoved(trimmedOrb, "capacity_trim");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OrbCmd.RemoveSlots postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterOrbRemoveSlots", ex);
        }
    }

    private static void BeforeOrbChannel(PlayerChoiceContext choiceContext, OrbModel orb, Player player, out bool __state)
    {
        __state = false;
        try
        {
            if (!BattleLogger.IsActive)
            {
                return;
            }

            if (!TryGetPlayerOrbQueue(player, nameof(BeforeOrbChannel), out var orbQueue))
            {
                return;
            }

            var shouldAutoAdd = player.Character.BaseOrbSlotCount == 0 && orbQueue.Capacity == 0;
            EnterOrbAutoAddScope(shouldAutoAdd);
            __state = true;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OrbCmd.Channel prefix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeOrbChannel", ex);
        }
    }

    private static void AfterOrbChannel(bool __state, ref Task __result)
    {
        try
        {
            if (!__state)
            {
                return;
            }

            __result = WrapOrbChannel(__result);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OrbCmd.Channel postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterOrbChannel", ex);
        }
    }

    private static bool BeforeOrbPassive(
        PlayerChoiceContext choiceContext,
        OrbModel orb,
        Creature? target,
        ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return true;
            }

            __result = RunOrbPassiveWithTruthSourceScope(choiceContext, orb, target);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OrbCmd.Passive prefix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeOrbPassive", ex);
            return true;
        }
    }

    private static bool BeforeOrbEvokeWithTruthSourceScope(
        PlayerChoiceContext choiceContext,
        Player player,
        OrbModel evokedOrb,
        bool dequeue,
        ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return true;
            }

            if (!TryGetPlayerOrbQueue(player, nameof(BeforeOrbEvokeWithTruthSourceScope), out var orbQueue) ||
                !TryGetPlayerCombatState(player, nameof(BeforeOrbEvokeWithTruthSourceScope), out var combatState))
            {
                return true;
            }

            __result = RunOrbEvokeWithTruthSourceScope(choiceContext, player, evokedOrb, dequeue, orbQueue, combatState);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OrbCmd.Evoke prefix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeOrbEvokeWithTruthSourceScope", ex);
            return true;
        }
    }

    private static void BeforeOrbReplace(OrbModel oldOrb, OrbModel newOrb, Player player, out OrbReplaceState __state)
    {
        if (!TryGetPlayerOrbQueue(player, nameof(BeforeOrbReplace), out var orbQueue))
        {
            __state = new OrbReplaceState(player, 0);
            return;
        }

        __state = new OrbReplaceState(player, Math.Max(0, orbQueue.Orbs.ToList().IndexOf(oldOrb)));
    }

    private static void AfterOrbReplace(OrbModel oldOrb, OrbModel newOrb, Player player, OrbReplaceState __state, ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return;
            }

            __result = WrapOrbReplace(oldOrb, newOrb, __state, __result);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OrbCmd.Replace postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterOrbReplace", ex);
        }
    }

    private static bool BeforeHookAfterOrbChanneledWithTruthSourceScope(
        CombatState combatState,
        PlayerChoiceContext choiceContext,
        Player player,
        OrbModel orb,
        ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return true;
            }

            __result = RunHookAfterOrbChanneledWithTruthSourceScope(combatState, choiceContext, player, orb);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] AfterOrbChanneled generic patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeHookAfterOrbChanneledWithTruthSourceScope", ex);
            return true;
        }
    }

    private static bool BeforeHookAfterModifyingOrbPassiveTriggerCountWithTruthSourceScope(
        CombatState combatState,
        OrbModel orb,
        IEnumerable<AbstractModel> modifiers,
        ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return true;
            }

            __result = RunHookAfterModifyingOrbPassiveTriggerCountWithTruthSourceScope(combatState, orb, modifiers);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] AfterModifyingOrbPassiveTriggerCount generic patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeHookAfterModifyingOrbPassiveTriggerCountWithTruthSourceScope", ex);
            return true;
        }
    }

    private static bool BeforeHookAfterOrbEvokedWithTruthSourceScope(
        PlayerChoiceContext choiceContext,
        CombatState combatState,
        OrbModel orb,
        IEnumerable<Creature> targets,
        ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return true;
            }

            __result = RunHookAfterOrbEvokedWithTruthSourceScope(choiceContext, combatState, orb, targets);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] AfterOrbEvoked generic patch: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeHookAfterOrbEvokedWithTruthSourceScope", ex);
            return true;
        }
    }

    private static bool BeforeOrbQueueBeforeTurnEndWithTruthSourceScope(
        OrbQueue __instance,
        PlayerChoiceContext choiceContext,
        ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return true;
            }

            if (!TryGetOrbQueueOwnerCombatState(
                    __instance,
                    nameof(BeforeOrbQueueBeforeTurnEndWithTruthSourceScope),
                    out var owner,
                    out var combatState))
            {
                return true;
            }

            __result = RunOrbQueueBeforeTurnEndWithTruthSourceScope(__instance, choiceContext, owner, combatState);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OrbQueue.BeforeTurnEnd prefix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeOrbQueueBeforeTurnEndWithTruthSourceScope", ex);
            return true;
        }
    }

    private static bool BeforeOrbQueueAfterTurnStartWithTruthSourceScope(
        OrbQueue __instance,
        PlayerChoiceContext choiceContext,
        ref Task __result)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return true;
            }

            if (!TryGetOrbQueueOwnerCombatState(
                    __instance,
                    nameof(BeforeOrbQueueAfterTurnStartWithTruthSourceScope),
                    out var owner,
                    out var combatState))
            {
                return true;
            }

            __result = RunOrbQueueAfterTurnStartWithTruthSourceScope(__instance, choiceContext, owner, combatState);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] OrbQueue.AfterTurnStart prefix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".BeforeOrbQueueAfterTurnStartWithTruthSourceScope", ex);
            return true;
        }
    }

    private static async Task WrapOrbAddSlots(OrbSlotsMutationState state, Task originalTask)
    {
        BattleLogger.PushTruthResolutionScope();
        try
        {
            await originalTask;
        }
        finally
        {
            try
            {
                if (state.Player != null)
                {
                    if (TryGetPlayerOrbQueue(state.Player, nameof(WrapOrbAddSlots), out var orbQueue))
                    {
                        BattleLogger.OnOrbSlotsChanged(
                            state.Player,
                            state.OldSlots,
                            orbQueue.Capacity,
                            state.Reason);
                    }
                }
            }
            finally
            {
                BattleLogger.PopTruthResolutionScope();
            }
        }
    }

    private static async Task WrapOrbChannel(Task originalTask)
    {
        try
        {
            await originalTask;
        }
        finally
        {
            ExitOrbAutoAddScope();
        }
    }

    private static async Task WrapOrbReplace(OrbModel oldOrb, OrbModel newOrb, OrbReplaceState state, Task originalTask)
    {
        BattleLogger.PushTruthResolutionScope();
        try
        {
            await originalTask;
        }
        finally
        {
            try
            {
                if (state.Player != null)
                {
                    BattleLogger.OnOrbReplace(state.Player, oldOrb, newOrb);
                }
            }
            finally
            {
                BattleLogger.PopTruthResolutionScope();
            }
        }
    }

    private static async Task RunOrbPassiveWithTruthSourceScope(
        PlayerChoiceContext choiceContext,
        OrbModel orb,
        Creature? target)
    {
        if (CombatManager.Instance.IsOverOrEnding)
        {
            return;
        }

        BattleLogger.PushTruthResolutionScope();
        try
        {
            BattleLogger.OnOrbPassiveTriggered(orb, "manual");
            choiceContext.PushModel(orb);
            BattleLogger.PushTruthSourceScope(orb);
            try
            {
                await orb.Passive(choiceContext, target);
                BattleLogger.OnOrbDisplayValuesPossiblyChanged(orb, "internal_state_changed");
            }
            finally
            {
                choiceContext.PopModel(orb);
                BattleLogger.PopTruthSourceScope();
            }
        }
        finally
        {
            BattleLogger.PopTruthResolutionScope();
        }
    }

    private static async Task RunOrbEvokeWithTruthSourceScope(
        PlayerChoiceContext choiceContext,
        Player player,
        OrbModel evokedOrb,
        bool dequeue,
        OrbQueue orbQueue,
        CombatState combatState)
    {
        if (CombatManager.Instance.IsOverOrEnding)
        {
            return;
        }

        if (orbQueue.Orbs.Count <= 0)
        {
            return;
        }

        BattleLogger.PushTruthResolutionScope();
        try
        {
            var oldIndex = Math.Max(0, orbQueue.Orbs.ToList().IndexOf(evokedOrb));
            var removed = false;
            if (dequeue)
            {
                removed = orbQueue.Remove(evokedOrb);
                NCombatRoom.Instance?.GetCreatureNode(player.Creature)?.OrbManager?.EvokeOrbAnim(evokedOrb);
            }

            choiceContext.PushModel(evokedOrb);
            BattleLogger.PushTruthSourceScope(evokedOrb);
            IEnumerable<Creature> targets;
            try
            {
                targets = await evokedOrb.Evoke(choiceContext);
            }
            finally
            {
                choiceContext.PopModel(evokedOrb);
                BattleLogger.PopTruthSourceScope();
            }

            var targetEntityIds = targets
                .Select(BattleLogger.ResolveTrackedEntityId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Cast<string>()
                .ToList();
            BattleLogger.OnOrbEvoked(evokedOrb, dequeue, targetEntityIds, oldIndex);
            if (removed)
            {
                BattleLogger.OnOrbRemoved(evokedOrb, "evoke");
            }

            await Hook.AfterOrbEvoked(choiceContext, combatState, evokedOrb, targets);

            if (removed)
            {
                evokedOrb.RemoveInternal();
            }
        }
        finally
        {
            BattleLogger.PopTruthResolutionScope();
        }
    }

    private static async Task RunHookAfterOrbChanneledWithTruthSourceScope(
        CombatState combatState,
        PlayerChoiceContext choiceContext,
        Player player,
        OrbModel orb)
    {
        BattleLogger.PushTruthResolutionScope();
        try
        {
            BattleLogger.OnOrbInserted(player, orb, "channel");

            foreach (var model in combatState.IterateHookListeners())
            {
                try
                {
                    choiceContext.PushModel(model);
                    await ExecuteHookListenerWithTruthSourceScope(
                        model,
                        () => model.AfterOrbChanneled(choiceContext, player, orb));
                }
                finally
                {
                    choiceContext.PopModel(model);
                }
            }
        }
        finally
        {
            BattleLogger.PopTruthResolutionScope();
        }
    }

    private static async Task RunHookAfterModifyingOrbPassiveTriggerCountWithTruthSourceScope(
        CombatState combatState,
        OrbModel orb,
        IEnumerable<AbstractModel> modifiers)
    {
        BattleLogger.PushTruthResolutionScope();
        try
        {
            foreach (var modifier in modifiers)
            {
                BattleLogger.PushTruthSourceScope(orb);
                try
                {
                    await ExecuteHookListenerWithTruthSourceScope(
                        modifier,
                        () => modifier.AfterModifyingOrbPassiveTriggerCount(orb));
                }
                finally
                {
                    BattleLogger.PopTruthSourceScope();
                }
            }
        }
        finally
        {
            BattleLogger.PopTruthResolutionScope();
        }
    }

    private static async Task RunHookAfterOrbEvokedWithTruthSourceScope(
        PlayerChoiceContext choiceContext,
        CombatState combatState,
        OrbModel orb,
        IEnumerable<Creature> targets)
    {
        BattleLogger.PushTruthResolutionScope();
        try
        {
            foreach (var model in combatState.IterateHookListeners())
            {
                await ExecuteHookListenerWithTruthSourceScope(
                    model,
                    () => model.AfterOrbEvoked(choiceContext, orb, targets));
            }
        }
        finally
        {
            BattleLogger.PopTruthResolutionScope();
        }
    }

    private static async Task RunOrbQueueBeforeTurnEndWithTruthSourceScope(
        OrbQueue orbQueue,
        PlayerChoiceContext choiceContext,
        Player owner,
        CombatState combatState)
    {
        BattleLogger.PushTruthResolutionScope();
        try
        {
            foreach (var orb in orbQueue.Orbs.ToList())
            {
                List<AbstractModel> modifyingModels;
                var triggerCount = Hook.ModifyOrbPassiveTriggerCount(combatState, orb, 1, out modifyingModels);
                await Hook.AfterModifyingOrbPassiveTriggerCount(combatState, orb, modifyingModels);
                var emitsPassiveTruth = OrbOverridesTriggerMethod(orb, OrbModelBeforeTurnEndTriggerMethod);
                for (var i = 0; i < triggerCount; i++)
                {
                    if (emitsPassiveTruth)
                    {
                        BattleLogger.OnOrbPassiveTriggered(orb, "before_turn_end");
                        BattleLogger.PushTruthSourceScope(orb);
                        try
                        {
                            await orb.BeforeTurnEndOrbTrigger(choiceContext);
                            BattleLogger.OnOrbDisplayValuesPossiblyChanged(orb, "internal_state_changed");
                        }
                        finally
                        {
                            BattleLogger.PopTruthSourceScope();
                        }
                    }
                    else
                    {
                        await orb.BeforeTurnEndOrbTrigger(choiceContext);
                    }

                    await InvokeOrbQueueSmallWait(orbQueue);
                }
            }
        }
        finally
        {
            BattleLogger.PopTruthResolutionScope();
        }
    }

    private static async Task RunOrbQueueAfterTurnStartWithTruthSourceScope(
        OrbQueue orbQueue,
        PlayerChoiceContext choiceContext,
        Player owner,
        CombatState combatState)
    {
        BattleLogger.PushTruthResolutionScope();
        try
        {
            foreach (var orb in orbQueue.Orbs.ToList())
            {
                List<AbstractModel> modifyingModels;
                var triggerCount = Hook.ModifyOrbPassiveTriggerCount(combatState, orb, 1, out modifyingModels);
                await Hook.AfterModifyingOrbPassiveTriggerCount(combatState, orb, modifyingModels);
                var emitsPassiveTruth = OrbOverridesTriggerMethod(orb, OrbModelAfterTurnStartTriggerMethod);
                for (var i = 0; i < triggerCount; i++)
                {
                    if (emitsPassiveTruth)
                    {
                        BattleLogger.OnOrbPassiveTriggered(orb, "after_turn_start");
                        BattleLogger.PushTruthSourceScope(orb);
                        try
                        {
                            await orb.AfterTurnStartOrbTrigger(choiceContext);
                            BattleLogger.OnOrbDisplayValuesPossiblyChanged(orb, "internal_state_changed");
                        }
                        finally
                        {
                            BattleLogger.PopTruthSourceScope();
                        }
                    }
                    else
                    {
                        await orb.AfterTurnStartOrbTrigger(choiceContext);
                    }

                    await InvokeOrbQueueSmallWait(orbQueue);
                }
            }
        }
        finally
        {
            BattleLogger.PopTruthResolutionScope();
        }
    }

    private static Stack<bool> EnsureOrbAutoAddScopeStack()
    {
        return OrbAutoAddScopeStack.Value ??= new Stack<bool>();
    }

    private static bool OrbOverridesTriggerMethod(OrbModel orb, MethodInfo? baseMethod)
    {
        if (baseMethod == null)
        {
            return true;
        }

        try
        {
            var method = orb.GetType().GetMethod(
                baseMethod.Name,
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(PlayerChoiceContext) },
                modifiers: null);
            return method != null && method.DeclaringType != baseMethod.DeclaringType;
        }
        catch
        {
            return true;
        }
    }

    private static void EnterOrbAutoAddScope(bool shouldAutoAdd)
    {
        EnsureOrbAutoAddScopeStack().Push(shouldAutoAdd);
    }

    private static void ExitOrbAutoAddScope()
    {
        var stack = EnsureOrbAutoAddScopeStack();
        if (stack.Count > 0)
        {
            stack.Pop();
        }
    }

    private static bool IsOrbAutoAddScopeActive()
    {
        var stack = EnsureOrbAutoAddScopeStack();
        return stack.Count > 0 && stack.Peek();
    }

    private static Player? GetOrbQueueOwner(OrbQueue queue)
    {
        return OrbQueueOwnerField?.GetValue(queue) as Player;
    }

    private static bool TryGetPlayerOrbQueue(
        Player? player,
        string context,
        [NotNullWhen(true)] out OrbQueue? orbQueue)
    {
        orbQueue = player?.PlayerCombatState?.OrbQueue;
        if (orbQueue != null)
        {
            return true;
        }

        DebugFileLogger.Log(
            nameof(RecorderMod) + "." + context,
            "Skipping orb recorder path because PlayerCombatState/OrbQueue was unavailable.");
        return false;
    }

    private static bool TryGetPlayerCombatState(
        Player? player,
        string context,
        [NotNullWhen(true)] out CombatState? combatState)
    {
        combatState = player?.Creature?.CombatState;
        if (combatState != null)
        {
            return true;
        }

        DebugFileLogger.Log(
            nameof(RecorderMod) + "." + context,
            "Skipping orb recorder path because the player's CombatState was unavailable.");
        return false;
    }

    private static bool TryGetOrbQueueOwnerCombatState(
        OrbQueue queue,
        string context,
        [NotNullWhen(true)] out Player? owner,
        [NotNullWhen(true)] out CombatState? combatState)
    {
        owner = GetOrbQueueOwner(queue);
        if (owner == null)
        {
            combatState = null;
            DebugFileLogger.Log(
                nameof(RecorderMod) + "." + context,
                "Skipping orb recorder path because the orb queue owner was unavailable.");
            return false;
        }

        return TryGetPlayerCombatState(owner, context, out combatState);
    }

    private static Task InvokeOrbQueueSmallWait(OrbQueue queue)
    {
        if (OrbQueueSmallWaitMethod?.Invoke(queue, null) is Task waitTask)
        {
            return waitTask;
        }

        return Task.CompletedTask;
    }

    private static void BeforeRelicIncrementStackCount(RelicModel __instance, out RelicIntMutationState __state)
    {
        __state = new RelicIntMutationState(__instance, __instance.StackCount);
    }

    private static void AfterRelicIncrementStackCount(RelicModel __instance, RelicIntMutationState __state)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return;
            }

            BattleLogger.OnRelicStackCountModified(__instance, __state.OldValue, __instance.StackCount);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] Relic stack count postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterRelicIncrementStackCount", ex);
        }
    }

    private static void BeforeRelicIsWaxMutated(RelicModel __instance, out RelicBoolMutationState __state)
    {
        __state = new RelicBoolMutationState(__instance, __instance.IsWax);
    }

    private static void AfterRelicIsWaxMutated(RelicModel __instance, RelicBoolMutationState __state)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return;
            }

            BattleLogger.OnRelicFlagModified(__instance, "is_wax", __state.OldValue, __instance.IsWax);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] Relic IsWax postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterRelicIsWaxMutated", ex);
        }
    }

    private static void BeforeRelicIsMeltedMutated(RelicModel __instance, out RelicBoolMutationState __state)
    {
        __state = new RelicBoolMutationState(__instance, __instance.IsMelted);
    }

    private static void AfterRelicIsMeltedMutated(RelicModel __instance, RelicBoolMutationState __state)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return;
            }

            BattleLogger.OnRelicFlagModified(__instance, "is_melted", __state.OldValue, __instance.IsMelted);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] Relic IsMelted postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterRelicIsMeltedMutated", ex);
        }
    }

    private static void AfterAddRelicInternal(Player __instance, RelicModel relic, bool silent)
    {
        try
        {
            if (!BattleLogger.IsActive || !silent)
            {
                return;
            }

            BattleLogger.HandleSilentRelicObtained(__instance, relic);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] AddRelicInternal postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterAddRelicInternal", ex);
        }
    }

    private static void AfterRemoveRelicInternal(Player __instance, RelicModel relic, bool silent)
    {
        try
        {
            if (!BattleLogger.IsActive || !silent)
            {
                return;
            }

            BattleLogger.HandleSilentRelicRemoved(__instance, relic);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] RemoveRelicInternal postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterRemoveRelicInternal", ex);
        }
    }

    private static CardTruthMutationState CaptureCardTruthMutationState(IEnumerable<CardModel?> cards)
    {
        var records = cards
            .Where(card => card != null)
            .Distinct()
            .Select(card => new CardTruthMutationRecord(card, BattleLogger.CaptureCardTruthState(card!)))
            .ToList();
        return new CardTruthMutationState(records);
    }

    private static CardTruthMutationState CaptureCardTruthMutationState(CardModel? card)
    {
        return CaptureCardTruthMutationState(card == null
            ? Array.Empty<CardModel?>()
            : new CardModel?[] { card });
    }

    private static CardTruthMutationState CaptureEnchantmentOwnerMutationState(EnchantmentModel? enchantment)
    {
        var card = enchantment != null && enchantment.HasCard
            ? enchantment.Card
            : null;
        return CaptureCardTruthMutationState(card);
    }

    private static CardTruthMutationState CaptureForgeMutationState(Player? player)
    {
        var cards = player?.PlayerCombatState?.AllCards
            .OfType<SovereignBlade>()
            .Where(card => !card.IsDupe)
            .Cast<CardModel?>()
            .ToList() ?? new List<CardModel?>();
        return CaptureCardTruthMutationState(cards);
    }

    private static void EmitCardTruthMutation(
        CardTruthMutationState state,
        string reason,
        CardTruthDiffFields fields)
    {
        foreach (var record in state.Records)
        {
            if (record.Card == null || record.OldState == null)
            {
                continue;
            }

            BattleLogger.OnCardStateModified(record.Card, record.OldState, reason, fields);
        }
    }

    private static void BeforeCardUpgrade(ref IEnumerable<CardModel> cards, out CardTruthMutationState __state)
    {
        var materializedCards = cards?.ToList() ?? new List<CardModel>();
        cards = materializedCards;
        __state = CaptureCardTruthMutationState(materializedCards);
    }

    private static void AfterCardUpgrade(CardTruthMutationState __state)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return;
            }

            EmitCardTruthMutation(__state, "upgrade", CardTruthDiffFields.Upgrade | CardTruthDiffFields.DynamicValues);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] Card upgrade postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterCardUpgrade", ex);
        }
    }

    private static void BeforeCardDowngrade(CardModel card, out CardTruthMutationState __state)
    {
        __state = CaptureCardTruthMutationState(card);
    }

    private static void AfterCardDowngrade(CardTruthMutationState __state)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return;
            }

            EmitCardTruthMutation(
                __state,
                "downgrade",
                CardTruthDiffFields.Upgrade |
                CardTruthDiffFields.StarCost |
                CardTruthDiffFields.Keywords |
                CardTruthDiffFields.VisibleFlags |
                CardTruthDiffFields.DynamicValues);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] Card downgrade postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterCardDowngrade", ex);
        }
    }

    private static void BeforeCardStarCostMutation(CardModel __instance, out CardTruthMutationState __state)
    {
        __state = CaptureCardTruthMutationState(__instance);
    }

    private static void AfterCardStarCostMutation(
        CardTruthMutationState __state,
        MethodBase __originalMethod)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return;
            }

            EmitCardTruthMutation(__state, MapCardStarCostModificationReason(__originalMethod.Name), CardTruthDiffFields.StarCost);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] Card star-cost postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterCardStarCostMutation", ex);
        }
    }

    private static void BeforeReplayCountMutation(CardModel __instance, out CardTruthMutationState __state)
    {
        __state = CaptureCardTruthMutationState(__instance);
    }

    private static void AfterReplayCountMutation(CardTruthMutationState __state)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return;
            }

            EmitCardTruthMutation(__state, "replay_count_set", CardTruthDiffFields.ReplayCount);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] Replay-count postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterReplayCountMutation", ex);
        }
    }

    private static void BeforeKeywordMutation(CardModel __instance, out CardTruthMutationState __state)
    {
        __state = CaptureCardTruthMutationState(__instance);
    }

    private static void AfterKeywordMutation(CardTruthMutationState __state, MethodBase __originalMethod)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return;
            }

            EmitCardTruthMutation(
                __state,
                GameStateReader.ToSnakeCase(__originalMethod.Name),
                CardTruthDiffFields.Keywords | CardTruthDiffFields.VisibleFlags);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] Keyword postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterKeywordMutation", ex);
        }
    }

    private static void BeforeSingleTurnRetainMutation(CardModel __instance, out CardTruthMutationState __state)
    {
        __state = CaptureCardTruthMutationState(__instance);
    }

    private static void AfterSingleTurnRetainMutation(CardTruthMutationState __state)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return;
            }

            EmitCardTruthMutation(__state, "give_single_turn_retain", CardTruthDiffFields.VisibleFlags);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] Single-turn retain postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterSingleTurnRetainMutation", ex);
        }
    }

    private static void BeforeSingleTurnSlyMutation(CardModel __instance, out CardTruthMutationState __state)
    {
        __state = CaptureCardTruthMutationState(__instance);
    }

    private static void AfterSingleTurnSlyMutation(CardTruthMutationState __state)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return;
            }

            EmitCardTruthMutation(__state, "give_single_turn_sly", CardTruthDiffFields.VisibleFlags);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] Single-turn sly postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterSingleTurnSlyMutation", ex);
        }
    }

    private static void BeforeCardEndOfTurnCleanup(CardModel __instance, out CardTruthMutationState __state)
    {
        __state = CaptureCardTruthMutationState(__instance);
    }

    private static void AfterCardEndOfTurnCleanup(CardTruthMutationState __state)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return;
            }

            EmitCardTruthMutation(__state, "cleanup_end_of_turn", CardTruthDiffFields.StarCost | CardTruthDiffFields.VisibleFlags);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] Card end-of-turn cleanup postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterCardEndOfTurnCleanup", ex);
        }
    }

    private static void BeforeCardEnchant(EnchantmentModel enchantment, CardModel card, decimal amount, out CardTruthMutationState __state)
    {
        __state = CaptureCardTruthMutationState(card);
    }

    private static void AfterCardEnchant(CardTruthMutationState __state)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return;
            }

            EmitCardTruthMutation(__state, "enchant", CardTruthDiffFields.Enchantment | CardTruthDiffFields.ReplayCount | CardTruthDiffFields.DynamicValues);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] Card enchant postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterCardEnchant", ex);
        }
    }

    private static void BeforeClearEnchantment(CardModel card, out CardTruthMutationState __state)
    {
        __state = CaptureCardTruthMutationState(card);
    }

    private static void AfterClearEnchantment(CardTruthMutationState __state)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return;
            }

            EmitCardTruthMutation(__state, "clear_enchantment", CardTruthDiffFields.Enchantment | CardTruthDiffFields.ReplayCount | CardTruthDiffFields.DynamicValues);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] Clear enchantment postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterClearEnchantment", ex);
        }
    }

    private static void BeforeEnchantmentStatusMutation(EnchantmentModel __instance, out CardTruthMutationState __state)
    {
        __state = CaptureEnchantmentOwnerMutationState(__instance);
    }

    private static void AfterEnchantmentStatusMutation(CardTruthMutationState __state)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return;
            }

            EmitCardTruthMutation(__state, "enchantment_status", CardTruthDiffFields.Enchantment | CardTruthDiffFields.ReplayCount | CardTruthDiffFields.DynamicValues);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] Enchantment status postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterEnchantmentStatusMutation", ex);
        }
    }

    private static void BeforeCardAfflict(AfflictionModel affliction, CardModel card, decimal amount, out CardTruthMutationState __state)
    {
        __state = CaptureCardTruthMutationState(card);
    }

    private static void AfterCardAfflict(CardTruthMutationState __state)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return;
            }

            EmitCardTruthMutation(__state, "afflict", CardTruthDiffFields.Affliction | CardTruthDiffFields.DynamicValues);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] Card afflict postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterCardAfflict", ex);
        }
    }

    private static void BeforeClearAffliction(CardModel card, out CardTruthMutationState __state)
    {
        __state = CaptureCardTruthMutationState(card);
    }

    private static void AfterClearAffliction(CardTruthMutationState __state)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return;
            }

            EmitCardTruthMutation(__state, "clear_affliction", CardTruthDiffFields.Affliction | CardTruthDiffFields.DynamicValues);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] Clear affliction postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterClearAffliction", ex);
        }
    }

    private static void BeforeForgeSovereignBladeDamage(decimal amount, Player player, out CardTruthMutationState __state)
    {
        __state = CaptureForgeMutationState(player);
    }

    private static void AfterForgeSovereignBladeDamage(CardTruthMutationState __state)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return;
            }

            EmitCardTruthMutation(__state, "forge", CardTruthDiffFields.DynamicValues);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] Forge postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterForgeSovereignBladeDamage", ex);
        }
    }

    private static void BeforeSovereignBladeSetRepeats(SovereignBlade __instance, out CardTruthMutationState __state)
    {
        __state = CaptureCardTruthMutationState(__instance);
    }

    private static void AfterSovereignBladeSetRepeats(CardTruthMutationState __state)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return;
            }

            EmitCardTruthMutation(__state, "set_repeats", CardTruthDiffFields.DynamicValues);
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] SovereignBlade.SetRepeats postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterSovereignBladeSetRepeats", ex);
        }
    }

    private static void ApplyCardEnergyCostMutationPatch(Harmony harmony, string methodName)
    {
        ApplyPatch(
            harmony,
            $"CardEnergyCost.{methodName}",
            AccessTools.Method(typeof(CardEnergyCost), methodName),
            AccessTools.Method(typeof(RecorderMod), nameof(BeforeCardEnergyCostMutation)),
            AccessTools.Method(typeof(RecorderMod), nameof(AfterCardEnergyCostMutation)));
    }

    private static void ApplyCardEnergyCostCleanupPatch(Harmony harmony, string methodName)
    {
        ApplyPatch(
            harmony,
            $"CardEnergyCost.{methodName}",
            AccessTools.Method(typeof(CardEnergyCost), methodName),
            AccessTools.Method(typeof(RecorderMod), nameof(BeforeCardEnergyCostMutation)),
            AccessTools.Method(typeof(RecorderMod), nameof(AfterCardEnergyCostCleanup)));
    }

    private static void BeforeCardEnergyCostMutation(CardEnergyCost __instance, out CardCostMutationState __state)
    {
        var card = ResolveCardFromEnergyCost(__instance);
        var oldCost = card != null ? GameStateReader.GetEnergyCost(card) : 0;
        __state = new CardCostMutationState(card, oldCost);
    }

    private static void AfterCardEnergyCostMutation(
        CardEnergyCost __instance,
        CardCostMutationState __state,
        MethodBase __originalMethod)
    {
        try
        {
            if (!BattleLogger.IsActive)
            {
                return;
            }

            var card = __state.Card ?? ResolveCardFromEnergyCost(__instance);
            if (card == null)
            {
                return;
            }

            BattleLogger.OnCardCostModified(
                card,
                __state.OldCost,
                GameStateReader.GetEnergyCost(card),
                MapCardCostModificationReason(__originalMethod.Name));
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] CardEnergyCost postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterCardEnergyCostMutation", ex);
        }
    }

    private static void AfterCardEnergyCostCleanup(
        CardEnergyCost __instance,
        CardCostMutationState __state,
        MethodBase __originalMethod,
        bool __result)
    {
        try
        {
            if (!BattleLogger.IsActive || !__result)
            {
                return;
            }

            var card = __state.Card ?? ResolveCardFromEnergyCost(__instance);
            if (card == null)
            {
                return;
            }

            BattleLogger.OnCardCostModified(
                card,
                __state.OldCost,
                GameStateReader.GetEnergyCost(card),
                MapCardCostModificationReason(__originalMethod.Name));
        }
        catch (Exception ex)
        {
            Log.Error($"[STS2CombatRecorder] CardEnergyCost cleanup postfix: {ex.Message}");
            DebugFileLogger.Error(nameof(RecorderMod) + ".AfterCardEnergyCostCleanup", ex);
        }
    }

    private static CardModel? ResolveCardFromEnergyCost(CardEnergyCost energyCost)
    {
        return CardEnergyCostCardField?.GetValue(energyCost) as CardModel;
    }

    private static string MapCardCostModificationReason(string methodName)
    {
        return methodName switch
        {
            nameof(CardEnergyCost.SetUntilPlayed) => "set_until_played",
            nameof(CardEnergyCost.SetThisTurnOrUntilPlayed) => "set_this_turn_or_until_played",
            nameof(CardEnergyCost.SetThisTurn) => "set_this_turn",
            nameof(CardEnergyCost.SetThisCombat) => "set_this_combat",
            nameof(CardEnergyCost.AddUntilPlayed) => "add_until_played",
            nameof(CardEnergyCost.AddThisTurnOrUntilPlayed) => "add_this_turn_or_until_played",
            nameof(CardEnergyCost.AddThisTurn) => "add_this_turn",
            nameof(CardEnergyCost.AddThisCombat) => "add_this_combat",
            nameof(CardEnergyCost.ResetForDowngrade) => "reset_for_downgrade",
            nameof(CardEnergyCost.SetCustomBaseCost) => "set_custom_base_cost",
            nameof(CardEnergyCost.EndOfTurnCleanup) => "cleanup_end_of_turn",
            nameof(CardEnergyCost.AfterCardPlayedCleanup) => "cleanup_after_play",
            _ => GameStateReader.ToSnakeCase(methodName),
        };
    }

    private static string MapCardStarCostModificationReason(string methodName)
    {
        return methodName switch
        {
            nameof(CardModel.SetStarCostUntilPlayed) => "set_star_cost_until_played",
            nameof(CardModel.SetStarCostThisTurn) => "set_star_cost_this_turn",
            nameof(CardModel.SetStarCostThisCombat) => "set_star_cost_this_combat",
            "UpgradeStarCostBy" => "upgrade_star_cost_by",
            _ => GameStateReader.ToSnakeCase(methodName),
        };
    }

    private static async Task ExecuteHookListenerWithTruthSourceScope(AbstractModel model, Func<Task> invocation)
    {
        var pushedScope = false;
        var pushedResolution = false;
        try
        {
            BattleLogger.PushTruthResolutionScope();
            pushedResolution = true;
            BattleLogger.PushTruthSourceScope(model);
            pushedScope = true;
            await invocation();
        }
        finally
        {
            model.InvokeExecutionFinished();
            if (pushedScope)
            {
                BattleLogger.PopTruthSourceScope();
            }
            if (pushedResolution)
            {
                BattleLogger.PopTruthResolutionScope();
            }
        }
    }

    private static async Task WrapTruthSourceScope(Task originalTask)
    {
        try
        {
            await originalTask;
        }
        finally
        {
            BattleLogger.PopTruthSourceScope();
        }
    }

    private static void ApplyPatch(Harmony harmony, string patchName, MethodBase? original, MethodInfo postfix)
    {
        if (original == null)
        {
            DebugFileLogger.Log(nameof(RecorderMod) + ".Initialize",
                $"Patch skipped: {patchName} original method not found.");
            return;
        }

        harmony.Patch(original, postfix: new HarmonyMethod(postfix));
        DebugFileLogger.Log(nameof(RecorderMod) + ".Initialize",
            $"Patch applied: {patchName}");
    }

    private static void ApplyPatch(Harmony harmony, string patchName, MethodBase? original, MethodInfo? prefix, MethodInfo? postfix)
    {
        if (original == null)
        {
            DebugFileLogger.Log(nameof(RecorderMod) + ".Initialize",
                $"Patch skipped: {patchName} original method not found.");
            return;
        }

        harmony.Patch(
            original,
            prefix: prefix != null ? new HarmonyMethod(prefix) : null,
            postfix: postfix != null ? new HarmonyMethod(postfix) : null);
        DebugFileLogger.Log(nameof(RecorderMod) + ".Initialize",
            $"Patch applied: {patchName}");
    }
}
