import type {
  BattleState,
  EntityState,
  CardInstanceState,
  IntentState,
  OrbState,
  PowerState,
  RelicState,
} from "../types/state";
import type {
  AttributionRef,
  CombatEvent,
  BlockChangedPayload,
  CardAfflictionPayload,
  CardEnchantmentPayload,
  CardKeywordValue,
  CardModifiedPayload,
  CardVisibleFlagsPayload,
  IntentChangedPayload,
  RelicModifiedPayload,
  PowerAppliedPayload,
  PowerRemovedPayload,
  PowerStacksChangedPayload,
  PotionDiscardedPayload,
  PotionUsedPayload,
  ResourceChangedPayload,
  TriggerFiredPayload,
} from "../types/events";
import type { ResolutionNode } from "../parser/resolutionTree";

// ─── formatEntity ────────────────────────────────────────────────────────────

export function formatEntity(entity: EntityState): string {
  const parts = [
    `  ${entity.side} ${entity.name ?? entity.entity_id}`,
    `HP: ${entity.current_hp}/${entity.max_hp}`,
    `Block: ${entity.block}`,
  ];
  if (entity.energy > 0) {
    parts.push(`Energy: ${entity.energy}`);
  }
  if (entity.resources && Object.keys(entity.resources).length > 0) {
    const resources = Object.entries(entity.resources)
      .map(([resourceId, amount]) => `${resourceId}=${amount}`)
      .join(", ");
    parts.push(`Resources: ${resources}`);
  }
  if (entity.intent) {
    parts.push(`Intent: ${formatIntent(entity.intent)}`);
  }
  if (entity.orb_slots !== undefined) {
    parts.push(`Orb Slots: ${entity.orb_slots}`);
  }
  if (entity.orbs && entity.orbs.length > 0) {
    parts.push(`Orbs: ${formatOrbs(entity.orbs)}`);
  }
  if (entity.relics && entity.relics.length > 0) {
    parts.push(`Relics: ${formatRelics(entity.relics)}`);
  }
  if (Object.keys(entity.powers).length > 0) {
    parts.push(`Powers: ${formatPowers(entity.powers)}`);
  }
  let line = parts.join(" ");
  if (!entity.alive) {
    line += " [DEAD]";
  }
  return line;
}

// ─── formatZone ──────────────────────────────────────────────────────────────

export function formatZone(
  zoneName: string,
  cardIds: string[],
  cards: Map<string, CardInstanceState>,
): string {
  const lines: string[] = [`  ${zoneName}:`];
  if (cardIds.length === 0) {
    lines.push("    (empty)");
  } else {
    for (let i = 0; i < cardIds.length; i++) {
      const id = cardIds[i];
      const card = cards.get(id);
      const name = card?.card_name ?? id;
      const defId = card?.card_def_id ?? "?";
      lines.push(`    [${i}] ${name} (${defId})`);
    }
  }
  return lines.join("\n");
}

// ─── formatState ─────────────────────────────────────────────────────────────

export function formatState(state: BattleState): string {
  const lines: string[] = [];

  // Header
  lines.push(
    `Battle: ${state.battle_id} | Turn: ${state.turn_index} | Phase: ${state.phase} | Active: ${state.active_side} | Last Seq: ${state.last_seq}${state.battle_result ? ` | Result: ${state.battle_result}` : ""}`,
  );
  lines.push("");

  // Entities
  lines.push("Entities:");
  for (const entity of state.entities.values()) {
    lines.push(formatEntity(entity));
  }
  lines.push("");

  // Zones
  for (const zoneName of ["draw", "hand", "discard", "play", "exhaust"]) {
    const cardIds = state.zones[zoneName] ?? [];
    lines.push(formatZone(zoneName, cardIds, state.cards));
  }
  lines.push("");

  // Potions
  lines.push("Potions:");
  const potions = Array.from(state.potions.values()).sort(
    (a, b) => a.slot_index - b.slot_index,
  );
  if (potions.length === 0) {
    lines.push("  (none)");
  } else {
    for (const potion of potions) {
      const occupied = potion.state === "discarded" ? "no" : "yes";
      lines.push(
        `  [${potion.slot_index}] ${potion.potion_name ?? potion.potion_def_id} - ${potion.state} occupied=${occupied}`,
      );
    }
  }

  return lines.join("\n");
}

