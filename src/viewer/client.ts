import type { Snapshot } from "../types/snapshot";
import type { DamageAttemptPayload, PowerAppliedPayload } from "../types/events";
import type {
  BattleState,
  CardInstanceState,
  EntityState,
  PowerState,
  RelicState,
} from "../types/state";
import { deserializeSnapshotMap, loadBattleFromBrowserFiles } from "./browserLoader";
import {
  createViewerModel,
  findContainingActionMarkerIndex,
  findNextActionStart,
  findNextSeq,
  findPreviousActionStart,
  findPreviousSeq,
  getFrameAtSeq,
  type ViewerBattleData,
  type ViewerBattleModel,
  type ViewerFrame,
} from "./model";
import {
  buildBattleOverview,
  buildKeyMoments,
  buildStepSummary,
  collectViewerAlerts,
  formatActionMarkerLabel,
  formatActionLabel,
  formatIntent,
  labelCard,
  labelEntity,
  labelOrb,
  labelPotion,
  labelRelic,
  type BattleOverview,
  type KeyMoment,
  type ViewerAlert,
} from "./presenter";

interface SerializedBattleData {
  metadata: ViewerBattleData["metadata"];
  events: ViewerBattleData["events"];
  snapshots: Record<string, Snapshot>;
}

interface FixtureDescriptor {
  id: string;
  label: string;
  note?: string;
}

interface LoadedBattleState {
  model: ViewerBattleModel;
  overview: BattleOverview;
  keyMoments: KeyMoment[];
  alerts: ViewerAlert[];
  sourceLabel: string;
  actionMarkerLabels: string[];
  rootActionLabels: string[];
}

interface ViewerState {
  loaded?: LoadedBattleState;
  currentSeq?: number;
  status: string;
  error?: string;
  loading: boolean;
  isPlaying: boolean;
  playMode: "action" | "event";
  playSpeedMs: number;
  playTimer?: ReturnType<typeof window.setInterval>;
  fixtures: FixtureDescriptor[];
  expandedZones: Set<string>;
  rawEventExpanded: boolean;
  summaryExpanded: boolean;
  scrollPositions: Map<string, { top: number; left: number }>;
  transitionTimer?: ReturnType<typeof window.setTimeout>;
  postRenderAnimationFrame?: number;
  isActionTransitioning: boolean;
  leftPanelCollapsed: boolean;
  rightPanelCollapsed: boolean;
}

const root = document.getElementById("app");
if (!root) {
  throw new Error("Viewer root element not found.");
}

const state: ViewerState = {
  status: "Open a battle folder or load a sample fixture.",
  loading: false,
  isPlaying: false,
  playMode: "action",
  playSpeedMs: 900,
  fixtures: [],
  expandedZones: new Set<string>(),
  rawEventExpanded: false,
  summaryExpanded: false,
  scrollPositions: new Map<string, { top: number; left: number }>(),
  isActionTransitioning: false,
  leftPanelCollapsed: false,
  rightPanelCollapsed: false,
};

const NORMAL_PLAY_SPEED_MS = 900;
const MIN_PLAYBACK_SPEED_MULTIPLIER = 0.5;
const MAX_PLAYBACK_SPEED_MULTIPLIER = 5;
const PLAYBACK_SPEED_STEP = 0.25;

function escapeHtml(value: string): string {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;")
    .replaceAll("'", "&#39;");
}

function getApiUrl(relativePath: string): string {
  return new URL(relativePath, window.location.href).toString();
}

function normalizeFixtureDescriptor(value: unknown): FixtureDescriptor | null {
  if (typeof value === "string") {
    return {
      id: value,
      label: value.replaceAll(/[_-]+/g, " "),
    };
  }

  if (
    typeof value === "object" &&
    value !== null &&
    typeof (value as { id?: unknown }).id === "string"
  ) {
    return {
      id: (value as { id: string }).id,
      label:
        typeof (value as { label?: unknown }).label === "string"
          ? ((value as { label: string }).label)
          : (value as { id: string }).id.replaceAll(/[_-]+/g, " "),
      note:
        typeof (value as { note?: unknown }).note === "string"
          ? (value as { note: string }).note
          : undefined,
    };
  }

  return null;
}

async function loadFixtures(): Promise<void> {
  try {
    const response = await fetch(getApiUrl("./api/fixtures"));
    if (!response.ok) {
      return;
    }

    const payload = await response.json();
    const items = Array.isArray(payload)
      ? payload
      : Array.isArray(payload.fixtures)
        ? payload.fixtures
        : [];
    state.fixtures = items
      .map(normalizeFixtureDescriptor)
      .filter((entry): entry is FixtureDescriptor => entry !== null);
    render();
  } catch {
    state.fixtures = [];
  }
}

function stopPlayback(): void {
  if (state.playTimer !== undefined) {
    window.clearInterval(state.playTimer);
    state.playTimer = undefined;
  }
  stopActionTransition();
  stopPostRenderAnimationFrame();
  state.isPlaying = false;
}

function stopActionTransition(): void {
  if (state.transitionTimer !== undefined) {
    window.clearTimeout(state.transitionTimer);
    state.transitionTimer = undefined;
  }
  state.isActionTransitioning = false;
}

function stopPostRenderAnimationFrame(): void {
  if (state.postRenderAnimationFrame !== undefined) {
    window.cancelAnimationFrame(state.postRenderAnimationFrame);
    state.postRenderAnimationFrame = undefined;
  }
}

function getInitialSeq(model: ViewerBattleModel): number {
  return (
    model.action_markers[0]?.anchor_seq ??
    model.turn_start_markers[0]?.seq ??
    model.snapshot_markers[0]?.seq ??
    model.event_seqs[0]
  );
}

function createLoadedState(data: ViewerBattleData, sourceLabel: string): LoadedBattleState {
  const model = createViewerModel(data);
  return {
    model,
    overview: buildBattleOverview(model, sourceLabel),
    keyMoments: buildKeyMoments(model),
    alerts: collectViewerAlerts(model.metadata),
    sourceLabel,
    actionMarkerLabels: model.action_markers.map((marker) => formatActionMarkerLabel(marker)),
    rootActionLabels: model.root_action_markers.map((marker) => formatActionLabel(marker)),
  };
}

function applyLoadedBattle(data: ViewerBattleData, sourceLabel: string): void {
  stopPlayback();
  stopActionTransition();
  stopPostRenderAnimationFrame();
  const loaded = createLoadedState(data, sourceLabel);
  state.loaded = loaded;
  state.currentSeq = getInitialSeq(loaded.model);
  state.error = undefined;
  state.scrollPositions.clear();
  state.expandedZones.clear();
  state.rawEventExpanded = false;
  state.summaryExpanded = false;
  state.status = `Loaded ${sourceLabel} · ${loaded.model.events.length} events`;
  render();
}

async function withLoading<T>(task: () => Promise<T>): Promise<T | undefined> {
  state.loading = true;
  state.error = undefined;
  render();

  try {
    return await task();
  } catch (error) {
    state.error = error instanceof Error ? error.message : String(error);
    stopPlayback();
    render();
    return undefined;
  } finally {
    state.loading = false;
    render();
  }
}

