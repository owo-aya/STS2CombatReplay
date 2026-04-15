import { formatEventSummary } from "../inspector/format";
import type { ResolutionNode } from "../parser/resolutionTree";
import type { IntentState, PowerState } from "../types/state";
import type { Snapshot } from "../types/snapshot";
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

interface SerializedBattleData {
  metadata: ViewerBattleData["metadata"];
  events: ViewerBattleData["events"];
  snapshots: Record<string, Snapshot>;
}

interface ViewerState {
  model?: ViewerBattleModel;
  currentSeq?: number;
  status: string;
  error?: string;
  isPlaying: boolean;
  playSpeed: number;
  playTimer?: ReturnType<typeof setInterval>;
}

const state: ViewerState = {
  status: "Load a fixture or choose a battle container folder.",
  isPlaying: false,
  playSpeed: 1000,
};

const root = document.getElementById("app");
if (!root) {
  throw new Error("Viewer root element not found.");
}

function escapeHtml(value: string): string {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;")
    .replaceAll("'", "&#39;");
}

function getCardType(defId: string): "attack" | "skill" | "power" | "neutral" {
  const upper = defId.toUpperCase();
  if (
    upper.includes("STRIKE") ||
    upper.includes("CORROSIVE_WAVE") ||
    upper.includes("PINPOINT")
  ) {
    return "attack";
  }
  if (
    upper.includes("DEFEND") ||
    upper.includes("SURVIVOR") ||
    upper.includes("PREPARED") ||
    upper.includes("DEFLECT") ||
    upper.includes("NEUTRALIZE")
  ) {
    return "skill";
  }
  if (
    upper.includes("FOOTWORK") ||
    upper.includes("NOXIOUS_FUMES") ||
    upper.includes("STORM_OF_STEEL")
  ) {
    return "power";
  }
  return "neutral";
}

function getIntentClass(intent?: IntentState): string {
  if (!intent) return "intent-unknown";
  const name = (intent.intent_name ?? intent.intent_id).toLowerCase();
  if (name.includes("attack") || name.includes("strike")) return "intent-attack";
  if (name.includes("buff") || name.includes("strength") || name.includes("dexterity"))
    return "intent-buff";
  if (name.includes("debuff") || name.includes("weak") || name.includes("vulnerable") || name.includes("frail"))
    return "intent-debuff";
  if (name.includes("defend") || name.includes("block") || name.includes("shield"))
    return "intent-defend";
  return "intent-unknown";
}

function getIntentIcon(intent?: IntentState): string {
  if (!intent) return "?";
  const name = (intent.intent_name ?? intent.intent_id).toLowerCase();
  if (name.includes("attack") || name.includes("strike")) return "\u2694\uFE0F";
  if (name.includes("buff") || name.includes("strength") || name.includes("dexterity")) return "\u2B06\uFE0F";
  if (name.includes("debuff") || name.includes("weak") || name.includes("vulnerable") || name.includes("frail")) return "\u2B07\uFE0F";
  if (name.includes("defend") || name.includes("block") || name.includes("shield")) return "\uD83D\uDEE1\uFE0F";
  return "\u2022";
}

function formatIntent(intent?: IntentState): string {
  if (!intent) {
    return "No Intent";
  }

  const label = intent.intent_name ?? intent.intent_id;
  if (intent.projected_damage === undefined) {
    return label;
  }

  if (intent.projected_hits !== undefined && intent.projected_hits > 1) {
    return `${label} ${intent.projected_damage}x${intent.projected_hits}`;
  }

  return `${label} ${intent.projected_damage}`;
}

function formatPowers(powers: Record<string, PowerState>): string[] {
  return Object.values(powers)
    .sort((left, right) => {
      const leftLabel = left.power_name ?? left.power_id;
      const rightLabel = right.power_name ?? right.power_id;
      return leftLabel.localeCompare(rightLabel);
    })
    .map((power) => `${power.power_name ?? power.power_id} ${power.stacks}`);
}

function getInitialSeq(model: ViewerBattleModel): number {
  return (
    model.turn_start_markers[0]?.seq ??
    model.snapshot_markers[0]?.seq ??
    model.root_action_markers[0]?.end_seq ??
    model.event_seqs[0]
  );
}

function renderPowerPills(powers: string[]): string {
  if (powers.length === 0) {
    return `<span class="chip muted-fill">No Powers</span>`;
  }

  return powers.map((power) => `<span class="chip">${escapeHtml(power)}</span>`).join("");
}

