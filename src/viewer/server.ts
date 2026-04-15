import { join } from "node:path";
import { loadBattleDir } from "../parser/loader";
import type { Snapshot } from "../types/snapshot";

const DEFAULT_PORT = 3000;
const FIXTURES_DIR = join(process.cwd(), "fixtures");

interface SerializedBattleData {
  metadata: Awaited<ReturnType<typeof loadBattleDir>>["metadata"];
  events: Awaited<ReturnType<typeof loadBattleDir>>["events"];
  snapshots: Record<string, Snapshot>;
}

function getPort(): number {
  const rawPort =
    Bun.argv.find((arg) => arg.startsWith("--port="))?.slice("--port=".length) ??
    process.env.PORT ??
    `${DEFAULT_PORT}`;
  const port = parseInt(rawPort, 10);
  return Number.isFinite(port) ? port : DEFAULT_PORT;
}

async function buildClientBundle(): Promise<string> {
  const result = await Bun.build({
    entrypoints: [join(import.meta.dir, "client.ts")],
    target: "browser",
    format: "esm",
    write: false,
    minify: false,
    sourcemap: "external",
  });

  if (!result.success) {
    const logs = result.logs.map((log) => log.message).join("\n");
    throw new Error(`Failed to build viewer client:\n${logs}`);
  }

  const jsOutput = result.outputs.find((output) => output.path.endsWith(".js"));
  if (!jsOutput) {
    throw new Error("Viewer client build did not produce a JavaScript bundle.");
  }

  return await jsOutput.text();
}

async function listFixtures(): Promise<string[]> {
  const entries = await Array.fromAsync(
    new Bun.Glob("*/metadata.json").scan({ cwd: FIXTURES_DIR }),
  );

  return entries
    .map((entry) => entry.split("/")[0])
    .filter((value, index, values) => values.indexOf(value) === index)
    .sort((left, right) => left.localeCompare(right));
}

function toSerializedBattleData(
  battle: Awaited<ReturnType<typeof loadBattleDir>>,
): SerializedBattleData {
  return {
    metadata: battle.metadata,
    events: battle.events,
    snapshots: Object.fromEntries(
      [...battle.snapshots.entries()].sort(([left], [right]) => left - right),
    ),
  };
}