async function loadSample(id: string): Promise<void> {
  await withLoading(async () => {
    const response = await fetch(getApiUrl(`./api/fixtures/${encodeURIComponent(id)}`));
    if (!response.ok) {
      throw new Error(`Failed to load fixture ${id}.`);
    }

    const payload = (await response.json()) as SerializedBattleData;
    applyLoadedBattle(
      {
        metadata: payload.metadata,
        events: payload.events,
        snapshots: deserializeSnapshotMap(payload.snapshots),
      },
      `Sample ${id}`,
    );
  });
}

async function loadFolderFiles(files: Iterable<File>, sourceLabel: string): Promise<void> {
  await withLoading(async () => {
    const data = await loadBattleFromBrowserFiles(files);
    applyLoadedBattle(data, sourceLabel);
  });
}

async function collectFilesFromDirectoryHandle(
  handle: any,
  rootName: string,
  prefix = "",
): Promise<File[]> {
  const collected: File[] = [];

  for await (const [name, entry] of handle.entries()) {
    const relativePath = prefix.length > 0 ? `${prefix}/${name}` : name;
    if (entry.kind === "directory") {
      collected.push(...(await collectFilesFromDirectoryHandle(entry, rootName, relativePath)));
      continue;
    }

    const source = await entry.getFile();
    const file = new File([await source.arrayBuffer()], source.name, {
      type: source.type,
      lastModified: source.lastModified,
    });
    Object.defineProperty(file, "webkitRelativePath", {
      configurable: true,
      value: `${rootName}/${relativePath}`,
    });
    collected.push(file);
  }

  return collected;
}

async function openDirectoryPicker(): Promise<void> {
  const picker = (window as Window & { showDirectoryPicker?: () => Promise<any> }).showDirectoryPicker;
  if (!picker) {
    const input = document.getElementById("battle-folder-input") as HTMLInputElement | null;
    input?.click();
    return;
  }

  await withLoading(async () => {
    const handle = await picker();
    const files = await collectFilesFromDirectoryHandle(handle, handle.name);
    const data = await loadBattleFromBrowserFiles(files);
    applyLoadedBattle(data, `Folder ${handle.name}`);
  });
}

function getCurrentFrame(): ViewerFrame | undefined {
  if (!state.loaded || state.currentSeq === undefined) {
    return undefined;
  }
  return getFrameAtSeq(state.loaded.model, state.currentSeq);
}

function setCurrentSeq(nextSeq: number | undefined): void {
  if (!state.loaded || nextSeq === undefined) {
    return;
  }

  if (!state.loaded.model.event_index_by_seq.has(nextSeq)) {
    return;
  }

  stopActionTransition();
  state.currentSeq = nextSeq;
  render();
}

function getSeqsBetween(currentSeq: number, targetSeq: number): number[] {
  if (!state.loaded || targetSeq <= currentSeq) {
    return [];
  }

  return state.loaded.model.event_seqs.filter((seq) => seq > currentSeq && seq <= targetSeq);
}

function getPlaybackPaceMultiplier(): number {
  return Math.max(0.12, state.playSpeedMs / NORMAL_PLAY_SPEED_MS);
}

function clampPlaybackSpeedMultiplier(value: number): number {
  if (!Number.isFinite(value)) {
    return 1;
  }

  return Math.min(
    MAX_PLAYBACK_SPEED_MULTIPLIER,
    Math.max(MIN_PLAYBACK_SPEED_MULTIPLIER, value),
  );
}

function getPlaybackSpeedMultiplierValue(): number {
  return NORMAL_PLAY_SPEED_MS / state.playSpeedMs;
}

function formatPlaybackSpeedMultiplier(value: number): string {
  const rounded = Math.round(value * 100) / 100;
  if (Math.abs(rounded - Math.round(rounded)) < 0.001) {
    return `${Math.round(rounded)}`;
  }
  if (Math.abs(rounded * 2 - Math.round(rounded * 2)) < 0.001) {
    return rounded.toFixed(1);
  }
  return rounded.toFixed(2);
}

function applyPlaybackSpeedMultiplier(value: number): void {
  const nextMultiplier = clampPlaybackSpeedMultiplier(value);
  const nextPlaySpeedMs = Math.max(90, Math.round(NORMAL_PLAY_SPEED_MS / nextMultiplier));
  if (nextPlaySpeedMs === state.playSpeedMs) {
    return;
  }

  state.playSpeedMs = nextPlaySpeedMs;
  if (state.isPlaying) {
    if (state.playTimer !== undefined) {
      window.clearInterval(state.playTimer);
    }
    state.playTimer = window.setInterval(advancePlayback, state.playSpeedMs);
  }
  render();
}

function getTransitionDelayForEventSeq(seq: number): number {
  if (!state.loaded) {
    return 150;
  }

  const index = state.loaded.model.event_index_by_seq.get(seq);
  const event = index !== undefined ? state.loaded.model.events[index] : undefined;
  let baseDelay = 120;

  switch (event?.event_type) {
    case "card_play_started":
      baseDelay = 180;
      break;
    case "card_moved": {
      const payload = event.payload as {
        reason?: string;
        from_zone?: string;
        to_zone?: string;
      };

      if (payload.reason === "zone_sync") {
        baseDelay =
          payload.from_zone === "discard" && payload.to_zone === "draw"
            ? 24
            : 34;
        break;
      }

      if (payload.reason === "draw") {
        baseDelay = 52;
        break;
      }

      if (payload.reason === "discard") {
        baseDelay = 56;
        break;
      }

      if (payload.reason === "resolve_play") {
        baseDelay = 88;
        break;
      }

      if (payload.to_zone === "play") {
        baseDelay = 210;
        break;
      }

      baseDelay = 90;
      break;
    }
    case "damage_attempt":
    case "hp_changed":
    case "block_changed":
    case "block_broken":
      baseDelay = 185;
      break;
    case "power_applied":
    case "power_removed":
      baseDelay = 155;
      break;
    case "card_play_resolved":
    case "turn_started":
      baseDelay = 110;
      break;
    default:
      baseDelay = 120;
      break;
  }

  return Math.max(18, Math.round(baseDelay * getPlaybackPaceMultiplier()));
}

function playActionTransitionTo(targetSeq: number): void {
  if (!state.loaded || state.currentSeq === undefined) {
    return;
  }

  const seqs = getSeqsBetween(state.currentSeq, targetSeq);
  if (seqs.length === 0) {
    setCurrentSeq(targetSeq);
    return;
  }

  stopActionTransition();
  state.isActionTransitioning = true;

  const advance = (): void => {
    const nextSeq = seqs.shift();
    if (nextSeq === undefined) {
      stopActionTransition();
      return;
    }

    state.currentSeq = nextSeq;
    render();

    if (seqs.length === 0) {
      stopActionTransition();
      return;
    }

    state.transitionTimer = window.setTimeout(advance, getTransitionDelayForEventSeq(nextSeq));
  };

  advance();
}