function renderHpBar(current: number, max: number): string {
  const pct = max > 0 ? Math.min(100, Math.max(0, (current / max) * 100)) : 0;
  return `<div class="hp-bar-track">
    <div class="hp-bar-fill" style="width:${pct}%"></div>
    <span class="hp-bar-text">${current}/${max}</span>
  </div>`;
}

function renderPlayerUnit(frame: ViewerFrame): string {
  const player = [...frame.state.entities.values()].find((entity) => entity.side === "player");
  if (!player) {
    return `<div class="actor actor-player missing">No player entity.</div>`;
  }

  const powers = formatPowers(player.powers);
  return `<div class="actor actor-player ${player.alive ? "" : "dead"}">
    <div class="actor-portrait">
      <div class="actor-badge">${escapeHtml((player.name ?? "P").slice(0, 1))}</div>
    </div>
    <div class="actor-name">${escapeHtml(player.name ?? player.entity_id)}</div>
    <div class="actor-hp-bar">${renderHpBar(player.current_hp, player.max_hp)}</div>
    <div class="actor-stats-row">
      ${player.block > 0 ? `<div class="stat-block shield-stat"><span class="stat-icon">\uD83D\uDEE1\uFE0F</span><span>${player.block}</span></div>` : ""}
      <div class="stat-block energy-stat"><span class="stat-icon">\u26A1</span><span>${player.energy ?? 0}/3</span></div>
    </div>
    <div class="actor-powers">${renderPowerPills(powers)}</div>
  </div>`;
}

function renderEnemyCard(enemy: { name: string; entity_id: string; current_hp: number; max_hp: number; block: number; alive: boolean; powers: Record<string, PowerState>; intent?: IntentState }): string {
  const powers = formatPowers(enemy.powers);
  const intentClass = getIntentClass(enemy.intent);
  return `<div class="enemy-card ${enemy.alive ? "" : "dead"}">
    <div class="intent-banner ${intentClass}">
      <span class="intent-icon">${getIntentIcon(enemy.intent)}</span>
      <span>${escapeHtml(formatIntent(enemy.intent))}</span>
    </div>
    <div class="enemy-portrait">
      <div class="actor-badge">${escapeHtml((enemy.name ?? "E").slice(0, 1))}</div>
    </div>
    <div class="enemy-name ${enemy.alive ? "" : "strikethrough"}">${escapeHtml(enemy.name ?? enemy.entity_id)}</div>
    <div class="enemy-hp-bar">${renderHpBar(enemy.current_hp, enemy.max_hp)}</div>
    ${enemy.block > 0 ? `<div class="enemy-block"><span class="block-icon">\uD83D\uDEE1\uFE0F</span> ${enemy.block}</div>` : ""}
    <div class="enemy-powers">${renderPowerPills(powers)}</div>
    ${!enemy.alive ? `<div class="death-overlay">[DEAD]</div>` : ""}
  </div>`;
}

function renderEnemyUnits(frame: ViewerFrame): string {
  const enemies = [...frame.state.entities.values()]
    .filter((entity) => entity.side === "enemy")
    .sort((left, right) => left.entity_id.localeCompare(right.entity_id));

  if (enemies.length === 0) {
    return `<div class="enemy-row"><div class="actor actor-enemy missing">No enemies.</div></div>`;
  }

  return `<div class="enemy-row">${enemies.map(renderEnemyCard).join("")}</div>`;
}

function renderZoneSummary(frame: ViewerFrame): string {
  const handCount = frame.state.zones.hand?.length ?? 0;
  const drawCount = frame.state.zones.draw?.length ?? 0;
  const discardCount = frame.state.zones.discard?.length ?? 0;
  const exhaustCount = frame.state.zones.exhaust?.length ?? 0;
  const playCount = frame.state.zones.play?.length ?? 0;
  return `<div class="zone-line mono">Draw: ${drawCount} | Hand: ${handCount} | Discard: ${discardCount} | Exhaust: ${exhaustCount} | Play: ${playCount}</div>`;
}

function getHandLayoutClass(handCount: number): string {
  if (handCount >= 16) return "ultra-compact";
  if (handCount >= 11) return "compact";
  return "";
}

