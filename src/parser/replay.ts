import type {
  CombatEvent,
  BattleStartedPayload,
  BattleEndedPayload,
  TurnStartedPayload,
  EntityDiedPayload,
  EntityRemovedPayload,
  EntityRevivedPayload,
  EntitySpawnedPayload,
  CardCreatedPayload,
  CardModifiedPayload,
  CardAfflictionPayload,
  CardEnchantmentPayload,
  CardKeywordValue,
  CardVisibleFlagsPayload,
  RelicInitializedPayload,
  RelicModifiedPayload,
  RelicObtainedPayload,
  RelicRemovedPayload,
  PotionCreatedPayload,
  PotionDiscardedPayload,
  PotionInitializedPayload,
  PotionUsedPayload,
  CardMovedPayload,
  HpChangedPayload,
  BlockChangedPayload,
  EnergyChangedPayload,
  ResourceChangedPayload,
  PowerAppliedPayload,
  PowerRemovedPayload,
  PowerStacksChangedPayload,
  IntentChangedPayload,
} from "../types/events";
import type {
  BattleState,
  EntityState,
  CardInstanceState,
  OrbState,
  PotionInstanceState,
  PowerState,
  RelicState,
} from "../types/state";
import { createInitialState } from "../types/state";
import type { Snapshot } from "../types/snapshot";

function normalizeOptionalNumber(value: unknown): number | undefined {
  return typeof value === "number" ? value : undefined;
}

function cloneCardKeywords(
  keywords: CardKeywordValue[] | undefined,
): CardKeywordValue[] | undefined {
  return keywords ? [...keywords] : undefined;
}

function cloneCardVisibleFlags(
  flags: CardVisibleFlagsPayload | undefined,
): CardVisibleFlagsPayload | undefined {
  if (!flags) {
    return undefined;
  }

  return {
    retain_this_turn: Boolean(flags.retain_this_turn),
    sly_this_turn: Boolean(flags.sly_this_turn),
  };
}

function cloneCardEnchantment(
  enchantment: CardEnchantmentPayload | undefined | null,
): CardEnchantmentPayload | undefined {
  if (!enchantment) {
    return undefined;
  }

  return { ...enchantment };
}

function cloneCardAffliction(
  affliction: CardAfflictionPayload | undefined | null,
): CardAfflictionPayload | undefined {
  if (!affliction) {
    return undefined;
  }

  return { ...affliction };
}

function cloneCardDynamicValues(
  values: Record<string, number> | undefined,
): Record<string, number> | undefined {
  return values ? { ...values } : undefined;
}

function cloneZones(zones: Snapshot["zones"]): BattleState["zones"] {
  return Object.fromEntries(
    Object.entries(zones).map(([zoneName, cardIds]) => [zoneName, [...cardIds]]),
  );
}

function ensureZone(state: BattleState, zoneName: string): string[] {
  const existing = state.zones[zoneName];
  if (existing) {
    return existing;
  }

  const next: string[] = [];
  state.zones[zoneName] = next;
  return next;
}

function removeCardFromZone(
  zone: string[],
  cardInstanceId: string,
  index?: number,
): void {
  if (index !== undefined && zone[index] === cardInstanceId) {
    zone.splice(index, 1);
    return;
  }

  const actualIndex = zone.indexOf(cardInstanceId);
  if (actualIndex !== -1) {
    zone.splice(actualIndex, 1);
  }
}

function insertCardIntoZone(
  zone: string[],
  cardInstanceId: string,
  index?: number,
): void {
  if (
    index !== undefined &&
    index >= 0 &&
    index <= zone.length
  ) {
    zone.splice(index, 0, cardInstanceId);
    return;
  }

  zone.push(cardInstanceId);
}

function cloneOrbs(orbs: { [key: string]: unknown }[] | undefined): OrbState[] | undefined {
  return orbs?.map((orb) => ({
    orb_instance_id: orb.orb_instance_id as string,
    orb_id: orb.orb_id as string,
    owner_entity_id: orb.owner_entity_id as string,
    slot_index: orb.slot_index as number,
    orb_name: orb.orb_name as string | undefined,
    passive: normalizeOptionalNumber(orb.passive),
    evoke: normalizeOptionalNumber(orb.evoke),
  }));
}