// ─── formatEventSummary ─────────────────────────────────────────────────────

export function formatEventSummary(event: CombatEvent): string {
  const base = `seq ${event.seq} ${event.event_type}`;
  const meta: string[] = [];
  if (event.resolution_id) {
    meta.push(`res=${event.resolution_id}`);
  }
  if (event.parent_resolution_id) {
    meta.push(`parent=${event.parent_resolution_id}`);
  }
  if (event.cause_event_seq !== undefined) {
    meta.push(`cause=${event.cause_event_seq}`);
  }

  const suffix = meta.length > 0 ? ` [${meta.join(" ")}]` : "";
  switch (event.event_type) {
    case "battle_started": {
      const p = event.payload;
      return `${base} ${p.encounter_name ?? p.encounter_id}${suffix}`;
    }
    case "battle_ended": {
      const p = event.payload;
      return `${base} ${p.result}${suffix}`;
    }
    case "entity_spawned": {
      const p = event.payload;
      const trigger = formatTriggerMeta(p.trigger);
      return `${base} ${p.entity_id} ${p.name ?? ""} ${p.side}${p.reason ? ` ${p.reason}` : ""}${trigger}${suffix}`;
    }
    case "entity_died": {
      const p = event.payload;
      const trigger = formatTriggerMeta(p.trigger);
      return `${base} ${p.entity_id} ${p.reason ?? "unknown"}${trigger}${suffix}`;
    }
    case "entity_removed": {
      const p = event.payload;
      const trigger = formatTriggerMeta(p.trigger);
      return `${base} ${p.entity_id} ${p.reason ?? "unknown"}${trigger}${suffix}`;
    }
    case "entity_revived": {
      const p = event.payload;
      const hp = p.current_hp !== undefined ? ` hp=${p.current_hp}` : "";
      const trigger = formatTriggerMeta(p.trigger);
      return `${base} ${p.entity_id}${hp}${p.reason ? ` ${p.reason}` : ""}${trigger}${suffix}`;
    }
    case "card_created": {
      const p = event.payload;
      const trigger = formatTriggerMeta(p.trigger);
      return `${base} ${p.card_instance_id} ${p.card_name ?? p.card_def_id} ${p.initial_zone}${trigger}${suffix}`;
    }
    case "card_moved": {
      const p = event.payload;
      const trigger = formatTriggerMeta(p.trigger);
      return `${base} ${p.card_instance_id} ${p.from_zone} -> ${p.to_zone} ${p.reason}${trigger}${suffix}`;
    }
    case "card_exhausted": {
      const p = event.payload;
      const trigger = formatTriggerMeta(p.trigger);
      return `${base} ${p.card_instance_id} from=${p.from_zone ?? "?"}${trigger}${suffix}`;
    }
    case "card_play_started": {
      const p = event.payload;
      return `${base} ${p.card_instance_id} ${p.target_entity_ids.join(",")} ${p.source_kind}${suffix}`;
    }
    case "card_play_resolved": {
      const p = event.payload;
      return `${base} ${p.card_instance_id} ${p.final_zone ?? "?"}${suffix}`;
    }
    case "card_modified": {
      const p = event.payload as CardModifiedPayload;
      const changes: string[] = [];
      if (p.changes.cost !== undefined) {
        changes.push(`cost ${p.changes.cost.old}->${p.changes.cost.new}`);
      }
      if (p.changes.star_cost !== undefined) {
        changes.push(`star ${formatNullableNumber(p.changes.star_cost.old)}->${formatNullableNumber(p.changes.star_cost.new)}`);
      }
      if (p.changes.upgraded !== undefined) {
        changes.push(`upgraded ${p.changes.upgraded.old}->${p.changes.upgraded.new}`);
      }
      if (p.changes.upgrade_level !== undefined) {
        changes.push(`lvl ${p.changes.upgrade_level.old}->${p.changes.upgrade_level.new}`);
      }
      if (p.changes.replay_count !== undefined) {
        changes.push(`replay ${p.changes.replay_count.old}->${p.changes.replay_count.new}`);
      }
      if (p.changes.keywords !== undefined) {
        changes.push(
          `keywords ${formatCardKeywords(p.changes.keywords.old)}->${formatCardKeywords(p.changes.keywords.new)}`,
        );
      }
      if (p.changes.visible_flags !== undefined) {
        changes.push(
          `flags ${formatCardVisibleFlags(p.changes.visible_flags.old)}->${formatCardVisibleFlags(p.changes.visible_flags.new)}`,
        );
      }
      if (p.changes.enchantment !== undefined) {
        changes.push(
          `enchant ${formatCardEnchantment(p.changes.enchantment.old)}->${formatCardEnchantment(p.changes.enchantment.new)}`,
        );
      }
      if (p.changes.affliction !== undefined) {
        changes.push(
          `afflict ${formatCardAffliction(p.changes.affliction.old)}->${formatCardAffliction(p.changes.affliction.new)}`,
        );
      }
      if (p.changes.dynamic_values !== undefined) {
        changes.push(
          `vars ${formatDynamicValues(p.changes.dynamic_values.old)}->${formatDynamicValues(p.changes.dynamic_values.new)}`,
        );
      }
      const reason = p.reason ? ` ${p.reason}` : "";
      const trigger = formatTriggerMeta(p.trigger);
      return `${base} ${p.card_instance_id} ${changes.join(" ")}${reason}${trigger}${suffix}`;
    }
    case "relic_initialized": {
      const p = event.payload;
      return `${base} ${p.relic_instance_id} ${p.relic_name ?? p.relic_id} status=${p.status}${suffix}`;
    }
    case "relic_obtained": {
      const p = event.payload;
      const trigger = formatTriggerMeta(p.trigger);
      return `${base} ${p.relic_instance_id} ${p.relic_name ?? p.relic_id} status=${p.status}${trigger}${suffix}`;
    }
    case "relic_removed": {
      const p = event.payload;
      const trigger = formatTriggerMeta(p.trigger);
      return `${base} ${p.relic_instance_id} ${p.relic_name ?? p.relic_id} status=${p.status}${trigger}${suffix}`;
    }
    case "relic_triggered": {
      const p = event.payload;
      const trigger = formatTriggerMeta(p.trigger);
      const targets =
        p.target_entity_ids && p.target_entity_ids.length > 0
          ? ` targets=${p.target_entity_ids.join(",")}`
          : "";
      return `${base} ${p.relic_instance_id} ${p.relic_name ?? p.relic_id}${targets}${trigger}${suffix}`;
    }
    case "relic_modified": {
      const p = event.payload as RelicModifiedPayload;
      const trigger = formatTriggerMeta(p.trigger);
      let detail = p.change_kind;
      switch (p.change_kind) {
        case "display_amount":
          detail = `display ${p.old_display_amount ?? "null"}->${p.new_display_amount ?? "null"}`;
          break;
        case "status":
          detail = `status ${p.old_status}->${p.new_status}`;
          break;
        case "stack_count":
          detail = `stack ${p.old_stack_count}->${p.new_stack_count}`;
          break;
        case "flag":
          detail = `${p.flag} ${p.old_value}->${p.new_value}`;
          break;
      }
      return `${base} ${p.relic_instance_id} ${p.relic_name ?? p.relic_id} ${detail}${trigger}${suffix}`;
    }
    case "orb_inserted": {
      const p = event.payload;
      const trigger = formatTriggerMeta(p.trigger);
      return `${base} ${p.orb_instance_id} ${p.orb_name ?? p.orb_id} slot=${p.slot_index} ${p.reason}${trigger}${suffix}`;
    }
    case "orb_evoked": {
      const p = event.payload;
      const trigger = formatTriggerMeta(p.trigger);
      return `${base} ${p.orb_instance_id} ${p.orb_name ?? p.orb_id} dequeued=${p.dequeued ? "yes" : "no"}${trigger}${suffix}`;
    }
    case "orb_removed": {
      const p = event.payload;
      const trigger = formatTriggerMeta(p.trigger);
      return `${base} ${p.orb_instance_id} ${p.orb_name ?? p.orb_id} ${p.reason}${trigger}${suffix}`;
    }
    case "orb_passive_triggered": {
      const p = event.payload;
      const trigger = formatTriggerMeta(p.trigger);
      return `${base} ${p.orb_instance_id} ${p.orb_name ?? p.orb_id} ${p.timing}${trigger}${suffix}`;
    }
    case "orb_modified": {
      const p = event.payload;
      const trigger = formatTriggerMeta(p.trigger);
      const changes: string[] = [];
      if (p.changes.passive !== undefined) {
        changes.push(`passive ${p.changes.passive.old}->${p.changes.passive.new}`);
      }
      if (p.changes.evoke !== undefined) {
        changes.push(`evoke ${p.changes.evoke.old}->${p.changes.evoke.new}`);
      }
      return `${base} ${p.orb_instance_id} ${p.orb_name ?? p.orb_id} ${changes.join(" ")} ${p.reason}${trigger}${suffix}`;
    }
    case "orb_slots_changed": {
      const p = event.payload;
      const trigger = formatTriggerMeta(p.trigger);
      return `${base} ${p.entity_id} ${p.old_slots} -> ${p.new_slots} ${p.reason}${trigger}${suffix}`;
    }
    case "trigger_fired": {
      const p = event.payload as TriggerFiredPayload;
      return `${base} ${p.trigger_type} -> ${p.triggered_resolution_id ?? "event_only"} subject=${p.subject_card_instance_id ?? p.subject_entity_id ?? "?"}${suffix}`;
    }
    case "damage_dealt": {
      const p = event.payload;
      const source = formatSourceMeta(p);
      return `${base} ${p.amount} ${p.target_entity_id} ${p.damage_kind ?? "normal"}${source}${suffix}`;
    }
    case "hp_changed": {
      const p = event.payload;
      return `${base} ${p.entity_id} ${p.old} -> ${p.new}${suffix}`;
    }
    case "block_changed": {
      const p = event.payload as BlockChangedPayload;
      const trigger = formatTriggerMeta(p.trigger);
      const source = trigger ? "" : formatSourceMeta(p);
      const reason = p.reason ? ` ${p.reason}` : "";
      const chain = formatBlockGainMeta(p);
      return `${base} ${p.entity_id} ${p.old} -> ${p.new}${reason}${trigger}${source}${chain}${suffix}`;
    }
    case "block_broken": {
      const p = event.payload;
      const trigger = formatTriggerMeta(p.trigger);
      return `${base} ${p.entity_id} ${p.old_block} -> ${p.new_block}${trigger}${suffix}`;
    }
    case "block_cleared": {
      const p = event.payload;
      const trigger = formatTriggerMeta(p.trigger);
      return `${base} ${p.entity_id} ${p.old_block} -> ${p.new_block}${trigger}${suffix}`;
    }
    case "block_clear_prevented": {
      const p = event.payload;
      const preventer = formatTriggerMeta(p.preventer);
      return `${base} ${p.entity_id} retained=${p.retained_block}${preventer}${suffix}`;
    }
    case "energy_changed": {
      const p = event.payload;
      const trigger = formatTriggerMeta(p.trigger);
      return `${base} ${p.entity_id} ${p.old} -> ${p.new}${trigger}${suffix}`;
    }
    case "resource_changed": {
      const p = event.payload as ResourceChangedPayload;
      const trigger = formatTriggerMeta(p.trigger);
      return `${base} ${p.entity_id} ${p.resource_id} ${p.old} -> ${p.new}${trigger}${suffix}`;
    }
    case "power_applied": {
      const p = event.payload as PowerAppliedPayload;
      const attribution = formatPowerTruthMeta(p.applier, p.trigger);
      return `${base} ${p.target_entity_id} ${p.power_name ?? p.power_id} ${p.stacks}${attribution}${suffix}`;
    }
    case "power_removed": {
      const p = event.payload as PowerRemovedPayload;
      const attribution = formatPowerTruthMeta(p.applier, p.trigger);
      const stacks = p.stacks !== undefined ? ` last=${p.stacks}` : "";
      return `${base} ${p.target_entity_id} ${p.power_name ?? p.power_id}${stacks}${attribution}${suffix}`;
    }
    case "power_stacks_changed": {
      const p = event.payload as PowerStacksChangedPayload;
      const attribution = formatPowerTruthMeta(p.applier, p.trigger);
      return `${base} ${p.target_entity_id} ${p.power_name ?? p.power_id} ${p.old_stacks} -> ${p.new_stacks} delta=${p.delta}${attribution}${suffix}`;
    }
    case "intent_changed": {
      const p = event.payload as IntentChangedPayload;
      const projectedDamage = normalizeOptionalNumber(p.projected_damage);
      const projectedHits = normalizeOptionalNumber(p.projected_hits);
      const projected =
        projectedDamage !== undefined
          ? ` ${projectedDamage}${projectedHits !== undefined && projectedHits > 1 ? `x${projectedHits}` : ""}`
          : "";
      return `${base} ${p.entity_id} ${p.intent_name ?? p.intent_id}${projected}${suffix}`;
    }
    case "turn_started": {
      const p = event.payload;
      return `${base} ${p.turn_index} ${p.active_side}${suffix}`;
    }
    case "potion_initialized": {
      const p = event.payload;
      return `${base} ${p.potion_instance_id} ${p.potion_name ?? p.potion_def_id}${suffix}`;
    }
    case "potion_created": {
      const p = event.payload;
      return `${base} ${p.potion_instance_id} ${p.potion_name ?? p.potion_def_id} slot=${p.slot_index}${suffix}`;
    }
    case "potion_used": {
      const p = event.payload as PotionUsedPayload;
      return `${base} ${p.potion_instance_id} slot=${p.slot_index} target_mode=${p.target_mode ?? "?"} targets=${p.target_entity_ids.join(",")}${suffix}`;
    }
    case "potion_discarded": {
      const p = event.payload as PotionDiscardedPayload;
      return `${base} ${p.potion_instance_id} slot=${p.slot_index}${suffix}`;
    }
    default:
      return `${base}${suffix}`;
  }
}