function getMarkerIndexBySeq(values: number[], seq: number): number {
  let currentIndex = -1;
  for (let index = 0; index < values.length; index++) {
    if (values[index] <= seq) {
      currentIndex = index;
    } else {
      break;
    }
  }
  return currentIndex;
}

function getVisibleWindow<T>(
  items: T[],
  activeIndex: number,
  before: number,
  after: number,
): { items: T[]; start: number; end: number } {
  if (items.length === 0) {
    return { items: [], start: 0, end: 0 };
  }

  const safeIndex = Math.max(0, Math.min(items.length - 1, activeIndex));
  const start = Math.max(0, safeIndex - before);
  const end = Math.min(items.length, safeIndex + after + 1);
  return {
    items: items.slice(start, end),
    start,
    end,
  };
}

function toggleZone(zoneName: string): void {
  if (state.expandedZones.has(zoneName)) {
    state.expandedZones.delete(zoneName);
  } else {
    state.expandedZones.add(zoneName);
  }
  render();
}

function toggleRawEvent(): void {
  state.rawEventExpanded = !state.rawEventExpanded;
  render();
}

function toggleSummary(): void {
  state.summaryExpanded = !state.summaryExpanded;
  render();
}

function togglePanel(side: "left" | "right"): void {
  if (side === "left") {
    state.leftPanelCollapsed = !state.leftPanelCollapsed;
  } else {
    state.rightPanelCollapsed = !state.rightPanelCollapsed;
  }
  render();
}

function captureScrollPositions(): void {
  const elements = root.querySelectorAll<HTMLElement>("[data-scroll-key]");
  for (const element of elements) {
    const key = element.dataset.scrollKey;
    if (!key) {
      continue;
    }
    state.scrollPositions.set(key, {
      top: element.scrollTop,
      left: element.scrollLeft,
    });
  }
}

function restoreScrollPositions(): void {
  const elements = root.querySelectorAll<HTMLElement>("[data-scroll-key]");
  for (const element of elements) {
    const key = element.dataset.scrollKey;
    if (!key) {
      continue;
    }
    const position = state.scrollPositions.get(key);
    if (!position) {
      continue;
    }
    element.scrollTop = position.top;
    element.scrollLeft = position.left;
  }
}

function stepEvent(direction: "prev" | "next"): void {
  if (!state.loaded || state.currentSeq === undefined) {
    return;
  }

  const seq =
    direction === "prev"
      ? findPreviousSeq(state.loaded.model.event_seqs, state.currentSeq)
      : findNextSeq(state.loaded.model.event_seqs, state.currentSeq);
  setCurrentSeq(seq);
}

function stepAction(direction: "prev" | "next"): void {
  if (!state.loaded || state.currentSeq === undefined) {
    return;
  }

  const nextSeq =
    direction === "prev"
      ? findPreviousActionStart(state.loaded.model.action_markers, state.currentSeq)
      : findNextActionStart(state.loaded.model.action_markers, state.currentSeq);

  if (nextSeq === undefined) {
    return;
  }

  if (direction === "next" && nextSeq > state.currentSeq) {
    playActionTransitionTo(nextSeq);
    return;
  }

  setCurrentSeq(nextSeq);
}

function stepTurn(direction: "prev" | "next"): void {
  if (!state.loaded || state.currentSeq === undefined) {
    return;
  }

  const turnTargets = state.loaded.model.turn_start_markers.map((marker) => marker.seq);
  if (turnTargets.length === 0) {
    return;
  }

  const currentIndex = getMarkerIndexBySeq(turnTargets, state.currentSeq);
  if (direction === "prev") {
    const targetIndex = currentIndex <= 0 ? 0 : currentIndex - 1;
    setCurrentSeq(turnTargets[targetIndex]);
    return;
  }

  const targetIndex = currentIndex < 0 ? 0 : Math.min(turnTargets.length - 1, currentIndex + 1);
  if (targetIndex === currentIndex && state.currentSeq === turnTargets[targetIndex]) {
    return;
  }
  setCurrentSeq(turnTargets[targetIndex]);
}

function advancePlayback(): void {
  if (!state.loaded || state.currentSeq === undefined) {
    stopPlayback();
    render();
    return;
  }

  if (state.isActionTransitioning) {
    return;
  }

  const current = state.currentSeq;
  const nextSeq =
    state.playMode === "event"
      ? findNextSeq(state.loaded.model.event_seqs, current)
      : findNextActionStart(state.loaded.model.action_markers, current);

  if (nextSeq === undefined) {
    stopPlayback();
    render();
    return;
  }

  if (state.playMode === "action" && nextSeq > current) {
    playActionTransitionTo(nextSeq);
    return;
  }

  state.currentSeq = nextSeq;
  render();
}

function togglePlayback(): void {
  if (state.isPlaying) {
    stopPlayback();
    render();
    return;
  }

  if (!state.loaded) {
    return;
  }

  stopPlayback();
  state.isPlaying = true;
  state.playTimer = window.setInterval(advancePlayback, state.playSpeedMs);
  render();
}

function renderBadges(badges: BattleOverview["badges"]): string {
  if (badges.length === 0) {
    return "";
  }

  return `<div class="badge-row">${badges
    .map(
      (badge) =>
        `<span class="badge tone-${escapeHtml(badge.tone)}">${escapeHtml(badge.label)}</span>`,
    )
    .join("")}</div>`;
}

function renderStats(stats: BattleOverview["stats"]): string {
  return `<div class="stat-row">${stats
    .map(
      (stat) =>
        `<span class="stat-pill"><strong>${escapeHtml(stat.value)}</strong>&nbsp;${escapeHtml(stat.label)}</span>`,
    )
    .join("")}</div>`;
}

function renderCompactSummaryMeta(loaded: LoadedBattleState): string {
  const summaryParts = [
    loaded.overview.subtitle,
    ...loaded.overview.stats.map((stat) => `${stat.value} ${stat.label}`),
  ];
  return `<div class="summary-inline-meta">${summaryParts
    .map((part) => `<span class="summary-inline-pill">${escapeHtml(part)}</span>`)
    .join("")}</div>`;
}

function renderWarnings(alerts: ViewerAlert[]): string {
  if (alerts.length === 0) {
    return "";
  }

  return `<section class="warning-stack">${alerts
    .map(
      (alert) => `<article class="surface warning-card tone-${escapeHtml(alert.tone)}">
        <h3>${escapeHtml(alert.title)}</h3>
        <p>${escapeHtml(alert.message)}</p>
      </article>`,
    )
    .join("")}</section>`;
}

function renderPanelDock(): string {
  if (!state.leftPanelCollapsed && !state.rightPanelCollapsed) {
    return "";
  }

  const buttons: string[] = [];
  if (state.leftPanelCollapsed) {
    buttons.push(
      `<button class="panel-dock-button" data-command="toggle-left-panel">Show Navigation</button>`,
    );
  }
  if (state.rightPanelCollapsed) {
    buttons.push(
      `<button class="panel-dock-button" data-command="toggle-right-panel">Show Inspector</button>`,
    );
  }

  return `<div class="panel-dock">
    <div class="panel-dock-label">Panels Hidden</div>
    <div class="panel-dock-actions">${buttons.join("")}</div>
  </div>`;
}