function renderHand(frame: ViewerFrame): string {
  const handIds = frame.state.zones.hand ?? [];
  if (handIds.length === 0) {
    return `<div class="hand-empty">Current hand is empty.</div>`;
  }

  const handLayoutClass = getHandLayoutClass(handIds.length);
  return handIds
    .map((cardId, index) => {
      const card = frame.state.cards.get(cardId);
      const title = card?.card_name ?? card?.card_def_id ?? cardId;
      const cost = card?.cost;
      const defId = card?.card_def_id ?? "unknown";
      const cardType = getCardType(defId);
      const tags: string[] = [];
      if (card?.created_this_combat) tags.push("created");
      if (card?.temporary) tags.push("temporary");
      return `<div class="hand-card ${handLayoutClass} card-type-${cardType}" style="--card-rotate:${(index - (handIds.length - 1) / 2) * 2.2}deg;">
        <div class="hand-card-cost">${cost ?? "?"}</div>
        <div class="hand-card-title">${escapeHtml(title)}</div>
        <div class="hand-card-def mono">${escapeHtml(defId)}</div>
        <div class="hand-card-id mono">${escapeHtml(cardId)}</div>
        <div class="hand-card-tags">${tags.length > 0 ? tags.map((tag) => `<span class="mini-chip">${escapeHtml(tag)}</span>`).join("") : `<span class="mini-chip muted-fill">stable</span>`}</div>
      </div>`;
    })
    .join("");
}

function renderPotions(frame: ViewerFrame): string {
  const potions = [...frame.state.potions.values()].sort((left, right) => left.slot_index - right.slot_index);
  if (potions.length === 0) {
    return `<div class="chip muted-fill">No Potions</div>`;
  }

  return potions
    .map((potion) => {
      const label = potion.potion_name ?? potion.potion_def_id;
      return `<div class="potion-slot ${escapeHtml(potion.state)}">
        <div class="potion-slot-index mono">${potion.slot_index}</div>
        <div>
          <div>${escapeHtml(label)}</div>
          <div class="muted mono">${escapeHtml(potion.state)}</div>
        </div>
      </div>`;
    })
    .join("");
}

function renderResolutionPath(path: ResolutionNode[]): string {
  if (path.length === 0) {
    return `<div class="debug-card">No active resolution on this frame.</div>`;
  }

  return path
    .map((node, index) => {
      const firstEvent = node.events[0];
      return `<div class="debug-card">
        <div class="debug-card-title">${index === 0 ? "Root" : `Nested ${index}`}: ${escapeHtml(node.resolution_id)}</div>
        <div>${escapeHtml(firstEvent ? formatEventSummary(firstEvent) : "resolution")}</div>
        ${
          node.trigger
            ? `<div class="muted">trigger ${escapeHtml(node.trigger.trigger_type)} @ seq ${node.trigger.event_seq}</div>`
            : ""
        }
      </div>`;
    })
    .join("");
}

function renderEventWindow(model: ViewerBattleModel, currentSeq: number): string {
  const index = model.event_index_by_seq.get(currentSeq);
  if (index === undefined) {
    return "";
  }

  const start = Math.max(0, index - 4);
  const end = Math.min(model.events.length, index + 5);
  return model.events
    .slice(start, end)
    .map((event) => {
      const activeClass = event.seq === currentSeq ? "active" : "";
      return `<button class="event-line ${activeClass}" data-jump-seq="${event.seq}">
        ${escapeHtml(formatEventSummary(event))}
      </button>`;
    })
    .join("");
}

function renderRootRail(model: ViewerBattleModel, frame: ViewerFrame): string {
  if (model.root_action_markers.length === 0) {
    return `<div class="debug-card">No root actions.</div>`;
  }

  return model.root_action_markers
    .map((marker) => {
      const active =
        frame.current_root_action?.resolution_id === marker.resolution_id ? "active" : "";
      return `<button class="timeline-stop ${active}" data-jump-seq="${marker.end_seq}">
        <div class="timeline-stop-title">${escapeHtml(marker.resolution_id)}</div>
        <div class="muted">${escapeHtml(marker.label)}</div>
        <div class="mono">${marker.start_seq}-${marker.end_seq}</div>
      </button>`;
    })
    .join("");
}

