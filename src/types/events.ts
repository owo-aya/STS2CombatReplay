// ─── Base Event ─────────────────────────────────────────────────────────────

export interface BaseEvent {
  seq: number;
  event_type: string;
  turn_index?: number;
  phase?: string;
  relative_ms?: number;
  action_id?: string;
  cause_event_seq?: number;
  resolution_id?: string;
  parent_resolution_id?: string;
  resolution_depth?: number;
  payload: unknown;
}

// ─── Payload Types ──────────────────────────────────────────────────────────

export interface BattleStartedPayload {
  encounter_id: string;
  player_entity_id: string;
  enemy_entity_ids: string[];
  encounter_name?: string;
}

export interface BattleEndedPayload {
  result: string;
  winning_side?: string;
  reason?: string;
}

export interface TurnStartedPayload {
  turn_index: number;
  active_side: string;
  phase?: string;
}

export interface EntitySpawnedPayload {
  entity_id: string;
  side: string;
  reason?: string;
  entity_def_id?: string;
  name?: string;
  current_hp?: number;
  max_hp?: number;
  block?: number;
  energy?: number;
  resources?: Record<string, number>;
  orb_slots?: number;
  orbs?: OrbStatePayload[];
  trigger?: AttributionRef;
}

export interface EntityDiedPayload {
  entity_id: string;
  reason?: string;
  trigger?: AttributionRef;
}

export interface EntityRemovedPayload {
  entity_id: string;
  reason?: string;
  trigger?: AttributionRef;
}

export interface EntityRevivedPayload {
  entity_id: string;
  current_hp?: number;
  reason?: string;
  trigger?: AttributionRef;
}

export interface CardCreatedPayload {
  card_instance_id: string;
  card_def_id: string;
  owner_entity_id: string;
  initial_zone: string;
  card_name?: string;
  cost?: number;
  current_upgrade_level?: number;
  created_this_combat?: boolean;
  temporary?: boolean;
  trigger?: AttributionRef;
}

export interface PotionInitializedPayload {
  potion_instance_id: string;
  potion_def_id: string;
  slot_index: number;
  potion_name?: string;
  origin?: string;
}

export interface PotionCreatedPayload {
  potion_instance_id: string;
  potion_def_id: string;
  slot_index: number;
  potion_name?: string;
  origin?: string;
}

export interface PotionUsedPayload {
  potion_instance_id: string;
  actor_entity_id: string;
  source_entity_id: string;
  source_kind: string;
  slot_index: number;
  target_entity_ids: string[];
  potion_def_id?: string;
  potion_name?: string;
  source_potion_instance_id?: string;
  target_mode?: string;
}

export interface PotionDiscardedPayload {
  potion_instance_id: string;
  slot_index: number;
  potion_def_id?: string;
  potion_name?: string;
}

export interface CardMovedPayload {
  card_instance_id: string;
  from_zone: string;
  to_zone: string;
  reason: string;
  card_def_id?: string;
  card_name?: string;
  owner_entity_id?: string;
  from_index?: number;
  to_index?: number;
  visible_to_player?: boolean;
  trigger?: AttributionRef;
}

export interface CardExhaustedPayload {
  card_instance_id: string;
  from_zone?: string;
  trigger?: AttributionRef;
}

export interface CardPlayStartedPayload {
  card_instance_id: string;
  actor_entity_id: string;
  source_entity_id: string;
  source_kind: string;
  target_entity_ids: string[];
  card_def_id?: string;
  source_card_instance_id?: string;
  energy_cost_paid?: number;
}

export interface CardPlayResolvedPayload {
  card_instance_id: string;
  final_zone?: string;
  resolution_outcome?: string;
}

export interface CardModifiedPayload {
  card_instance_id: string;
  card_name?: string;
  current_upgrade_level?: number;
  changes: {
    cost?: {
      old: number;
      new: number;
    };
    upgraded?: {
      old: boolean;
      new: boolean;
    };
    upgrade_level?: {
      old: number;
      new: number;
    };
  };
  reason?: string;
  trigger?: AttributionRef;
}

export type RelicStatusValue = "normal" | "active" | "disabled";

export interface RelicStatePayload {
  relic_instance_id: string;
  relic_id: string;
  owner_entity_id: string;
  relic_name?: string;
  stack_count: number;
  status: RelicStatusValue;
  display_amount?: number;
  is_used_up: boolean;
  is_wax: boolean;
  is_melted: boolean;
}

export interface RelicInitializedPayload extends RelicStatePayload {}