function getBarPercent(current: number, max: number): number {
  if (max <= 0) {
    return 0;
  }
  return Math.max(0, Math.min(100, (current / max) * 100));
}

function renderPowerChips(powers: Record<string, PowerState>): string {
  const entries = Object.values(powers).sort((left, right) => {
    const leftLabel = left.power_name ?? left.power_id;
    const rightLabel = right.power_name ?? right.power_id;
    return leftLabel.localeCompare(rightLabel);
  });
  const visibleEntries = entries.slice(0, 4);

  if (entries.length === 0) {
    return `<span class="mini-pill">No powers</span>`;
  }

  return visibleEntries
    .map(
      (power) =>
        `<span class="mini-pill">${escapeHtml(power.power_name ?? power.power_id)} ${escapeHtml(
          String(power.stacks),
        )}</span>`,
    )
    .concat(
      entries.length > visibleEntries.length
        ? [`<span class="mini-pill">+${escapeHtml(String(entries.length - visibleEntries.length))} more</span>`]
        : [],
    )
    .join("");
}

function renderOrbChips(orbs: EntityState["orbs"]): string {
  if (!orbs || orbs.length === 0) {
    return `<span class="mini-pill">No orbs</span>`;
  }

  return orbs
    .map(
      (orb) =>
        `<span class="mini-pill">${escapeHtml(labelOrb(orb))} · slot ${escapeHtml(
          String(orb.slot_index),
        )}</span>`,
    )
    .join("");
}

function renderRelicChips(relics: RelicState[] | undefined): string {
  if (!relics || relics.length === 0) {
    return `<span class="mini-pill">No relics</span>`;
  }

  return relics
    .map(
      (relic) =>
        `<span class="mini-pill">${escapeHtml(labelRelic(relic))}${
          relic.stack_count > 1 ? ` · ${escapeHtml(String(relic.stack_count))}` : ""
        }</span>`,
    )
    .join("");
}

function renderResourcePills(entity: EntityState): string {
  const extras = Object.entries(entity.resources ?? {}).map(
    ([resourceId, amount]) =>
      `<span class="mini-pill">${escapeHtml(resourceId)} ${escapeHtml(String(amount))}</span>`,
  );

  return [
    entity.block > 0
      ? `<span class="mini-pill is-block">Block ${escapeHtml(String(entity.block))}</span>`
      : "",
    `<span class="mini-pill is-energy">Energy ${escapeHtml(String(entity.energy ?? 0))}</span>`,
    ...extras,
    !entity.alive ? `<span class="mini-pill is-dead">Dead</span>` : "",
  ]
    .filter(Boolean)
    .join("");
}

function renderEnemyCard(enemy: EntityState): string {
  return `<article class="enemy-card ${enemy.alive ? "" : "is-dead"}" data-entity="${escapeHtml(enemy.entity_id)}">
    <div class="enemy-top">
      <div>
        <div class="enemy-name">${escapeHtml(labelEntity(enemy))}</div>
        <div class="enemy-subline mono">${escapeHtml(enemy.entity_id)}</div>
      </div>
      <span class="intent-chip">${escapeHtml(formatIntent(enemy.intent))}</span>
    </div>
    <div class="hp-bar"><div class="hp-fill" style="width:${getBarPercent(
      enemy.current_hp,
      enemy.max_hp,
    )}%"></div></div>
    <div class="hp-copy">${escapeHtml(String(enemy.current_hp))}/${escapeHtml(
      String(enemy.max_hp),
    )} HP</div>
    <div class="mini-row">${renderResourcePills(enemy)}</div>
    <div class="mini-row">${renderPowerChips(enemy.powers)}</div>
  </article>`;
}

function renderPlayerCard(player: EntityState): string {
  return `<article class="actor-card player-card ${player.alive ? "" : "is-dead"}" data-entity="${escapeHtml(player.entity_id)}">
    <div class="actor-top">
      <div>
        <div class="actor-name">${escapeHtml(labelEntity(player))}</div>
        <div class="actor-subline mono">${escapeHtml(player.entity_id)}</div>
      </div>
      <span class="intent-chip">${escapeHtml(player.alive ? "Player" : "Defeated")}</span>
    </div>
    <div class="hp-bar"><div class="hp-fill" style="width:${getBarPercent(
      player.current_hp,
      player.max_hp,
    )}%"></div></div>
    <div class="hp-copy">${escapeHtml(String(player.current_hp))}/${escapeHtml(
      String(player.max_hp),
    )} HP</div>
    <div class="mini-row">${renderResourcePills(player)}</div>
    <div class="mini-row">${renderPowerChips(player.powers)}</div>
  </article>`;
}

function renderCardTags(card: CardInstanceState): string {
  const tags: string[] = [];
  if (card.created_this_combat) {
    tags.push("Created");
  }
  if (card.temporary) {
    tags.push("Temporary");
  }
  if (card.current_upgrade_level !== undefined && card.current_upgrade_level > 0) {
    tags.push(`Upgrade ${card.current_upgrade_level}`);
  }

  if (tags.length === 0) {
    tags.push(card.zone);
  }

  return tags.map((tag) => `<span class="tag-chip">${escapeHtml(tag)}</span>`).join("");
}

function renderHand(frame: ViewerFrame): string {
  const handIds = frame.state.zones.hand ?? [];
  if (handIds.length === 0) {
    return `<div class="hand-empty">Hand is empty at this step.</div>`;
  }

  return `<div class="hand-grid" data-scroll-key="board-hand">${handIds
    .map((cardId) => {
      const card = frame.state.cards.get(cardId);
      if (!card) {
        return `<article class="hand-card" data-card="${escapeHtml(cardId)}">
          <div class="card-cost">?</div>
          <div class="card-title">${escapeHtml(cardId)}</div>
          <div class="card-meta mono">${escapeHtml(cardId)}</div>
          <div class="chip-row"><span class="tag-chip">Unknown card</span></div>
        </article>`;
      }

      return `<article class="hand-card" data-card="${escapeHtml(card.card_instance_id)}">
        <div class="card-cost">${escapeHtml(card.cost !== undefined ? String(card.cost) : "?")}</div>
        <div class="card-title">${escapeHtml(labelCard(card))}</div>
        <div class="card-meta">${escapeHtml(card.card_def_id)}<br /><span class="mono">${escapeHtml(
          card.card_instance_id,
        )}</span></div>
        <div class="chip-row">${renderCardTags(card)}</div>
      </article>`;
    })
    .join("")}</div>`;
}

function getZonePreview(cardIds: string[], cards: Map<string, CardInstanceState>): string {
  if (cardIds.length === 0) {
    return "Empty.";
  }

  const preview = cardIds
    .slice(0, 3)
    .map((cardId) => labelCard(cards.get(cardId), cardId))
    .join(", ");
  const remainder = cardIds.length > 3 ? ` +${cardIds.length - 3} more` : "";
  return `${preview}${remainder}.`;
}

