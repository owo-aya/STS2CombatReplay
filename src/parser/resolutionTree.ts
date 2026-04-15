import type { CombatEvent, TriggerFiredPayload } from "../types/events";

export interface ResolutionTriggerLink {
  event_seq: number;
  cause_event_seq?: number;
  trigger_type: string;
  source_resolution_id: string;
  triggered_resolution_id?: string;
  subject_card_instance_id?: string;
  subject_entity_id?: string;
}

export interface ResolutionNode {
  resolution_id: string;
  parent_resolution_id?: string;
  resolution_depth?: number;
  events: CombatEvent[];
  children: ResolutionNode[];
  trigger?: ResolutionTriggerLink;
}

function ensureNode(
  nodes: Map<string, ResolutionNode>,
  resolutionId: string,
): ResolutionNode {
  let node = nodes.get(resolutionId);
  if (!node) {
    node = {
      resolution_id: resolutionId,
      events: [],
      children: [],
    };
    nodes.set(resolutionId, node);
  }
  return node;
}

function getNodeSortSeq(node: ResolutionNode): number {
  const eventSeq = node.events[0]?.seq;
  if (eventSeq !== undefined) return eventSeq;
  const triggerSeq = node.trigger?.event_seq;
  if (triggerSeq !== undefined) return triggerSeq;
  return Number.MAX_SAFE_INTEGER;
}

function sortTree(nodes: ResolutionNode[]): void {
  nodes.sort((a, b) => getNodeSortSeq(a) - getNodeSortSeq(b));
  for (const node of nodes) {
    sortTree(node.children);
  }
}

export function buildResolutionForest(events: CombatEvent[]): ResolutionNode[] {
  const nodes = new Map<string, ResolutionNode>();

  for (const event of events) {
    if (event.resolution_id) {
      const node = ensureNode(nodes, event.resolution_id);
      node.events.push(event);
      if (!node.parent_resolution_id && event.parent_resolution_id) {
        node.parent_resolution_id = event.parent_resolution_id;
      }
      if (node.resolution_depth === undefined && event.resolution_depth !== undefined) {
        node.resolution_depth = event.resolution_depth;
      }
    }

    if (event.event_type === "trigger_fired") {
      const payload = event.payload as TriggerFiredPayload;
      if (!payload.triggered_resolution_id) continue;

      const childNode = ensureNode(nodes, payload.triggered_resolution_id);
      childNode.trigger = {
        event_seq: event.seq,
        cause_event_seq: event.cause_event_seq,
        trigger_type: payload.trigger_type,
        source_resolution_id: payload.source_resolution_id,
        triggered_resolution_id: payload.triggered_resolution_id,
        subject_card_instance_id: payload.subject_card_instance_id,
        subject_entity_id: payload.subject_entity_id,
      };

      if (!childNode.parent_resolution_id) {
        childNode.parent_resolution_id =
          event.resolution_id ?? payload.source_resolution_id;
      }
      if (childNode.resolution_depth === undefined && childNode.parent_resolution_id) {
        childNode.resolution_depth = 1;
      }
    }
  }

  const roots: ResolutionNode[] = [];
  const attached = new Set<string>();

  for (const node of nodes.values()) {
    const parentId = node.parent_resolution_id ?? node.trigger?.source_resolution_id;
    if (parentId && parentId !== node.resolution_id) {
      const parent = nodes.get(parentId);
      if (parent) {
        parent.children.push(node);
        attached.add(node.resolution_id);
      }
    }
  }

  for (const node of nodes.values()) {
    if (!attached.has(node.resolution_id)) {
      roots.push(node);
    }
  }

  sortTree(roots);
  return roots;
}
