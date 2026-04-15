import { loadBattleDir } from "../parser/loader";
import { buildResolutionForest } from "../parser/resolutionTree";
import { replayFromStart, replayFromSnapshot } from "../parser/replay";
import { formatState, formatEventLog, formatResolutionTree } from "./format";

async function main(): Promise<void> {
  const args = process.argv.slice(2);

  if (args.length === 0) {
    process.stderr.write("Usage: bun run src/inspector/cli.ts <battle-dir> [--seq N] [--snapshot <seq>] [--events] [--tree]\n");
    process.exit(1);
  }

  const battleDir = args[0];

  let seqLimit: number | null = null;
  let snapshotSeq: number | null = null;
  let showEvents = false;
  let showTree = false;

  for (let i = 1; i < args.length; i++) {
    if (args[i] === "--seq" && i + 1 < args.length) {
      seqLimit = parseInt(args[i + 1], 10);
      i++;
    } else if (args[i] === "--snapshot" && i + 1 < args.length) {
      snapshotSeq = parseInt(args[i + 1], 10);
      i++;
    } else if (args[i] === "--events") {
      showEvents = true;
    } else if (args[i] === "--tree") {
      showTree = true;
    }
  }

  const { metadata, events, snapshots } = await loadBattleDir(battleDir);

  if (showEvents) {
    let filtered = events;
    if (seqLimit !== null) {
      filtered = events.filter((e) => e.seq <= seqLimit);
    }
    process.stdout.write(formatEventLog(filtered) + "\n");
    return;
  }

  if (showTree) {
    let filtered = events;
    if (seqLimit !== null) {
      filtered = events.filter((e) => e.seq <= seqLimit);
    }
    const tree = buildResolutionForest(filtered);
    process.stdout.write(formatResolutionTree(tree) + "\n");
    return;
  }

  let state;

  if (snapshotSeq !== null) {
    const snapshot = snapshots.get(snapshotSeq);
    if (!snapshot) {
      process.stderr.write(`No snapshot found at seq ${snapshotSeq}\n`);
      process.exit(1);
    }
    let relevantEvents = events.filter((e) => e.seq > snapshotSeq);
    if (seqLimit !== null) {
      relevantEvents = relevantEvents.filter((e) => e.seq <= seqLimit);
    }
    state = replayFromSnapshot(snapshot, relevantEvents, metadata.battle_id);
  } else {
    let relevantEvents = events;
    if (seqLimit !== null) {
      relevantEvents = events.filter((e) => e.seq <= seqLimit);
    }
    state = replayFromStart(relevantEvents, metadata.battle_id);
  }

  process.stdout.write(formatState(state) + "\n");
}

main().catch((err: unknown) => {
  const message = err instanceof Error ? err.message : String(err);
  process.stderr.write(`Error: ${message}\n`);
  process.exit(1);
});
