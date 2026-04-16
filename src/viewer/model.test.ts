import { describe, expect, test } from "bun:test";

import type { RootActionMarker } from "./model";
import { createViewerModel, findNextActionStart, findPreviousActionStart } from "./model";

const markers: RootActionMarker[] = [
  {
    kind: "root_action",
    index: 0,
    resolution_id: "r_card_001",
    start_seq: 38,
    anchor_seq: 44,
    end_seq: 44,
    label: "r_card_001",
    node: {} as RootActionMarker["node"],
  },
  {
    kind: "root_action",
    index: 1,
    resolution_id: "r_card_002",
    start_seq: 45,
    anchor_seq: 50,
    end_seq: 53,
    label: "r_card_002",
    node: {} as RootActionMarker["node"],
  },
  {
    kind: "root_action",
    index: 2,
    resolution_id: "r_enemy_003",
    start_seq: 55,
    anchor_seq: 55,
    end_seq: 56,
    label: "r_enemy_003",
    node: {} as RootActionMarker["node"],
  },
];

describe("viewer action navigation", () => {
  test("advances to the current action anchor before moving on to the next action", () => {
    expect(findNextActionStart(markers, 35)).toBe(44);
    expect(findNextActionStart(markers, 44)).toBe(50);
    expect(findNextActionStart(markers, 45)).toBe(50);
    expect(findNextActionStart(markers, 46)).toBe(50);
    expect(findNextActionStart(markers, 47)).toBe(50);
    expect(findNextActionStart(markers, 50)).toBe(55);
    expect(findNextActionStart(markers, 54)).toBe(55);
  });

  test("rewinds to the current action anchor before stepping to the previous action", () => {
    expect(findPreviousActionStart(markers, 50)).toBe(44);
    expect(findPreviousActionStart(markers, 47)).toBe(44);
    expect(findPreviousActionStart(markers, 46)).toBe(44);
    expect(findPreviousActionStart(markers, 54)).toBe(50);
  });

  test("anchors manual player actions to card_play_resolved", () => {
    const model = createViewerModel({
      metadata: { battle_id: "battle:test" } as never,
      events: [
        {
          seq: 10,
          event_type: "card_play_started",
          resolution_id: "r_card_001",
          payload: {
            card_instance_id: "card:002",
            actor_entity_id: "player:0",
            source_entity_id: "player:0",
            source_kind: "card",
            target_entity_ids: [],
          },
        },
        {
          seq: 11,
          event_type: "energy_changed",
          resolution_id: "r_card_001",
          payload: {
            entity_id: "player:0",
            old: 3,
            new: 2,
            delta: -1,
            reason: "card_play",
          },
        },
        {
          seq: 12,
          event_type: "card_moved",
          resolution_id: "r_card_001",
          payload: {
            card_instance_id: "card:002",
            from_zone: "hand",
            to_zone: "play",
            reason: "manual_play",
          },
        },
        {
          seq: 13,
          event_type: "block_changed",
          resolution_id: "r_card_001",
          payload: {
            entity_id: "player:0",
            old: 0,
            new: 5,
            delta: 5,
            reason: "block_gain",
          },
        },
        {
          seq: 14,
          event_type: "card_play_resolved",
          resolution_id: "r_card_001",
          payload: {
            card_instance_id: "card:002",
            final_zone: "discard",
          },
        },
      ] as never,
      snapshots: new Map(),
    });

    expect(model.root_action_markers[0]?.start_seq).toBe(10);
    expect(model.root_action_markers[0]?.anchor_seq).toBe(14);
  });

  test("inserts a turn-start action marker before the first player action of the next turn", () => {
    const model = createViewerModel({
      metadata: { battle_id: "battle:test" } as never,
      events: [
        {
          seq: 57,
          event_type: "damage_attempt",
          turn_index: 1,
          phase: "enemy_action",
          resolution_id: "r_enemy_004",
          payload: {
            attempt_id: "atk_00002",
            attempt_group_id: "grp_attack_0002",
            timing_kind: "enemy_action",
            delivery_kind: "attack_root",
            actor_entity_id: "enemy:2",
            settled_target_entity_id: "player:0",
            hp_loss: 3,
            executor: { kind: "enemy_move", ref: "move:enemy:2:SMASH_MOVE" },
          },
        },
        {
          seq: 64,
          event_type: "turn_started",
          turn_index: 2,
          phase: "turn_start",
          payload: {
            turn_index: 2,
            active_side: "player",
            phase: "turn_start",
          },
        },
        {
          seq: 67,
          event_type: "energy_changed",
          turn_index: 2,
          phase: "turn_start",
          payload: {
            entity_id: "player:0",
            old: 0,
            new: 3,
            delta: 3,
            reason: "turn_start",
          },
        },
        {
          seq: 72,
          event_type: "card_moved",
          turn_index: 2,
          phase: "turn_start",
          payload: {
            card_instance_id: "card:010",
            from_zone: "draw",
            to_zone: "hand",
            reason: "draw",
          },
        },
        {
          seq: 73,
          event_type: "card_play_started",
          turn_index: 2,
          phase: "player_action",
          resolution_id: "r_card_005",
          payload: {
            card_instance_id: "card:008",
            actor_entity_id: "player:0",
            source_entity_id: "player:0",
            source_kind: "card",
            target_entity_ids: [],
          },
        },
        {
          seq: 75,
          event_type: "card_moved",
          turn_index: 2,
          phase: "player_action",
          resolution_id: "r_card_005",
          payload: {
            card_instance_id: "card:008",
            from_zone: "hand",
            to_zone: "play",
            reason: "manual_play",
          },
        },
      ] as never,
      snapshots: new Map(),
    });

    expect(model.action_markers.map((marker) => ({
      kind: marker.kind,
      start: marker.start_seq,
      anchor: marker.anchor_seq,
      end: marker.end_seq,
    }))).toEqual([
      { kind: "root_action", start: 57, anchor: 57, end: 57 },
      { kind: "turn_start", start: 64, anchor: 72, end: 72 },
      { kind: "root_action", start: 73, anchor: 75, end: 75 },
    ]);

    expect(findNextActionStart(model.action_markers, 57)).toBe(72);
    expect(findNextActionStart(model.action_markers, 64)).toBe(72);
    expect(findNextActionStart(model.action_markers, 72)).toBe(75);
    expect(findPreviousActionStart(model.action_markers, 75)).toBe(72);
  });
});