// ─── formatEventLog ──────────────────────────────────────────────────────────

export function formatEventLog(events: CombatEvent[]): string {
  return events.map(formatEventSummary).join("\n");
}

function formatSourceMeta(payload: {
  source_entity_id?: string;
  source_kind?: string;
}): string {
  const parts: string[] = [];
  if (payload.source_entity_id) {
    parts.push(`src=${payload.source_entity_id}`);
  }
  if (payload.source_kind) {
    parts.push(`kind=${payload.source_kind}`);
  }
  return parts.length > 0 ? ` ${parts.join(" ")}` : "";
}

function formatPowerTruthMeta(
  applier?: AttributionRef,
  trigger?: AttributionRef,
): string {
  const parts: string[] = [];
  const applierLabel = formatAttributionRef(applier);
  const triggerLabel = formatAttributionRef(trigger);

  if (applierLabel) {
    parts.push(`applier=${applierLabel}`);
  }

  if (triggerLabel) {
    parts.push(`trigger=${triggerLabel}`);
  }

  return parts.length > 0 ? ` ${parts.join(" ")}` : "";
}

function formatTriggerMeta(trigger?: AttributionRef): string {
  const triggerLabel = formatAttributionRef(trigger);
  return triggerLabel ? ` trigger=${triggerLabel}` : "";
}

