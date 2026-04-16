import type { ResolutionNode } from "../parser/resolutionTree";
import type {
  AttributionRef,
  BlockChangedPayload,
  CardModifiedPayload,
  CombatEvent,
  DamageAttemptPayload,
  IntentChangedPayload,
  PowerAppliedPayload,
  PowerRemovedPayload,
  PowerStacksChangedPayload,
  RelicModifiedPayload,
  ResourceChangedPayload,
  TriggerFiredPayload,
} from "../types/events";
import type { BattleMetadata } from "../types/metadata";
import type {
  CardInstanceState,
  EntityState,
  IntentState,
  OrbState,
  PotionInstanceState,
  RelicState,
} from "../types/state";
import type { RootActionMarker, ViewerBattleModel, ViewerFrame } from "./model";

export type ViewerTone = "accent" | "good" | "warn" | "bad" | "muted";

export interface ViewerBadge {
  label: string;
  tone: ViewerTone;
}

export interface ViewerStat {
  label: string;
  value: string;
}

export interface BattleOverview {
  title: string;
  subtitle: string;
  source_line: string;
  badges: ViewerBadge[];
  stats: ViewerStat[];
}

export interface ViewerAlert {
  title: string;
  message: string;
  tone: Exclude<ViewerTone, "accent">;
}

export interface KeyMoment {
  seq: number;
  label: string;
  detail: string;
  tone: ViewerTone;
}

export interface StepSummary {
  headline: string;
  detail: string;
  meta: string[];
}

const ROOT_EVENT_PRIORITY = [
  "card_play_started",
  "potion_used",
  "damage_attempt",
  "entity_died",
  "orb_evoked",
  "relic_triggered",
  "power_applied",
  "power_stacks_changed",
  "block_changed",
  "turn_started",
  "battle_ended",
] as const;

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