function renderHtml(): string {
  return `<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>STS2 Combat Viewer Spike</title>
    <style>
      :root {
        color-scheme: dark;
        --bg: #09111b;
        --panel: rgba(11, 18, 28, 0.88);
        --panel-soft: rgba(19, 31, 48, 0.88);
        --panel-glass: rgba(15, 24, 36, 0.78);
        --border: rgba(142, 175, 200, 0.22);
        --text: #f4f0e8;
        --muted: #a6b4c2;
        --accent: #6fd0b0;
        --accent-2: #f3b86b;
        --active: #8de3c7;
        --danger: #ff8b7f;
        --shadow: rgba(0, 0, 0, 0.35);
        --attack-color: #e85d5d;
        --skill-color: #5d8ae8;
        --power-color: #5de87d;
        --intent-attack-bg: rgba(232, 93, 93, 0.2);
        --intent-attack-border: rgba(232, 93, 93, 0.5);
        --intent-buff-bg: rgba(168, 93, 232, 0.2);
        --intent-buff-border: rgba(168, 93, 232, 0.5);
        --intent-debuff-bg: rgba(168, 93, 232, 0.15);
        --intent-debuff-border: rgba(168, 93, 232, 0.4);
        --intent-defend-bg: rgba(93, 168, 232, 0.18);
        --intent-defend-border: rgba(93, 168, 232, 0.45);
        --intent-unknown-bg: rgba(142, 175, 200, 0.12);
        --intent-unknown-border: rgba(142, 175, 200, 0.3);
        --mono: "SF Mono", "Monaco", "Cascadia Mono", monospace;
        --sans: "Avenir Next", "Trebuchet MS", "Segoe UI", sans-serif;
      }
      * { box-sizing: border-box; }
      html, body {
        margin: 0;
        height: 100%;
        background:
          radial-gradient(circle at 12% 10%, rgba(111, 208, 176, 0.08), transparent 30%),
          radial-gradient(circle at 88% 6%, rgba(243, 184, 107, 0.09), transparent 22%),
          linear-gradient(180deg, #0c141e 0%, #09111b 40%, #060d14 100%);
        color: var(--text);
        font-family: var(--sans);
        overflow-x: hidden;
      }
      button, select, input {
        font: inherit;
      }

      /* SHELL LAYOUT */
      .viewer-shell {
        height: 100vh;
        display: flex;
        flex-direction: column;
        gap: 8px;
        padding: 8px 12px 12px;
        overflow-y: auto;
      }
      .empty-shell {
        justify-content: center;
        align-items: center;
      }

      /* TOP STATUS BAR */
      .topbar {
        background: var(--panel);
        border: 1px solid var(--border);
        border-radius: 16px;
        box-shadow: 0 8px 32px var(--shadow);
        backdrop-filter: blur(20px);
        display: grid;
        grid-template-columns: auto 1fr auto;
        gap: 16px;
        padding: 10px 18px;
        align-items: center;
        flex-shrink: 0;
      }
      .topbar-left {
        display: flex;
        align-items: center;
        gap: 12px;
      }
      .topbar-center {
        text-align: center;
      }
      .topbar-right {
        display: flex;
        gap: 10px;
        align-items: center;
      }
      .portrait-circle {
        width: 48px;
        height: 48px;
        border-radius: 50%;
        background:
          radial-gradient(circle at 35% 28%, rgba(255, 255, 255, 0.25), transparent 18%),
          linear-gradient(180deg, rgba(111, 208, 176, 0.4) 0%, rgba(47, 71, 101, 0.7) 100%);
        border: 2px solid rgba(255, 255, 255, 0.15);
        display: grid;
        place-items: center;
        font-weight: 800;
        font-size: 1.15rem;
        box-shadow: 0 4px 12px rgba(0, 0, 0, 0.25);
        flex-shrink: 0;
      }
      .player-portrait-lg {
        width: 44px;
        height: 44px;
        font-size: 1.05rem;
      }
      .status-group {
        display: flex;
        flex-direction: column;
        gap: 3px;
      }
      .hp-display {
        font-size: 0.85rem;
        font-weight: 600;
        letter-spacing: 0.02em;
      }
      .energy-gem {
        font-size: 0.78rem;
        color: var(--active);
        font-weight: 500;
      }
      .block-indicator {
        font-size: 0.75rem;
        color: #7cb3e8;
      }
      .turn-indicator {
        font-size: 0.95rem;
        font-weight: 700;
        letter-spacing: 0.04em;
        color: var(--accent-2);
      }
      .encounter-name {
        font-size: 0.82rem;
        color: var(--muted);
        margin-top: 2px;
      }
      .seq-counter {
        font-size: 0.72rem;
        color: var(--muted);
        opacity: 0.7;
        margin-top: 1px;
      }
      .eyebrow {
        color: var(--accent-2);
        font-size: 0.75rem;
        text-transform: uppercase;
        letter-spacing: 0.12em;
      }

      /* BATTLE SCENE */
      .battle-scene {
        flex: 1 1 0;
        min-height: 0;
        display: flex;
        flex-direction: column;
        gap: 4px;
        overflow: hidden;
      }
      .scene-backdrop {
        flex: 1 1 0;
        min-height: 0;
        border-radius: 18px;
        border: 1px solid rgba(255, 255, 255, 0.07);
        background:
          radial-gradient(circle at 50% 50%, rgba(59, 89, 120, 0.2), transparent 50%),
          linear-gradient(180deg, rgba(28, 42, 58, 0.94) 0%, rgba(14, 21, 32, 0.96) 50%, rgba(8, 13, 22, 0.98) 100%);
        position: relative;
        overflow: hidden;
        display: grid;
        grid-template-columns: 200px minmax(0, 1fr) 200px;
        gap: 10px;
        padding: 10px;
      }
      .scene-backdrop::before {
        content: "";
        position: absolute;
        inset: 0;
        background:
          repeating-linear-gradient(90deg, rgba(255, 255, 255, 0.008) 0 1px, transparent 1px 80px),
          repeating-linear-gradient(0deg, rgba(255, 255, 255, 0.005) 0 1px, transparent 1px 80px);
        pointer-events: none;
      }
      .scene-hud,
      .combat-stage {
        position: relative;
        z-index: 1;
      }
      .scene-hud {
        display: grid;
        align-content: start;
        gap: 8px;
        overflow-y: auto;
      }
      .combat-stage {
        display: flex;
        flex-direction: column;
        justify-content: center;
        min-height: 0;
        gap: 0;
      }

      /* HUD CARDS */
      .loader-card,
      .hud-card,
      .debug-card {
        background: var(--panel-soft);
        border: 1px solid var(--border);
        border-radius: 14px;
        padding: 9px 10px;
      }
      .control-label,
      .hud-label {
        display: block;
        font-size: 0.72rem;
        text-transform: uppercase;
        letter-spacing: 0.09em;
        color: var(--muted);
        margin-bottom: 5px;
      }
      .hud-value {
        font-size: 0.82rem;
        line-height: 1.4;
      }
      .hud-value.small {
        font-size: 0.74rem;
      }
      .toolbar-row {
        display: flex;
        gap: 6px;
        align-items: stretch;
      }

      /* ENEMY ROW */
      .enemy-row {
        display: flex;
        justify-content: center;
        align-items: flex-start;
        gap: 12px;
        flex-wrap: wrap;
        padding: 4px 0;
      }
      .enemy-card {
        display: grid;
        gap: 4px;
        justify-items: center;
        text-align: center;
        min-width: 100px;
        max-width: 130px;
        padding: 4px;
        border-radius: 14px;
        border: 1px solid rgba(255, 255, 255, 0.07);
        background: rgba(0, 0, 0, 0.18);
        position: relative;
        transition: opacity 200ms ease;
      }
      .enemy-card.dead {
        opacity: 0.38;
      }
      .enemy-portrait {
        width: 40px;
        height: 40px;
        border-radius: 50%;
        background:
          radial-gradient(circle at 35% 28%, rgba(255, 100, 100, 0.3), transparent 18%),
          linear-gradient(180deg, rgba(180, 70, 70, 0.35) 0%, rgba(80, 40, 40, 0.65) 100%);
        border: 2px solid rgba(255, 100, 100, 0.2);
        display: grid;
        place-items: center;
        box-shadow: 0 6px 16px rgba(0, 0, 0, 0.3);
      }
      .enemy-name {
        font-size: 0.72rem;
        font-weight: 700;
      }
      .enemy-name.strikethrough {
        text-decoration: line-through;
        opacity: 0.6;
      }
      .enemy-block {
        font-size: 0.72rem;
        color: #7cb3e8;
        display: flex;
        align-items: center;
        gap: 3px;
      }
      .block-icon {
        font-style: normal;
      }
      .enemy-powers {
        display: flex;
        flex-wrap: wrap;
        gap: 3px;
        justify-content: center;
      }
      .death-overlay {
        position: absolute;
        inset: 0;
        display: grid;
        place-items: center;
        background: rgba(0, 0, 0, 0.5);
        border-radius: 14px;
        font-weight: 800;
        font-size: 0.78rem;
        letter-spacing: 0.1em;
        color: var(--danger);
        text-transform: uppercase;
      }

      /* INTENT BANNERS */
      .intent-banner {
        border-radius: 999px;
        padding: 2px 6px;
        min-height: 18px;
        display: flex;
        align-items: center;
        justify-content: center;
        gap: 4px;
        font-size: 0.62rem;
        font-weight: 600;
        white-space: nowrap;
        max-width: 100%;
      }
      .intent-icon {
        font-style: normal;
        font-size: 0.8rem;
      }
      .intent-attack {
        background: var(--intent-attack-bg);
        border: 1px solid var(--intent-attack-border);
        color: #ffb3b3;
      }
      .intent-buff {
        background: var(--intent-buff-bg);
        border: 1px solid var(--intent-buff-border);
        color: #d9b3ff;
      }
      .intent-debuff {
        background: var(--intent-debuff-bg);
        border: 1px solid var(--intent-debuff-border);
        color: #c9a3e8;
      }
      .intent-defend {
        background: var(--intent-defend-bg);
        border: 1px solid var(--intent-defend-border);
        color: #a3c9ef;
      }
      .intent-unknown {
        background: var(--intent-unknown-bg);
        border: 1px solid var(--intent-unknown-border);
        color: var(--muted);
      }

      /* ACTOR (PLAYER) */
      .actor {
        display: grid;
        gap: 5px;
        justify-items: center;
        text-align: center;
        min-width: 140px;
      }
      .actor.missing {
        padding: 20px;
        background: rgba(255, 255, 255, 0.04);
        border-radius: 14px;
        border: 1px dashed var(--border);
      }
      .actor.dead {
        opacity: 0.38;
      }
      .actor-portrait {
        width: 48px;
        height: 48px;
        border-radius: 50%;
        background:
          radial-gradient(circle at 35% 28%, rgba(255, 255, 255, 0.3), transparent 18%),
          linear-gradient(180deg, rgba(111, 208, 176, 0.35) 0%, rgba(47, 71, 101, 0.72) 100%);
        border: 2px solid rgba(255, 255, 255, 0.14);
        display: grid;
        place-items: center;
        box-shadow: 0 8px 18px rgba(0, 0, 0, 0.28);
      }
      .actor-badge {
        width: 28px;
        height: 28px;
        border-radius: 50%;
        display: grid;
        place-items: center;
        font-size: 0.85rem;
        font-weight: 700;
        background: rgba(9, 17, 27, 0.72);
        border: 1px solid rgba(255, 255, 255, 0.12);
      }
      .actor-name {
        font-size: 0.72rem;
        font-weight: 700;
      }
      .actor-meta,
      .mono {
        font-family: var(--mono);
      }

      /* HP BAR */
      .actor-hp-bar,
      .enemy-hp-bar {
        width: 100%;
        max-width: 120px;
      }
      .hp-bar-track {
        position: relative;
        height: 12px;
        border-radius: 6px;
        background: rgba(40, 20, 20, 0.8);
        border: 1px solid rgba(192, 62, 74, 0.3);
        overflow: hidden;
      }
      .hp-bar-fill {
        position: absolute;
        inset: 0 0 0 0;
        background: linear-gradient(90deg, #a02020 0%, #d44040 50%, #e86060 100%);
        border-radius: 6px;
        transition: width 200ms ease;
      }
      .hp-bar-text {
        position: relative;
        z-index: 1;
        display: grid;
        place-items: center;
        height: 100%;
        font-size: 0.58rem;
        font-weight: 700;
        color: #fff;
        text-shadow: 0 1px 3px rgba(0, 0, 0, 0.7);
        letter-spacing: 0.02em;
      }

      /* STAT BLOCKS */
      .actor-stats-row {
        display: flex;
        gap: 4px;
        justify-content: center;
      }
      .stat-block {
        display: flex;
        align-items: center;
        gap: 3px;
        font-size: 0.62rem;
        font-weight: 600;
        padding: 1px 5px;
        border-radius: 999px;
      }
      .shield-stat {
        background: rgba(82, 136, 204, 0.18);
        color: #7cb3e8;
      }
      .energy-stat {
        background: rgba(91, 193, 147, 0.18);
        color: var(--active);
      }
      .stat-icon {
        font-style: normal;
      }

      /* MIDDLE BANNER */
      .middle-banner {
        display: flex;
        align-items: center;
        justify-content: center;
        padding: 2px 0;
      }
      .middle-banner span {
        color: rgba(243, 184, 107, 0.65);
        font-size: clamp(0.7rem, 1.5vw, 1rem);
        letter-spacing: 0.1em;
        text-transform: uppercase;
        font-weight: 600;
      }

      /* POWERS */
      .actor-powers {
        display: flex;
        flex-wrap: wrap;
        gap: 4px;
        justify-content: center;
      }
      .badge,
      .chip,
      .mini-chip,
      .stat-pill {
        border-radius: 999px;
        border: 1px solid var(--border);
        background: rgba(255, 255, 255, 0.06);
        padding: 2px 5px;
        font-size: 0.66rem;
      }
      .mini-chip {
        font-size: 0.66rem;
        padding: 2px 5px;
      }
      .muted-fill {
        background: rgba(255, 255, 255, 0.03);
        color: var(--muted);
      }
      .muted {
        color: var(--muted);
      }

      /* ZONE SUMMARY */
      .zone-line {
        font-size: 0.62rem;
        line-height: 1.5;
        padding: 4px 0;
      }

      /* POTIONS */
      .potion-grid {
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .potion-slot {
        border-radius: 12px;
        border: 1px solid var(--border);
        background: rgba(255, 255, 255, 0.04);
        padding: 6px 8px;
        display: flex;
        gap: 8px;
        align-items: center;
      }
      .potion-slot.discarded {
        opacity: 0.5;
      }
      .potion-slot-index {
        width: 24px;
        height: 24px;
        border-radius: 50%;
        display: grid;
        place-items: center;
        border: 1px solid var(--border);
        background: rgba(255, 255, 255, 0.06);
        font-size: 0.7rem;
        flex-shrink: 0;
      }

      /* BOTTOM AREA */
      .bottom-area {
        display: flex;
        flex-direction: column;
        gap: 4px;
        flex-shrink: 0;
      }

      /* CONTROL STRIP */
      .control-strip {
        background: var(--panel);
        border: 1px solid var(--border);
        border-radius: 10px;
        box-shadow: 0 6px 24px var(--shadow);
        backdrop-filter: blur(16px);
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 4px;
        padding: 4px 8px;
      }
      .nav-group {
        display: flex;
        flex-wrap: wrap;
        gap: 6px;
        align-items: center;
      }
      .jump-group {
        display: flex;
        flex-wrap: wrap;
        gap: 6px;
        align-items: center;
        flex: 1 1 300px;
      }
      .autoplay-group {
        display: flex;
        align-items: center;
        gap: 6px;
        flex-shrink: 0;
      }
      .speed-selector {
        display: flex;
        gap: 2px;
      }
      .speed-btn {
        padding: 5px 8px;
        font-size: 0.72rem;
        border-radius: 8px;
        min-width: 0;
      }
      .speed-btn.active {
        background: rgba(111, 208, 176, 0.2);
        border-color: rgba(111, 208, 176, 0.5);
        color: var(--active);
      }
      .speed-btn:hover:not(.active) {
        transform: none;
      }
      #btn-play.playing {
        background: rgba(111, 208, 176, 0.2);
        border-color: rgba(111, 208, 176, 0.5);
        color: var(--active);
      }

      /* INPUTS & BUTTONS */
      .loader-card input[type="file"],
      .loader-card select,
      .jump-group select {
        width: 100%;
        min-width: 0;
      }
      input[type="file"],
      select,
      button {
        border-radius: 10px;
        border: 1px solid var(--border);
        background: rgba(255, 255, 255, 0.06);
        color: var(--text);
        padding: 4px 7px;
      }
      button {
        cursor: pointer;
        transition: transform 100ms ease, border-color 120ms ease, background 120ms ease;
        white-space: nowrap;
      }
      button:hover:not(:disabled) {
        transform: translateY(-1px);
        border-color: rgba(111, 208, 176, 0.45);
        background: rgba(111, 208, 176, 0.08);
      }
      button:disabled {
        opacity: 0.4;
        cursor: default;
      }

      /* HAND TRAY */
      .hand-tray {
        display: grid;
        gap: 4px;
        padding: 6px 8px;
        border-radius: 10px;
        background: var(--panel-glass);
        border: 1px solid var(--border);
      }
      .hand-header,
      .panel-title-row {
        display: flex;
        justify-content: center;
        align-items: center;
        gap: 12px;
      }
      .hand-cards {
        display: flex;
        justify-content: center;
        align-items: flex-end;
        gap: 4px;
        flex-wrap: wrap;
        min-height: 90px;
        padding: 2px 0;
      }

      /* HAND CARDS - TYPE COLORING */
      .hand-card {
        width: 82px;
        min-height: 90px;
        padding: 5px 5px;
        border-radius: 8px;
        border: 1.5px solid rgba(255, 255, 255, 0.12);
        background:
          linear-gradient(180deg, rgba(183, 210, 236, 0.12) 0%, rgba(26, 34, 47, 0.92) 100%);
        box-shadow: 0 10px 20px rgba(0, 0, 0, 0.26);
        transform: rotate(var(--card-rotate, 0deg)) translateY(0);
        display: grid;
        align-content: start;
        gap: 3px;
        transition: transform 150ms ease, box-shadow 150ms ease;
      }
      .hand-card.card-type-attack {
        border-color: rgba(232, 93, 93, 0.45);
        background:
          linear-gradient(180deg, rgba(232, 93, 93, 0.1) 0%, rgba(40, 20, 20, 0.92) 100%);
      }
      .hand-card.card-type-skill {
        border-color: rgba(93, 138, 232, 0.45);
        background:
          linear-gradient(180deg, rgba(93, 138, 232, 0.1) 0%, rgba(20, 28, 45, 0.92) 100%);
      }
      .hand-card.card-type-power {
        border-color: rgba(93, 232, 125, 0.4);
        background:
          linear-gradient(180deg, rgba(93, 232, 125, 0.08) 0%, rgba(20, 40, 28, 0.92) 100%);
      }
      .hand-card.compact {
        width: 68px;
        min-height: 78px;
        padding: 4px 4px;
        font-size: 0.82rem;
      }
      .hand-card.ultra-compact {
        width: 56px;
        min-height: 66px;
        padding: 3px 3px;
        font-size: 0.74rem;
      }
      .hand-card:hover {
        transform: rotate(var(--card-rotate, 0deg)) translateY(-4px);
        box-shadow: 0 16px 28px rgba(0, 0, 0, 0.35);
      }
      .hand-card-cost {
        width: 20px;
        height: 20px;
        border-radius: 50%;
        display: grid;
        place-items: center;
        font-weight: 700;
        font-size: 0.7rem;
        background: rgba(111, 208, 176, 0.16);
        border: 1px solid rgba(111, 208, 176, 0.38);
      }
      .hand-card-title {
        font-size: 0.72rem;
        font-weight: 700;
        line-height: 1.2;
      }
      .hand-card.compact .hand-card-title,
      .hand-card.ultra-compact .hand-card-title {
        font-size: 0.73rem;
      }
      .hand-card-def,
      .hand-card-id {
        font-size: 0.66rem;
        opacity: 0.75;
      }
      .hand-card.ultra-compact .hand-card-id {
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
        max-width: 100%;
      }
      .hand-card-tags {
        display: flex;
        flex-wrap: wrap;
        gap: 3px;
        margin-top: auto;
      }
      .diagnostic-list {
        font-size: 0.73rem;
        display: grid;
        gap: 2px;
      }
      .hand-empty,
      .status-line {
        padding: 10px;
        border-radius: 14px;
        background: rgba(255, 255, 255, 0.03);
        border: 1px solid var(--border);
      }

      /* TIMELINE & DEBUG PANELS */
      .timeline-panel,
      .debug-panel,
      .empty-state-panel {
        background: var(--panel);
        border: 1px solid var(--border);
        border-radius: 14px;
        box-shadow: 0 6px 24px var(--shadow);
        backdrop-filter: blur(16px);
        padding: 10px 14px;
        flex-shrink: 0;
      }
      .timeline-rail {
        display: flex;
        gap: 7px;
        overflow: auto;
        padding-bottom: 4px;
        margin-top: 8px;
      }
      .timeline-stop,
      .event-line {
        min-width: 170px;
        text-align: left;
      }
      .timeline-stop.active,
      .event-line.active {
        border-color: rgba(141, 227, 199, 0.65);
        background: rgba(111, 208, 176, 0.12);
      }
      .timeline-stop-title,
      .debug-card-title {
        font-weight: 700;
        margin-bottom: 3px;
        font-size: 0.82rem;
      }
      .debug-grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
        gap: 8px;
        margin-top: 8px;
      }
      .debug-column {
        display: grid;
        gap: 7px;
        align-content: start;
      }
      .event-window {
        display: grid;
        gap: 5px;
      }
      .debug-card pre {
        margin: 0;
        font-family: var(--mono);
        font-size: 0.76rem;
        white-space: pre-wrap;
        word-break: break-word;
        max-height: 280px;
        overflow-y: auto;
      }
      .error-text {
        color: var(--danger);
      }
      summary {
        cursor: pointer;
        font-weight: 700;
        font-size: 0.88rem;
        color: var(--muted);
        padding: 4px 0;
      }
      summary:hover {
        color: var(--text);
      }
      details[open] > summary {
        margin-bottom: 6px;
      }
      h1, h2, h3, p {
        margin: 0;
      }
      .title-block h1 {
        margin-top: 4px;
        font-size: clamp(1.1rem, 2.5vw, 1.5rem);
      }

      /* SCROLLBAR STYLING */
      ::-webkit-scrollbar {
        width: 6px;
        height: 6px;
      }
      ::-webkit-scrollbar-track {
        background: transparent;
      }
      ::-webkit-scrollbar-thumb {
        background: rgba(142, 175, 200, 0.2);
        border-radius: 3px;
      }
      ::-webkit-scrollbar-thumb:hover {
        background: rgba(142, 175, 200, 0.35);
      }

      @media (max-width: 1180px) {
        .topbar {
          grid-template-columns: 1fr;
          gap: 10px;
          justify-items: center;
        }
        .topbar-left, .topbar-right {
          justify-content: center;
        }
        .scene-backdrop {
          grid-template-columns: 1fr;
          min-height: 0;
        }
        .scene-hud {
          flex-direction: row;
          flex-wrap: wrap;
          justify-content: center;
        }
        .scene-hud > * {
          flex: 1 1 180px;
          min-width: 140px;
        }
      }
      @media (max-width: 780px) {
        .viewer-shell {
          padding: 8px;
          gap: 6px;
        }
        .control-strip,
        .nav-group,
        .jump-group,
        .autoplay-group {
          flex-direction: column;
          align-items: stretch;
        }
        .middle-banner span {
          font-size: 1.2rem;
        }
        .enemy-row {
          flex-direction: column;
          align-items: center;
        }
        .enemy-card {
          max-width: 100%;
        }
      }
    </style>
  </head>
  <body>
    <div id="app"></div>
    <script type="module" src="/viewer.js"></script>
  </body>
</html>`;
}