function ensureOrbQueue(entity: EntityState): OrbState[] {
  if (!entity.orbs) {
    entity.orbs = [];
  }

  return entity.orbs;
}

function cloneRelics(relics: { [key: string]: unknown }[] | undefined): RelicState[] | undefined {
  return relics?.map((relic) => ({
    relic_instance_id: relic.relic_instance_id as string,
    relic_id: relic.relic_id as string,
    owner_entity_id: relic.owner_entity_id as string,
    relic_name: relic.relic_name as string | undefined,
    stack_count: relic.stack_count as number,
    status: relic.status as RelicState["status"],
    display_amount: normalizeOptionalNumber(relic.display_amount),
    is_used_up: Boolean(relic.is_used_up),
    is_wax: Boolean(relic.is_wax),
    is_melted: Boolean(relic.is_melted),
  }));
}

function ensureRelicInventory(entity: EntityState): RelicState[] {
  if (!entity.relics) {
    entity.relics = [];
  }

  return entity.relics;
}

function upsertRelicState(entity: EntityState, relic: RelicState): void {
  const relics = ensureRelicInventory(entity);
  const existingIndex = relics.findIndex(
    (existing) => existing.relic_instance_id === relic.relic_instance_id,
  );
  if (existingIndex !== -1) {
    relics.splice(existingIndex, 1, { ...relic });
    return;
  }

  relics.push({ ...relic });
  relics.sort((left, right) => left.relic_instance_id.localeCompare(right.relic_instance_id));
}

function removeRelicState(entity: EntityState, relicInstanceId: string): void {
  const relics = entity.relics;
  if (!relics) {
    return;
  }

  const existingIndex = relics.findIndex(
    (relic) => relic.relic_instance_id === relicInstanceId,
  );
  if (existingIndex === -1) {
    return;
  }

  relics.splice(existingIndex, 1);
}

function normalizeOrbQueue(orbs: OrbState[]): void {
  orbs.sort((left, right) => left.slot_index - right.slot_index);
  for (let index = 0; index < orbs.length; index++) {
    orbs[index].slot_index = index;
  }
}

function upsertOrbState(entity: EntityState, orb: OrbState): void {
  const queue = ensureOrbQueue(entity);
  const existingIndex = queue.findIndex((existing) => existing.orb_instance_id === orb.orb_instance_id);
  if (existingIndex !== -1) {
    queue.splice(existingIndex, 1);
  }

  const insertIndex =
    orb.slot_index >= 0 && orb.slot_index <= queue.length
      ? orb.slot_index
      : queue.length;
  queue.splice(insertIndex, 0, { ...orb });
  normalizeOrbQueue(queue);
}

function removeOrbState(entity: EntityState, orbInstanceId: string): void {
  const queue = entity.orbs;
  if (!queue) {
    return;
  }

  const existingIndex = queue.findIndex((orb) => orb.orb_instance_id === orbInstanceId);
  if (existingIndex === -1) {
    return;
  }

  queue.splice(existingIndex, 1);
  normalizeOrbQueue(queue);
}

// ─── Hydrate from snapshot ────────────────────────────────────────────────