function formatNullableNumber(value: number | null): string {
  return value === null ? "null" : String(value);
}

function formatCardKeywords(keywords: CardKeywordValue[]): string {
  return `[${keywords.join(",")}]`;
}

function formatCardVisibleFlags(flags: CardVisibleFlagsPayload): string {
  return `retain=${Boolean(flags.retain_this_turn)},sly=${Boolean(flags.sly_this_turn)}`;
}

function formatCardEnchantment(
  enchantment: CardEnchantmentPayload | null,
): string {
  if (!enchantment) {
    return "none";
  }

  const label = enchantment.name ?? enchantment.enchantment_id;
  const parts: string[] = [];
  if (enchantment.amount !== undefined) {
    parts.push(String(enchantment.amount));
  }
  if (enchantment.status !== undefined) {
    parts.push(enchantment.status);
  }

  return parts.length > 0 ? `${label}(${parts.join(",")})` : label;
}

function formatCardAffliction(
  affliction: CardAfflictionPayload | null,
): string {
  if (!affliction) {
    return "none";
  }

  const label = affliction.name ?? affliction.affliction_id;
  if (affliction.amount === undefined) {
    return label;
  }

  return `${label}(${affliction.amount})`;
}

function formatDynamicValues(values: Record<string, number>): string {
  return Object.entries(values)
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([key, value]) => `${key}:${value}`)
    .join(",");
}

