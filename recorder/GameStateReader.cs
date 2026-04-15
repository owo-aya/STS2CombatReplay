using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Runs;

namespace STS2CombatRecorder;

/// <summary>
/// Pure state-reading utilities. No side effects — just maps game objects
/// to the domain types needed by BattleLogger.
/// </summary>
public static class GameStateReader
{
    // ─── Entity helpers ─────────────────────────────────────────────────────

    public static Player? GetPlayer(CombatState? combat)
    {
        if (combat == null) return null;
        try { return combat.Players.FirstOrDefault(); }
        catch { return null; }
    }

    public static List<Creature> GetAliveEnemies(CombatState combat)
    {
        try { return combat.Enemies.Where(e => e.IsAlive).ToList(); }
        catch { return new List<Creature>(); }
    }

    public static List<Creature> GetAllEnemies(CombatState combat)
    {
        try { return combat.Enemies.ToList(); }
        catch { return new List<Creature>(); }
    }

    public struct EntityInfo
    {
        public string DefId;
        public string Name;
        public int CurrentHp;
        public int MaxHp;
        public int Block;
    }

    public struct IntentInfo
    {
        public string IntentId;
        public string IntentName;
        public int? ProjectedDamage;
        public int? ProjectedHits;
    }

    public struct PowerInfo
    {
        public string PowerId;
        public string Name;
        public int Stacks;
    }

    public struct OrbInfo
    {
        public OrbModel Instance;
        public string OrbId;
        public string Name;
        public int SlotIndex;
        public decimal Passive;
        public decimal Evoke;
    }

    public struct RelicInfo
    {
        public RelicModel Instance;
        public string RelicId;
        public string Name;
        public int StackCount;
        public string Status;
        public int? DisplayAmount;
        public bool IsUsedUp;
        public bool IsWax;
        public bool IsMelted;
    }

    public static EntityInfo GetEntityInfo(Creature creature, string side)
    {
        var info = new EntityInfo();
        try
        {
            info.CurrentHp = creature.CurrentHp;
            info.MaxHp = creature.MaxHp;
            info.Block = creature.Block;
            info.Name = creature.Name ?? (side == "player" ? "Player" : "Enemy");

            if (side == "enemy")
            {
                var monster = creature.Monster;
                info.DefId = monster?.Id?.Entry ?? ToSnakeCase(monster?.GetType().Name ?? "unknown");
                info.Name = SafeTitle(monster?.Title) ?? info.Name;
                if (info.Name == null || info.Name.Contains("LocString"))
                    info.Name = monster?.GetType().Name ?? "Enemy";
            }
            else
            {
                var player = creature.Player;
                info.DefId = player != null ? GetCharacterId(player) : "player";
                info.Name = player != null ? GetCharacterName(player) : "Player";
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2CombatRecorder] GetEntityInfo failed: {ex.Message}");
        }
        return info;
    }

