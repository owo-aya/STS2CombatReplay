import { applyEvent } from "../parser/replay";
import type {
  AttributionRef,
  BlockChangedPayload,
  CardCreatedPayload,
  CardModifiedPayload,
  CardMovedPayload,
  CardPlayStartedPayload,
  CombatEvent,
  DamageAttemptPayload,
  EntityDiedPayload,
  PowerAppliedPayload,
  PowerRemovedPayload,
  PowerStacksChangedPayload,
} from "../types/events";
import type { BattleMetadata } from "../types/metadata";
import { createInitialState, type BattleState, type CardInstanceState } from "../types/state";
import type { ResolutionNode } from "../parser/resolutionTree";
import type { ViewerBattleModel } from "./model";

export interface BattleSummaryLeader {
  key: string;
  label: string;
  source_kind: "card" | "enemy_move" | "entity" | "other";
  total: number;
  detail: string;
  last_seq: number;
}

export interface TurnDiagnosticsRow {
  turn_index: number;
  player_damage_taken: number;
  enemy_damage_taken: number;
  block_gained: number;
  debuffs_taken: number;
  net_pressure: number;
  end_buffer: number | null;
  is_collapse_turn: boolean;
}

export interface TerminalChain {
  mode: "victory" | "defeat";
  subject_entity_id?: string;
  subject_label: string;
  reason: string;
  terminal_seq: number;
  start_seq: number;
  end_seq: number;
  root_action_resolution_id?: string;
  root_action_label?: string;
  is_provisional: boolean;
  events: CombatEvent[];
}

export interface CardContributionPlay {
  card_instance_id: string;
  seq: number;
  turn_index?: number;
  resolution_id?: string;
  energy_cost_paid?: number;
  target_labels: string[];
  damage_total: number;
  block_total: number;
  power_events: number;
  power_stacks: number;
  generated_cards: number;
  kills: number;
}

export interface CardContributionInstance {
  card_instance_id: string;
  card_def_id: string;
  card_name: string;
  owner_entity_id: string;
  created_this_combat: boolean;
  temporary: boolean;
  current_upgrade_level: number;
  copies_seen: number;
  times_drawn: number;
  times_played: number;
  damage_total: number;
  block_total: number;
  power_events: number;
  power_stacks: number;
  generated_cards: number;
  kills: number;
  contribution_score: number;
  zones_seen: string[];
  plays: CardContributionPlay[];
}

export interface CardContributionGroup {
  card_def_id: string;
  card_name: string;
  copies_seen: number;
  times_drawn: number;
  times_played: number;
  damage_total: number;
  block_total: number;
  power_events: number;
  power_stacks: number;
  generated_cards: number;
  kills: number;
  contribution_score: number;
  contribution_share: number;
  has_created_copy: boolean;
  has_temporary_copy: boolean;
  highest_upgrade_level: number;
  instances: CardContributionInstance[];
}

export interface BattleSummary {
  result_label: string;
  turns: number;
  player_damage_dealt: number;
  player_damage_taken: number;
  player_block_gained: number;
  cards_played: number;
  is_provisional: boolean;
  key_cards: CardContributionGroup[];
  highest_damage_source?: BattleSummaryLeader;
  highest_block_source?: BattleSummaryLeader;
  highest_enemy_pressure_source?: BattleSummaryLeader;
  collapse_turn?: TurnDiagnosticsRow;
}

export interface BattleAnalytics {
  player_entity_id?: string;
  summary: BattleSummary;
  turns: TurnDiagnosticsRow[];
  terminal_chain?: TerminalChain;
  cards: CardContributionGroup[];
}

interface MutableTurnRow extends TurnDiagnosticsRow {}

interface MutableCardContributionPlay extends CardContributionPlay {
  power_applied_count: number;
}

interface MutableCardContributionInstance extends CardContributionInstance {
  power_applied_count: number;
  zone_set: Set<string>;
}

interface LeaderAccumulator {
  key: string;
  label: string;
  source_kind: BattleSummaryLeader["source_kind"];
  total: number;
  detail: string;
  last_seq: number;
}

interface AnalyticsContext {
  metadata: BattleMetadata;
  model: ViewerBattleModel;
  state: BattleState;
  player_entity_id?: string;
  entity_side_by_id: Map<string, string>;
  entity_name_by_id: Map<string, string>;
  player_damage_dealt: number;
  player_damage_taken: number;
  player_block_gained: number;
  cards_played: number;
  turns: Map<number, MutableTurnRow>;
  card_instances: Map<string, MutableCardContributionInstance>;
  play_by_resolution_id: Map<string, MutableCardContributionPlay>;
  last_play_by_instance_id: Map<string, MutableCardContributionPlay>;
  damage_leaders: Map<string, LeaderAccumulator>;
  block_leaders: Map<string, LeaderAccumulator>;
  pressure_leaders: Map<string, LeaderAccumulator>;
}

function humanizeToken(value: string | undefined | null): string {
  if (!value) {
    return "Unknown";
  }

  return value
    .replaceAll(/[_-]+/g, " ")
    .replaceAll(/\s+/g, " ")
    .trim()
    .replace(/\b\w/g, (match) => match.toUpperCase());
}

function formatCompactNumber(value: number): string {
  return Number.isInteger(value) ? `${value}` : value.toFixed(1);
}

function normalizeCardName(cardName: string | undefined, fallbackId: string): string {
  return cardName?.trim() || humanizeToken(fallbackId);
}

