import type {
  CardAfflictionPayload,
  CardEnchantmentPayload,
  CardKeywordValue,
  CardVisibleFlagsPayload,
  CombatEvent,
  OrbStatePayload,
  RelicStatePayload,
} from "./events";

export interface IntentState {
  intent_id: string;
  intent_name?: string;
  projected_damage?: number;
  projected_hits?: number;
}

export interface PowerState {
  power_id: string;
  stacks: number;
  power_name?: string;
}

export interface OrbState extends OrbStatePayload {}

export interface RelicState extends RelicStatePayload {}

export interface EntityState {
  entity_id: string;
  entity_def_id?: string;
  name?: string;
  side: string;
  current_hp: number;
  max_hp: number;
  block: number;
  energy: number;
  resources?: Record<string, number>;
  orb_slots?: number;
  orbs?: OrbState[];
  relics?: RelicState[];
  alive: boolean;
  powers: Record<string, PowerState>;
  intent?: IntentState;
}

export interface CardInstanceState {
  card_instance_id: string;
  card_def_id: string;
  card_name?: string;
  owner_entity_id: string;
  zone: string;
  cost?: number;
  star_cost?: number;
  replay_count?: number;
  keywords?: CardKeywordValue[];
  visible_flags?: CardVisibleFlagsPayload;
  enchantment?: CardEnchantmentPayload;
  affliction?: CardAfflictionPayload;
  dynamic_values?: Record<string, number>;
  current_upgrade_level?: number;
  created_this_combat: boolean;
  temporary: boolean;
}

export interface PotionInstanceState {
  potion_instance_id: string;
  potion_def_id: string;
  potion_name?: string;
  slot_index: number;
  origin?: string;
  state: "available" | "used" | "discarded";
}

export type ZoneState = Record<string, string[]>;

export interface BattleState {
  battle_id: string;
  turn_index: number;
  phase: string;
  active_side: string;
  battle_result: string | null;
  ended: boolean;
  entities: Map<string, EntityState>;
  cards: Map<string, CardInstanceState>;
  potions: Map<string, PotionInstanceState>;
  zones: ZoneState;
  events: CombatEvent[];
  last_seq: number;
}

export const DEFAULT_ZONE_NAMES = [
  "draw",
  "hand",
  "discard",
  "play",
  "exhaust",
  "limbo",
  "reveal",
  "void",
  "removed",
  "unknown",
] as const;

export function createInitialState(): BattleState {
  const zones: ZoneState = {};
  for (const zoneName of DEFAULT_ZONE_NAMES) {
    zones[zoneName] = [];
  }

  return {
    battle_id: "",
    turn_index: 0,
    phase: "",
    active_side: "",
    battle_result: null,
    ended: false,
    entities: new Map(),
    cards: new Map(),
    potions: new Map(),
    zones,
    events: [],
    last_seq: -1,
  };
}
