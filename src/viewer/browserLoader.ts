import { parseEventsText, parseMetadataText, parseSnapshotFilename, parseSnapshotText } from "../parser/parse";
import type { Snapshot } from "../types/snapshot";
import type { ViewerBattleData } from "./model";

interface BrowserBattleFile {
  path: string;
  file: File;
}

function normalizeRelativePath(path: string): string {
  const normalized = path.replace(/\\/g, "/");
  const segments = normalized.split("/").filter((segment) => segment.length > 0);
  if (segments.length <= 1) {
    return segments[0] ?? normalized;
  }
  return segments.slice(1).join("/");
}

function collectBattleFiles(files: Iterable<File>): BrowserBattleFile[] {
  const result: BrowserBattleFile[] = [];

  for (const file of files) {
    const relativePath =
      typeof file.webkitRelativePath === "string" && file.webkitRelativePath.length > 0
        ? file.webkitRelativePath
        : file.name;
    result.push({
      path: normalizeRelativePath(relativePath),
      file,
    });
  }

  return result;
}

export async function loadBattleFromBrowserFiles(
  files: Iterable<File>,
): Promise<ViewerBattleData> {
  const battleFiles = collectBattleFiles(files);
  const metadataFile = battleFiles.find((entry) => entry.path === "metadata.json")?.file;
  const eventsFile = battleFiles.find((entry) => entry.path === "events.ndjson")?.file;

  if (!metadataFile || !eventsFile) {
    throw new Error(
      "Selected folder must contain metadata.json and events.ndjson at the battle container root.",
    );
  }

  const metadata = parseMetadataText(
    await metadataFile.text(),
    metadataFile.webkitRelativePath || metadataFile.name,
  );
  const events = parseEventsText(
    await eventsFile.text(),
    eventsFile.webkitRelativePath || eventsFile.name,
  );

  const snapshotEntries = battleFiles
    .filter((entry) => entry.path.startsWith("snapshots/"))
    .map((entry) => {
      const snapshotName = entry.path.slice("snapshots/".length);
      return {
        seq: parseSnapshotFilename(snapshotName),
        file: entry.file,
      };
    })
    .filter((entry): entry is { seq: number; file: File } => entry.seq !== null)
    .sort((left, right) => left.seq - right.seq);

  const snapshots = new Map<number, Snapshot>();
  for (const entry of snapshotEntries) {
    snapshots.set(
      entry.seq,
      parseSnapshotText(
        await entry.file.text(),
        entry.file.webkitRelativePath || entry.file.name,
      ),
    );
  }

  return { metadata, events, snapshots };
}

export function deserializeSnapshotMap(
  snapshots: Record<string, Snapshot>,
): Map<number, Snapshot> {
  return new Map(
    Object.entries(snapshots)
      .map(([seq, snapshot]) => [parseInt(seq, 10), snapshot] as const)
      .sort(([left], [right]) => left - right),
  );
}