function makeEmptyTurnRow(turnIndex: number): MutableTurnRow {
  return {
    turn_index: turnIndex,
    player_damage_taken: 0,
    enemy_damage_taken: 0,
    block_gained: 0,
    debuffs_taken: 0,
    net_pressure: 0,
    end_buffer: null,
    is_collapse_turn: false,
  };
}

function resultLooksLikeVictory(result: string | null | undefined): boolean {
  if (!result) {
    return false;
  }
  return result.toLowerCase().includes("victory") || result.toLowerCase().includes("win");
}

function resultLooksLikeDefeat(result: string | null | undefined): boolean {
  if (!result) {
    return false;
  }
  return result.toLowerCase().includes("defeat") || result.toLowerCase().includes("lose");
}

function isCompletedContainer(metadata: BattleMetadata): boolean {
  return metadata.container?.completion_state === "completed";
}

function isProvisionalResult(metadata: BattleMetadata): boolean {
  return metadata.battle.result == null || metadata.battle.result === "unknown" || !isCompletedContainer(metadata);
}

function getTurnRow(context: AnalyticsContext, turnIndex: number | undefined): MutableTurnRow | undefined {
  if (turnIndex === undefined || turnIndex <= 0) {
    return undefined;
  }

  let row = context.turns.get(turnIndex);
  if (!row) {
    row = makeEmptyTurnRow(turnIndex);
    context.turns.set(turnIndex, row);
  }
  return row;
}

function isPlayerSide(context: AnalyticsContext, entityId: string | undefined | null): boolean {
  if (!entityId) {
    return false;
  }

  if (context.player_entity_id && entityId === context.player_entity_id) {
    return true;
  }

  const entity = context.state.entities.get(entityId);
  if (entity) {
    return entity.side === "player";
  }

  return context.entity_side_by_id.get(entityId) === "player";
}

function isEnemySide(context: AnalyticsContext, entityId: string | undefined | null): boolean {
  if (!entityId) {
    return false;
  }

  const entity = context.state.entities.get(entityId);
  if (entity) {
    return entity.side === "enemy";
  }

  return context.entity_side_by_id.get(entityId) === "enemy";
}

function labelEntityRef(
  context: AnalyticsContext,
  entityId: string | undefined | null,
): string {
  if (!entityId) {
    return "Unknown Entity";
  }

  const current = context.state.entities.get(entityId);
  if (current?.name) {
    return current.name;
  }

  return context.entity_name_by_id.get(entityId) ?? entityId;
}

function getCardState(
  context: AnalyticsContext,
  cardInstanceId: string | undefined | null,
): CardInstanceState | undefined {
  if (!cardInstanceId) {
    return undefined;
  }

  return context.state.cards.get(cardInstanceId);
}

function labelCardInstance(
  context: AnalyticsContext,
  cardInstanceId: string | undefined | null,
  fallbackId?: string,
): string {
  if (!cardInstanceId) {
    return humanizeToken(fallbackId ?? "unknown_card");
  }

  const card = context.card_instances.get(cardInstanceId) ?? undefined;
  if (card) {
    return card.card_name;
  }

  const stateCard = getCardState(context, cardInstanceId);
  if (stateCard) {
    return normalizeCardName(stateCard.card_name, stateCard.card_def_id);
  }

  return humanizeToken(fallbackId ?? cardInstanceId);
}

function getSourceKind(ref: AttributionRef | undefined): BattleSummaryLeader["source_kind"] {
  if (!ref) {
    return "other";
  }

  if (ref.kind === "card_instance") {
    return "card";
  }

  if (ref.kind === "enemy_move") {
    return "enemy_move";
  }

  if (ref.kind === "entity") {
    return "entity";
  }

  return "other";
}

function labelAttribution(
  context: AnalyticsContext,
  ref: AttributionRef | undefined,
): string {
  if (!ref) {
    return "Unknown Source";
  }

  switch (ref.kind) {
    case "card_instance":
      return labelCardInstance(context, ref.card_instance_id ?? ref.ref, ref.model_id ?? ref.ref);
    case "enemy_move": {
      const ownerLabel = labelEntityRef(context, ref.owner_entity_id);
      return `${ownerLabel} · ${humanizeToken(ref.move_id ?? ref.ref)}`;
    }
    case "entity":
      return labelEntityRef(context, ref.entity_id ?? ref.ref);
    case "relic":
      return ref.relic_name ?? ref.relic_id ?? humanizeToken(ref.ref);
    case "power_instance":
      return ref.power_name ?? ref.power_id ?? humanizeToken(ref.ref);
    case "potion_instance":
      return humanizeToken(ref.potion_instance_id ?? ref.ref);
    case "orb_instance":
      return ref.orb_name ?? ref.orb_id ?? humanizeToken(ref.ref);
    default:
      return humanizeToken(ref.ref);
  }
}

function makeLeaderDetail(kind: "damage" | "block" | "pressure", total: number): string {
  switch (kind) {
    case "damage":
      return `${formatCompactNumber(total)} total HP`;
    case "block":
      return `${formatCompactNumber(total)} total Block`;
    case "pressure":
      return `${formatCompactNumber(total)} total pressure`;
  }
}