function compactDate(value: string | null | undefined): string | undefined {
  if (!value) {
    return undefined;
  }

  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) {
    return value;
  }

  return parsed.toLocaleString(undefined, {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

function formatSignedDelta(delta: number): string {
  return delta >= 0 ? `+${delta}` : `${delta}`;
}

function formatCompatLabel(metadata: BattleMetadata): ViewerBadge | undefined {
  const status = metadata.compat?.status;
  if (!status) {
    return undefined;
  }

  switch (status) {
    case "verified":
      return { label: "Recorder Verified", tone: "good" };
    case "unverified":
      return { label: "Recorder Unverified", tone: "warn" };
    case "unsupported":
      return { label: "Recorder Unsupported", tone: "bad" };
    default:
      return { label: "Recorder Unknown", tone: "muted" };
  }
}

function formatCompletionLabel(metadata: BattleMetadata): ViewerBadge | undefined {
  const state = metadata.container?.completion_state;
  if (!state) {
    return undefined;
  }

  switch (state) {
    case "completed":
      return { label: "Container Complete", tone: "good" };
    case "partial":
      return { label: "Partial Battle", tone: "warn" };
    case "failed_finalize":
      return { label: "Finalize Failed", tone: "bad" };
    default:
      return { label: humanizeToken(state), tone: "muted" };
  }
}

function formatResultLabel(metadata: BattleMetadata): ViewerBadge | undefined {
  const result = metadata.battle.result;
  if (!result) {
    return undefined;
  }

  const lowered = result.toLowerCase();
  if (lowered.includes("victory") || lowered.includes("win")) {
    return { label: humanizeToken(result), tone: "good" };
  }
  if (lowered.includes("defeat") || lowered.includes("lose")) {
    return { label: humanizeToken(result), tone: "bad" };
  }
  return { label: humanizeToken(result), tone: "accent" };
}

function flattenResolutionEvents(node: ResolutionNode): CombatEvent[] {
  const nested = node.children.flatMap(flattenResolutionEvents);
  return [...node.events, ...nested].sort((left, right) => left.seq - right.seq);
}

function getPrimaryResolutionEvent(node: ResolutionNode): CombatEvent | undefined {
  const events = flattenResolutionEvents(node);
  for (const eventType of ROOT_EVENT_PRIORITY) {
    const match = events.find((event) => event.event_type === eventType);
    if (match) {
      return match;
    }
  }
  return events[0];
}

function labelEntityRef(entityId: string | undefined | null): string {
  return entityId ? humanizeToken(entityId) : "Unknown Entity";
}

export function labelEntity(entity: EntityState): string {
  return entity.name ?? entity.entity_def_id ?? labelEntityRef(entity.entity_id);
}

export function labelCard(card: CardInstanceState | undefined, fallbackId?: string): string {
  if (card) {
    return card.card_name ?? card.card_def_id ?? fallbackId ?? card.card_instance_id;
  }
  return fallbackId ? humanizeToken(fallbackId) : "Unknown Card";
}

export function labelPotion(potion: PotionInstanceState): string {
  return potion.potion_name ?? potion.potion_def_id ?? potion.potion_instance_id;
}

export function labelOrb(orb: OrbState): string {
  return orb.orb_name ?? orb.orb_id ?? orb.orb_instance_id;
}

export function labelRelic(relic: RelicState): string {
  return relic.relic_name ?? relic.relic_id ?? relic.relic_instance_id;
}

export function formatIntent(intent: IntentState | undefined): string {
  if (!intent) {
    return "No intent";
  }

  const label = intent.intent_name ?? humanizeToken(intent.intent_id);
  if (intent.projected_damage === undefined) {
    return label;
  }
  if (intent.projected_hits !== undefined && intent.projected_hits > 1) {
    return `${label} ${intent.projected_damage}x${intent.projected_hits}`;
  }
  return `${label} ${intent.projected_damage}`;
}

export function formatActionLabel(marker: RootActionMarker): string {
  const primaryEvent = getPrimaryResolutionEvent(marker.node);
  if (!primaryEvent) {
    return humanizeToken(marker.resolution_id);
  }

  switch (primaryEvent.event_type) {
    case "card_play_started": {
      const payload = primaryEvent.payload;
      const title = payload.card_name ?? payload.card_def_id ?? primaryEvent.payload.card_instance_id;
      return `Play ${title}`;
    }
    case "potion_used": {
      const payload = primaryEvent.payload;
      return `Use ${payload.potion_name ?? payload.potion_def_id ?? payload.potion_instance_id}`;
    }
    case "damage_attempt": {
      const payload = primaryEvent.payload as DamageAttemptPayload;
      return `${labelAttribution(payload.executor)} -> ${labelEntityRef(payload.settled_target_entity_id)} ${payload.hp_loss}`;
    }
    case "entity_died": {
      return `${labelEntityRef(primaryEvent.payload.entity_id)} dies`;
    }
    case "orb_evoked": {
      const payload = primaryEvent.payload;
      return `Evoke ${payload.orb_name ?? payload.orb_id ?? payload.orb_instance_id}`;
    }
    case "relic_triggered": {
      const payload = primaryEvent.payload;
      return `${payload.relic_name ?? payload.relic_id ?? payload.relic_instance_id} triggers`;
    }
    case "power_applied": {
      const payload = primaryEvent.payload as PowerAppliedPayload;
      return `Apply ${payload.power_name ?? payload.power_id}`;
    }
    case "power_stacks_changed": {
      const payload = primaryEvent.payload as PowerStacksChangedPayload;
      return `${payload.power_name ?? payload.power_id} ${formatSignedDelta(payload.delta)}`;
    }
    case "block_changed": {
      const payload = primaryEvent.payload as BlockChangedPayload;
      return `${labelEntityRef(payload.entity_id)} Block ${formatSignedDelta(payload.delta)}`;
    }
    case "turn_started": {
      return `Turn ${primaryEvent.payload.turn_index}`;
    }
    case "battle_ended": {
      return `Battle ends ${humanizeToken(primaryEvent.payload.result)}`;
    }
    default:
      return formatEventHeadline(primaryEvent);
  }
}

export function buildBattleOverview(model: ViewerBattleModel, sourceLabel: string): BattleOverview {
  const metadata = model.metadata;
  const encounterTitle =
    metadata.battle.encounter_name ??
    humanizeToken(metadata.battle.encounter_id) ??
    "Unknown Encounter";
  const characterTitle =
    metadata.battle.character_name ??
    humanizeToken(metadata.battle.character_id) ??
    "Unknown Character";
  const resultLabel = metadata.battle.result ? humanizeToken(metadata.battle.result) : "Battle Replay";

  const startedAt = compactDate(metadata.battle.started_at);
  const buildBits = [
    metadata.game.version ?? undefined,
    metadata.game.build ?? undefined,
  ].filter((value): value is string => Boolean(value));

  const badges = [
    formatResultLabel(metadata),
    formatCompatLabel(metadata),
    formatCompletionLabel(metadata),
  ].filter((value): value is ViewerBadge => Boolean(value));

  return {
    title: encounterTitle,
    subtitle: `${characterTitle} · ${resultLabel}`,
    source_line: [
      sourceLabel,
      startedAt ? `Started ${startedAt}` : undefined,
      buildBits.length > 0 ? `Game ${buildBits.join(" / ")}` : undefined,
    ]
      .filter((value): value is string => Boolean(value))
      .join(" · "),
    badges,
    stats: [
      { label: "Turns", value: `${model.turn_start_markers.length}` },
      { label: "Actions", value: `${model.root_action_markers.length}` },
      { label: "Events", value: `${model.events.length}` },
      { label: "Snapshots", value: `${model.snapshot_markers.length}` },
    ],
  };
}

export function collectViewerAlerts(metadata: BattleMetadata): ViewerAlert[] {
  const alerts: ViewerAlert[] = [];

  switch (metadata.compat?.status) {
    case "unsupported":
      alerts.push({
        tone: "bad",
        title: "Unsupported Recorder Build",
        message: "This battle was captured on a game build the viewer does not trust. Replay may drift.",
      });
      break;
    case "unverified":
      alerts.push({
        tone: "warn",
        title: "Unverified Recorder Build",
        message: "The battle can still open, but state fidelity has not been validated against this game build.",
      });
      break;
    case "unknown":
      alerts.push({
        tone: "muted",
        title: "Unknown Recorder Build",
        message: "Version metadata is incomplete, so compatibility checks are weaker than usual.",
      });
      break;
  }

  switch (metadata.container?.completion_state) {
    case "partial":
      alerts.push({
        tone: "warn",
        title: "Partial Battle Container",
        message: "Recorder output ended before a clean battle closeout. Replay should still be inspectable.",
      });
      break;
    case "failed_finalize":
      alerts.push({
        tone: "bad",
        title: "Finalize Failed",
        message: "Battle output was captured, but container finalization did not complete cleanly.",
      });
      break;
  }

  for (const warning of metadata.compat?.warning_messages ?? []) {
    alerts.push({
      tone: "warn",
      title: "Recorder Warning",
      message: warning,
    });
  }

  return alerts;
}

function shouldIncludeKeyMoment(event: CombatEvent): boolean {
  switch (event.event_type) {
    case "battle_started":
    case "battle_ended":
    case "turn_started":
    case "card_play_started":
    case "potion_used":
    case "entity_died":
    case "entity_revived":
    case "orb_evoked":
    case "relic_triggered":
      return true;
    case "damage_attempt": {
      const payload = event.payload as DamageAttemptPayload;
      return payload.hp_loss > 0 || payload.blocked_amount > 0 || payload.target_died;
    }
    case "block_changed": {
      const payload = event.payload as BlockChangedPayload;
      return Math.abs(payload.delta) >= 5;
    }
    case "power_applied":
      return true;
    case "power_stacks_changed": {
      const payload = event.payload as PowerStacksChangedPayload;
      return Math.abs(payload.delta) >= 2;
    }
    default:
      return false;
  }
}

function toneForEvent(event: CombatEvent): ViewerTone {
  switch (event.event_type) {
    case "entity_died":
    case "battle_ended":
      return "bad";
    case "damage_attempt":
      return "accent";
    case "power_applied":
    case "power_stacks_changed":
    case "relic_triggered":
    case "orb_evoked":
      return "good";
    case "turn_started":
      return "muted";
    default:
      return "accent";
  }
}

export function buildKeyMoments(model: ViewerBattleModel): KeyMoment[] {
  return model.events
    .filter(shouldIncludeKeyMoment)
    .map((event) => ({
      seq: event.seq,
      label: formatEventHeadline(event),
      detail: formatEventDetail(event),
      tone: toneForEvent(event),
    }));
}

export function buildStepSummary(frame: ViewerFrame): StepSummary {
  const meta = [`Seq ${frame.seq}`];
  if (frame.event.turn_index !== undefined) {
    meta.push(`Turn ${frame.event.turn_index}`);
  }
  if (frame.event.phase) {
    meta.push(humanizeToken(frame.event.phase));
  }
  if (frame.current_root_action) {
    meta.push(frame.current_root_action.resolution_id);
  }
  if (frame.source_snapshot_seq !== undefined) {
    meta.push(`Snapshot ${frame.source_snapshot_seq}`);
  }
  if (frame.event.cause_event_seq != null) {
    meta.push(`Cause ${frame.event.cause_event_seq}`);
  }

  return {
    headline: formatEventHeadline(frame.event),
    detail: formatEventDetail(frame.event),
    meta,
  };
}

function labelAttribution(ref: AttributionRef | undefined): string {
  if (!ref) {
    return "Unknown source";
  }

  if (ref.kind === "entity") {
    return labelEntityRef(ref.entity_id ?? ref.ref);
  }
  if (ref.kind === "card_instance") {
    return humanizeToken(ref.card_instance_id ?? ref.ref);
  }
  if (ref.kind === "potion_instance") {
    return humanizeToken(ref.potion_instance_id ?? ref.ref);
  }
  if (ref.kind === "orb_instance") {
    return ref.orb_name ?? ref.orb_id ?? ref.orb_instance_id ?? humanizeToken(ref.ref);
  }
  if (ref.kind === "power_instance") {
    return ref.power_name ?? ref.power_id ?? ref.power_instance_id ?? humanizeToken(ref.ref);
  }
  if (ref.kind === "relic") {
    return ref.relic_id ?? ref.relic_instance_id ?? humanizeToken(ref.ref);
  }
  if (ref.kind === "enemy_move") {
    return ref.move_id ?? humanizeToken(ref.ref);
  }
  return humanizeToken(ref.ref);
}

function formatTriggerRef(ref: AttributionRef | undefined): string | undefined {
  if (!ref) {
    return undefined;
  }
  return `Triggered by ${labelAttribution(ref)}`;
}

function formatResourceChange(payload: ResourceChangedPayload): string {
  return `${labelEntityRef(payload.entity_id)} ${humanizeToken(payload.resource_id)} ${formatSignedDelta(payload.delta)}`;
}

export function formatEventHeadline(event: CombatEvent): string {
  switch (event.event_type) {
    case "battle_started":
      return `Battle starts in ${event.payload.encounter_name ?? humanizeToken(event.payload.encounter_id)}`;
    case "battle_ended":
      return `Battle ends: ${humanizeToken(event.payload.result)}`;
    case "turn_started":
      return `Turn ${event.payload.turn_index} starts`;
    case "entity_spawned":
      return `${event.payload.name ?? labelEntityRef(event.payload.entity_id)} appears`;
    case "entity_died":
      return `${labelEntityRef(event.payload.entity_id)} dies`;
    case "entity_removed":
      return `${labelEntityRef(event.payload.entity_id)} leaves the battle`;
    case "entity_revived":
      return `${labelEntityRef(event.payload.entity_id)} revives`;
    case "card_created":
      return `Create ${event.payload.card_name ?? event.payload.card_def_id}`;
    case "card_moved":
      return `${event.payload.card_name ?? event.payload.card_def_id ?? event.payload.card_instance_id} -> ${humanizeToken(event.payload.to_zone)}`;
    case "card_exhausted":
      return `${humanizeToken(event.payload.card_instance_id)} exhausts`;
    case "card_play_started":
      return `Play ${event.payload.card_name ?? event.payload.card_def_id ?? event.payload.card_instance_id}`;
    case "card_play_resolved":
      return `${humanizeToken(event.payload.card_instance_id)} resolves`;
    case "card_modified":
      return `${humanizeToken(event.payload.card_instance_id)} changes`;
    case "potion_initialized":
      return `Initialize potion ${event.payload.potion_name ?? event.payload.potion_def_id}`;
    case "potion_created":
      return `Create potion ${event.payload.potion_name ?? event.payload.potion_def_id}`;
    case "potion_used":
      return `Use ${event.payload.potion_name ?? event.payload.potion_def_id ?? event.payload.potion_instance_id}`;
    case "potion_discarded":
      return `Discard ${event.payload.potion_name ?? event.payload.potion_def_id ?? event.payload.potion_instance_id}`;
    case "relic_initialized":
      return `Equip ${event.payload.relic_name ?? event.payload.relic_id}`;
    case "relic_obtained":
      return `Gain ${event.payload.relic_name ?? event.payload.relic_id}`;
    case "relic_removed":
      return `Lose ${event.payload.relic_name ?? event.payload.relic_id}`;
    case "relic_triggered":
      return `${event.payload.relic_name ?? event.payload.relic_id} triggers`;
    case "relic_modified":
      return `${event.payload.relic_name ?? event.payload.relic_id} updates`;
    case "orb_inserted":
      return `Channel ${event.payload.orb_name ?? event.payload.orb_id}`;
    case "orb_evoked":
      return `Evoke ${event.payload.orb_name ?? event.payload.orb_id}`;
    case "orb_removed":
      return `${event.payload.orb_name ?? event.payload.orb_id} leaves queue`;
    case "orb_passive_triggered":
      return `${event.payload.orb_name ?? event.payload.orb_id} passive triggers`;
    case "orb_modified":
      return `${event.payload.orb_name ?? event.payload.orb_id} changes`;
    case "orb_slots_changed":
      return `${labelEntityRef(event.payload.entity_id)} orb slots ${formatSignedDelta(event.payload.delta)}`;
    case "trigger_fired":
      return `${humanizeToken(event.payload.trigger_type)} fires`;
    case "damage_attempt": {
      const payload = event.payload as DamageAttemptPayload;
      return `${labelAttribution(payload.executor)} -> ${labelEntityRef(payload.settled_target_entity_id)} ${payload.hp_loss} HP`;
    }
    case "damage_dealt":
      return `${labelEntityRef(event.payload.target_entity_id)} takes ${event.payload.amount}`;
    case "hp_changed":
      return `${labelEntityRef(event.payload.entity_id)} HP ${formatSignedDelta(event.payload.delta)}`;
    case "block_changed":
      return `${labelEntityRef(event.payload.entity_id)} Block ${formatSignedDelta(event.payload.delta)}`;
    case "block_broken":
      return `${labelEntityRef(event.payload.entity_id)} block breaks`;
    case "block_cleared":
      return `${labelEntityRef(event.payload.entity_id)} block clears`;
    case "block_clear_prevented":
      return `${labelEntityRef(event.payload.entity_id)} retains block`;
    case "energy_changed":
      return `${labelEntityRef(event.payload.entity_id)} energy ${formatSignedDelta(event.payload.delta)}`;
    case "resource_changed":
      return formatResourceChange(event.payload as ResourceChangedPayload);
    case "power_applied":
      return `${labelEntityRef(event.payload.target_entity_id)} gains ${event.payload.power_name ?? event.payload.power_id}`;
    case "power_removed":
      return `${labelEntityRef(event.payload.target_entity_id)} loses ${event.payload.power_name ?? event.payload.power_id}`;
    case "power_stacks_changed":
      return `${event.payload.power_name ?? event.payload.power_id} ${formatSignedDelta(event.payload.delta)}`;
    case "intent_changed":
      return `${labelEntityRef(event.payload.entity_id)} intent -> ${event.payload.intent_name ?? humanizeToken(event.payload.intent_id)}`;
    default:
      return humanizeToken(event.event_type);
  }
}

export function formatEventDetail(event: CombatEvent): string {
  switch (event.event_type) {
    case "battle_started":
      return `Player ${labelEntityRef(event.payload.player_entity_id)} enters against ${event.payload.enemy_entity_ids.length} enemies.`;
    case "battle_ended":
      return event.payload.reason
        ? `${humanizeToken(event.payload.result)} via ${humanizeToken(event.payload.reason)}.`
        : `${humanizeToken(event.payload.result)}.`;
    case "turn_started":
      return `${humanizeToken(event.payload.active_side)} side becomes active.`;
    case "entity_spawned":
      return [
        `${event.payload.name ?? labelEntityRef(event.payload.entity_id)} joins the battlefield.`,
        event.payload.reason ? `Reason: ${humanizeToken(event.payload.reason)}.` : undefined,
        formatTriggerRef(event.payload.trigger),
      ]
        .filter((value): value is string => Boolean(value))
        .join(" ");
    case "entity_died":
      return [
        `${labelEntityRef(event.payload.entity_id)} is marked dead.`,
        event.payload.reason ? `Reason: ${humanizeToken(event.payload.reason)}.` : undefined,
        formatTriggerRef(event.payload.trigger),
      ]
        .filter((value): value is string => Boolean(value))
        .join(" ");
    case "entity_removed":
      return [
        `${labelEntityRef(event.payload.entity_id)} is removed from active state.`,
        event.payload.reason ? `Reason: ${humanizeToken(event.payload.reason)}.` : undefined,
        formatTriggerRef(event.payload.trigger),
      ]
        .filter((value): value is string => Boolean(value))
        .join(" ");
    case "entity_revived":
      return [
        `${labelEntityRef(event.payload.entity_id)} returns to battle.`,
        event.payload.current_hp !== undefined ? `HP now ${event.payload.current_hp}.` : undefined,
        event.payload.reason ? `Reason: ${humanizeToken(event.payload.reason)}.` : undefined,
      ]
        .filter((value): value is string => Boolean(value))
        .join(" ");
    case "card_created":
      return [
        `${event.payload.card_name ?? event.payload.card_def_id} enters ${humanizeToken(event.payload.initial_zone)}.`,
        event.payload.current_upgrade_level !== undefined
          ? `Upgrade level ${event.payload.current_upgrade_level}.`
          : undefined,
        event.payload.temporary ? "Temporary." : undefined,
        event.payload.created_this_combat ? "Created this combat." : undefined,
      ]
        .filter((value): value is string => Boolean(value))
        .join(" ");
    case "card_moved":
      return [
        `${event.payload.card_name ?? event.payload.card_def_id ?? event.payload.card_instance_id} moves from ${humanizeToken(event.payload.from_zone)} to ${humanizeToken(event.payload.to_zone)}.`,
        `Reason: ${humanizeToken(event.payload.reason)}.`,
        formatTriggerRef(event.payload.trigger),
      ]
        .filter((value): value is string => Boolean(value))
        .join(" ");
    case "card_play_started":
      return [
        `${event.payload.card_name ?? event.payload.card_def_id ?? event.payload.card_instance_id} is played by ${labelEntityRef(event.payload.actor_entity_id)}.`,
        event.payload.target_entity_ids.length > 0
          ? `Targets ${event.payload.target_entity_ids.map(labelEntityRef).join(", ")}.`
          : "No explicit targets.",
        event.payload.energy_cost_paid !== undefined
          ? `Paid ${event.payload.energy_cost_paid} energy.`
          : undefined,
      ]
        .filter((value): value is string => Boolean(value))
        .join(" ");
    case "card_play_resolved":
      return [
        `${humanizeToken(event.payload.card_instance_id)} resolves.`,
        event.payload.final_zone ? `Final zone ${humanizeToken(event.payload.final_zone)}.` : undefined,
        event.payload.resolution_outcome
          ? `Outcome ${humanizeToken(event.payload.resolution_outcome)}.`
          : undefined,
      ]
        .filter((value): value is string => Boolean(value))
        .join(" ");
    case "card_modified": {
      const payload = event.payload as CardModifiedPayload;
      const changes: string[] = [];
      if (payload.changes.cost) {
        changes.push(`cost ${payload.changes.cost.old} -> ${payload.changes.cost.new}`);
      }
      if (payload.changes.upgrade_level) {
        changes.push(`upgrade ${payload.changes.upgrade_level.old} -> ${payload.changes.upgrade_level.new}`);
      }
      if (payload.changes.upgraded) {
        changes.push(`upgraded ${payload.changes.upgraded.old} -> ${payload.changes.upgraded.new}`);
      }
      return [
        changes.length > 0 ? changes.join(", ") : "Card metadata changed.",
        payload.reason ? `Reason: ${humanizeToken(payload.reason)}.` : undefined,
        formatTriggerRef(payload.trigger),
      ]
        .filter((value): value is string => Boolean(value))
        .join(" ");
    }
    case "potion_used":
      return `${event.payload.potion_name ?? event.payload.potion_def_id ?? event.payload.potion_instance_id} used on ${event.payload.target_entity_ids.map(labelEntityRef).join(", ") || "no target"}.`;
    case "potion_discarded":
      return `${event.payload.potion_name ?? event.payload.potion_def_id ?? event.payload.potion_instance_id} discarded from slot ${event.payload.slot_index}.`;
    case "relic_triggered":
      return [
        `${event.payload.relic_name ?? event.payload.relic_id} activates.`,
        event.payload.target_entity_ids && event.payload.target_entity_ids.length > 0
          ? `Targets ${event.payload.target_entity_ids.map(labelEntityRef).join(", ")}.`
          : undefined,
        formatTriggerRef(event.payload.trigger),
      ]
        .filter((value): value is string => Boolean(value))
        .join(" ");
    case "relic_modified": {
      const payload = event.payload as RelicModifiedPayload;
      switch (payload.change_kind) {
        case "display_amount":
          return `Display amount ${payload.old_display_amount ?? "?"} -> ${payload.new_display_amount ?? "?"}.`;
        case "status":
          return `Status ${humanizeToken(payload.old_status)} -> ${humanizeToken(payload.new_status)}.`;
        case "stack_count":
          return `Stacks ${payload.old_stack_count} -> ${payload.new_stack_count}.`;
        case "flag":
          return `${humanizeToken(payload.flag)} ${String(payload.old_value)} -> ${String(payload.new_value)}.`;
      }
      return "Relic state changes.";
    }
    case "orb_inserted":
      return `${event.payload.orb_name ?? event.payload.orb_id} enters slot ${event.payload.slot_index} by ${humanizeToken(event.payload.reason)}.`;
    case "orb_evoked":
      return `${event.payload.orb_name ?? event.payload.orb_id} evokes from slot ${event.payload.slot_index}.`;
    case "orb_removed":
      return `${event.payload.orb_name ?? event.payload.orb_id} leaves slot ${event.payload.slot_index} by ${humanizeToken(event.payload.reason)}.`;
    case "orb_passive_triggered":
      return `${event.payload.orb_name ?? event.payload.orb_id} passive fires at ${humanizeToken(event.payload.timing)}.`;
    case "orb_modified":
      return [
        event.payload.changes.passive
          ? `Passive ${event.payload.changes.passive.old} -> ${event.payload.changes.passive.new}.`
          : undefined,
        event.payload.changes.evoke
          ? `Evoke ${event.payload.changes.evoke.old} -> ${event.payload.changes.evoke.new}.`
          : undefined,
        `Reason: ${humanizeToken(event.payload.reason)}.`,
      ]
        .filter((value): value is string => Boolean(value))
        .join(" ");
    case "orb_slots_changed":
      return `${labelEntityRef(event.payload.entity_id)} orb slots ${event.payload.old_slots} -> ${event.payload.new_slots}.`;
    case "trigger_fired": {
      const payload = event.payload as TriggerFiredPayload;
      return [
        `${humanizeToken(payload.trigger_type)} fired from ${payload.source_resolution_id}.`,
        payload.triggered_resolution_id ? `Child resolution ${payload.triggered_resolution_id}.` : undefined,
      ]
        .filter((value): value is string => Boolean(value))
        .join(" ");
    }
    case "damage_attempt": {
      const payload = event.payload as DamageAttemptPayload;
      return [
        `${labelAttribution(payload.executor)} targets ${labelEntityRef(payload.settled_target_entity_id)}.`,
        `HP loss ${payload.hp_loss}, blocked ${payload.blocked_amount}, settled ${payload.final_settled_damage}.`,
        payload.target_died ? "Target dies." : undefined,
        payload.trigger ? `Triggered by ${labelAttribution(payload.trigger)}.` : undefined,
      ]
        .filter((value): value is string => Boolean(value))
        .join(" ");
    }
    case "damage_dealt":
      return `${labelEntityRef(event.payload.target_entity_id)} takes ${event.payload.amount} total damage.`;
    case "hp_changed":
      return `${labelEntityRef(event.payload.entity_id)} HP ${event.payload.old} -> ${event.payload.new}.`;
    case "block_changed": {
      const payload = event.payload as BlockChangedPayload;
      return [
        `${labelEntityRef(payload.entity_id)} block ${payload.old} -> ${payload.new}.`,
        payload.base_amount !== undefined ? `Base ${payload.base_amount}.` : undefined,
        payload.modified_amount !== undefined ? `Modified ${payload.modified_amount}.` : undefined,
        payload.final_gain_amount !== undefined ? `Final gain ${payload.final_gain_amount}.` : undefined,
        formatTriggerRef(payload.trigger),
      ]
        .filter((value): value is string => Boolean(value))
        .join(" ");
    }
    case "block_broken":
      return `${labelEntityRef(event.payload.entity_id)} block ${event.payload.old_block} -> ${event.payload.new_block}.`;
    case "block_cleared":
      return `${labelEntityRef(event.payload.entity_id)} block ${event.payload.old_block} -> ${event.payload.new_block}.`;
    case "block_clear_prevented":
      return `${labelEntityRef(event.payload.entity_id)} retains ${event.payload.retained_block} block via ${labelAttribution(event.payload.preventer)}.`;
    case "energy_changed":
      return `${labelEntityRef(event.payload.entity_id)} energy ${event.payload.old} -> ${event.payload.new}.`;
    case "resource_changed":
      return `${humanizeToken(event.payload.resource_id)} ${event.payload.old} -> ${event.payload.new}.`;
    case "power_applied": {
      const payload = event.payload as PowerAppliedPayload;
      return [
        `${payload.power_name ?? payload.power_id} applied to ${labelEntityRef(payload.target_entity_id)} for ${payload.stacks}.`,
        payload.applier ? `Applier ${labelAttribution(payload.applier)}.` : undefined,
        formatTriggerRef(payload.trigger),
      ]
        .filter((value): value is string => Boolean(value))
        .join(" ");
    }
    case "power_removed": {
      const payload = event.payload as PowerRemovedPayload;
      return [
        `${payload.power_name ?? payload.power_id} removed from ${labelEntityRef(payload.target_entity_id)}.`,
        payload.applier ? `Applier ${labelAttribution(payload.applier)}.` : undefined,
        formatTriggerRef(payload.trigger),
      ]
        .filter((value): value is string => Boolean(value))
        .join(" ");
    }
    case "power_stacks_changed": {
      const payload = event.payload as PowerStacksChangedPayload;
      return [
        `${payload.power_name ?? payload.power_id} stacks ${payload.old_stacks} -> ${payload.new_stacks}.`,
        payload.applier ? `Applier ${labelAttribution(payload.applier)}.` : undefined,
        formatTriggerRef(payload.trigger),
      ]
        .filter((value): value is string => Boolean(value))
        .join(" ");
    }
    case "intent_changed": {
      const payload = event.payload as IntentChangedPayload;
      const parts = [
        `${labelEntityRef(payload.entity_id)} intent becomes ${payload.intent_name ?? humanizeToken(payload.intent_id)}.`,
      ];
      if (payload.projected_damage !== undefined) {
        if (payload.projected_hits !== undefined && payload.projected_hits > 1) {
          parts.push(`Projected ${payload.projected_damage}x${payload.projected_hits}.`);
        } else {
          parts.push(`Projected ${payload.projected_damage}.`);
        }
      }
      return parts.join(" ");
    }
    default:
      return "No specialized viewer summary for this event yet.";
  }
}
