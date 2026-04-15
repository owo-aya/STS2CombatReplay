export type CompatStatus = "verified" | "unverified" | "unsupported" | "unknown";

export type BattleContainerCompletionState =
  | "initialized"
  | "active"
  | "completed"
  | "partial"
  | "failed_finalize";

export interface BattleMetadata {
  schema_name: string;
  protocol_version?: string;
  schema_version: string;
  mod_version?: string;
  battle_id: string;
  game: {
    title: string;
    channel?: string | null;
    version?: string | null;
    build: string | null;
    sts2_dll_hash?: string | null;
  };
  compat?: {
    status: CompatStatus;
    warning_codes: string[];
    warning_messages: string[];
  };
  recorder: { name: string; version: string };
  container?: {
    completion_state: BattleContainerCompletionState;
  };
  battle: {
    character_id: string;
    character_name?: string;
    encounter_id: string;
    encounter_name?: string;
    seed: string | null;
    started_at: string;
    ended_at: string | null;
    result: string | null;
  };
}
