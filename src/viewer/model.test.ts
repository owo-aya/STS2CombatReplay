import { describe, expect, test } from "bun:test";

import type { RootActionMarker } from "./model";
import { createViewerModel, findNextActionStart, findPreviousActionStart } from "./model";

const markers: RootActionMarker[] = [
  {
    index: 0,
    resolution_id: "r_card_001",
    start_seq: 38,
    anchor_seq: 40,
    end_seq: 44,
    label: "r_card_001",
    node: {} as RootActionMarker["node"],
  },
  {
    index: 1,
    resolution_id: "r_card_002",
    start_seq: 45,
    anchor_seq: 47,
    end_seq: 53,
    label: "r_card_002",
    node: {} as RootActionMarker["node"],
  },
  {
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
    expect(findNextActionStart(markers, 44)).toBe(47);
    expect(findNextActionStart(markers, 45)).toBe(47);
    expect(findNextActionStart(markers, 46)).toBe(47);
    expect(findNextActionStart(markers, 47)).toBe(55);
    expect(findNextActionStart(markers, 50)).toBe(55);
    expect(findNextActionStart(markers, 54)).toBe(55);
  });

  test("rewinds to the current action anchor before stepping to the previous action", () => {
    expect(findPreviousActionStart(markers, 50)).toBe(47);
    expect(findPreviousActionStart(markers, 47)).toBe(40);
    expect(findPreviousActionStart(markers, 46)).toBe(40);
    expect(findPreviousActionStart(markers, 54)).toBe(47);
  });

  test("anchors manual player actions to the hand-to-play move", () => {
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
      ] as never,
      snapshots: new Map(),
    });

    expect(model.root_action_markers[0]?.start_seq).toBe(10);
    expect(model.root_action_markers[0]?.anchor_seq).toBe(12);
  });
});
