import { buildResolutionForest, type ResolutionNode } from "../parser/resolutionTree";
import { replayFromSnapshot, replayFromStart } from "../parser/replay";
import type { CombatEvent, TurnStartedPayload } from "../types/events";
import type { BattleMetadata } from "../types/metadata";
import type { BattleState } from "../types/state";
import type { Snapshot } from "../types/snapshot";

export interface ViewerBattleData {
  metadata: BattleMetadata;
  events: CombatEvent[];
  snapshots: Map<number, Snapshot>;
}

export interface SnapshotMarker {
  seq: number;
  snapshot: Snapshot;
}

export interface TurnStartMarker {
  seq: number;
  turn_index: number;
  active_side: string;
  phase: string;
}

export interface RootActionMarker {
  index: number;
  resolution_id: string;
  start_seq: number;
  anchor_seq: number;
  end_seq: number;
  label: string;
  node: ResolutionNode;
}

export interface ViewerBattleModel extends ViewerBattleData {
  event_seqs: number[];
  event_index_by_seq: Map<number, number>;
  resolution_forest: ResolutionNode[];
  resolution_path_by_seq: Map<number, ResolutionNode[]>;
  root_action_by_seq: Map<number, RootActionMarker>;
  snapshot_markers: SnapshotMarker[];
  turn_start_markers: TurnStartMarker[];
  root_action_markers: RootActionMarker[];
}

export interface ViewerFrame {
  seq: number;
  event_index: number;
  event: CombatEvent;
  state: BattleState;
  resolution_path: ResolutionNode[];
  current_root_action?: RootActionMarker;
  source_snapshot_seq?: number;
}

interface SeqBounds {
  start_seq: number;
  end_seq: number;
}

function computeNodeBounds(node: ResolutionNode): SeqBounds {
  let startSeq = node.trigger?.event_seq ?? Number.MAX_SAFE_INTEGER;
  let endSeq = node.trigger?.event_seq ?? Number.MIN_SAFE_INTEGER;

  for (const event of node.events) {
    startSeq = Math.min(startSeq, event.seq);
    endSeq = Math.max(endSeq, event.seq);
  }

  for (const child of node.children) {
    const childBounds = computeNodeBounds(child);
    startSeq = Math.min(startSeq, childBounds.start_seq);
    endSeq = Math.max(endSeq, childBounds.end_seq);
  }

  if (startSeq === Number.MAX_SAFE_INTEGER) {
    startSeq = node.trigger?.event_seq ?? -1;
  }
  if (endSeq === Number.MIN_SAFE_INTEGER) {
    endSeq = node.trigger?.event_seq ?? -1;
  }

  return { start_seq: startSeq, end_seq: endSeq };
}

function collectResolutionPaths(
  node: ResolutionNode,
  path: ResolutionNode[],
  pathBySeq: Map<number, ResolutionNode[]>,
): void {
  const nextPath = [...path, node];
  for (const event of node.events) {
    pathBySeq.set(event.seq, nextPath);
  }
  for (const child of node.children) {
    collectResolutionPaths(child, nextPath, pathBySeq);
  }
}

function collectNodeEvents(node: ResolutionNode): CombatEvent[] {
  const events = [...node.events];
  for (const child of node.children) {
    events.push(...collectNodeEvents(child));
  }

  events.sort((left, right) => left.seq - right.seq);
  return events;
}

function pickActionAnchorSeq(node: ResolutionNode, bounds: SeqBounds): number {
  const events = collectNodeEvents(node);

  for (const event of events) {
    if (event.event_type !== "card_moved") {
      continue;
    }

    const payload = event.payload as {
      from_zone?: string;
      to_zone?: string;
      reason?: string;
    };
    if (
      payload.from_zone === "hand" &&
      payload.to_zone === "play" &&
      payload.reason === "manual_play"
    ) {
      return event.seq;
    }
  }

  for (const event of events) {
    if (event.event_type !== "card_moved") {
      continue;
    }

    const payload = event.payload as {
      to_zone?: string;
    };
    if (payload.to_zone === "play") {
      return event.seq;
    }
  }

  for (const event of events) {
    if (event.event_type === "card_play_started" || event.event_type === "potion_used") {
      return event.seq;
    }
  }

  return bounds.start_seq;
}

function buildRootActionMarkers(
  roots: ResolutionNode[],
): RootActionMarker[] {
  return roots
    .map((node, index) => {
      const bounds = computeNodeBounds(node);

      return {
        index,
        resolution_id: node.resolution_id,
        start_seq: bounds.start_seq,
        anchor_seq: pickActionAnchorSeq(node, bounds),
        end_seq: bounds.end_seq,
        label: node.resolution_id,
        node,
      };
    })
    .sort((left, right) => left.start_seq - right.start_seq)
    .map((marker, index) => ({ ...marker, index }));
}