function accumulateLeader(
  target: Map<string, LeaderAccumulator>,
  kind: "damage" | "block" | "pressure",
  key: string,
  label: string,
  sourceKind: BattleSummaryLeader["source_kind"],
  delta: number,
  seq: number,
): void {
  if (delta <= 0) {
    return;
  }

  const existing = target.get(key);
  if (existing) {
    existing.total += delta;
    existing.detail = makeLeaderDetail(kind, existing.total);
    existing.last_seq = seq;
    return;
  }

  target.set(key, {
    key,
    label,
    source_kind: sourceKind,
    total: delta,
    detail: makeLeaderDetail(kind, delta),
    last_seq: seq,
  });
}

function pickTopLeader(values: Map<string, LeaderAccumulator>): BattleSummaryLeader | undefined {
  return [...values.values()]
    .sort((left, right) => {
      if (right.total !== left.total) {
        return right.total - left.total;
      }
      return left.label.localeCompare(right.label);
    })
    .map((value) => ({
      key: value.key,
      label: value.label,
      source_kind: value.source_kind,
      total: value.total,
      detail: value.detail,
      last_seq: value.last_seq,
    }))[0];
}

function inferPlayerEntityId(metadata: BattleMetadata, state: BattleState, events: CombatEvent[]): string | undefined {
  const battleStarted = events.find((event) => event.event_type === "battle_started");
  if (battleStarted?.event_type === "battle_started") {
    return battleStarted.payload.player_entity_id;
  }

  const fromState = [...state.entities.values()].find((entity) => entity.side === "player");
  if (fromState) {
    return fromState.entity_id;
  }

  const fromSpawn = events.find((event) => {
    return event.event_type === "entity_spawned" && event.payload.side === "player";
  });

  if (fromSpawn?.event_type === "entity_spawned") {
    return fromSpawn.payload.entity_id;
  }

  return metadata.battle.character_id ? "player:0" : undefined;
}

function getFallbackCardState(context: AnalyticsContext, cardInstanceId: string): CardInstanceState | undefined {
  return context.state.cards.get(cardInstanceId);
}

function ensureCardInstance(
  context: AnalyticsContext,
  cardInstanceId: string,
  seed?: Partial<CardContributionInstance> & {
    card_def_id?: string;
    card_name?: string;
    owner_entity_id?: string;
  },
): MutableCardContributionInstance | undefined {
  const existing = context.card_instances.get(cardInstanceId);
  if (existing) {
    if (seed?.card_name) {
      existing.card_name = normalizeCardName(seed.card_name, existing.card_def_id);
    }
    if (seed?.card_def_id) {
      existing.card_def_id = seed.card_def_id;
    }
    if (seed?.owner_entity_id) {
      existing.owner_entity_id = seed.owner_entity_id;
    }
    if (seed?.created_this_combat) {
      existing.created_this_combat = true;
    }
    if (seed?.temporary) {
      existing.temporary = true;
    }
    if (seed?.current_upgrade_level !== undefined) {
      existing.current_upgrade_level = Math.max(existing.current_upgrade_level, seed.current_upgrade_level);
    }
    return existing;
  }

  const fallback = getFallbackCardState(context, cardInstanceId);
  const ownerEntityId = seed?.owner_entity_id ?? fallback?.owner_entity_id;

  if (ownerEntityId && !isPlayerSide(context, ownerEntityId)) {
    return undefined;
  }

  const cardDefId = seed?.card_def_id ?? fallback?.card_def_id ?? cardInstanceId;
  const cardName = normalizeCardName(seed?.card_name ?? fallback?.card_name, cardDefId);

  const created: MutableCardContributionInstance = {
    card_instance_id: cardInstanceId,
    card_def_id: cardDefId,
    card_name: cardName,
    owner_entity_id: ownerEntityId ?? context.player_entity_id ?? "player:0",
    created_this_combat: seed?.created_this_combat ?? fallback?.created_this_combat ?? false,
    temporary: seed?.temporary ?? fallback?.temporary ?? false,
    current_upgrade_level:
      seed?.current_upgrade_level ??
      fallback?.current_upgrade_level ??
      0,
    copies_seen: 1,
    times_drawn: 0,
    times_played: 0,
    damage_total: 0,
    block_total: 0,
    power_events: 0,
    power_stacks: 0,
    generated_cards: 0,
    kills: 0,
    contribution_score: 0,
    zones_seen: [],
    zone_set: new Set<string>(),
    plays: [],
    power_applied_count: 0,
  };

  context.card_instances.set(cardInstanceId, created);
  return created;
}

function appendZone(instance: MutableCardContributionInstance | undefined, zoneName: string | undefined): void {
  if (!instance || !zoneName || instance.zone_set.has(zoneName)) {
    return;
  }

  instance.zone_set.add(zoneName);
  instance.zones_seen.push(zoneName);
}

function ensurePlay(
  context: AnalyticsContext,
  event: CombatEvent & { event_type: "card_play_started" },
): MutableCardContributionPlay | undefined {
  if (!context.player_entity_id || event.payload.actor_entity_id !== context.player_entity_id) {
    return undefined;
  }

  const instance = ensureCardInstance(context, event.payload.card_instance_id, {
    card_def_id: event.payload.card_def_id,
    owner_entity_id: event.payload.actor_entity_id,
  });
  if (!instance) {
    return undefined;
  }

  instance.times_played += 1;

  const play: MutableCardContributionPlay = {
    card_instance_id: event.payload.card_instance_id,
    seq: event.seq,
    turn_index: event.turn_index,
    resolution_id: event.resolution_id ?? undefined,
    energy_cost_paid: event.payload.energy_cost_paid,
    target_labels: event.payload.target_entity_ids.map((entityId) => labelEntityRef(context, entityId)),
    damage_total: 0,
    block_total: 0,
    power_events: 0,
    power_stacks: 0,
    generated_cards: 0,
    kills: 0,
    power_applied_count: 0,
  };

  instance.plays.push(play);

  if (event.resolution_id) {
    context.play_by_resolution_id.set(event.resolution_id, play);
  }
  context.last_play_by_instance_id.set(event.payload.card_instance_id, play);

  return play;
}

