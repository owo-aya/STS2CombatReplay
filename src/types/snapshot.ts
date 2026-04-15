import type { OrbStatePayload, RelicStatePayload } from "./events";

export interface SnapshotPower {
  power_id: string;
  stacks: number;
  power_name?: string;
}

export interface SnapshotOrb extends OrbStatePayload {}

export interface SnapshotRelic extends RelicStatePayload {}

export interface SnapshotIntent {
  intent_id: string;
  intent_name?: string;
  projected_damage?: number;
  projected_hits?: number;
}

export interface SnapshotEntity {
  entity_id: string;
  kind: string;
  name?: string;
  hp: number;
  max_hp: number;
  block: number;
  energy?: number;
  resources?: Record<string, number>;
  orb_slots?: number;
  orbs?: SnapshotOrb[];
  relics?: SnapshotRelic[];
  powers: SnapshotPower[];
  intent?: SnapshotIntent;
}

export interface SnapshotCard {
  card_def_id: string;
  card_name?: string;
  owner_entity_id: string;
  zone: string;
  cost?: number;
  current_upgrade_level?: number;
}

export interface SnapshotPotion {
  potion_def_id: string;
  potion_name?: string;
  slot_index: number;
  state: string;
}

export interface Snapshot {
  schema_name: string;
  schema_version: string;
  seq: number;
  turn_index: number;
  phase: string;
  battle_state: {
    active_side: string;
    result?: string;
    winning_side?: string;
  };
  entities: SnapshotEntity[];
  zones: Record<string, string[]>;
  cards: Record<string, SnapshotCard>;
  potions: Record<string, SnapshotPotion>;
}