function renderZoneContents(
  zoneName: string,
  cardIds: string[],
  cards: Map<string, CardInstanceState>,
): string {
  if (cardIds.length === 0) {
    return "";
  }

  return `<div class="pile-detail">
    <div class="pile-detail-label">${escapeHtml(zoneName === "draw" ? "Top to Bottom" : "Current Order")}</div>
    <div class="pile-list" data-scroll-key="pile-${escapeHtml(zoneName)}">${cardIds
      .map((cardId, index) => {
        const card = cards.get(cardId);
        return `<div class="pile-item">
          <span class="pile-index mono">${escapeHtml(String(index + 1))}</span>
          <span>${escapeHtml(labelCard(card, cardId))}</span>
        </div>`;
      })
      .join("")}</div>
  </div>`;
}

function renderZoneGrid(frame: ViewerFrame): string {
  const orderedZoneNames = ["draw", "discard", "exhaust", "play", "limbo", "reveal", "void", "removed", "unknown"];
  const zoneEntries = orderedZoneNames
    .map((zoneName) => ({
      zoneName,
      cards: frame.state.zones[zoneName] ?? [],
    }))
    .filter((entry) => entry.cards.length > 0 || ["draw", "discard", "play", "exhaust"].includes(entry.zoneName));

  return `<section class="zone-grid">${zoneEntries
    .map((entry) => {
      const isExpanded = state.expandedZones.has(entry.zoneName);
      const isExpandable = entry.cards.length > 0;
      return `<button
        class="zone-card zone-card-button ${isExpanded ? "is-expanded" : ""}"
        data-zone-card="${escapeHtml(entry.zoneName)}"
        ${isExpandable ? `data-command="toggle-zone" data-zone="${escapeHtml(entry.zoneName)}"` : "disabled"}
      >
        <h3 class="zone-title">${escapeHtml(entry.zoneName)}</h3>
        <div class="zone-copy">${escapeHtml(String(entry.cards.length))} cards</div>
        <div class="zone-copy">${escapeHtml(getZonePreview(entry.cards, frame.state.cards))}</div>
        <div class="zone-hint">${escapeHtml(
          isExpandable ? (isExpanded ? "Click to collapse" : "Click to expand") : "No cards",
        )}</div>
        ${isExpanded ? renderZoneContents(entry.zoneName, entry.cards, frame.state.cards) : ""}
      </button>`;
    })
    .join("")}</section>`;
}

function renderResourceCards(frame: ViewerFrame): string {
  const player = [...frame.state.entities.values()].find((entity) => entity.side === "player");
  const potionEntries = [...frame.state.potions.values()].sort(
    (left, right) => left.slot_index - right.slot_index,
  );

  return `<section class="resource-grid">
    <article class="resource-card">
      <h3 class="resource-title">Battle Resources</h3>
      <div class="resource-section">
        <div class="resource-section-label">Potions</div>
        <div class="chip-row">${
          potionEntries.length > 0
            ? potionEntries
                .map(
                  (potion) =>
                    `<span class="mini-pill">${escapeHtml(labelPotion(potion))} · slot ${escapeHtml(
                      String(potion.slot_index),
                    )} · ${escapeHtml(potion.state)}</span>`,
                )
                .join("")
            : `<span class="mini-pill">No potions</span>`
        }</div>
      </div>
      <div class="resource-section">
        <div class="resource-section-label">Orbs</div>
        <div class="chip-row">${player ? renderOrbChips(player.orbs) : `<span class="mini-pill">No player</span>`}</div>
      </div>
      <div class="resource-section">
        <div class="resource-section-label">Relics</div>
        <div class="chip-row">${player ? renderRelicChips(player.relics) : `<span class="mini-pill">No player</span>`}</div>
      </div>
    </article>
  </section>`;
}

function renderArena(frame: ViewerFrame): string {
  const entities = [...frame.state.entities.values()];
  const player = entities.find((entity) => entity.side === "player");
  const enemies = entities
    .filter((entity) => entity.side === "enemy")
    .sort((left, right) => left.entity_id.localeCompare(right.entity_id));

  return `<section class="arena">
    <section class="battle-top-grid">
      <div class="actor-stack">
        <section class="enemy-grid">${
          enemies.length > 0
            ? enemies.map(renderEnemyCard).join("")
            : `<article class="enemy-card"><div class="enemy-name">No enemies</div><div class="enemy-subline">This frame has no active enemies.</div></article>`
        }</section>
        ${player ? renderPlayerCard(player) : `<article class="actor-card"><div class="actor-name">No player entity</div></article>`}
        <section class="hand-panel hand-panel-embedded">
          <div class="hand-header">
            <div>
              <h3 class="surface-title">Hand</h3>
              <div class="surface-note">${escapeHtml(String((frame.state.zones.hand ?? []).length))} visible cards</div>
            </div>
            <div class="surface-note mono">Turn ${escapeHtml(String(frame.state.turn_index))}</div>
          </div>
          ${renderHand(frame)}
        </section>
      </div>
      <div class="support-stack">
        ${renderResourceCards(frame)}
        ${renderZoneGrid(frame)}
      </div>
    </section>
  </section>`;
}

function renderTimeline(frame: ViewerFrame, loaded: LoadedBattleState): string {
  const activeActionIndex = Math.max(
    0,
    findContainingActionMarkerIndex(loaded.model.action_markers, frame.seq),
  );
  const visibleWindow = getVisibleWindow(loaded.model.action_markers, activeActionIndex, 2, 3);

  return `<div class="section-note">Showing ${escapeHtml(String(visibleWindow.items.length))} of ${escapeHtml(
    String(loaded.model.action_markers.length),
  )} actions near the current step.</div><div class="timeline-list">${visibleWindow.items
    .map((marker) => {
      const isActive = frame.seq >= marker.start_seq && frame.seq <= marker.end_seq;
      return `<button class="timeline-item ${isActive ? "is-active" : ""}" data-command="jump-seq" data-seq="${marker.anchor_seq}">
        <div class="timeline-overline">Action ${marker.index + 1} · seq ${marker.start_seq}-${marker.end_seq}</div>
        <div class="timeline-title">${escapeHtml(loaded.actionMarkerLabels[marker.index] ?? "Action")}</div>
        <div class="timeline-copy mono">${escapeHtml(
          marker.kind === "root_action" ? marker.resolution_id : `${marker.phase} · ${marker.active_side}`,
        )}</div>
      </button>`;
    })
    .join("")}</div>`;
}

function renderPanelToolbar(
  title: string,
  note: string,
  command: string,
  actionLabel: string,
): string {
  return `<div class="panel-toolbar">
    <div>
      <div class="surface-title">${escapeHtml(title)}</div>
      <div class="surface-note">${escapeHtml(note)}</div>
    </div>
    <button class="panel-toggle-button" data-command="${escapeHtml(command)}">${escapeHtml(actionLabel)}</button>
  </div>`;
}

