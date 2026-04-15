using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Models;

namespace STS2CombatRecorder;

[Flags]
internal enum CardTruthDiffFields
{
    None = 0,
    Cost = 1 << 0,
    StarCost = 1 << 1,
    Upgrade = 1 << 2,
    ReplayCount = 1 << 3,
    Keywords = 1 << 4,
    VisibleFlags = 1 << 5,
    Enchantment = 1 << 6,
    Affliction = 1 << 7,
    DynamicValues = 1 << 8,
    All = Cost | StarCost | Upgrade | ReplayCount | Keywords | VisibleFlags | Enchantment | Affliction | DynamicValues,
}

internal sealed class CardVisibleFlagsSnapshot
{
    public bool RetainThisTurn { get; init; }
    public bool SlyThisTurn { get; init; }
}

internal sealed class CardEnchantmentSnapshot
{
    public required string EnchantmentId { get; init; }
    public required string Name { get; init; }
    public int Amount { get; init; }
    public required string Status { get; init; }
    public int DisplayAmount { get; init; }
    public bool ShowAmount { get; init; }
}

internal sealed class CardAfflictionSnapshot
{
    public required string AfflictionId { get; init; }
    public required string Name { get; init; }
    public int Amount { get; init; }
}

internal sealed class CardTruthStateSnapshot
{
    public required string CardName { get; init; }
    public int Cost { get; init; }
    public int? StarCost { get; init; }
    public int CurrentUpgradeLevel { get; init; }
    public int ReplayCount { get; init; }
    public required IReadOnlyList<string> Keywords { get; init; }
    public required CardVisibleFlagsSnapshot VisibleFlags { get; init; }
    public CardEnchantmentSnapshot? Enchantment { get; init; }
    public CardAfflictionSnapshot? Affliction { get; init; }
    public required IReadOnlyDictionary<string, int> DynamicValues { get; init; }

    public static CardTruthStateSnapshot Capture(CardModel card)
    {
        var visibleFlags = GameStateReader.GetVisibleFlags(card);
        var dynamicValues = GameStateReader.GetDynamicValues(card);

        CardEnchantmentSnapshot? enchantment = null;
        if (GameStateReader.TryGetEnchantmentInfo(card, out var enchantmentInfo))
        {
            enchantment = new CardEnchantmentSnapshot
            {
                EnchantmentId = enchantmentInfo.EnchantmentId,
                Name = enchantmentInfo.Name,
                Amount = enchantmentInfo.Amount,
                Status = enchantmentInfo.Status,
                DisplayAmount = enchantmentInfo.DisplayAmount,
                ShowAmount = enchantmentInfo.ShowAmount,
            };
        }

        CardAfflictionSnapshot? affliction = null;
        if (GameStateReader.TryGetAfflictionInfo(card, out var afflictionInfo))
        {
            affliction = new CardAfflictionSnapshot
            {
                AfflictionId = afflictionInfo.AfflictionId,
                Name = afflictionInfo.Name,
                Amount = afflictionInfo.Amount,
            };
        }

        return new CardTruthStateSnapshot
        {
            CardName = card.Title?.ToString() ?? card.GetType().Name,
            Cost = GameStateReader.GetEnergyCost(card),
            StarCost = GameStateReader.GetVisibleStarCost(card),
            CurrentUpgradeLevel = card.CurrentUpgradeLevel,
            ReplayCount = GameStateReader.GetReplayCount(card),
            Keywords = GameStateReader.GetKeywords(card),
            VisibleFlags = new CardVisibleFlagsSnapshot
            {
                RetainThisTurn = visibleFlags.RetainThisTurn,
                SlyThisTurn = visibleFlags.SlyThisTurn,
            },
            Enchantment = enchantment,
            Affliction = affliction,
            DynamicValues = dynamicValues.ToDictionary(
                entry => entry.Key,
                entry => entry.Value,
                StringComparer.Ordinal),
        };
    }
}
