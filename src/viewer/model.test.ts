import { describe, expect, test } from "bun:test";

import type { RootActionMarker } from "./model";
import { findNextActionStart, findPreviousActionStart } from "./model";

const markers: RootActionMarker[] = [
  {
    index: 0,
    resolution_id: "r_card_001",
    start_seq: 38,
    end_seq: 44,
    label: "r_card_001",
    node: {} as RootActionMarker["node"],
  },
  {
    index: 1,
    resolution_id: "r_card_002",
    start_seq: 45,
    end_seq: 53,
    label: "r_card_002",
    node: {} as RootActionMarker["node"],
  },
  {
    index: 2,
    resolution_id: "r_enemy_003",
    start_seq: 55,
    end_seq: 56,
    label: "r_enemy_003",
    node: {} as RootActionMarker["node"],
  },
];

describe("viewer action navigation", () => {
  test("advances to the next action start instead of the current action end", () => {
    expect(findNextActionStart(markers, 44)).toBe(45);
    expect(findNextActionStart(markers, 45)).toBe(55);
    expect(findNextActionStart(markers, 50)).toBe(55);
    expect(findNextActionStart(markers, 54)).toBe(55);
  });

  test("rewinds to the current action start before stepping to the previous action", () => {
    expect(findPreviousActionStart(markers, 50)).toBe(45);
    expect(findPreviousActionStart(markers, 45)).toBe(38);
    expect(findPreviousActionStart(markers, 54)).toBe(45);
  });
});