export function createViewerModel(data: ViewerBattleData): ViewerBattleModel {
  const eventSeqs = data.events.map((event) => event.seq);
  const eventIndexBySeq = new Map<number, number>();
  data.events.forEach((event, index) => {
    eventIndexBySeq.set(event.seq, index);
  });

  const resolutionForest = buildResolutionForest(data.events);
  const resolutionPathBySeq = new Map<number, ResolutionNode[]>();
  for (const root of resolutionForest) {
    collectResolutionPaths(root, [], resolutionPathBySeq);
  }

  const rootActionMarkers = buildRootActionMarkers(resolutionForest);
  const rootActionBySeq = new Map<number, RootActionMarker>();
  for (const marker of rootActionMarkers) {
    const stack = [marker.node];
    while (stack.length > 0) {
      const current = stack.pop()!;
      for (const event of current.events) {
        rootActionBySeq.set(event.seq, marker);
      }
      stack.push(...current.children);
    }
  }

  const snapshotMarkers = [...data.snapshots.entries()]
    .sort(([left], [right]) => left - right)
    .map(([seq, snapshot]) => ({ seq, snapshot }));

  const turnStartMarkers = data.events
    .filter((event): event is CombatEvent & { event_type: "turn_started" } => {
      return event.event_type === "turn_started";
    })
    .map((event) => {
      const payload = event.payload as TurnStartedPayload;
      return {
        seq: event.seq,
        turn_index: payload.turn_index,
        active_side: payload.active_side,
        phase: payload.phase ?? event.phase ?? "",
      };
    });

  return {
    ...data,
    event_seqs: eventSeqs,
    event_index_by_seq: eventIndexBySeq,
    resolution_forest: resolutionForest,
    resolution_path_by_seq: resolutionPathBySeq,
    root_action_by_seq: rootActionBySeq,
    snapshot_markers: snapshotMarkers,
    turn_start_markers: turnStartMarkers,
    root_action_markers: rootActionMarkers,
  };
}

function findSourceSnapshotSeq(
  snapshots: Map<number, Snapshot>,
  targetSeq: number,
): number | undefined {
  let bestSeq: number | undefined;
  for (const seq of snapshots.keys()) {
    if (seq <= targetSeq && (bestSeq === undefined || seq > bestSeq)) {
      bestSeq = seq;
    }
  }
  return bestSeq;
}

export function getFrameAtSeq(
  model: ViewerBattleModel,
  targetSeq: number,
): ViewerFrame {
  const eventIndex = model.event_index_by_seq.get(targetSeq);
  if (eventIndex === undefined) {
    throw new Error(`No event found at seq ${targetSeq}`);
  }

  const sourceSnapshotSeq = findSourceSnapshotSeq(model.snapshots, targetSeq);
  const relevantEvents = model.events.filter((event) => event.seq <= targetSeq);

  const state =
    sourceSnapshotSeq !== undefined
      ? replayFromSnapshot(
          model.snapshots.get(sourceSnapshotSeq)!,
          relevantEvents,
          model.metadata.battle_id,
        )
      : replayFromStart(relevantEvents, model.metadata.battle_id);

  return {
    seq: targetSeq,
    event_index: eventIndex,
    event: model.events[eventIndex],
    state,
    resolution_path: model.resolution_path_by_seq.get(targetSeq) ?? [],
    current_root_action: model.root_action_by_seq.get(targetSeq),
    source_snapshot_seq: sourceSnapshotSeq,
  };
}

export function findPreviousSeq(
  seqs: number[],
  currentSeq: number,
): number | undefined {
  let previous: number | undefined;
  for (const seq of seqs) {
    if (seq >= currentSeq) {
      break;
    }
    previous = seq;
  }
  return previous;
}

export function findNextSeq(
  seqs: number[],
  currentSeq: number,
): number | undefined {
  for (const seq of seqs) {
    if (seq > currentSeq) {
      return seq;
    }
  }
  return undefined;
}

function findContainingRootActionIndex(
  markers: RootActionMarker[],
  currentSeq: number,
): number {
  for (let index = 0; index < markers.length; index++) {
    const marker = markers[index];
    if (currentSeq >= marker.start_seq && currentSeq <= marker.end_seq) {
      return index;
    }
  }

  return -1;
}

export function findPreviousActionStart(
  markers: RootActionMarker[],
  currentSeq: number,
): number | undefined {
  if (markers.length === 0) {
    return undefined;
  }

  const containingIndex = findContainingRootActionIndex(markers, currentSeq);
  if (containingIndex !== -1) {
    const current = markers[containingIndex];
    if (currentSeq > current.anchor_seq) {
      return current.anchor_seq;
    }

    return markers[Math.max(0, containingIndex - 1)]?.anchor_seq;
  }

  let previousIndex = -1;
  for (let index = 0; index < markers.length; index++) {
    if (markers[index].start_seq >= currentSeq) {
      break;
    }
    previousIndex = index;
  }

  if (previousIndex === -1) {
    return markers[0]?.anchor_seq;
  }

  return markers[previousIndex]?.anchor_seq;
}

export function findNextActionStart(
  markers: RootActionMarker[],
  currentSeq: number,
): number | undefined {
  if (markers.length === 0) {
    return undefined;
  }

  const containingIndex = findContainingRootActionIndex(markers, currentSeq);
  if (containingIndex !== -1) {
    const current = markers[containingIndex];
    if (currentSeq < current.anchor_seq) {
      return current.anchor_seq;
    }

    return markers[containingIndex + 1]?.anchor_seq;
  }

  for (const marker of markers) {
    if (marker.anchor_seq > currentSeq) {
      return marker.anchor_seq;
    }
  }

  return undefined;
}
