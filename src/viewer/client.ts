import type { Snapshot } from "../types/snapshot";
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
  findNextSeq,
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
  actionLabels: string[];
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
};

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
  state.isPlaying = false;
}

function getInitialSeq(model: ViewerBattleModel): number {
  return (
    model.turn_start_markers[0]?.seq ??
    model.snapshot_markers[0]?.seq ??
    model.root_action_markers[0]?.end_seq ??
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
    actionLabels: model.root_action_markers.map((marker) => formatActionLabel(marker)),
  };
}

function applyLoadedBattle(data: ViewerBattleData, sourceLabel: string): void {
  stopPlayback();
  const loaded = createLoadedState(data, sourceLabel);
  state.loaded = loaded;
  state.currentSeq = getInitialSeq(loaded.model);
  state.error = undefined;
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

  state.currentSeq = nextSeq;
  render();
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

  const actionTargets = state.loaded.model.root_action_markers.map((marker) => marker.end_seq);
  if (actionTargets.length === 0) {
    return;
  }

  const currentIndex = getMarkerIndexBySeq(actionTargets, state.currentSeq);
  if (direction === "prev") {
    const targetIndex = currentIndex <= 0 ? 0 : currentIndex - 1;
    setCurrentSeq(actionTargets[targetIndex]);
    return;
  }

  const targetIndex = currentIndex < 0 ? 0 : Math.min(actionTargets.length - 1, currentIndex + 1);
  if (targetIndex === currentIndex && state.currentSeq === actionTargets[targetIndex]) {
    return;
  }
  setCurrentSeq(actionTargets[targetIndex]);
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

  const current = state.currentSeq;
  const nextSeq =
    state.playMode === "event"
      ? findNextSeq(state.loaded.model.event_seqs, current)
      : (() => {
          const targets = state.loaded.model.root_action_markers.map((marker) => marker.end_seq);
          const currentIndex = getMarkerIndexBySeq(targets, current);
          if (targets.length === 0) {
            return undefined;
          }
          const nextIndex = currentIndex < 0 ? 0 : currentIndex + 1;
          return targets[nextIndex];
        })();

  if (nextSeq === undefined) {
    stopPlayback();
    render();
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
  return `<article class="enemy-card ${enemy.alive ? "" : "is-dead"}">
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
  return `<article class="actor-card ${player.alive ? "" : "is-dead"}">
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

  return `<div class="hand-grid">${handIds
    .map((cardId) => {
      const card = frame.state.cards.get(cardId);
      if (!card) {
        return `<article class="hand-card">
          <div class="card-cost">?</div>
          <div class="card-title">${escapeHtml(cardId)}</div>
          <div class="card-meta mono">${escapeHtml(cardId)}</div>
          <div class="chip-row"><span class="tag-chip">Unknown card</span></div>
        </article>`;
      }

      return `<article class="hand-card">
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
    <div class="pile-list">${cardIds
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
      </div>
      <div class="support-stack">
        ${renderResourceCards(frame)}
        ${renderZoneGrid(frame)}
      </div>
    </section>
    <section class="hand-panel">
      <div class="hand-header">
        <div>
          <h3 class="surface-title">Hand</h3>
          <div class="surface-note">${escapeHtml(String((frame.state.zones.hand ?? []).length))} visible cards</div>
        </div>
        <div class="surface-note mono">Turn ${escapeHtml(String(frame.state.turn_index))}</div>
      </div>
      ${renderHand(frame)}
    </section>
  </section>`;
}

function renderTimeline(frame: ViewerFrame, loaded: LoadedBattleState): string {
  const activeActionIndex = frame.current_root_action?.index ?? 0;
  const visibleWindow = getVisibleWindow(loaded.model.root_action_markers, activeActionIndex, 2, 3);

  return `<div class="section-note">Showing ${escapeHtml(String(visibleWindow.items.length))} of ${escapeHtml(
    String(loaded.model.root_action_markers.length),
  )} actions near the current step.</div><div class="timeline-list">${visibleWindow.items
    .map((marker) => {
      const isActive = frame.current_root_action?.resolution_id === marker.resolution_id;
      return `<button class="timeline-item ${isActive ? "is-active" : ""}" data-command="jump-seq" data-seq="${marker.end_seq}">
        <div class="timeline-overline">Action ${marker.index + 1} · seq ${marker.start_seq}-${marker.end_seq}</div>
        <div class="timeline-title">${escapeHtml(loaded.actionLabels[marker.index] ?? marker.label)}</div>
        <div class="timeline-copy mono">${escapeHtml(marker.resolution_id)}</div>
      </button>`;
    })
    .join("")}</div>`;
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
          ? loaded.actionLabels[frame.current_root_action.index] ?? frame.current_root_action.label
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
        <div class="controls-group">
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
        <div class="controls-group">
          <select id="play-mode" class="control-select">
            <option value="action" ${state.playMode === "action" ? "selected" : ""}>Autoplay Actions</option>
            <option value="event" ${state.playMode === "event" ? "selected" : ""}>Autoplay Events</option>
          </select>
          <select id="play-speed" class="control-select">
            <option value="1500" ${state.playSpeedMs === 1500 ? "selected" : ""}>Slow</option>
            <option value="900" ${state.playSpeedMs === 900 ? "selected" : ""}>Normal</option>
            <option value="450" ${state.playSpeedMs === 450 ? "selected" : ""}>Fast</option>
          </select>
        </div>
      </section>
    </section>
    <main class="workspace">
      <aside class="surface panel scroll-panel">
        ${renderJumpSections(frame, loaded)}
        <div class="surface-header">
          <h2 class="surface-title">Action Rail</h2>
          <span class="surface-note">${escapeHtml(String(loaded.model.root_action_markers.length))} grouped roots</span>
        </div>
        ${renderTimeline(frame, loaded)}
      </aside>
      <section class="surface panel board-panel scroll-panel">
        ${renderArena(frame)}
      </section>
      <aside class="surface panel scroll-panel">
        ${renderStepDetails(frame, loaded)}
        <div class="surface-header">
          <h2 class="surface-title">Key Moments</h2>
          <span class="surface-note">${escapeHtml(String(loaded.keyMoments.length))} selected events</span>
        </div>
        ${renderKeyMoments(frame, loaded)}
      </aside>
    </main>
  </div>`;
}

function render(): void {
  const frame = getCurrentFrame();

  if (!state.loaded || !frame) {
    root.innerHTML = renderEmptyState();
    return;
  }

  root.innerHTML = renderLoadedState(state.loaded, frame);
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

  if (target.id === "play-speed" && target instanceof HTMLSelectElement) {
    state.playSpeedMs = Number.parseInt(target.value, 10);
    if (state.isPlaying) {
      togglePlayback();
      togglePlayback();
    } else {
      render();
    }
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