function renderLoadedViewer(model: ViewerBattleModel, frame: ViewerFrame): string {
  const battleName =
    model.metadata.battle.encounter_name ?? model.metadata.battle.encounter_id;
  const currentEventSummary = formatEventSummary(frame.event);
  const rootStops = model.root_action_markers.map((marker) => marker.end_seq);
  const turnStops = model.turn_start_markers.map((marker) => marker.seq);
  const prevRoot = findPreviousSeq(rootStops, frame.seq);
  const nextRoot = findNextSeq(rootStops, frame.seq);
  const prevTurn = findPreviousSeq(turnStops, frame.seq);
  const nextTurn = findNextSeq(turnStops, frame.seq);
  const prevEvent = findPreviousSeq(model.event_seqs, frame.seq);
  const nextEvent = findNextSeq(model.event_seqs, frame.seq);
  const snapshotOptions = model.snapshot_markers
    .map((marker) => `<option value="${marker.seq}">seq ${marker.seq}</option>`)
    .join("");
  const turnOptions = model.turn_start_markers
    .map(
      (marker) =>
        `<option value="${marker.seq}">Turn ${marker.turn_index} @ seq ${marker.seq} (${marker.active_side})</option>`,
    )
    .join("");

  const speedOptions = [
    { label: "0.5x", ms: 2000 },
    { label: "1x", ms: 1000 },
    { label: "2x", ms: 500 },
    { label: "4x", ms: 200 },
  ];

  return `
    <div class="viewer-shell">

      <!-- TOP STATUS BAR -->
      <header class="topbar">
        <div class="topbar-left">
          <div class="player-status">
            <div class="portrait-circle player-portrait-lg">
              ${(() => {
                const p = [...frame.state.entities.values()].find(e => e.side === "player");
                return escapeHtml((p?.name ?? "P").slice(0, 1));
              })()}
            </div>
            <div class="status-group">
              <div class="hp-display">\u2764\uFE0F ${(() => {
                const p = [...frame.state.entities.values()].find(e => e.side === "player");
                return p ? `${p.current_hp}/${p.max_hp}` : "?/?";
              })()}</div>
              <div class="energy-gem">\u26A1 ${(() => {
                const p = [...frame.state.entities.values()].find(e => e.side === "player");
                return p ? `${p.energy ?? 0}/3` : "?/?";
              })()}</div>
              ${(() => {
                const p = [...frame.state.entities.values()].find(e => e.side === "player");
                return p && p.block > 0 ? `<div class="block-indicator">\uD83D\uDEE1\uFE0F ${p.block}</div>` : "";
              })()}
            </div>
          </div>
        </div>
        <div class="topbar-center">
          <div class="turn-indicator">Turn ${frame.state.turn_index} \u2014 ${escapeHtml(frame.state.active_side === "enemy" ? "Enemy" : "Player")}</div>
          <div class="encounter-name">${escapeHtml(battleName)}</div>
          <div class="seq-counter mono">seq ${frame.seq} / ${model.event_seqs[model.event_seqs.length - 1] ?? frame.seq}</div>
        </div>
        <div class="topbar-right">
          <div class="loader-card">
            <label class="control-label" for="fixture-select">Fixture</label>
            <div class="toolbar-row">
              <select id="fixture-select"></select>
              <button id="load-fixture">Load</button>
            </div>
          </div>
          <div class="loader-card">
            <label class="control-label" for="folder-input">Battle Folder</label>
            <input id="folder-input" type="file" webkitdirectory directory multiple />
          </div>
        </div>
      </header>

      <!-- MAIN BATTLE ARENA -->
      <section class="battle-scene">
        <div class="scene-backdrop">
          <!-- LEFT HUD -->
          <div class="scene-hud left-hud">
            <div class="hud-card">
              <div class="hud-label">Current Action</div>
              <div class="hud-value">${escapeHtml(frame.current_root_action?.label ?? "No active root action")}</div>
            </div>
            <div class="hud-card">
              <div class="hud-label">Current Event</div>
              <div class="hud-value mono small">${escapeHtml(currentEventSummary)}</div>
            </div>
            <div class="hud-card">
              <div class="hud-label">Potions</div>
              <div class="potion-grid">${renderPotions(frame)}</div>
            </div>
          </div>

          <!-- COMBAT STAGE -->
          <div class="combat-stage">
            <div class="enemy-stage">
              ${renderEnemyUnits(frame)}
            </div>
            <div class="middle-banner">
              <span>${escapeHtml(frame.state.active_side === "enemy" ? "Enemy Turn" : "Player Turn")}</span>
            </div>
            <div class="player-stage">
              ${renderPlayerUnit(frame)}
            </div>
          </div>

          <!-- RIGHT HUD -->
          <div class="scene-hud right-hud">
            <div class="hud-card">
              <div class="hud-label">Zones</div>
              ${renderZoneSummary(frame)}
            </div>
            <div class="hud-card">
              <div class="hud-label">Replay Base</div>
              <div class="hud-value small">${escapeHtml(frame.source_snapshot_seq !== undefined ? `snapshot ${frame.source_snapshot_seq}` : "full replay")}</div>
            </div>
            <div class="hud-card">
              <div class="hud-label">Status</div>
              <div class="${state.error ? "error-text" : ""} hud-value small">${escapeHtml(state.status)}</div>
            </div>
          </div>
        </div>

        <!-- BOTTOM AREA -->
        <div class="bottom-area">
          <!-- CONTROL STRIP -->
          <div class="control-strip">
            <div class="nav-group">
              <button id="prev-root" ${prevRoot === undefined ? "disabled" : ""}>\u00AB Prev Action</button>
              <button id="next-root" ${nextRoot === undefined ? "disabled" : ""}>Next Action \u00BB</button>
              <button id="prev-turn" ${prevTurn === undefined ? "disabled" : ""}>\u00AB Prev Turn</button>
              <button id="next-turn" ${nextTurn === undefined ? "disabled" : ""}>Next Turn \u00BB</button>
              <button id="prev-event" ${prevEvent === undefined ? "disabled" : ""}>\u00AB Prev Seq</button>
              <button id="next-event" ${nextEvent === undefined ? "disabled" : ""}>Next Seq \u00BB</button>
            </div>
            <div class="jump-group">
              <select id="snapshot-select">${snapshotOptions || `<option value="">No snapshots</option>`}</select>
              <button id="jump-snapshot" ${model.snapshot_markers.length === 0 ? "disabled" : ""}>Jump</button>
              <select id="turn-select">${turnOptions || `<option value="">No turn starts</option>`}</select>
              <button id="jump-turn" ${model.turn_start_markers.length === 0 ? "disabled" : ""}>Jump</button>
            </div>
            <div class="autoplay-group">
              <button id="btn-play" class="${state.isPlaying ? "playing" : ""}">${state.isPlaying ? "\u23F8 Pause" : "\u25B6 Play"}</button>
              <div class="speed-selector">
                ${speedOptions.map(opt => `<button class="speed-btn ${state.playSpeed === opt.ms ? "active" : ""}" data-speed="${opt.ms}">${opt.label}</button>`).join("")}
              </div>
            </div>
          </div>

          <!-- HAND TRAY -->
          <div class="hand-tray">
            <div class="hand-header">
              <div>
                <div class="eyebrow">Current Hand</div>
                <strong>${(frame.state.zones.hand ?? []).length} Cards</strong>
              </div>
            </div>
            <div class="hand-cards">${renderHand(frame)}</div>
          </div>
        </div>
      </section>

      <!-- TIMELINE PANEL -->
      <section class="timeline-panel">
        <details>
          <summary>Action Timeline</summary>
        <div class="timeline-rail">
          ${renderRootRail(model, frame)}
        </div>
        </details>
      </section>

      <!-- DEBUG PANEL -->
      <section class="debug-panel">
        <details>
          <summary>Debug Details</summary>
          <div class="debug-grid">
            <div class="debug-column">
              <h3>Resolution Context</h3>
              ${renderResolutionPath(frame.resolution_path)}
            </div>
            <div class="debug-column">
              <h3>Nearby Events</h3>
              <div class="event-window">${renderEventWindow(model, frame.seq)}</div>
            </div>
            <div class="debug-column">
              <h3>Current Payload</h3>
              <div class="debug-card"><pre>${escapeHtml(
                JSON.stringify(frame.event.payload, null, 2),
              )}</pre></div>
            </div>
          </div>
        </details>
      </section>
    </div>
  `;
}