function formatBlockGainMeta(payload: BlockChangedPayload): string {
  if (payload.reason !== "block_gain") {
    return "";
  }

  const baseAmount = normalizeOptionalNumber(payload.base_amount);
  const finalGainAmount = normalizeOptionalNumber(
    payload.final_gain_amount ?? payload.delta,
  );
  const modifierLabels =
    payload.steps
      ?.filter((step) => step.stage !== "base" && step.stage !== "settled")
      .map((step) => formatAttributionRef(step.modifier_ref) ?? step.stage)
      .filter((label) => label.length > 0) ?? [];

  const parts: string[] = [];
  if (baseAmount !== undefined && finalGainAmount !== undefined) {
    parts.push(`gain=${baseAmount}->${finalGainAmount}`);
  }
  if (modifierLabels.length > 0) {
    parts.push(`mods=${modifierLabels.join(">")}`);
  }

  return parts.length > 0 ? ` ${parts.join(" ")}` : "";
}

function formatAttributionRef(ref?: AttributionRef): string | undefined {
  if (!ref) {
    return undefined;
  }

  switch (ref.kind) {
    case "entity":
      return ref.entity_id ?? ref.ref;
    case "card_instance":
      return ref.card_instance_id ?? ref.ref;
    case "potion_instance":
      return ref.potion_instance_id ?? ref.ref;
    case "enemy_move":
      return ref.move_id ?? ref.ref;
    case "orb_instance":
      return ref.orb_id ?? ref.orb_instance_id ?? ref.ref;
    case "power_instance":
      return ref.power_id ?? ref.power_instance_id ?? ref.ref;
    case "relic":
      return ref.relic_id ?? ref.relic_instance_id ?? ref.ref;
    case "generic_model":
      return ref.model_id ?? ref.model_type ?? ref.ref;
    case "unknown":
      return ref.unknown_reason ? `unknown(${ref.unknown_reason})` : "unknown";
    default:
      return ref.ref;
  }
}