export interface RelicObtainedPayload extends RelicStatePayload {
  trigger?: AttributionRef;
}

export interface RelicRemovedPayload extends RelicStatePayload {
  trigger?: AttributionRef;
}

export interface RelicTriggeredPayload {
  relic_instance_id: string;
  relic_id: string;
  owner_entity_id: string;
  relic_name?: string;
  target_entity_ids?: string[];
  trigger?: AttributionRef;
}

export interface RelicDisplayAmountModifiedPayload extends RelicStatePayload {
  change_kind: "display_amount";
  old_display_amount?: number;
  new_display_amount?: number;
  trigger?: AttributionRef;
}

export interface RelicStatusModifiedPayload extends RelicStatePayload {
  change_kind: "status";
  old_status: RelicStatusValue;
  new_status: RelicStatusValue;
  trigger?: AttributionRef;
}

export interface RelicStackCountModifiedPayload extends RelicStatePayload {
  change_kind: "stack_count";
  old_stack_count: number;
  new_stack_count: number;
  trigger?: AttributionRef;
}

export interface RelicFlagModifiedPayload extends RelicStatePayload {
  change_kind: "flag";
  flag: "is_used_up" | "is_wax" | "is_melted";
  old_value: boolean;
  new_value: boolean;
  trigger?: AttributionRef;
}

export type RelicModifiedPayload =
  | RelicDisplayAmountModifiedPayload
  | RelicStatusModifiedPayload
  | RelicStackCountModifiedPayload
  | RelicFlagModifiedPayload;

export interface OrbStatePayload {
  orb_instance_id: string;
  orb_id: string;
  owner_entity_id: string;
  slot_index: number;
  orb_name?: string;
  passive?: number;
  evoke?: number;
}

export interface OrbInsertedPayload extends OrbStatePayload {
  reason: "channel" | "replace";
  trigger?: AttributionRef;
}

export interface OrbEvokedPayload extends OrbStatePayload {
  dequeued: boolean;
  target_entity_ids?: string[];
  trigger?: AttributionRef;
}

export interface OrbRemovedPayload extends OrbStatePayload {
  reason: "evoke" | "replace" | "capacity_trim" | "combat_end_cleanup";
  trigger?: AttributionRef;
}

export interface OrbPassiveTriggeredPayload extends OrbStatePayload {
  timing: "before_turn_end" | "after_turn_start" | "manual";
  trigger?: AttributionRef;
}

export interface OrbModifiedPayload extends OrbStatePayload {
  changes: {
    passive?: {
      old: number;
      new: number;
    };
    evoke?: {
      old: number;
      new: number;
    };
  };
  reason: "power_changed" | "internal_state_changed";
  trigger?: AttributionRef;
}

export interface OrbSlotsChangedPayload {
  entity_id: string;
  old_slots: number;
  new_slots: number;
  delta: number;
  reason: "add_slots" | "remove_slots" | "auto_add_for_channel";
  trigger?: AttributionRef;
}

export interface TriggerFiredPayload {
  trigger_type: string;
  source_resolution_id: string;
  triggered_resolution_id?: string;
  subject_card_instance_id?: string;
  subject_entity_id?: string;
}

export interface DamageDealtPayload {
  target_entity_id: string;
  amount: number;
  source_entity_id?: string;
  source_kind?: string;
  source_card_instance_id?: string;
  source_potion_instance_id?: string;
  source_resolution_id?: string;
  damage_kind?: string;
  blocked?: number;
  hp_loss?: number;
}

export interface AttributionRef {
  kind:
    | "entity"
    | "card_instance"
    | "potion_instance"
    | "enemy_move"
    | "orb_instance"
    | "power_instance"
    | "relic"
    | "generic_model"
    | "unknown";
  ref: string;
  entity_id?: string;
  owner_entity_id?: string;
  model_id?: string;
  model_type?: string;
  card_instance_id?: string;
  potion_instance_id?: string;
  orb_instance_id?: string;
  orb_id?: string;
  orb_name?: string;
  slot_index?: number;
  power_instance_id?: string;
  power_id?: string;
  power_name?: string;
  applier_entity_id?: string;
  relic_id?: string;
  relic_instance_id?: string;
  move_id?: string;
  origin?: AttributionRef;
  unknown_reason?: string;
}

export type DamageSourceRef = AttributionRef;

export interface DamageAttemptStep {
  stage: string;
  operation: string;
  before?: number;
  after?: number;
  delta?: number;
  modifier_ref?: DamageSourceRef;
  target_entity_id?: string;
  is_unknown?: boolean;
  unknown_reason?: string;
  overkill_amount?: number;
}

