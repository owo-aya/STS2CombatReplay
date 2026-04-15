import type { CombatEvent } from "../types/events";
import type { BattleMetadata } from "../types/metadata";
import type { Snapshot } from "../types/snapshot";
import {
  parseEventsText,
  parseMetadataText,
  parseSnapshotFilename,
  parseSnapshotText,
} from "./parse";

const SNAPSHOTS_DIR = "snapshots";
const METADATA_FILE = "metadata.json";
const EVENTS_FILE = "events.ndjson";

async function readTextFile(filePath: string): Promise<string> {
  const file = Bun.file(filePath);
  const exists = await file.exists();
  if (!exists) {
    throw new Error(`File not found: ${filePath}`);
  }
  return await file.text();
}

export async function loadMetadata(dirPath: string): Promise<BattleMetadata> {
  const filePath = `${dirPath}/${METADATA_FILE}`;
  return parseMetadataText(await readTextFile(filePath), filePath);
}

export async function loadEvents(dirPath: string): Promise<CombatEvent[]> {
  const filePath = `${dirPath}/${EVENTS_FILE}`;
  return parseEventsText(await readTextFile(filePath), filePath);
}

export async function loadSnapshots(
  dirPath: string
): Promise<Map<number, Snapshot>> {
  const snapshotsDir = `${dirPath}/${SNAPSHOTS_DIR}`;
  let entries: string[];
  try {
    entries = await Array.fromAsync(
      new Bun.Glob("*.json").scan({ cwd: snapshotsDir })
    );
  } catch {
    throw new Error(`Snapshots directory not found: ${snapshotsDir}`);
  }

  const result = new Map<number, Snapshot>();

  for (const name of entries) {
    const seq = parseSnapshotFilename(name);
    if (seq === null) {
      continue;
    }

    const filePath = `${snapshotsDir}/${name}`;
    const snapshot = parseSnapshotText(await readTextFile(filePath), filePath);
    result.set(seq, snapshot);
  }

  return result;
}

export type { BattleMetadata } from "../types/metadata";

export async function loadBattleDir(dirPath: string): Promise<{
  metadata: BattleMetadata;
  events: CombatEvent[];
  snapshots: Map<number, Snapshot>;
}> {
  const [metadata, events, snapshots] = await Promise.all([
    loadMetadata(dirPath),
    loadEvents(dirPath),
    loadSnapshots(dirPath),
  ]);

  return { metadata, events, snapshots };
}