async function fetchFixtures(): Promise<string[]> {
  const response = await fetch("/api/fixtures");
  if (!response.ok) {
    throw new Error(`Failed to load fixture list: ${response.status}`);
  }
  return (await response.json()) as string[];
}

async function fetchFixtureBattle(name: string): Promise<ViewerBattleData> {
  const response = await fetch(`/api/fixtures/${encodeURIComponent(name)}`);
  if (!response.ok) {
    throw new Error(`Failed to load fixture ${name}: ${response.status}`);
  }

  const payload = (await response.json()) as SerializedBattleData;
  return {
    metadata: payload.metadata,
    events: payload.events,
    snapshots: deserializeSnapshotMap(payload.snapshots),
  };
}

function setLoadedBattle(data: ViewerBattleData): void {
  stopAutoPlay();
  const model = createViewerModel(data);
  state.model = model;
  state.currentSeq = getInitialSeq(model);
  state.error = undefined;
  state.status = `Loaded ${model.metadata.battle_id}`;
  render();
}

function setError(message: string): void {
  state.error = message;
  state.status = message;
  render();
}

function jumpToSeq(seq: number | undefined): void {
  if (!state.model || seq === undefined) {
    return;
  }
  state.currentSeq = seq;
  state.error = undefined;
  render();
}