function getAttributedPlay(
  context: AnalyticsContext,
  event: CombatEvent,
  cardInstanceId: string,
): MutableCardContributionPlay | undefined {
  if (event.resolution_id) {
    const byResolution = context.play_by_resolution_id.get(event.resolution_id);
    if (byResolution && byResolution.card_instance_id === cardInstanceId) {
      return byResolution;
    }
  }

  return context.last_play_by_instance_id.get(cardInstanceId);
}

function resolvePlayerCardInstanceId(
  context: AnalyticsContext,
  ref: AttributionRef | undefined,
): string | undefined {
  if (!ref || ref.kind !== "card_instance" || !ref.card_instance_id) {
    return undefined;
  }

  const instance = ensureCardInstance(context, ref.card_instance_id, {
    card_def_id: ref.model_id,
    owner_entity_id: ref.owner_entity_id,
  });
  return instance?.card_instance_id;
}

function isEnemySource(
  context: AnalyticsContext,
  event: CombatEvent,
  ref: AttributionRef | undefined,
  explicitEntityId?: string,
): boolean {
  if (explicitEntityId && isEnemySide(context, explicitEntityId)) {
    return true;
  }

  if (ref?.owner_entity_id && isEnemySide(context, ref.owner_entity_id)) {
    return true;
  }

  if (ref?.entity_id && isEnemySide(context, ref.entity_id)) {
    return true;
  }

  if (ref?.kind === "enemy_move") {
    return true;
  }

  if ("actor_entity_id" in event.payload && isEnemySide(context, event.payload.actor_entity_id)) {
    return true;
  }

  return false;
}

function isPlayerSource(
  context: AnalyticsContext,
  event: CombatEvent,
  ref: AttributionRef | undefined,
  explicitEntityId?: string,
): boolean {
  if (explicitEntityId && isPlayerSide(context, explicitEntityId)) {
    return true;
  }

  if (ref?.owner_entity_id && isPlayerSide(context, ref.owner_entity_id)) {
    return true;
  }

  if (ref?.entity_id && isPlayerSide(context, ref.entity_id)) {
    return true;
  }

  if (ref?.kind === "card_instance" && ref.owner_entity_id) {
    return isPlayerSide(context, ref.owner_entity_id);
  }

  if ("actor_entity_id" in event.payload && isPlayerSide(context, event.payload.actor_entity_id)) {
    return true;
  }

  return false;
}

function recordCardCreated(
  context: AnalyticsContext,
  event: CombatEvent & { event_type: "card_created" },
): void {
  const payload = event.payload as CardCreatedPayload;
  if (!isPlayerSide(context, payload.owner_entity_id)) {
    return;
  }

  const instance = ensureCardInstance(context, payload.card_instance_id, {
    card_def_id: payload.card_def_id,
    card_name: payload.card_name,
    owner_entity_id: payload.owner_entity_id,
    created_this_combat: payload.created_this_combat,
    temporary: payload.temporary,
    current_upgrade_level: payload.current_upgrade_level ?? 0,
  });
  appendZone(instance, payload.initial_zone);

  const sourceCardInstanceId = resolvePlayerCardInstanceId(context, payload.trigger);
  if (!sourceCardInstanceId) {
    return;
  }

  const sourceInstance = context.card_instances.get(sourceCardInstanceId);
  if (!sourceInstance) {
    return;
  }

  sourceInstance.generated_cards += 1;
  const play = getAttributedPlay(context, event, sourceCardInstanceId);
  if (play) {
    play.generated_cards += 1;
  }
}

function recordCardModified(
  context: AnalyticsContext,
  event: CombatEvent & { event_type: "card_modified" },
): void {
  const payload = event.payload as CardModifiedPayload;
  const instance = ensureCardInstance(context, payload.card_instance_id, {
    card_name: payload.card_name,
    current_upgrade_level:
      payload.changes.upgrade_level?.new ??
      payload.current_upgrade_level ??
      0,
  });

  if (!instance) {
    return;
  }

  if (payload.changes.upgrade_level?.new !== undefined) {
    instance.current_upgrade_level = Math.max(
      instance.current_upgrade_level,
      payload.changes.upgrade_level.new,
    );
  } else if (payload.current_upgrade_level !== undefined) {
    instance.current_upgrade_level = Math.max(
      instance.current_upgrade_level,
      payload.current_upgrade_level,
    );
  }
}

function recordCardMoved(
  context: AnalyticsContext,
  event: CombatEvent & { event_type: "card_moved" },
): void {
  const payload = event.payload as CardMovedPayload;
  const instance = ensureCardInstance(context, payload.card_instance_id);
  if (!instance) {
    return;
  }

  appendZone(instance, payload.from_zone);
  appendZone(instance, payload.to_zone);

  if (payload.to_zone === "hand" && payload.reason === "draw") {
    instance.times_drawn += 1;
  }
}