function renderJumpSections(frame: ViewerFrame, loaded: LoadedBattleState): string {
  const turnSeqs = loaded.model.turn_start_markers.map((marker) => marker.seq);
  const snapshotSeqs = loaded.model.snapshot_markers.map((marker) => marker.seq);

  return `<div class="jump-list">
    <div>
      <div class="surface-header">
        <h2 class="surface-title">Turn Jumps</h2>
        <span class="surface-note">${escapeHtml(String(turnSeqs.length))} markers</span>
      </div>
      <div class="jump-row">${loaded.model.turn_start_markers
        .map((marker) => {
          const isActive = marker.seq === (turnSeqs[getMarkerIndexBySeq(turnSeqs, frame.seq)] ?? -1);
          return `<button class="jump-item ${isActive ? "is-active" : ""}" data-command="jump-seq" data-seq="${marker.seq}">
            <div class="jump-title">Turn ${marker.turn_index}</div>
            <div class="jump-copy">${escapeHtml(marker.active_side)} · seq ${marker.seq}</div>
          </button>`;
        })
        .join("")}</div>
    </div>
    <div>
      <div class="surface-header">
        <h2 class="surface-title">Snapshots</h2>
        <span class="surface-note">${escapeHtml(String(snapshotSeqs.length))} restore points</span>
      </div>
      <div class="jump-row">${loaded.model.snapshot_markers
        .map((marker) => {
          const isActive = marker.seq === frame.source_snapshot_seq;
          return `<button class="jump-item ${isActive ? "is-active" : ""}" data-command="jump-seq" data-seq="${marker.seq}">
            <div class="jump-title">Snapshot ${marker.seq}</div>
            <div class="jump-copy">${escapeHtml(marker.snapshot.phase)} · turn ${escapeHtml(
              String(marker.snapshot.turn_index),
            )}</div>
          </button>`;
        })
        .join("")}</div>
    </div>
  </div>`;
}

function renderStepDetails(frame: ViewerFrame, loaded: LoadedBattleState): string {
  const summary = buildStepSummary(frame);
  const resolutionPath =
    frame.resolution_path.length > 0
      ? frame.resolution_path.map((node) => node.resolution_id).join(" → ")
      : "No nested resolution path";

  return `<section class="detail-stack">
    <article class="detail-card">
      <h2 class="detail-title">Current Step</h2>
      <h3 class="detail-headline">${escapeHtml(summary.headline)}</h3>
      <p class="detail-copy">${escapeHtml(summary.detail)}</p>
      <div class="meta-pill-row">${summary.meta
        .map((item) => `<span class="meta-pill">${escapeHtml(item)}</span>`)
        .join("")}</div>
    </article>
    <article class="detail-card">
      <h2 class="detail-title">Resolution Context</h2>
      <div class="detail-copy">${escapeHtml(
        frame.current_root_action
          ? loaded.rootActionLabels[frame.current_root_action.index] ?? frame.current_root_action.label
          : "No root action",
      )}</div>
      <div class="detail-copy mono">${escapeHtml(resolutionPath)}</div>
    </article>
    <article class="detail-card">
      <h2 class="detail-title">Raw Event</h2>
      <button class="detail-toggle" data-command="toggle-raw-event">
        ${escapeHtml(state.rawEventExpanded ? "Hide Raw Event" : "Show Raw Event")}
      </button>
      ${
        state.rawEventExpanded
          ? `<pre class="code-block">${escapeHtml(JSON.stringify(frame.event, null, 2))}</pre>`
          : `<div class="detail-copy">Hidden by default to keep the replay view compact.</div>`
      }
    </article>
  </section>`;
}

function renderKeyMoments(frame: ViewerFrame, loaded: LoadedBattleState): string {
  const momentSeqs = loaded.keyMoments.map((moment) => moment.seq);
  const activeMomentIndex = getMarkerIndexBySeq(momentSeqs, frame.seq);
  const visibleWindow = getVisibleWindow(loaded.keyMoments, Math.max(activeMomentIndex, 0), 2, 3);

  return `<div class="section-note">Showing ${escapeHtml(String(visibleWindow.items.length))} of ${escapeHtml(
    String(loaded.keyMoments.length),
  )} selected moments near the current step.</div><div class="moments-list">${visibleWindow.items
    .map((moment) => {
      const isActive = moment.seq === frame.seq;
      return `<button class="moment-item ${isActive ? "is-active" : ""}" data-command="jump-seq" data-seq="${moment.seq}">
        <div class="moment-overline">Seq ${moment.seq}</div>
        <div class="moment-title">${escapeHtml(moment.label)}</div>
        <div class="moment-copy">${escapeHtml(moment.detail)}</div>
      </button>`;
    })
    .join("")}</div>`;
}

function renderEmptyState(): string {
  return `<div class="viewer-shell">
    <input id="battle-folder-input" class="hidden-input" type="file" webkitdirectory directory multiple />
    <section class="surface hero">
      <div class="hero-grid">
        <div>
          <p class="hero-kicker">Battle-Only Replay</p>
          <h1 class="hero-title">STS2 Battle Replay</h1>
          <p class="hero-copy">
            This viewer rebuilds one battle container from <span class="mono">metadata.json</span>,
            <span class="mono">events.ndjson</span>, and snapshots. Open a real recorder folder, or use a fixture while iterating locally.
          </p>
          <div class="hero-actions">
            <button class="button-primary" data-command="open-folder-picker">
              Open Battle Folder
            </button>
            <button class="button-secondary" data-command="open-folder-fallback">
              Choose Folder Manually
            </button>
          </div>
          <p class="hero-copy">${escapeHtml(
            state.loading ? "Loading battle container…" : state.error ?? state.status,
          )}</p>
        </div>
        <aside class="surface hero-meta">
          <div class="hero-meta-grid">
            <div class="hero-meta-row">
              <div class="meta-label">What This Is</div>
              <div class="meta-value">Tactical Replay, Not Game UI Emulation</div>
              <div class="meta-copy">The viewer favors object traceability, event order, and state reconstruction over original animation fidelity.</div>
            </div>
            <div class="hero-meta-row">
              <div class="meta-label">Open Battle Container</div>
              <div class="meta-copy">
                Expected contents: <span class="mono">metadata.json</span>, <span class="mono">events.ndjson</span>, and optional <span class="mono">snapshots/*.json</span>.
              </div>
            </div>
            <div class="hero-meta-row">
              <div class="meta-label">Keyboard</div>
              <div class="meta-copy">Arrow keys step events. Shift + arrows step actions. Space toggles autoplay.</div>
            </div>
          </div>
        </aside>
      </div>
    </section>
    <section class="load-grid">
      <article class="surface load-card">
        <div class="surface-header">
          <h2 class="surface-title">Folder Import</h2>
          <span class="surface-note">Local-first</span>
        </div>
        <div class="load-card-body">
          <div class="meta-copy">
            Primary path for productized replay. On supported browsers the viewer uses the local folder picker directly; otherwise it falls back to a manual folder selection dialog.
          </div>
          <div class="hero-actions">
            <button class="button-primary" data-command="open-folder-picker">Open Folder</button>
            <button class="button-ghost" data-command="open-folder-fallback">Fallback Picker</button>
          </div>
        </div>
      </article>
      <article class="surface load-card">
        <div class="surface-header">
          <h2 class="surface-title">Fixture Samples</h2>
          <span class="surface-note">${escapeHtml(String(state.fixtures.length))} available</span>
        </div>
        <div class="load-card-body">
          ${
            state.fixtures.length > 0
              ? `<div class="sample-grid">${state.fixtures
                  .map(
                    (fixture) => `<button class="sample-card" data-command="load-sample" data-sample-id="${escapeHtml(
                      fixture.id,
                    )}">
                      <div class="sample-title">${escapeHtml(fixture.label)}</div>
                      <div class="sample-copy">${escapeHtml(
                        fixture.note ?? "Load fixture from the local dev server.",
                      )}</div>
                    </button>`,
                  )
                  .join("")}</div>`
              : `<div class="meta-copy">No fixture API detected. This is expected in the static build path.</div>`
          }
        </div>
      </article>
    </section>
    <div class="footer-note">Viewer can open unknown events without crashing; unsupported events remain visible in the raw event pane.</div>
  </div>`;
}