function formatIntent(intent: IntentState): string {
  const label = intent.intent_name ?? intent.intent_id;
  const projectedDamage = normalizeOptionalNumber(intent.projected_damage);
  const projectedHits = normalizeOptionalNumber(intent.projected_hits);
  if (projectedDamage === undefined) {
    return label;
  }

  if (projectedHits !== undefined && projectedHits > 1) {
    return `${label} ${projectedDamage}x${projectedHits}`;
  }

  return `${label} ${projectedDamage}`;
}

function formatPowers(powers: Record<string, PowerState>): string {
  return Object.values(powers)
    .sort((left, right) => {
      const leftLabel = left.power_name ?? left.power_id;
      const rightLabel = right.power_name ?? right.power_id;
      return leftLabel.localeCompare(rightLabel);
    })
    .map((power) => `${power.power_name ?? power.power_id} ${power.stacks}`)
    .join(", ");
}

function formatRelics(relics: RelicState[]): string {
  return relics
    .map((relic) => {
      const details: string[] = [];
      if (relic.display_amount !== undefined) {
        details.push(`display=${relic.display_amount}`);
      }
      if (relic.stack_count !== 1) {
        details.push(`stack=${relic.stack_count}`);
      }
      details.push(`status=${relic.status}`);
      if (relic.is_used_up) {
        details.push("used_up");
      }
      if (relic.is_wax) {
        details.push("wax");
      }
      if (relic.is_melted) {
        details.push("melted");
      }
      return `${relic.relic_name ?? relic.relic_id} (${details.join(",")})`;
    })
    .join("; ");
}