export interface DamageAttemptPayload {
  attempt_id: string;
  attempt_group_id: string;
  parent_attempt_id?: string;
  timing_kind?: string;
  delivery_kind: "attack_root" | "effect_damage";
  actor_entity_id?: string;
  original_target_entity_id: string;
  settled_target_entity_id: string;
  base_amount: number;
  damage_after_damage_stage: number;
  blocked_amount: number;
  hp_loss: number;
  overkill_amount: number;
  final_settled_damage: number;
  bypasses_block: boolean;
  is_powered_damage: boolean;
  is_move_damage: boolean;
  was_fully_blocked: boolean;
  was_block_broken: boolean;
  target_died: boolean;
  target_hp_before?: number;
  target_hp_after?: number;
  target_block_before?: number;
  target_block_after?: number;
  redirected?: boolean;
  original_target_hp_before?: number;
  original_target_hp_after?: number;
  original_target_block_before?: number;
  original_target_block_after?: number;
  executor: DamageSourceRef;
  trigger?: DamageSourceRef;
  unknown_flags?: string[];
  steps: DamageAttemptStep[];
}

export interface BlockGainStep {
  stage: string;
  operation: string;
  before?: number;
  after?: number;
  delta?: number;
  modifier_ref?: AttributionRef;
  is_unknown?: boolean;
  unknown_reason?: string;
}

export interface HpChangedPayload {
  entity_id: string;
  old: number;
  new: number;
  delta: number;
  reason?: string;
}

export interface BlockChangedPayload {
  entity_id: string;
  old: number;
  new: number;
  delta: number;
  reason?: string;
  trigger?: AttributionRef;
  base_amount?: number;
  modified_amount?: number;
  final_gain_amount?: number;
  steps?: BlockGainStep[];
}

export interface BlockBrokenPayload {
  entity_id: string;
  old_block: number;
  new_block: number;
  trigger?: AttributionRef;
}

export interface BlockClearedPayload {
  entity_id: string;
  old_block: number;
  new_block: number;
  trigger?: AttributionRef;
}

export interface BlockClearPreventedPayload {
  entity_id: string;
  retained_block: number;
  preventer: AttributionRef;
}

export interface EnergyChangedPayload {
  entity_id: string;
  old: number;
  new: number;
  delta: number;
  reason?: string;
  trigger?: AttributionRef;
}

export interface ResourceChangedPayload {
  entity_id: string;
  resource_id: string;
  old: number;
  new: number;
  delta: number;
  reason?: string;
  trigger?: AttributionRef;
}

export interface PowerAppliedPayload {
  target_entity_id: string;
  power_id: string;
  stacks: number;
  power_name?: string;
  applier?: AttributionRef;
  trigger?: AttributionRef;
}

export interface PowerRemovedPayload {
  target_entity_id: string;
  power_id: string;
  stacks?: number;
  power_name?: string;
  applier?: AttributionRef;
  trigger?: AttributionRef;
}

export interface PowerStacksChangedPayload {
  target_entity_id: string;
  power_id: string;
  old_stacks: number;
  new_stacks: number;
  delta: number;
  power_name?: string;
  applier?: AttributionRef;
  trigger?: AttributionRef;
}

export interface IntentChangedPayload {
  entity_id: string;
  intent_id: string;
  intent_name?: string;
  projected_damage?: number;
  projected_hits?: number;
}

// ─── Discriminated Union ────────────────────────────────────────────────────

