import type { CombatEvent } from "../types/events";
import type { BattleMetadata } from "../types/metadata";
import type { Snapshot } from "../types/snapshot";

export function parseJsonText<T>(text: string, sourceLabel: string): T {
  try {
    return JSON.parse(text) as T;
  } catch (err) {
    throw new Error(`Failed to parse JSON from ${sourceLabel}: ${err}`);
  }
}

export function parseMetadataText(
  text: string,
  sourceLabel: string,
): BattleMetadata {
  return parseJsonText<BattleMetadata>(text, sourceLabel);
}

export function parseSnapshotText(
  text: string,
  sourceLabel: string,
): Snapshot {
  return parseJsonText<Snapshot>(text, sourceLabel);
}

export function parseEventsText(
  text: string,
  sourceLabel: string,
): CombatEvent[] {
  const lines = text.split("\n").filter((line) => line.trim().length > 0);
  const events: CombatEvent[] = [];

  for (let i = 0; i < lines.length; i++) {
    try {
      events.push(JSON.parse(lines[i]) as CombatEvent);
    } catch (err) {
      throw new Error(
        `Failed to parse line ${i + 1} in ${sourceLabel}: ${err}`,
      );
    }
  }

  events.sort((left, right) => left.seq - right.seq);
  return events;
}

export function parseSnapshotFilename(name: string): number | null {
  const match = name.match(/^(\d+)\.json$/);
  return match ? parseInt(match[1], 10) : null;
}