function startAutoPlay(): void {
  if (state.isPlaying || !state.model) return;
  state.isPlaying = true;
  state.playTimer = setInterval(() => {
    if (!state.model || state.currentSeq === undefined) {
      stopAutoPlay();
      return;
    }
    const nextSeq = findNextSeq(state.model.event_seqs, state.currentSeq);
    if (nextSeq === undefined) {
      stopAutoPlay();
      return;
    }
    state.currentSeq = nextSeq;
    state.error = undefined;
    render();
  }, state.playSpeed);
  render();
}

function stopAutoPlay(): void {
  if (state.playTimer) {
    clearInterval(state.playTimer);
    state.playTimer = undefined;
  }
  state.isPlaying = false;
}

function toggleAutoPlay(): void {
  if (state.isPlaying) {
    stopAutoPlay();
    render();
  } else {
    startAutoPlay();
  }
}

function setPlaySpeed(ms: number): void {
  state.playSpeed = ms;
  if (state.isPlaying) {
    stopAutoPlay();
    startAutoPlay();
  } else {
    render();
  }
}

function bindKeyboardShortcuts(): void {
  document.addEventListener("keydown", (e) => {
    if (e.target instanceof HTMLInputElement || e.target instanceof HTMLSelectElement) return;

    switch (e.key) {
      case "ArrowLeft":
        e.preventDefault();
        if (e.shiftKey && state.model) {
          const rootStops = state.model.root_action_markers.map(m => m.end_seq);
          jumpToSeq(findPreviousSeq(rootStops, state.currentSeq ?? 0));
        } else if (state.model) {
          jumpToSeq(findPreviousSeq(state.model.event_seqs, state.currentSeq ?? 0));
        }
        break;
      case "ArrowRight":
        e.preventDefault();
        if (e.shiftKey && state.model) {
          const rootStops = state.model.root_action_markers.map(m => m.end_seq);
          jumpToSeq(findNextSeq(rootStops, state.currentSeq ?? 0));
        } else if (state.model) {
          jumpToSeq(findNextSeq(state.model.event_seqs, state.currentSeq ?? 0));
        }
        break;
      case "Home":
        e.preventDefault();
        if (state.model) jumpToSeq(state.model.event_seqs[0]);
        break;
      case "End":
        e.preventDefault();
        if (state.model) jumpToSeq(state.model.event_seqs[state.model.event_seqs.length - 1]);
        break;
      case " ":
        e.preventDefault();
        toggleAutoPlay();
        break;
    }
  });
}

function renderEmptyState(): string {
  return `
    <div class="viewer-shell empty-shell">
      <header class="topbar">
        <div class="topbar-left"></div>
        <div class="topbar-center">
          <div class="eyebrow">STS2 Combat Replay Viewer</div>
          <h1>Battle Scene Mode</h1>
          <div class="muted">Load a battle to begin replay.</div>
        </div>
        <div class="topbar-right">
          <div class="loader-card">
            <label class="control-label" for="fixture-select">Fixture</label>
            <div class="toolbar-row">
              <select id="fixture-select"></select>
              <button id="load-fixture">Load</button>
            </div>
          </div>
          <div class="loader-card">
            <label class="control-label" for="folder-input">Battle Folder</label>
            <input id="folder-input" type="file" webkitdirectory directory multiple />
          </div>
        </div>
      </header>

      <section class="empty-state-panel">
        <div class="empty-state-copy">
          <h2>Load a battle container</h2>
          <p>The main view now prioritizes action-level forward/back navigation and a combat-scene layout. Fine-grained seq stepping stays in the debug section after load.</p>
          <div class="status-line ${state.error ? "error-text" : ""}">${escapeHtml(state.status)}</div>
        </div>
      </section>
    </div>
  `;
}