function recordDamageAttempt(
  context: AnalyticsContext,
  event: CombatEvent & { event_type: "damage_attempt" },
): void {
  const payload = event.payload as DamageAttemptPayload;
  const targetEntityId = payload.settled_target_entity_id;
  const targetIsPlayer = isPlayerSide(context, targetEntityId);
  const targetIsEnemy = isEnemySide(context, targetEntityId);
  const playerSource = isPlayerSource(context, event, payload.executor, payload.actor_entity_id);
  const enemySource = isEnemySource(context, event, payload.executor, payload.actor_entity_id);

  if (targetIsEnemy && playerSource) {
    context.player_damage_dealt += payload.hp_loss;
    const turn = getTurnRow(context, event.turn_index);
    if (turn) {
      turn.enemy_damage_taken += payload.hp_loss;
    }
  }

  if (targetIsPlayer) {
    context.player_damage_taken += payload.hp_loss;
    const turn = getTurnRow(context, event.turn_index);
    if (turn) {
      turn.player_damage_taken += payload.hp_loss;
    }
  }

  const damageSourceLabel = labelAttribution(context, payload.executor);
  const damageSourceKey = `${payload.executor.kind}:${payload.executor.ref}`;
  accumulateLeader(
    context.damage_leaders,
    "damage",
    damageSourceKey,
    damageSourceLabel,
    getSourceKind(payload.executor),
    payload.hp_loss,
    event.seq,
  );

  if (targetIsPlayer && enemySource) {
    const pressureValue = payload.hp_loss + payload.blocked_amount;
    accumulateLeader(
      context.pressure_leaders,
      "pressure",
      damageSourceKey,
      damageSourceLabel,
      getSourceKind(payload.executor),
      pressureValue,
      event.seq,
    );
  }

  const sourceCardInstanceId = resolvePlayerCardInstanceId(context, payload.executor);
  if (!sourceCardInstanceId) {
    return;
  }

  const instance = context.card_instances.get(sourceCardInstanceId);
  if (!instance) {
    return;
  }

  instance.damage_total += payload.hp_loss;
  if (payload.target_died) {
    instance.kills += 1;
  }

  const play = getAttributedPlay(context, event, sourceCardInstanceId);
  if (play) {
    play.damage_total += payload.hp_loss;
    if (payload.target_died) {
      play.kills += 1;
    }
  }
}

function recordBlockChanged(
  context: AnalyticsContext,
  event: CombatEvent & { event_type: "block_changed" },
): void {
  const payload = event.payload as BlockChangedPayload;
  if (payload.delta > 0 && isPlayerSide(context, payload.entity_id)) {
    context.player_block_gained += payload.delta;
    const turn = getTurnRow(context, event.turn_index);
    if (turn) {
      turn.block_gained += payload.delta;
    }
  }

  if (payload.delta <= 0 || payload.trigger?.kind !== "card_instance") {
    return;
  }

  const sourceCardInstanceId = resolvePlayerCardInstanceId(context, payload.trigger);
  if (!sourceCardInstanceId) {
    return;
  }

  const value = payload.final_gain_amount ?? payload.delta;
  const instance = context.card_instances.get(sourceCardInstanceId);
  if (!instance) {
    return;
  }

  instance.block_total += value;
  const play = getAttributedPlay(context, event, sourceCardInstanceId);
  if (play) {
    play.block_total += value;
  }

  const leaderKey = `${payload.trigger.kind}:${payload.trigger.ref}`;
  accumulateLeader(
    context.block_leaders,
    "block",
    leaderKey,
    labelAttribution(context, payload.trigger),
    getSourceKind(payload.trigger),
    value,
    event.seq,
  );
}

function recordPowerAttributedToCard(
  context: AnalyticsContext,
  event: CombatEvent,
  triggerRef: AttributionRef | undefined,
  positiveStacks: number,
  countAsApplied: boolean,
): void {
  const sourceCardInstanceId = resolvePlayerCardInstanceId(context, triggerRef);
  if (!sourceCardInstanceId) {
    return;
  }

  const instance = context.card_instances.get(sourceCardInstanceId);
  if (!instance) {
    return;
  }

  instance.power_events += 1;
  if (positiveStacks > 0) {
    instance.power_stacks += positiveStacks;
  }
  if (countAsApplied) {
    instance.power_applied_count += 1;
  }

  const play = getAttributedPlay(context, event, sourceCardInstanceId);
  if (play) {
    play.power_events += 1;
    if (positiveStacks > 0) {
      play.power_stacks += positiveStacks;
    }
    if (countAsApplied) {
      play.power_applied_count += 1;
    }
  }
}

function recordPowerApplied(
  context: AnalyticsContext,
  event: CombatEvent & { event_type: "power_applied" },
): void {
  const payload = event.payload as PowerAppliedPayload;
  recordPowerAttributedToCard(context, event, payload.trigger, payload.stacks, true);

  if (!isPlayerSide(context, payload.target_entity_id) || !isEnemySource(context, event, payload.trigger ?? payload.applier, payload.applier?.entity_id)) {
    return;
  }

  const turn = getTurnRow(context, event.turn_index);
  if (turn) {
    turn.debuffs_taken += Math.max(payload.stacks, 1);
  }

  const sourceRef = payload.trigger ?? payload.applier;
  if (!sourceRef) {
    return;
  }

  const pressure = Math.max(payload.stacks, 1) * 3;
  accumulateLeader(
    context.pressure_leaders,
    "pressure",
    `${sourceRef.kind}:${sourceRef.ref}`,
    labelAttribution(context, sourceRef),
    getSourceKind(sourceRef),
    pressure,
    event.seq,
  );
}