function renderLoadedState(loaded: LoadedBattleState, frame: ViewerFrame): string {
  const workspaceClassNames = [
    "workspace",
    state.leftPanelCollapsed ? "is-left-collapsed" : "",
    state.rightPanelCollapsed ? "is-right-collapsed" : "",
  ]
    .filter(Boolean)
    .join(" ");

  return `<div class="viewer-shell viewer-shell-loaded">
    <input id="battle-folder-input" class="hidden-input" type="file" webkitdirectory directory multiple />
    <section class="shell-top">
      <section class="surface summary-bar ${state.summaryExpanded ? "is-expanded" : "is-compact"}">
        <div class="summary-main">
          <h1 class="summary-title">${escapeHtml(loaded.overview.title)}</h1>
          ${renderCompactSummaryMeta(loaded)}
          ${renderBadges(loaded.overview.badges)}
          ${
            state.summaryExpanded
              ? `<div class="summary-source">${escapeHtml(loaded.overview.source_line)}</div>${renderStats(
                  loaded.overview.stats,
                )}`
              : ""
          }
        </div>
        <div class="summary-side">
          ${
            state.summaryExpanded
              ? `<div class="status-copy">${escapeHtml(state.error ?? state.status)}</div>`
              : ""
          }
          <div class="summary-actions">
            <button class="button-ghost" data-command="toggle-summary">${
              state.summaryExpanded ? "Hide Details" : "Show Details"
            }</button>
            <button class="button-primary" data-command="open-folder-picker">Open Another Battle</button>
            <button class="button-ghost" data-command="open-folder-fallback">Manual Folder</button>
          </div>
        </div>
      </section>
      ${state.summaryExpanded ? renderWarnings(loaded.alerts) : ""}
      <section class="surface controls-bar">
        <div class="controls-group controls-group-nav">
          <button class="control-button" data-command="step-prev-turn">Prev Turn</button>
          <button class="control-button" data-command="step-prev-action">Prev Action</button>
          <button class="control-button" data-command="step-prev-event">Prev Event</button>
          <button class="control-button is-primary" data-command="toggle-play">${
            state.isPlaying ? "Pause" : "Play"
          }</button>
          <button class="control-button" data-command="step-next-event">Next Event</button>
          <button class="control-button" data-command="step-next-action">Next Action</button>
          <button class="control-button" data-command="step-next-turn">Next Turn</button>
        </div>
        <div class="controls-group controls-group-playback">
          <select id="play-mode" class="control-select">
            <option value="action" ${state.playMode === "action" ? "selected" : ""}>Autoplay Actions</option>
            <option value="event" ${state.playMode === "event" ? "selected" : ""}>Autoplay Events</option>
          </select>
          <label class="control-speed" for="play-speed-range">
            <span class="control-speed-label">Speed</span>
            <input
              id="play-speed-range"
              class="control-range"
              type="range"
              min="${MIN_PLAYBACK_SPEED_MULTIPLIER}"
              max="${MAX_PLAYBACK_SPEED_MULTIPLIER}"
              step="${PLAYBACK_SPEED_STEP}"
              value="${formatPlaybackSpeedMultiplier(getPlaybackSpeedMultiplierValue())}"
            />
            <div class="control-speed-number">
              <input
                id="play-speed-input"
                class="control-number"
                type="number"
                min="${MIN_PLAYBACK_SPEED_MULTIPLIER}"
                max="${MAX_PLAYBACK_SPEED_MULTIPLIER}"
                step="${PLAYBACK_SPEED_STEP}"
                inputmode="decimal"
                value="${formatPlaybackSpeedMultiplier(getPlaybackSpeedMultiplierValue())}"
              />
              <span class="control-suffix">x</span>
            </div>
          </label>
        </div>
      </section>
    </section>
    <main class="${workspaceClassNames}">
      ${
        state.leftPanelCollapsed
          ? ""
          : `<aside class="surface panel panel-left scroll-panel" data-scroll-key="panel-left">
              ${renderPanelToolbar(
                "Navigation",
                "Turn jumps and action rail",
                "toggle-left-panel",
                "Hide",
              )}
              ${renderJumpSections(frame, loaded)}
              <div class="surface-header">
                <h2 class="surface-title">Action Rail</h2>
                <span class="surface-note">${escapeHtml(String(loaded.model.action_markers.length))} action markers</span>
              </div>
              ${renderTimeline(frame, loaded)}
            </aside>`
      }
      <section class="surface panel board-panel scroll-panel" data-scroll-key="panel-board">
        ${renderArena(frame)}
        ${renderPanelDock()}
      </section>
      ${
        state.rightPanelCollapsed
          ? ""
          : `<aside class="surface panel panel-right scroll-panel" data-scroll-key="panel-right">
              ${renderPanelToolbar(
                "Inspector",
                "Current step and key moments",
                "toggle-right-panel",
                "Hide",
              )}
              ${renderStepDetails(frame, loaded)}
              <div class="surface-header">
                <h2 class="surface-title">Key Moments</h2>
                <span class="surface-note">${escapeHtml(String(loaded.keyMoments.length))} selected events</span>
              </div>
              ${renderKeyMoments(frame, loaded)}
            </aside>`
      }
    </main>
  </div>`;
}

function render(): void {
  stopPostRenderAnimationFrame();
  captureScrollPositions();
  const frame = getCurrentFrame();

  if (!state.loaded || !frame) {
    root.innerHTML = renderEmptyState();
    restoreScrollPositions();
    return;
  }

  root.innerHTML = renderLoadedState(state.loaded, frame);
  restoreScrollPositions();
  state.postRenderAnimationFrame = window.requestAnimationFrame(() => {
    state.postRenderAnimationFrame = undefined;
    if (state.currentSeq !== frame.seq) {
      return;
    }
    postRenderAnimate(frame);
  });
}