export function hydrateFromSnapshot(
  snapshot: Snapshot,
  battleId = "",
): BattleState {
  const entities = new Map<string, EntityState>();
  for (const se of snapshot.entities) {
    const powers: Record<string, PowerState> = {};
    for (const p of se.powers) {
      powers[p.power_id] = {
        power_id: p.power_id,
        power_name: p.power_name,
        stacks: p.stacks,
      };
    }
    entities.set(se.entity_id, {
      entity_id: se.entity_id,
      name: se.name,
      side: se.kind,
      current_hp: se.hp,
      max_hp: se.max_hp,
      block: se.block,
      energy: se.energy ?? 0,
      resources: { ...(se.resources ?? {}) },
      orb_slots: normalizeOptionalNumber(se.orb_slots),
      orbs: cloneOrbs(se.orbs as { [key: string]: unknown }[] | undefined),
      relics: cloneRelics(se.relics as { [key: string]: unknown }[] | undefined),
      alive: se.hp > 0,
      powers,
      intent: se.intent
        ? {
            intent_id: se.intent.intent_id,
            intent_name: se.intent.intent_name,
            projected_damage: normalizeOptionalNumber(se.intent.projected_damage),
            projected_hits: normalizeOptionalNumber(se.intent.projected_hits),
          }
        : undefined,
    });
  }

  const cards = new Map<string, CardInstanceState>();
  for (const [id, sc] of Object.entries(snapshot.cards)) {
    cards.set(id, {
      card_instance_id: id,
      card_def_id: sc.card_def_id,
      card_name: sc.card_name,
      owner_entity_id: sc.owner_entity_id,
      zone: sc.zone,
      cost: sc.cost,
      star_cost: sc.star_cost,
      replay_count: sc.replay_count,
      keywords: cloneCardKeywords(sc.keywords),
      visible_flags: cloneCardVisibleFlags(sc.visible_flags),
      enchantment: cloneCardEnchantment(sc.enchantment),
      affliction: cloneCardAffliction(sc.affliction),
      dynamic_values: cloneCardDynamicValues(sc.dynamic_values),
      current_upgrade_level: sc.current_upgrade_level,
      created_this_combat: false,
      temporary: false,
    });
  }

  const potions = new Map<string, PotionInstanceState>();
  for (const [id, sp] of Object.entries(snapshot.potions)) {
    potions.set(id, {
      potion_instance_id: id,
      potion_def_id: sp.potion_def_id,
      potion_name: sp.potion_name,
      slot_index: sp.slot_index,
      state: sp.state as PotionInstanceState["state"],
      origin: undefined,
    });
  }

  return {
    battle_id: battleId,
    turn_index: snapshot.turn_index,
    phase: snapshot.phase,
    active_side: snapshot.battle_state.active_side,
    battle_result: snapshot.battle_state.result ?? null,
    ended: snapshot.phase === "battle_end" || snapshot.battle_state.result !== undefined,
    entities,
    cards,
    potions,
    zones: cloneZones(snapshot.zones),
    events: [],
    last_seq: snapshot.seq,
  };
}

// ─── Apply single event (mutates state in place) ──────────────────────────