async function main(): Promise<void> {
  const clientBundle = await buildClientBundle();
  const fixtureNames = await listFixtures();
  const allowedFixtures = new Set(fixtureNames);
  const port = getPort();

  const server = Bun.serve({
    port,
    routes: {
      "/": new Response(renderHtml(), {
        headers: { "content-type": "text/html; charset=utf-8" },
      }),
      "/viewer.js": new Response(clientBundle, {
        headers: { "content-type": "application/javascript; charset=utf-8" },
      }),
      "/api/fixtures": Response.json(fixtureNames),
    },
    async fetch(req) {
      const url = new URL(req.url);

      if (url.pathname.startsWith("/api/fixtures/")) {
        const fixtureName = decodeURIComponent(url.pathname.slice("/api/fixtures/".length));
        if (!allowedFixtures.has(fixtureName)) {
          return new Response(`Unknown fixture: ${fixtureName}`, { status: 404 });
        }

        const battle = await loadBattleDir(join(FIXTURES_DIR, fixtureName));
        return Response.json(toSerializedBattleData(battle));
      }

      return new Response("Not found", { status: 404 });
    },
  });

  process.stdout.write(
    `STS2 viewer spike listening on http://localhost:${server.port}\n`,
  );
}

main().catch((err: unknown) => {
  const message = err instanceof Error ? err.message : String(err);
  process.stderr.write(`${message}\n`);
  process.exit(1);
});