function recordPowerStacksChanged(
  context: AnalyticsContext,
  event: CombatEvent & { event_type: "power_stacks_changed" },
): void {
  const payload = event.payload as PowerStacksChangedPayload;
  recordPowerAttributedToCard(context, event, payload.trigger, Math.max(payload.delta, 0), false);

  if (payload.delta <= 0) {
    return;
  }

  if (!isPlayerSide(context, payload.target_entity_id) || !isEnemySource(context, event, payload.trigger ?? payload.applier, payload.applier?.entity_id)) {
    return;
  }

  const turn = getTurnRow(context, event.turn_index);
  if (turn) {
    turn.debuffs_taken += payload.delta;
  }

  const sourceRef = payload.trigger ?? payload.applier;
  if (!sourceRef) {
    return;
  }

  accumulateLeader(
    context.pressure_leaders,
    "pressure",
    `${sourceRef.kind}:${sourceRef.ref}`,
    labelAttribution(context, sourceRef),
    getSourceKind(sourceRef),
    payload.delta * 3,
    event.seq,
  );
}

function recordPowerRemoved(
  context: AnalyticsContext,
  event: CombatEvent & { event_type: "power_removed" },
): void {
  const payload = event.payload as PowerRemovedPayload;
  recordPowerAttributedToCard(context, event, payload.trigger, 0, false);
}

function recordEntitySpawned(context: AnalyticsContext, event: CombatEvent): void {
  if (event.event_type !== "entity_spawned") {
    return;
  }

  context.entity_side_by_id.set(event.payload.entity_id, event.payload.side);
  if (event.payload.name) {
    context.entity_name_by_id.set(event.payload.entity_id, event.payload.name);
  }

  if (event.payload.side === "player" && !context.player_entity_id) {
    context.player_entity_id = event.payload.entity_id;
  }
}

function updateTurnEndBuffer(context: AnalyticsContext, event: CombatEvent): void {
  const row = getTurnRow(context, event.turn_index);
  if (!row || !context.player_entity_id) {
    return;
  }

  const player = context.state.entities.get(context.player_entity_id);
  if (!player) {
    return;
  }

  row.end_buffer = player.current_hp + player.block;
}

function computeContributionScore(instance: Pick<MutableCardContributionInstance, "damage_total" | "block_total" | "power_applied_count" | "power_stacks" | "generated_cards" | "kills">): number {
  const positivePowerScore = instance.power_applied_count * 2 + instance.power_stacks;
  return (
    instance.damage_total +
    instance.block_total * 0.8 +
    positivePowerScore +
    instance.generated_cards * 3 +
    instance.kills * 8
  );
}

function buildCardGroups(instances: Map<string, MutableCardContributionInstance>): CardContributionGroup[] {
  const groups = new Map<string, CardContributionGroup>();

  for (const instance of instances.values()) {
    instance.contribution_score = computeContributionScore(instance);

    let group = groups.get(instance.card_def_id);
    if (!group) {
      group = {
        card_def_id: instance.card_def_id,
        card_name: instance.card_name,
        copies_seen: 0,
        times_drawn: 0,
        times_played: 0,
        damage_total: 0,
        block_total: 0,
        power_events: 0,
        power_stacks: 0,
        generated_cards: 0,
        kills: 0,
        contribution_score: 0,
        contribution_share: 0,
        has_created_copy: false,
        has_temporary_copy: false,
        highest_upgrade_level: 0,
        instances: [],
      };
      groups.set(instance.card_def_id, group);
    }

    group.card_name = group.card_name || instance.card_name;
    group.copies_seen += 1;
    group.times_drawn += instance.times_drawn;
    group.times_played += instance.times_played;
    group.damage_total += instance.damage_total;
    group.block_total += instance.block_total;
    group.power_events += instance.power_events;
    group.power_stacks += instance.power_stacks;
    group.generated_cards += instance.generated_cards;
    group.kills += instance.kills;
    group.contribution_score += instance.contribution_score;
    group.has_created_copy ||= instance.created_this_combat;
    group.has_temporary_copy ||= instance.temporary;
    group.highest_upgrade_level = Math.max(group.highest_upgrade_level, instance.current_upgrade_level);
    group.instances.push({
      ...instance,
      zones_seen: [...instance.zones_seen],
      plays: instance.plays.map((play) => ({
        card_instance_id: play.card_instance_id,
        seq: play.seq,
        turn_index: play.turn_index,
        resolution_id: play.resolution_id,
        energy_cost_paid: play.energy_cost_paid,
        target_labels: [...play.target_labels],
        damage_total: play.damage_total,
        block_total: play.block_total,
        power_events: play.power_events,
        power_stacks: play.power_stacks,
        generated_cards: play.generated_cards,
        kills: play.kills,
      })),
    });
  }

  const list = [...groups.values()];
  const totalScore = list.reduce((sum, entry) => sum + entry.contribution_score, 0);

  for (const group of list) {
    group.instances.sort((left, right) => {
      if (right.contribution_score !== left.contribution_score) {
        return right.contribution_score - left.contribution_score;
      }
      if (right.times_played !== left.times_played) {
        return right.times_played - left.times_played;
      }
      return left.card_instance_id.localeCompare(right.card_instance_id);
    });
    group.contribution_share =
      totalScore > 0 ? group.contribution_score / totalScore : 0;
  }

  list.sort((left, right) => {
    const leftZero = left.contribution_score === 0;
    const rightZero = right.contribution_score === 0;
    if (leftZero !== rightZero) {
      return leftZero ? 1 : -1;
    }
    if (right.contribution_score !== left.contribution_score) {
      return right.contribution_score - left.contribution_score;
    }
    if (right.times_played !== left.times_played) {
      return right.times_played - left.times_played;
    }
    if (right.copies_seen !== left.copies_seen) {
      return right.copies_seen - left.copies_seen;
    }
    return left.card_name.localeCompare(right.card_name);
  });

  return list;
}