export type CombatEvent =
  | (BaseEvent & { event_type: "battle_started"; payload: BattleStartedPayload })
  | (BaseEvent & { event_type: "battle_ended"; payload: BattleEndedPayload })
  | (BaseEvent & { event_type: "turn_started"; payload: TurnStartedPayload })
  | (BaseEvent & { event_type: "entity_spawned"; payload: EntitySpawnedPayload })
  | (BaseEvent & { event_type: "entity_died"; payload: EntityDiedPayload })
  | (BaseEvent & { event_type: "entity_removed"; payload: EntityRemovedPayload })
  | (BaseEvent & { event_type: "entity_revived"; payload: EntityRevivedPayload })
  | (BaseEvent & { event_type: "card_created"; payload: CardCreatedPayload })
  | (BaseEvent & { event_type: "potion_initialized"; payload: PotionInitializedPayload })
  | (BaseEvent & { event_type: "potion_created"; payload: PotionCreatedPayload })
  | (BaseEvent & { event_type: "potion_used"; payload: PotionUsedPayload })
  | (BaseEvent & { event_type: "potion_discarded"; payload: PotionDiscardedPayload })
  | (BaseEvent & { event_type: "card_moved"; payload: CardMovedPayload })
  | (BaseEvent & { event_type: "card_exhausted"; payload: CardExhaustedPayload })
  | (BaseEvent & { event_type: "card_play_started"; payload: CardPlayStartedPayload })
  | (BaseEvent & { event_type: "card_play_resolved"; payload: CardPlayResolvedPayload })
  | (BaseEvent & { event_type: "card_modified"; payload: CardModifiedPayload })
  | (BaseEvent & { event_type: "relic_initialized"; payload: RelicInitializedPayload })
  | (BaseEvent & { event_type: "relic_obtained"; payload: RelicObtainedPayload })
  | (BaseEvent & { event_type: "relic_removed"; payload: RelicRemovedPayload })
  | (BaseEvent & { event_type: "relic_triggered"; payload: RelicTriggeredPayload })
  | (BaseEvent & { event_type: "relic_modified"; payload: RelicModifiedPayload })
  | (BaseEvent & { event_type: "orb_inserted"; payload: OrbInsertedPayload })
  | (BaseEvent & { event_type: "orb_evoked"; payload: OrbEvokedPayload })
  | (BaseEvent & { event_type: "orb_removed"; payload: OrbRemovedPayload })
  | (BaseEvent & { event_type: "orb_passive_triggered"; payload: OrbPassiveTriggeredPayload })
  | (BaseEvent & { event_type: "orb_modified"; payload: OrbModifiedPayload })
  | (BaseEvent & { event_type: "orb_slots_changed"; payload: OrbSlotsChangedPayload })
  | (BaseEvent & { event_type: "trigger_fired"; payload: TriggerFiredPayload })
  | (BaseEvent & { event_type: "damage_attempt"; payload: DamageAttemptPayload })
  | (BaseEvent & { event_type: "damage_dealt"; payload: DamageDealtPayload })
  | (BaseEvent & { event_type: "hp_changed"; payload: HpChangedPayload })
  | (BaseEvent & { event_type: "block_changed"; payload: BlockChangedPayload })
  | (BaseEvent & { event_type: "block_broken"; payload: BlockBrokenPayload })
  | (BaseEvent & { event_type: "block_cleared"; payload: BlockClearedPayload })
  | (BaseEvent & { event_type: "block_clear_prevented"; payload: BlockClearPreventedPayload })
  | (BaseEvent & { event_type: "energy_changed"; payload: EnergyChangedPayload })
  | (BaseEvent & { event_type: "resource_changed"; payload: ResourceChangedPayload })
  | (BaseEvent & { event_type: "power_applied"; payload: PowerAppliedPayload })
  | (BaseEvent & { event_type: "power_removed"; payload: PowerRemovedPayload })
  | (BaseEvent & { event_type: "power_stacks_changed"; payload: PowerStacksChangedPayload })
  | (BaseEvent & { event_type: "intent_changed"; payload: IntentChangedPayload });

// ─── Event Type Registry ────────────────────────────────────────────────────

export const M1_EVENT_TYPES: readonly string[] = [
  "battle_started",
  "battle_ended",
  "turn_started",
  "entity_spawned",
  "entity_died",
  "entity_removed",
  "entity_revived",
  "card_created",
  "potion_initialized",
  "potion_created",
  "potion_used",
  "potion_discarded",
  "card_moved",
  "card_exhausted",
  "card_play_started",
  "card_play_resolved",
  "card_modified",
  "relic_initialized",
  "relic_obtained",
  "relic_removed",
  "relic_triggered",
  "relic_modified",
  "orb_inserted",
  "orb_evoked",
  "orb_removed",
  "orb_passive_triggered",
  "orb_modified",
  "orb_slots_changed",
  "trigger_fired",
  "damage_attempt",
  "damage_dealt",
  "hp_changed",
  "block_changed",
  "block_broken",
  "block_cleared",
  "block_clear_prevented",
  "energy_changed",
  "resource_changed",
  "power_applied",
  "power_removed",
  "power_stacks_changed",
  "intent_changed",
];

// ─── Type Guard ─────────────────────────────────────────────────────────────

export function isKnownEventType(event: BaseEvent): event is CombatEvent {
  return (M1_EVENT_TYPES as readonly string[]).includes(event.event_type);
}