function formatOrbs(orbs: OrbState[]): string {
  return [...orbs]
    .sort((left, right) => left.slot_index - right.slot_index)
    .map((orb) => {
      const stats: string[] = [];
      if (orb.passive !== undefined) {
        stats.push(`P=${orb.passive}`);
      }
      if (orb.evoke !== undefined) {
        stats.push(`E=${orb.evoke}`);
      }

      return `[${orb.slot_index}] ${orb.orb_name ?? orb.orb_id}${stats.length > 0 ? ` (${stats.join(" ")})` : ""}`;
    })
    .join(", ");
}

function normalizeOptionalNumber(value: unknown): number | undefined {
  return typeof value === "number" ? value : undefined;
}

function findLeadSource(node: ResolutionNode): { kind: "card" | "potion" | "enemy" | "orb" | "relic"; id: string } | undefined {
  for (const event of node.events) {
    if (event.event_type === "card_play_started" || event.event_type === "card_play_resolved") {
      const payload = event.payload as { card_instance_id?: string };
      if (payload.card_instance_id) {
        return { kind: "card", id: payload.card_instance_id };
      }
    }
    if (event.event_type === "potion_used" || event.event_type === "potion_discarded") {
      const payload = event.payload as { potion_instance_id?: string };
      if (payload.potion_instance_id) {
        return { kind: "potion", id: payload.potion_instance_id };
      }
    }
    if (event.event_type === "damage_dealt" || event.event_type === "block_changed") {
      const payload = event.payload as { source_entity_id?: string; source_kind?: string; trigger?: AttributionRef };
      if (payload.trigger?.kind === "enemy_move" && payload.trigger.owner_entity_id) {
        return { kind: "enemy", id: payload.trigger.owner_entity_id };
      }
      if (payload.trigger?.kind === "orb_instance" && payload.trigger.orb_instance_id) {
        return { kind: "orb", id: payload.trigger.orb_instance_id };
      }
      if (payload.trigger?.kind === "relic" && (payload.trigger.relic_instance_id || payload.trigger.relic_id)) {
        return { kind: "relic", id: payload.trigger.relic_instance_id ?? payload.trigger.relic_id! };
      }
      if (payload.source_kind === "enemy_action" && payload.source_entity_id) {
        return { kind: "enemy", id: payload.source_entity_id };
      }
    }
    if (
      (event.event_type === "relic_initialized" ||
        event.event_type === "relic_obtained" ||
        event.event_type === "relic_removed" ||
        event.event_type === "relic_triggered" ||
        event.event_type === "relic_modified") &&
      "relic_instance_id" in event.payload &&
      typeof event.payload.relic_instance_id === "string"
    ) {
      return { kind: "relic", id: event.payload.relic_instance_id };
    }
    if (
      (event.event_type === "orb_inserted" ||
        event.event_type === "orb_evoked" ||
        event.event_type === "orb_removed" ||
        event.event_type === "orb_modified" ||
        event.event_type === "orb_passive_triggered") &&
      "orb_instance_id" in event.payload &&
      typeof event.payload.orb_instance_id === "string"
    ) {
      return { kind: "orb", id: event.payload.orb_instance_id };
    }
  }
  if (node.trigger?.subject_card_instance_id) {
    return { kind: "card", id: node.trigger.subject_card_instance_id };
  }
  return undefined;
}

function formatResolutionNode(node: ResolutionNode, depth: number): string[] {
  const indent = "  ".repeat(depth);
  const leadSource = findLeadSource(node);
  const firstEvent = node.events[0]?.event_type ?? "unknown";
  const lines = [
    `${indent}${node.resolution_id} ${firstEvent}${leadSource ? ` ${leadSource.kind}=${leadSource.id}` : ""}`,
  ];

  if (node.trigger) {
    lines.push(
      `${indent}  trigger seq=${node.trigger.event_seq} type=${node.trigger.trigger_type} cause=${node.trigger.cause_event_seq ?? "?"} subject=${node.trigger.subject_card_instance_id ?? node.trigger.subject_entity_id ?? "?"}`,
    );
  }

  for (const child of node.children) {
    lines.push(...formatResolutionNode(child, depth + 1));
  }

  return lines;
}

export function formatResolutionTree(nodes: ResolutionNode[]): string {
  if (nodes.length === 0) {
    return "(no resolutions)";
  }

  return nodes.flatMap((node) => formatResolutionNode(node, 0)).join("\n");
}