function computeTurnDiagnostics(context: AnalyticsContext): TurnDiagnosticsRow[] {
  const rows = [...context.turns.values()].sort((left, right) => left.turn_index - right.turn_index);
  let collapseRow: MutableTurnRow | undefined;
  let bestNetPressure = Number.NEGATIVE_INFINITY;
  let lowestEndBuffer = Number.POSITIVE_INFINITY;

  for (const row of rows) {
    row.net_pressure =
      row.player_damage_taken -
      row.enemy_damage_taken -
      row.block_gained * 0.5 +
      row.debuffs_taken * 3;

    const endBuffer = row.end_buffer ?? Number.POSITIVE_INFINITY;
    if (
      collapseRow === undefined &&
      row.net_pressure > 0 &&
      endBuffer < lowestEndBuffer
    ) {
      collapseRow = row;
    }

    if (row.net_pressure > bestNetPressure) {
      bestNetPressure = row.net_pressure;
    }

    lowestEndBuffer = Math.min(lowestEndBuffer, endBuffer);
  }

  if (!collapseRow && rows.length > 0) {
    collapseRow = [...rows].sort((left, right) => {
      if (right.net_pressure !== left.net_pressure) {
        return right.net_pressure - left.net_pressure;
      }
      if ((left.end_buffer ?? Number.POSITIVE_INFINITY) !== (right.end_buffer ?? Number.POSITIVE_INFINITY)) {
        return (left.end_buffer ?? Number.POSITIVE_INFINITY) - (right.end_buffer ?? Number.POSITIVE_INFINITY);
      }
      return left.turn_index - right.turn_index;
    })[0];
  }

  if (collapseRow) {
    collapseRow.is_collapse_turn = true;
  }

  return rows.map((row) => ({ ...row }));
}

function findTerminalEvent(
  context: AnalyticsContext,
  finalState: BattleState,
): { mode: "victory" | "defeat"; subject_entity_id?: string; subject_label: string; reason: string; event: CombatEvent } | undefined {
  const result = context.metadata.battle.result;
  const events = context.model.events;

  if (resultLooksLikeDefeat(result) && context.player_entity_id) {
    for (let index = events.length - 1; index >= 0; index--) {
      const event = events[index];
      if (event.event_type === "entity_died" && event.payload.entity_id === context.player_entity_id) {
        return {
          mode: "defeat",
          subject_entity_id: context.player_entity_id,
          subject_label: labelEntityRef(context, context.player_entity_id),
          reason: "Player defeat chain",
          event,
        };
      }
    }
  }

  if (resultLooksLikeVictory(result)) {
    for (let index = events.length - 1; index >= 0; index--) {
      const event = events[index];
      if (event.event_type !== "entity_died") {
        continue;
      }

      const payload = event.payload as EntityDiedPayload;
      if (!isEnemySide(context, payload.entity_id)) {
        continue;
      }

      return {
        mode: "victory",
        subject_entity_id: payload.entity_id,
        subject_label: labelEntityRef(context, payload.entity_id),
        reason: "Final enemy defeat chain",
        event,
      };
    }
  }

  const player = context.player_entity_id ? finalState.entities.get(context.player_entity_id) : undefined;
  if (player && !player.alive) {
    for (let index = events.length - 1; index >= 0; index--) {
      const event = events[index];
      if (event.event_type === "damage_attempt") {
        const payload = event.payload as DamageAttemptPayload;
        if (payload.target_died && payload.settled_target_entity_id === context.player_entity_id) {
          return {
            mode: "defeat",
            subject_entity_id: context.player_entity_id,
            subject_label: labelEntityRef(context, context.player_entity_id),
            reason: "Player defeat chain",
            event,
          };
        }
      }
    }
  }

  return undefined;
}

function isTerminalChainEventRelevant(
  subjectEntityId: string | undefined,
  event: CombatEvent,
): boolean {
  switch (event.event_type) {
    case "card_play_started":
    case "potion_used":
      return true;
    case "damage_attempt":
      return !subjectEntityId || event.payload.settled_target_entity_id === subjectEntityId || event.payload.target_died;
    case "block_changed":
    case "block_broken":
    case "block_cleared":
    case "block_clear_prevented":
    case "hp_changed":
    case "power_applied":
    case "power_stacks_changed":
    case "power_removed":
      return !subjectEntityId || ("entity_id" in event.payload
        ? event.payload.entity_id === subjectEntityId
        : "target_entity_id" in event.payload
          ? event.payload.target_entity_id === subjectEntityId
          : false);
    case "entity_died":
      return !subjectEntityId || event.payload.entity_id === subjectEntityId;
    default:
      return false;
  }
}