let keyboardBound = false;

function render(): void {
  if (!state.model || state.currentSeq === undefined) {
    root.innerHTML = renderEmptyState();
    bindGlobalControls();
    return;
  }

  const frame = getFrameAtSeq(state.model, state.currentSeq);
  root.innerHTML = renderLoadedViewer(state.model, frame);
  bindGlobalControls();
  bindLoadedControls(frame);

  if (!keyboardBound) {
    bindKeyboardShortcuts();
    keyboardBound = true;
  }
}

function bindGlobalControls(): void {
  const fixtureSelect = document.getElementById("fixture-select") as HTMLSelectElement | null;
  const loadFixtureButton = document.getElementById("load-fixture");
  const folderInput = document.getElementById("folder-input") as HTMLInputElement | null;

  if (fixtureSelect) {
    void fetchFixtures()
      .then((fixtures) => {
        fixtureSelect.innerHTML = fixtures
          .map((fixture) => `<option value="${escapeHtml(fixture)}">${escapeHtml(fixture)}</option>`)
          .join("");
      })
      .catch((err) => {
        setError(err instanceof Error ? err.message : String(err));
      });
  }

  loadFixtureButton?.addEventListener("click", () => {
    if (!fixtureSelect || fixtureSelect.value.length === 0) {
      return;
    }

    state.status = `Loading fixture ${fixtureSelect.value}...`;
    state.error = undefined;
    render();

    void fetchFixtureBattle(fixtureSelect.value)
      .then(setLoadedBattle)
      .catch((err) => {
        setError(err instanceof Error ? err.message : String(err));
      });
  });

  folderInput?.addEventListener("change", () => {
    if (!folderInput.files || folderInput.files.length === 0) {
      return;
    }

    state.status = "Loading selected battle folder...";
    state.error = undefined;
    render();

    void loadBattleFromBrowserFiles(folderInput.files)
      .then(setLoadedBattle)
      .catch((err) => {
        setError(err instanceof Error ? err.message : String(err));
      });
  });
}

function bindLoadedControls(frame: ViewerFrame): void {
  const model = state.model;
  if (!model) {
    return;
  }

  const rootStops = model.root_action_markers.map((marker) => marker.end_seq);
  const turnStops = model.turn_start_markers.map((marker) => marker.seq);

  document.getElementById("prev-root")?.addEventListener("click", () => {
    jumpToSeq(findPreviousSeq(rootStops, frame.seq));
  });
  document.getElementById("next-root")?.addEventListener("click", () => {
    jumpToSeq(findNextSeq(rootStops, frame.seq));
  });
  document.getElementById("prev-turn")?.addEventListener("click", () => {
    jumpToSeq(findPreviousSeq(turnStops, frame.seq));
  });
  document.getElementById("next-turn")?.addEventListener("click", () => {
    jumpToSeq(findNextSeq(turnStops, frame.seq));
  });
  document.getElementById("prev-event")?.addEventListener("click", () => {
    jumpToSeq(findPreviousSeq(model.event_seqs, frame.seq));
  });
  document.getElementById("next-event")?.addEventListener("click", () => {
    jumpToSeq(findNextSeq(model.event_seqs, frame.seq));
  });
  document.getElementById("jump-snapshot")?.addEventListener("click", () => {
    const select = document.getElementById("snapshot-select") as HTMLSelectElement | null;
    if (select?.value) {
      jumpToSeq(parseInt(select.value, 10));
    }
  });
  document.getElementById("jump-turn")?.addEventListener("click", () => {
    const select = document.getElementById("turn-select") as HTMLSelectElement | null;
    if (select?.value) {
      jumpToSeq(parseInt(select.value, 10));
    }
  });

  document.getElementById("btn-play")?.addEventListener("click", () => {
    toggleAutoPlay();
  });

  document.querySelectorAll<HTMLElement>(".speed-btn").forEach((btn) => {
    btn.addEventListener("click", () => {
      const speed = parseInt(btn.dataset.speed ?? "1000", 10);
      setPlaySpeed(speed);
    });
  });

  document.querySelectorAll<HTMLElement>("[data-jump-seq]").forEach((element) => {
    element.addEventListener("click", () => {
      const rawSeq = element.dataset.jumpSeq;
      if (!rawSeq) {
        return;
      }
      jumpToSeq(parseInt(rawSeq, 10));
    });
  });
}

render();