export function applyEvent(state: BattleState, event: CombatEvent): void {
  switch (event.event_type) {
    case "battle_started": {
      const p = event.payload as BattleStartedPayload;
      state.turn_index = event.turn_index ?? 0;
      state.phase = event.phase ?? "combat";
      state.active_side = "player";
      state.battle_result = null;
      state.ended = false;
      void p;
      break;
    }

    case "battle_ended": {
      const p = event.payload as BattleEndedPayload;
      state.phase = event.phase ?? "battle_end";
      state.battle_result = p.result;
      state.ended = true;
      break;
    }

    case "turn_started": {
      const p = event.payload as TurnStartedPayload;
      state.turn_index = p.turn_index;
      state.active_side = p.active_side;
      state.phase = p.phase ?? state.phase;
      break;
    }

    case "entity_spawned": {
      const p = event.payload as EntitySpawnedPayload;
      state.entities.set(p.entity_id, {
        entity_id: p.entity_id,
        entity_def_id: p.entity_def_id,
        name: p.name,
        side: p.side,
        current_hp: p.current_hp ?? 0,
        max_hp: p.max_hp ?? 0,
        block: p.block ?? 0,
        energy: p.energy ?? 0,
        resources: { ...(p.resources ?? {}) },
        orb_slots: normalizeOptionalNumber(p.orb_slots),
        orbs: cloneOrbs((p.orbs as { [key: string]: unknown }[] | undefined) ?? []),
        relics: undefined,
        alive: true,
        powers: {},
        intent: undefined,
      });
      break;
    }

    case "entity_died": {
      const p = event.payload as EntityDiedPayload;
      const entity = state.entities.get(p.entity_id);
      if (entity) {
        entity.alive = false;
        entity.intent = undefined;
      }
      break;
    }

    case "entity_removed": {
      const p = event.payload as EntityRemovedPayload;
      state.entities.delete(p.entity_id);
      break;
    }

    case "entity_revived": {
      const p = event.payload as EntityRevivedPayload;
      const entity = state.entities.get(p.entity_id);
      if (entity) {
        entity.alive = true;
        if (p.current_hp !== undefined) {
          entity.current_hp = p.current_hp;
        }
      }
      break;
    }

    case "card_created": {
      const p = event.payload as CardCreatedPayload;
      state.cards.set(p.card_instance_id, {
        card_instance_id: p.card_instance_id,
        card_def_id: p.card_def_id,
        card_name: p.card_name,
        owner_entity_id: p.owner_entity_id,
        zone: p.initial_zone,
        cost: p.cost,
        star_cost: p.star_cost,
        replay_count: p.replay_count,
        keywords: cloneCardKeywords(p.keywords),
        visible_flags: cloneCardVisibleFlags(p.visible_flags),
        enchantment: cloneCardEnchantment(p.enchantment),
        affliction: cloneCardAffliction(p.affliction),
        dynamic_values: cloneCardDynamicValues(p.dynamic_values),
        current_upgrade_level: p.current_upgrade_level,
        created_this_combat: p.created_this_combat ?? false,
        temporary: p.temporary ?? false,
      });
      ensureZone(state, p.initial_zone).push(p.card_instance_id);
      break;
    }

    case "potion_initialized": {
      const p = event.payload as PotionInitializedPayload;
      state.potions.set(p.potion_instance_id, {
        potion_instance_id: p.potion_instance_id,
        potion_def_id: p.potion_def_id,
        potion_name: p.potion_name,
        slot_index: p.slot_index,
        origin: p.origin,
        state: "available",
      });
      break;
    }

    case "potion_created": {
      const p = event.payload as PotionCreatedPayload;
      state.potions.set(p.potion_instance_id, {
        potion_instance_id: p.potion_instance_id,
        potion_def_id: p.potion_def_id,
        potion_name: p.potion_name,
        slot_index: p.slot_index,
        origin: p.origin,
        state: "available",
      });
      break;
    }

    case "potion_used": {
      const p = event.payload as PotionUsedPayload;
      const existing = state.potions.get(p.potion_instance_id);
      state.potions.set(p.potion_instance_id, {
        potion_instance_id: p.potion_instance_id,
        potion_def_id: p.potion_def_id ?? existing?.potion_def_id ?? "unknown",
        potion_name: p.potion_name ?? existing?.potion_name,
        slot_index: p.slot_index,
        origin: existing?.origin,
        state: "used",
      });
      break;
    }

    case "potion_discarded": {
      const p = event.payload as PotionDiscardedPayload;
      const existing = state.potions.get(p.potion_instance_id);
      state.potions.set(p.potion_instance_id, {
        potion_instance_id: p.potion_instance_id,
        potion_def_id: p.potion_def_id ?? existing?.potion_def_id ?? "unknown",
        potion_name: p.potion_name ?? existing?.potion_name,
        slot_index: p.slot_index,
        origin: existing?.origin,
        state: "discarded",
      });
      break;
    }

    case "card_moved": {
      const p = event.payload as CardMovedPayload;
      const from = ensureZone(state, p.from_zone);
      removeCardFromZone(from, p.card_instance_id, p.from_index);

      const to = ensureZone(state, p.to_zone);
      insertCardIntoZone(to, p.card_instance_id, p.to_index);

      const card = state.cards.get(p.card_instance_id);
      if (card) card.zone = p.to_zone;
      break;
    }

    case "card_exhausted": {
      break;
    }

    case "card_play_started": {
      state.phase = "player_action";
      break;
    }

    case "card_play_resolved": {
      break;
    }

    case "card_modified": {
      const p = event.payload as CardModifiedPayload;
      const card = state.cards.get(p.card_instance_id);
      if (card) {
        if (p.changes.cost !== undefined) {
          card.cost = p.changes.cost.new;
        }
        if (p.changes.star_cost !== undefined) {
          card.star_cost = p.changes.star_cost.new ?? undefined;
        }
        if (p.changes.upgrade_level !== undefined) {
          card.current_upgrade_level = p.changes.upgrade_level.new;
        } else if (p.current_upgrade_level !== undefined) {
          card.current_upgrade_level = p.current_upgrade_level;
        }
        if (p.changes.replay_count !== undefined) {
          card.replay_count = p.changes.replay_count.new;
        }
        if (p.changes.keywords !== undefined) {
          card.keywords = cloneCardKeywords(p.changes.keywords.new);
        }
        if (p.changes.visible_flags !== undefined) {
          card.visible_flags = cloneCardVisibleFlags(p.changes.visible_flags.new);
        }
        if (p.changes.enchantment !== undefined) {
          card.enchantment = cloneCardEnchantment(p.changes.enchantment.new);
        }
        if (p.changes.affliction !== undefined) {
          card.affliction = cloneCardAffliction(p.changes.affliction.new);
        }
        if (p.changes.dynamic_values !== undefined) {
          card.dynamic_values = cloneCardDynamicValues(p.changes.dynamic_values.new);
        }
        if (p.card_name !== undefined) {
          card.card_name = p.card_name;
        }
      }
      break;
    }

    case "relic_initialized": {
      const p = event.payload as RelicInitializedPayload;
      const entity = state.entities.get(p.owner_entity_id);
      if (entity) {
        upsertRelicState(entity, { ...p });
      }
      break;
    }

    case "relic_obtained": {
      const p = event.payload as RelicObtainedPayload;
      const entity = state.entities.get(p.owner_entity_id);
      if (entity) {
        upsertRelicState(entity, { ...p });
      }
      break;
    }

    case "relic_removed": {
      const p = event.payload as RelicRemovedPayload;
      const entity = state.entities.get(p.owner_entity_id);
      if (entity) {
        removeRelicState(entity, p.relic_instance_id);
      }
      break;
    }

    case "relic_triggered": {
      break;
    }

    case "relic_modified": {
      const p = event.payload as RelicModifiedPayload;
      const entity = state.entities.get(p.owner_entity_id);
      if (!entity) {
        break;
      }

      const relics = ensureRelicInventory(entity);
      const existing = relics.find((relic) => relic.relic_instance_id === p.relic_instance_id);
      const baseState: RelicState = existing
        ? { ...existing }
        : {
            relic_instance_id: p.relic_instance_id,
            relic_id: p.relic_id,
            owner_entity_id: p.owner_entity_id,
            relic_name: p.relic_name,
            stack_count: p.stack_count,
            status: p.status,
            display_amount: p.display_amount,
            is_used_up: p.is_used_up,
            is_wax: p.is_wax,
            is_melted: p.is_melted,
          };

      baseState.relic_id = p.relic_id;
      baseState.owner_entity_id = p.owner_entity_id;
      baseState.relic_name = p.relic_name;
      baseState.stack_count = p.stack_count;
      baseState.status = p.status;
      baseState.display_amount = p.display_amount;
      baseState.is_used_up = p.is_used_up;
      baseState.is_wax = p.is_wax;
      baseState.is_melted = p.is_melted;

      upsertRelicState(entity, baseState);
      break;
    }

    case "orb_inserted": {
      const p = event.payload;
      const entity = state.entities.get(p.owner_entity_id);
      if (entity) {
        upsertOrbState(entity, {
          orb_instance_id: p.orb_instance_id,
          orb_id: p.orb_id,
          orb_name: p.orb_name,
          owner_entity_id: p.owner_entity_id,
          slot_index: p.slot_index,
          passive: normalizeOptionalNumber(p.passive),
          evoke: normalizeOptionalNumber(p.evoke),
        });
      }
      break;
    }

    case "orb_evoked":
    case "orb_passive_triggered": {
      break;
    }

    case "orb_modified": {
      const p = event.payload;
      const entity = state.entities.get(p.owner_entity_id);
      if (entity?.orbs) {
        const orb = entity.orbs.find((existing) => existing.orb_instance_id === p.orb_instance_id);
        if (orb) {
          if (p.changes.passive !== undefined) {
            orb.passive = p.changes.passive.new;
          } else if (p.passive !== undefined) {
            orb.passive = normalizeOptionalNumber(p.passive);
          }

          if (p.changes.evoke !== undefined) {
            orb.evoke = p.changes.evoke.new;
          } else if (p.evoke !== undefined) {
            orb.evoke = normalizeOptionalNumber(p.evoke);
          }
        }
      }
      break;
    }

    case "orb_removed": {
      const p = event.payload;
      const entity = state.entities.get(p.owner_entity_id);
      if (entity) {
        removeOrbState(entity, p.orb_instance_id);
      }
      break;
    }

    case "orb_slots_changed": {
      const p = event.payload;
      const entity = state.entities.get(p.entity_id);
      if (entity) {
        entity.orb_slots = p.new_slots;
      }
      break;
    }

    case "damage_dealt": {
      break;
    }

    case "hp_changed": {
      const p = event.payload as HpChangedPayload;
      const entity = state.entities.get(p.entity_id);
      if (entity) {
        entity.current_hp = p.new;
        if (p.new <= 0) {
          entity.alive = false;
          entity.intent = undefined;
        } else if (p.delta > 0 && !entity.alive) {
          // Compatibility fallback for older logs captured before entity_revived.
          entity.alive = true;
        }
      }
      break;
    }

    case "block_changed": {
      const p = event.payload as BlockChangedPayload;
      const entity = state.entities.get(p.entity_id);
      if (entity) entity.block = p.new;
      break;
    }

    case "block_broken":
    case "block_cleared":
    case "block_clear_prevented": {
      break;
    }

    case "energy_changed": {
      const p = event.payload as EnergyChangedPayload;
      const entity = state.entities.get(p.entity_id);
      if (entity) entity.energy = p.new;
      break;
    }

    case "resource_changed": {
      const p = event.payload as ResourceChangedPayload;
      const entity = state.entities.get(p.entity_id);
      if (entity) {
        entity.resources ??= {};
        entity.resources[p.resource_id] = p.new;
      }
      break;
    }

    case "power_applied": {
      const p = event.payload as PowerAppliedPayload;
      const entity = state.entities.get(p.target_entity_id);
      if (entity) {
        entity.powers[p.power_id] = {
          power_id: p.power_id,
          power_name: p.power_name,
          stacks: p.stacks,
        };
      }
      break;
    }

    case "power_removed": {
      const p = event.payload as PowerRemovedPayload;
      const entity = state.entities.get(p.target_entity_id);
      if (entity) {
        delete entity.powers[p.power_id];
      }
      break;
    }

    case "power_stacks_changed": {
      const p = event.payload as PowerStacksChangedPayload;
      const entity = state.entities.get(p.target_entity_id);
      if (entity) {
        entity.powers[p.power_id] = {
          power_id: p.power_id,
          power_name: p.power_name ?? entity.powers[p.power_id]?.power_name,
          stacks: p.new_stacks,
        };
      }
      break;
    }

    case "intent_changed": {
      const p = event.payload as IntentChangedPayload;
      const entity = state.entities.get(p.entity_id);
      if (entity) {
        entity.intent = {
          intent_id: p.intent_id,
          intent_name: p.intent_name,
          projected_damage: normalizeOptionalNumber(p.projected_damage),
          projected_hits: normalizeOptionalNumber(p.projected_hits),
        };
      }
      break;
    }

    default:
      break;
  }

  if (event.turn_index !== undefined) {
    state.turn_index = event.turn_index;
  }
  if (event.phase !== undefined) {
    state.phase = event.phase;
  }
  state.last_seq = event.seq;
  state.events.push(event);
}

// ─── Replay helpers ───────────────────────────────────────────────────────

export function replayFromStart(
  events: CombatEvent[],
  battleId = "",
): BattleState {
  const state = createInitialState();
  state.battle_id = battleId;
  for (const event of events) {
    applyEvent(state, event);
    if (event.event_type === "battle_ended") {
      break;
    }
  }
  return state;
}

export function replayFromSnapshot(
  snapshot: Snapshot,
  events: CombatEvent[],
  battleId = "",
): BattleState {
  const state = hydrateFromSnapshot(snapshot, battleId);
  if (state.ended) {
    return state;
  }
  const filtered = events.filter((e) => e.seq > snapshot.seq);
  for (const event of filtered) {
    applyEvent(state, event);
    if (event.event_type === "battle_ended") {
      break;
    }
  }
  return state;
}