function getRootActionMarkerLabel(
  node: ResolutionNode | undefined,
): string | undefined {
  if (!node) {
    return undefined;
  }
  return node.resolution_id;
}

function buildTerminalChain(
  context: AnalyticsContext,
  finalState: BattleState,
): TerminalChain | undefined {
  const terminal = findTerminalEvent(context, finalState);
  if (!terminal) {
    return undefined;
  }

  const rootMarker = context.model.root_action_by_seq.get(terminal.event.seq);
  const rootEvents = rootMarker
    ? context.model.events.filter((event) => event.seq >= rootMarker.start_seq && event.seq <= rootMarker.end_seq)
    : context.model.events.filter((event) => event.seq >= Math.max(0, terminal.event.seq - 4) && event.seq <= terminal.event.seq + 2);

  const relevantEvents = rootEvents.filter((event) =>
    isTerminalChainEventRelevant(terminal.subject_entity_id, event),
  );

  if (relevantEvents.length === 0) {
    return undefined;
  }

  return {
    mode: terminal.mode,
    subject_entity_id: terminal.subject_entity_id,
    subject_label: terminal.subject_label,
    reason: terminal.reason,
    terminal_seq: terminal.event.seq,
    start_seq: relevantEvents[0].seq,
    end_seq: relevantEvents[relevantEvents.length - 1].seq,
    root_action_resolution_id: rootMarker?.resolution_id,
    root_action_label: getRootActionMarkerLabel(rootMarker?.node),
    is_provisional: isProvisionalResult(context.metadata),
    events: relevantEvents,
  };
}

function seedFinalStateCards(
  context: AnalyticsContext,
  finalState: BattleState,
): void {
  const playerEntityId = context.player_entity_id;
  if (!playerEntityId) {
    return;
  }

  for (const card of finalState.cards.values()) {
    if (card.owner_entity_id !== playerEntityId) {
      continue;
    }

    const instance = ensureCardInstance(context, card.card_instance_id, {
      card_def_id: card.card_def_id,
      card_name: card.card_name,
      owner_entity_id: card.owner_entity_id,
      created_this_combat: card.created_this_combat,
      temporary: card.temporary,
      current_upgrade_level: card.current_upgrade_level ?? 0,
    });
    appendZone(instance, card.zone);
  }
}

export function createBattleAnalytics(
  model: ViewerBattleModel,
  finalState: BattleState,
): BattleAnalytics {
  const state = createInitialState();
  state.battle_id = model.metadata.battle_id;

  const context: AnalyticsContext = {
    metadata: model.metadata,
    model,
    state,
    player_entity_id: inferPlayerEntityId(model.metadata, finalState, model.events),
    entity_side_by_id: new Map<string, string>(),
    entity_name_by_id: new Map<string, string>(),
    player_damage_dealt: 0,
    player_damage_taken: 0,
    player_block_gained: 0,
    cards_played: 0,
    turns: new Map<number, MutableTurnRow>(),
    card_instances: new Map<string, MutableCardContributionInstance>(),
    play_by_resolution_id: new Map<string, MutableCardContributionPlay>(),
    last_play_by_instance_id: new Map<string, MutableCardContributionPlay>(),
    damage_leaders: new Map<string, LeaderAccumulator>(),
    block_leaders: new Map<string, LeaderAccumulator>(),
    pressure_leaders: new Map<string, LeaderAccumulator>(),
  };

  for (const event of model.events) {
    recordEntitySpawned(context, event);

    if (event.event_type === "card_created") {
      recordCardCreated(context, event);
    } else if (event.event_type === "card_modified") {
      recordCardModified(context, event);
    } else if (event.event_type === "card_moved") {
      recordCardMoved(context, event);
    } else if (event.event_type === "card_play_started") {
      if (context.player_entity_id && event.payload.actor_entity_id === context.player_entity_id) {
        context.cards_played += 1;
      }
      ensurePlay(context, event);
    } else if (event.event_type === "damage_attempt") {
      recordDamageAttempt(context, event);
    } else if (event.event_type === "block_changed") {
      recordBlockChanged(context, event);
    } else if (event.event_type === "power_applied") {
      recordPowerApplied(context, event);
    } else if (event.event_type === "power_stacks_changed") {
      recordPowerStacksChanged(context, event);
    } else if (event.event_type === "power_removed") {
      recordPowerRemoved(context, event);
    }

    applyEvent(state, event);
    updateTurnEndBuffer(context, event);
  }

  seedFinalStateCards(context, finalState);

  const cards = buildCardGroups(context.card_instances);
  const turns = computeTurnDiagnostics(context);
  const summary: BattleSummary = {
    result_label: humanizeToken(model.metadata.battle.result ?? "unknown"),
    turns: turns.length,
    player_damage_dealt: context.player_damage_dealt,
    player_damage_taken: context.player_damage_taken,
    player_block_gained: context.player_block_gained,
    cards_played: context.cards_played,
    is_provisional: isProvisionalResult(model.metadata),
    key_cards: cards.slice(0, 3),
    highest_damage_source: pickTopLeader(context.damage_leaders),
    highest_block_source: pickTopLeader(context.block_leaders),
    highest_enemy_pressure_source: pickTopLeader(context.pressure_leaders),
    collapse_turn: turns.find((row) => row.is_collapse_turn),
  };

  const terminalChain = buildTerminalChain(context, finalState);

  return {
    player_entity_id: context.player_entity_id,
    summary,
    turns,
    terminal_chain: terminalChain,
    cards,
  };
}