function postRenderAnimate(frame: ViewerFrame): void {
  const et = frame.event.event_type;
  const board = root.querySelector<HTMLElement>(".board-panel");
  if (!board) return;

  switch (et) {
    case "damage_attempt": {
      const targetId = (frame.event.payload as DamageAttemptPayload).settled_target_entity_id;
      const card = targetId
        ? board.querySelector<HTMLElement>(`.enemy-card[data-entity="${targetId}"], .player-card[data-entity="${targetId}"]`)
        : null;
      if (card) {
        card.classList.add("anim-hp-damage");
        setTimeout(() => card.classList.remove("anim-hp-damage"), 250);
      }
      break;
    }
    case "block_changed":
    case "block_broken": {
      const entityCards = board.querySelectorAll<HTMLElement>(".enemy-card, .player-card");
      entityCards.forEach((card) => {
        if (card.querySelector(".is-block")) {
          card.classList.add("anim-block-change");
          setTimeout(() => card.classList.remove("anim-block-change"), 130);
        }
      });
      break;
    }
    case "entity_died": {
      const deadId = frame.event.payload.entity_id;
      const deadCard = deadId
        ? board.querySelector<HTMLElement>(`.enemy-card[data-entity="${deadId}"], .player-card[data-entity="${deadId}"]`)
        : null;
      if (deadCard) {
        deadCard.classList.add("anim-entity-die");
      }
      break;
    }
    case "card_play_started":
    case "card_moved": {
      const isCardPlayMove =
        et === "card_moved" &&
        (frame.event.payload as { to_zone?: string }).to_zone === "play";
      if (et === "card_play_started" || isCardPlayMove) {
        const playZoneCard = board.querySelector<HTMLElement>('[data-zone-card="play"]');
        const handPanel = board.querySelector<HTMLElement>(".hand-panel");
        const firstHandCard = board.querySelector<HTMLElement>(".hand-card");
        const animationTargets = [handPanel, playZoneCard, firstHandCard].filter(
          (target, index, values): target is HTMLElement =>
            target instanceof HTMLElement && values.indexOf(target) === index,
        );
        animationTargets.forEach((target) => target.classList.add("anim-card-play"));
        if (animationTargets.length > 0) {
          board.classList.add("anim-card-play-board");
          setTimeout(() => {
            animationTargets.forEach((target) => target.classList.remove("anim-card-play"));
            board.classList.remove("anim-card-play-board");
          }, 260);
        }
      }
      break;
    }
    case "power_applied":
    case "power_removed": {
      const targetEntity = (frame.event.payload as PowerAppliedPayload).target_entity_id;
      const entityCard = targetEntity
        ? board.querySelector<HTMLElement>(`.enemy-card[data-entity="${targetEntity}"], .player-card[data-entity="${targetEntity}"]`)
        : null;
      if (entityCard) {
        entityCard.classList.add("anim-power-change");
        setTimeout(() => entityCard.classList.remove("anim-power-change"), 160);
      }
      break;
    }
    case "orb_evoked": {
      board.classList.add("anim-orb-evoke");
      setTimeout(() => board.classList.remove("anim-orb-evoke"), 160);
      break;
    }
    case "hp_changed": {
      const hpEntity = frame.event.payload.entity_id;
      const hpCard = hpEntity
        ? board.querySelector<HTMLElement>(`.enemy-card[data-entity="${hpEntity}"], .player-card[data-entity="${hpEntity}"]`)
        : null;
      if (hpCard) {
        hpCard.classList.add("anim-hp-tick");
        setTimeout(() => hpCard.classList.remove("anim-hp-tick"), 210);
      }
      break;
    }
  }
}

function getClosestCommandTarget(target: EventTarget | null): HTMLElement | null {
  if (!(target instanceof Element)) {
    return null;
  }
  return target.closest("[data-command]");
}

root.addEventListener("click", (event) => {
  const target = getClosestCommandTarget(event.target);
  if (!target) {
    return;
  }

  const command = target.getAttribute("data-command");
  if (!command) {
    return;
  }

  switch (command) {
    case "open-folder-picker":
      void openDirectoryPicker();
      break;
    case "open-folder-fallback": {
      const input = document.getElementById("battle-folder-input") as HTMLInputElement | null;
      input?.click();
      break;
    }
    case "load-sample": {
      const sampleId = target.getAttribute("data-sample-id");
      if (sampleId) {
        void loadSample(sampleId);
      }
      break;
    }
    case "toggle-zone": {
      const zoneName = target.getAttribute("data-zone");
      if (zoneName) {
        toggleZone(zoneName);
      }
      break;
    }
    case "toggle-raw-event":
      toggleRawEvent();
      break;
    case "toggle-summary":
      toggleSummary();
      break;
    case "toggle-left-panel":
      togglePanel("left");
      break;
    case "toggle-right-panel":
      togglePanel("right");
      break;
    case "jump-seq": {
      const seqValue = target.getAttribute("data-seq");
      if (seqValue) {
        setCurrentSeq(Number.parseInt(seqValue, 10));
      }
      break;
    }
    case "step-prev-event":
      stepEvent("prev");
      break;
    case "step-next-event":
      stepEvent("next");
      break;
    case "step-prev-action":
      stepAction("prev");
      break;
    case "step-next-action":
      stepAction("next");
      break;
    case "step-prev-turn":
      stepTurn("prev");
      break;
    case "step-next-turn":
      stepTurn("next");
      break;
    case "toggle-play":
      togglePlayback();
      break;
  }
});

root.addEventListener("change", (event) => {
  const target = event.target;
  if (!(target instanceof HTMLInputElement || target instanceof HTMLSelectElement)) {
    return;
  }

  if (target.id === "battle-folder-input" && target instanceof HTMLInputElement && target.files) {
    const files = [...target.files];
    const firstPath = files[0]?.webkitRelativePath ?? files[0]?.name ?? "battle folder";
    const label = firstPath.split("/")[0] || "battle folder";
    void loadFolderFiles(files, `Folder ${label}`);
    target.value = "";
    return;
  }

  if (
    (target.id === "play-speed-range" || target.id === "play-speed-input") &&
    target instanceof HTMLInputElement
  ) {
    const nextValue = Number.parseFloat(target.value);
    applyPlaybackSpeedMultiplier(nextValue);
    return;
  }

  if (target.id === "play-mode" && target instanceof HTMLSelectElement) {
    state.playMode = target.value === "event" ? "event" : "action";
    render();
  }
});

document.addEventListener("keydown", (event) => {
  const activeElement = document.activeElement;
  if (
    activeElement instanceof HTMLInputElement ||
    activeElement instanceof HTMLSelectElement ||
    activeElement instanceof HTMLTextAreaElement
  ) {
    return;
  }

  if (!state.loaded) {
    return;
  }

  if (event.key === " ") {
    event.preventDefault();
    togglePlayback();
    return;
  }

  if (event.key === "ArrowLeft" && event.shiftKey) {
    event.preventDefault();
    stepAction("prev");
    return;
  }

  if (event.key === "ArrowRight" && event.shiftKey) {
    event.preventDefault();
    stepAction("next");
    return;
  }

  if (event.key === "ArrowLeft") {
    event.preventDefault();
    stepEvent("prev");
    return;
  }

  if (event.key === "ArrowRight") {
    event.preventDefault();
    stepEvent("next");
  }
});

render();
void loadFixtures();