    public static bool TryGetVisibleIntentInfo(Creature creature, out IntentInfo info)
    {
        info = default;

        try
        {
            var monster = creature.Monster;
            if (monster == null) return false;

            var intents = monster.NextMove.Intents;
            if (intents == null || intents.Count == 0) return false;

            var componentIds = new List<string>();
            var componentNames = new List<string>();
            int? projectedDamage = null;
            int? projectedHits = null;
            var targets = creature.CombatState?.PlayerCreatures ?? Array.Empty<Creature>();

            foreach (var intent in intents)
            {
                var (componentId, componentName) = NormalizeIntentComponent(intent);
                if (!string.IsNullOrEmpty(componentId))
                {
                    componentIds.Add(componentId);
                    componentNames.Add(componentName);
                }

                if (intent is AttackIntent attackIntent)
                {
                    projectedDamage = attackIntent.GetSingleDamage(targets, creature);
                    projectedHits = Math.Max(1, attackIntent.Repeats);
                }
            }

            if (componentIds.Count == 0) return false;

            info.IntentId = string.Join("+", componentIds.Distinct());
            info.IntentName = string.Join(" + ", componentNames.Distinct());
            info.ProjectedDamage = projectedDamage;
            info.ProjectedHits = projectedHits;
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2CombatRecorder] TryGetVisibleIntentInfo failed: {ex.Message}");
            return false;
        }
    }

    private static (string Id, string Name) NormalizeIntentComponent(AbstractIntent intent)
    {
        return intent.IntentType switch
        {
            IntentType.Attack or IntentType.DeathBlow => ("attack", "Attack"),
            IntentType.Defend => ("defend", "Defend"),
            IntentType.Buff => ("buff", "Buff"),
            IntentType.Debuff or IntentType.DebuffStrong or IntentType.CardDebuff or IntentType.StatusCard => ("debuff", "Debuff"),
            IntentType.Heal => ("heal", "Heal"),
            IntentType.Escape => ("escape", "Escape"),
            IntentType.Hidden => ("hidden", "Hidden"),
            IntentType.Sleep => ("sleep", "Sleep"),
            IntentType.Stun => ("stun", "Stun"),
            IntentType.Summon => ("summon", "Summon"),
            _ => ("unknown", "Unknown"),
        };
    }

    // ─── Card helpers ───────────────────────────────────────────────────────

    public struct CardZoneInfo
    {
        public CardModel Model;
        public string DefId;
        public string Name;
        public string Zone;
        public int Cost;
    }

    /// <summary>
    /// Read all cards across combat zones. Uses reflection to extract CardModel
    /// from potential wrapper types (CardInHand, etc.).
    /// </summary>
    public static List<CardZoneInfo> ReadAllCards(PlayerCombatState? pcs)
    {
        var result = new List<CardZoneInfo>();
        if (pcs == null) return result;

        var zones = new (object? pile, string zoneName)[]
        {
            (pcs.DrawPile?.Cards, "draw"),
            (pcs.Hand?.Cards, "hand"),
            (pcs.PlayPile?.Cards, "play"),
            (pcs.DiscardPile?.Cards, "discard"),
            (pcs.ExhaustPile?.Cards, "exhaust"),
        };

        foreach (var (pile, zoneName) in zones)
        {
            if (pile == null) continue;
            foreach (var item in EnumerateItems(pile))
            {
                var model = GetCardModel(item);
                if (model == null) continue;
                result.Add(new CardZoneInfo
                {
                    Model = model,
                    DefId = model.Id?.Entry ?? ToSnakeCase(model.GetType().Name),
                    Name = SafeTitle(model.Title) ?? model.GetType().Name,
                    Zone = zoneName,
                    Cost = GetEnergyCost(model),
                });
            }
        }
        return result;
    }

    public static List<CardZoneInfo> GetCardsInZone(PlayerCombatState pcs, string zone)
    {
        var pile = zone switch
        {
            "draw" => pcs.DrawPile?.Cards,
            "hand" => pcs.Hand?.Cards,
            "play" => pcs.PlayPile?.Cards,
            "discard" => pcs.DiscardPile?.Cards,
            "exhaust" => pcs.ExhaustPile?.Cards,
            _ => null,
        };
        if (pile == null) return new List<CardZoneInfo>();

        var result = new List<CardZoneInfo>();
        foreach (var item in EnumerateItems(pile))
        {
            var model = GetCardModel(item);
            if (model == null) continue;
            result.Add(new CardZoneInfo
            {
                Model = model,
                DefId = model.Id?.Entry ?? ToSnakeCase(model.GetType().Name),
                Name = SafeTitle(model.Title) ?? model.GetType().Name,
                Zone = zone,
                Cost = GetEnergyCost(model),
            });
        }
        return result;
    }

    /// <summary>
    /// Extract CardModel from a pile item. Items may be CardModel directly
    /// or wrapper objects with Model/CardModel/Card property.
    /// </summary>
    public static CardModel? GetCardModel(object? item)
    {
        if (item == null) return null;
        if (item is CardModel cm) return cm;

        try
        {
            var t = item.GetType();
            foreach (var propName in new[] { "Model", "CardModel", "Card" })
            {
                var p = t.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (p?.GetValue(item) is CardModel m) return m;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    public static int GetEnergyCost(CardModel model)
    {
        try
        {
            if (model.EnergyCost == null) return 0;
            return model.EnergyCost.GetAmountToSpend();
        }
        catch { return 0; }
    }

    // ─── Potion helpers ─────────────────────────────────────────────────────

    public struct PotionInfo
    {
        public PotionModel Instance;
        public string DefId;
        public string Name;
        public int SlotIndex;
    }

    public static List<PotionInfo> ReadPotions(Player player)
    {
        var result = new List<PotionInfo>();
        try
        {
            int slotIndex = 0;
            foreach (var potion in player.PotionSlots)
            {
                if (potion != null)
                {
                    result.Add(new PotionInfo
                    {
                        Instance = potion,
                        DefId = potion.Id?.Entry ?? ToSnakeCase(potion.GetType().Name),
                        Name = SafeTitle(potion.Title) ?? potion.GetType().Name,
                        SlotIndex = slotIndex,
                    });
                }
                slotIndex++;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2CombatRecorder] ReadPotions failed: {ex.Message}");
        }
        return result;
    }

    public static int? FindPotionSlotIndex(Player player, PotionModel potion)
    {
        try
        {
            for (int i = 0; i < player.PotionSlots.Count; i++)
            {
                if (ReferenceEquals(player.PotionSlots[i], potion))
                {
                    return i;
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2CombatRecorder] FindPotionSlotIndex failed: {ex.Message}");
        }

        return null;
    }

    // ─── Power helpers ──────────────────────────────────────────────────────

    public static List<PowerInfo> ReadPowers(Creature creature)
    {
        var result = new Dictionary<string, PowerInfo>(StringComparer.Ordinal);

        try
        {
            foreach (var power in creature.Powers)
            {
                if (power == null)
                {
                    continue;
                }

                if (!power.IsVisible)
                {
                    continue;
                }

                var powerId = GetPowerId(power);
                var powerName = GetPowerName(power);
                var stacks = power.Amount;

                if (result.TryGetValue(powerId, out var existing))
                {
                    existing.Stacks += stacks;
                    if (string.IsNullOrEmpty(existing.Name) && !string.IsNullOrEmpty(powerName))
                    {
                        existing.Name = powerName;
                    }
                    result[powerId] = existing;
                    continue;
                }

                result[powerId] = new PowerInfo
                {
                    PowerId = powerId,
                    Name = powerName,
                    Stacks = stacks,
                };
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2CombatRecorder] ReadPowers failed: {ex.Message}");
        }

        return result.Values.OrderBy(info => info.PowerId, StringComparer.Ordinal).ToList();
    }

    public static int GetOrbSlots(Player player)
    {
        try
        {
            return player.PlayerCombatState?.OrbQueue.Capacity ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    public static List<OrbInfo> ReadOrbs(Player player)
    {
        var result = new List<OrbInfo>();

        try
        {
            var orbQueue = player.PlayerCombatState?.OrbQueue;
            if (orbQueue == null)
            {
                return result;
            }

            for (int slotIndex = 0; slotIndex < orbQueue.Orbs.Count; slotIndex++)
            {
                var orb = orbQueue.Orbs[slotIndex];
                if (orb == null)
                {
                    continue;
                }

                result.Add(new OrbInfo
                {
                    Instance = orb,
                    OrbId = GetOrbId(orb),
                    Name = GetOrbName(orb),
                    SlotIndex = slotIndex,
                    Passive = orb.PassiveVal,
                    Evoke = orb.EvokeVal,
                });
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2CombatRecorder] ReadOrbs failed: {ex.Message}");
        }

        return result;
    }

    public static List<RelicInfo> ReadRelics(Player player)
    {
        var result = new List<RelicInfo>();

        try
        {
            foreach (var relic in player.Relics)
            {
                if (relic == null)
                {
                    continue;
                }

                result.Add(new RelicInfo
                {
                    Instance = relic,
                    RelicId = GetRelicId(relic),
                    Name = GetRelicName(relic),
                    StackCount = relic.StackCount,
                    Status = GetRelicStatus(relic.Status),
                    DisplayAmount = relic.ShowCounter ? relic.DisplayAmount : null,
                    IsUsedUp = relic.IsUsedUp,
                    IsWax = relic.IsWax,
                    IsMelted = relic.IsMelted,
                });
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2CombatRecorder] ReadRelics failed: {ex.Message}");
        }

        return result;
    }

    // ─── Encounter / character helpers ──────────────────────────────────────

    public static string GetEncounterId(CombatState combat)
    {
        try
        {
            var encounter = combat.Encounter;
            var encounterEntry = encounter?.Id.Entry;
            if (!string.IsNullOrWhiteSpace(encounterEntry))
            {
                return NormalizeModelIdEntry(encounterEntry);
            }
        }
        catch { }

        try
        {
            // Try reflection — some builds expose EncounterId on CombatState
            var prop = combat.GetType().GetProperty("EncounterId", BindingFlags.Public | BindingFlags.Instance);
            if (prop != null)
            {
                var val = prop.GetValue(combat);
                if (val != null) return val.ToString() ?? "unknown";
            }
        }
        catch { }
        return "unknown";
    }

    public static string GetEncounterName(CombatState combat)
    {
        try
        {
            var encounter = combat.Encounter;
            if (encounter != null)
            {
                var title = encounter.Title?.GetFormattedText();
                if (!string.IsNullOrWhiteSpace(title) && !title.Contains("LocString"))
                {
                    return title;
                }

                var rawTitle = encounter.Title?.GetRawText();
                if (!string.IsNullOrWhiteSpace(rawTitle))
                {
                    return rawTitle;
                }

                var encounterEntry = encounter.Id.Entry;
                if (!string.IsNullOrWhiteSpace(encounterEntry))
                {
                    return HumanizeModelIdEntry(encounterEntry);
                }
            }
        }
        catch { }

        try
        {
            var enemies = GetAliveEnemies(combat);
            if (enemies.Count == 0) return "Unknown Encounter";
            var names = enemies.Select(e =>
            {
                try { return SafeTitle(e.Monster?.Title) ?? e.Monster?.GetType().Name ?? "Enemy"; }
                catch { return "Enemy"; }
            }).Distinct().ToList();
            return string.Join(" & ", names);
        }
        catch { return "Unknown Encounter"; }
    }

    public static string GetCharacterId(Player player)
    {
        try
        {
            var characterId = NormalizeModelIdEntry(player.Character?.Id.Entry);
            if (characterId != "unknown") return characterId;

            var titleStr = SafeTitle(player.Character?.Title);
            if (!string.IsNullOrEmpty(titleStr)) return ToSnakeCase(titleStr);
            var typeName = player.GetType().BaseType?.Name ?? player.GetType().Name;
            if (typeName.Contains("Silent", StringComparison.OrdinalIgnoreCase)) return "silent";
            if (typeName.Contains("Defect", StringComparison.OrdinalIgnoreCase)) return "defect";
            if (typeName.Contains("Necrobinder", StringComparison.OrdinalIgnoreCase)) return "necrobinder";
            if (typeName.Contains("Regent", StringComparison.OrdinalIgnoreCase)) return "regent";
            return ToSnakeCase(typeName);
        }
        catch { return "unknown"; }
    }

    public static string GetCharacterName(Player player)
    {
        try
        {
            var formattedTitle = player.Character?.Title?.GetFormattedText();
            if (!string.IsNullOrWhiteSpace(formattedTitle) && !formattedTitle.Contains("LocString"))
                return formattedTitle;

            var titleStr = SafeTitle(player.Character?.Title);
            if (!string.IsNullOrEmpty(titleStr)) return titleStr;

            var characterId = player.Character?.Id.Entry;
            if (!string.IsNullOrWhiteSpace(characterId))
                return HumanizeModelIdEntry(characterId);

            var typeName = player.GetType().BaseType?.Name ?? player.GetType().Name;
            return typeName;
        }
        catch { return "Unknown"; }
    }

    // ─── Snapshot builder ───────────────────────────────────────────────────

    public static Dictionary<string, object?> BuildSnapshot(
        CombatState combat, Player player,
        int seq, int turnIndex, string phase,
        Dictionary<CardModel, string> cardIds,
        Dictionary<Creature, string> entityIds,
        string playerEntityId)
    {
        var pcs = player.PlayerCombatState;
        var creature = player.Creature;

        // Entities
        var entities = new List<Dictionary<string, object?>>();

        // Player entity
        if (entityIds.TryGetValue(creature, out var pId))
        {
            var snapshotOrbs = ReadOrbs(player)
                .Select(orb => (object?)new Dictionary<string, object?>
                {
                    ["orb_instance_id"] = $"snapshot-orb:{orb.SlotIndex:D3}",
                    ["orb_id"] = orb.OrbId,
                    ["orb_name"] = orb.Name,
                    ["owner_entity_id"] = pId,
                    ["slot_index"] = orb.SlotIndex,
                    ["passive"] = orb.Passive,
                    ["evoke"] = orb.Evoke,
                })
                .ToList();
            var snapshotRelics = ReadRelics(player)
                .Select((relic, index) => (object?)new Dictionary<string, object?>
                {
                    ["relic_instance_id"] = $"snapshot-relic:{index + 1:D3}",
                    ["relic_id"] = relic.RelicId,
                    ["relic_name"] = relic.Name,
                    ["owner_entity_id"] = pId,
                    ["stack_count"] = relic.StackCount,
                    ["status"] = relic.Status,
                    ["display_amount"] = relic.DisplayAmount,
                    ["is_used_up"] = relic.IsUsedUp,
                    ["is_wax"] = relic.IsWax,
                    ["is_melted"] = relic.IsMelted,
                })
                .ToList();
            entities.Add(new Dictionary<string, object?>
            {
                ["entity_id"] = pId,
                ["kind"] = "player",
                ["name"] = GetCharacterName(player),
                ["hp"] = creature.CurrentHp,
                ["max_hp"] = creature.MaxHp,
                ["block"] = creature.Block,
                ["energy"] = pcs?.Energy ?? 0,
                ["resources"] = new Dictionary<string, object?>
                {
                    ["stars"] = pcs?.Stars ?? 0,
                },
                ["orb_slots"] = GetOrbSlots(player),
                ["orbs"] = snapshotOrbs,
                ["relics"] = snapshotRelics,
                ["powers"] = new List<object?>(),
            });
        }

        // Enemy entities
        foreach (var enemy in GetAllEnemies(combat))
        {
            if (entityIds.TryGetValue(enemy, out var eId))
            {
                entities.Add(new Dictionary<string, object?>
                {
                    ["entity_id"] = eId,
                    ["kind"] = "enemy",
                    ["name"] = SafeTitle(enemy.Name) ?? enemy.Monster?.GetType().Name ?? "Enemy",
                    ["hp"] = enemy.CurrentHp,
                    ["max_hp"] = enemy.MaxHp,
                    ["block"] = enemy.Block,
                    ["powers"] = new List<object?>(),
                });
            }
        }

        // Cards in zones
        var zones = new Dictionary<string, List<string>>
        {
            ["draw"] = new(), ["hand"] = new(), ["discard"] = new(),
            ["exhaust"] = new(), ["play"] = new(),
        };
        var cards = new Dictionary<string, Dictionary<string, object?>>();

        if (pcs != null)
        {
            var allCards = ReadAllCards(pcs);
            foreach (var ci in allCards)
            {
                if (!cardIds.TryGetValue(ci.Model, out var cid)) continue;
                if (zones.ContainsKey(ci.Zone))
                    zones[ci.Zone].Add(cid);

                cards[cid] = new Dictionary<string, object?>
                {
                    ["card_def_id"] = ci.DefId,
                    ["card_name"] = ci.Name,
                    ["owner_entity_id"] = playerEntityId,
                    ["zone"] = ci.Zone,
                    ["cost"] = ci.Cost,
                };
            }
        }

        // Potions
        var potions = new Dictionary<string, Dictionary<string, object?>>();
        var potionList = ReadPotions(player);
        for (int i = 0; i < potionList.Count; i++)
        {
            var pid = $"potion:{i + 1:D3}";
            potions[pid] = new Dictionary<string, object?>
            {
                ["potion_def_id"] = potionList[i].DefId,
                ["potion_name"] = potionList[i].Name,
                ["slot_index"] = potionList[i].SlotIndex,
                ["state"] = "available",
            };
        }

        return new Dictionary<string, object?>
        {
            ["schema_name"] = "sts2-combat-log",
            ["schema_version"] = "0.1.0",
            ["seq"] = seq,
            ["turn_index"] = turnIndex,
            ["phase"] = phase,
            ["battle_state"] = new Dictionary<string, object?> { ["active_side"] = "player" },
            ["entities"] = entities,
            ["zones"] = zones.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value),
            ["cards"] = cards,
            ["potions"] = potions,
        };
    }

    // ─── Utilities ──────────────────────────────────────────────────────────

    /// <summary>Enumerate items from a card pile collection (may be IEnumerable or IList).</summary>
    private static IEnumerable<object> EnumerateItems(object? collection)
    {
        if (collection == null) yield break;
        if (collection is System.Collections.IEnumerable en)
        {
            foreach (var item in en)
                if (item != null) yield return item;
        }
    }

    private static string? SafeTitle(object? locStr)
    {
        if (locStr == null) return null;
        var s = locStr.ToString();
        if (string.IsNullOrEmpty(s) || s.Contains("LocString")) return null;
        return s;
    }

    private static string GetPowerId(PowerModel power)
    {
        var id = power.Id?.Entry;
        if (!string.IsNullOrEmpty(id))
        {
            return id;
        }

        var typeName = power.GetType().Name;
        if (typeName.EndsWith("Power", StringComparison.Ordinal))
        {
            typeName = typeName[..^"Power".Length];
        }

        return ToSnakeCase(typeName);
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

        var title = SafeTitle(power.Title);
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        var typeName = power.GetType().Name;
        return typeName.EndsWith("Power", StringComparison.Ordinal)
            ? typeName[..^"Power".Length]
            : typeName;
    }

    public static string GetOrbId(OrbModel orb)
    {
        var id = orb.Id?.Entry;
        if (!string.IsNullOrEmpty(id))
        {
            return id;
        }

        var typeName = orb.GetType().Name;
        return typeName.EndsWith("Orb", StringComparison.Ordinal)
            ? ToSnakeCase(typeName[..^"Orb".Length])
            : ToSnakeCase(typeName);
    }

    public static string GetOrbName(OrbModel orb)
    {
        try
        {
            var formatted = orb.Title?.GetFormattedText();
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                return formatted;
            }
        }
        catch
        {
            // Fallback below.
        }

        var title = SafeTitle(orb.Title);
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        var typeName = orb.GetType().Name;
        return typeName.EndsWith("Orb", StringComparison.Ordinal)
            ? typeName[..^"Orb".Length]
            : typeName;
    }

    public static string GetRelicId(RelicModel relic)
    {
        var id = relic.Id?.Entry;
        if (!string.IsNullOrEmpty(id))
        {
            return id;
        }

        var typeName = relic.GetType().Name;
        return typeName.EndsWith("Relic", StringComparison.Ordinal)
            ? ToSnakeCase(typeName[..^"Relic".Length])
            : ToSnakeCase(typeName);
    }

    public static string GetRelicName(RelicModel relic)
    {
        try
        {
            var formatted = relic.Title?.GetFormattedText();
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                return formatted;
            }
        }
        catch
        {
            // Fallback below.
        }

        var title = SafeTitle(relic.Title);
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        return relic.GetType().Name.EndsWith("Relic", StringComparison.Ordinal)
            ? relic.GetType().Name[..^"Relic".Length]
            : relic.GetType().Name;
    }

    public static string GetRelicStatus(RelicStatus status)
    {
        return status switch
        {
            RelicStatus.Normal => "normal",
            RelicStatus.Active => "active",
            RelicStatus.Disabled => "disabled",
            _ => ToSnakeCase(status.ToString()),
        };
    }

    public static string ToSnakeCase(string? input)
    {
        if (string.IsNullOrEmpty(input)) return "unknown";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsUpper(c) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static string NormalizeModelIdEntry(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return "unknown";
        }

        if (entry.All(c => char.IsUpper(c) || char.IsDigit(c) || c == '_'))
        {
            return entry.ToLowerInvariant();
        }

        if (entry.Any(char.IsUpper))
        {
            return ToSnakeCase(entry);
        }

        return entry;
    }

    private static string HumanizeModelIdEntry(string entry)
    {
        var normalized = NormalizeModelIdEntry(entry);
        if (normalized == "unknown")
        {
            return "Unknown Encounter";
        }

        return string.Join(" ",
            normalized
                .Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part.Substring(1)));
    }
}
