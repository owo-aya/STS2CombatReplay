interface ViewerDocumentOptions {
  clientScriptPath: string;
}

function escapeAttribute(value: string): string {
  return value.replaceAll("&", "&amp;").replaceAll("\"", "&quot;");
}

const VIEWER_STYLES = `
:root {
  color-scheme: dark;
  --bg: #081018;
  --bg-soft: #0d1823;
  --surface: rgba(16, 28, 39, 0.88);
  --surface-strong: rgba(11, 20, 29, 0.94);
  --surface-tint: rgba(20, 36, 49, 0.95);
  --surface-raised: rgba(24, 42, 58, 0.92);
  --line: rgba(155, 178, 193, 0.22);
  --line-strong: rgba(214, 198, 166, 0.28);
  --text: #f2eadf;
  --muted: #b2c1cc;
  --muted-strong: #d2c6ad;
  --accent: #f0b56a;
  --accent-strong: #ffd087;
  --accent-soft: rgba(240, 181, 106, 0.18);
  --sea: #7cc9bd;
  --sea-soft: rgba(124, 201, 189, 0.18);
  --danger: #ff8873;
  --danger-soft: rgba(255, 136, 115, 0.18);
  --warn: #ffd36c;
  --warn-soft: rgba(255, 211, 108, 0.18);
  --good: #88dfb6;
  --good-soft: rgba(136, 223, 182, 0.18);
  --shadow: rgba(0, 0, 0, 0.36);
  --serif: "Iowan Old Style", "Palatino Linotype", "Book Antiqua", Georgia, serif;
  --sans: "Avenir Next", "Trebuchet MS", "Segoe UI", sans-serif;
  --mono: "SF Mono", "Monaco", "Cascadia Mono", monospace;
}

* {
  box-sizing: border-box;
}

html,
body {
  margin: 0;
  min-height: 100%;
  background:
    radial-gradient(circle at 12% 10%, rgba(240, 181, 106, 0.08), transparent 26%),
    radial-gradient(circle at 84% 16%, rgba(124, 201, 189, 0.09), transparent 24%),
    radial-gradient(circle at 50% 100%, rgba(255, 255, 255, 0.04), transparent 28%),
    linear-gradient(180deg, #111a24 0%, #09111a 48%, #050b11 100%);
  color: var(--text);
  font-family: var(--sans);
}

body::before {
  content: "";
  position: fixed;
  inset: 0;
  pointer-events: none;
  background:
    linear-gradient(90deg, rgba(255, 255, 255, 0.015) 1px, transparent 1px),
    linear-gradient(0deg, rgba(255, 255, 255, 0.015) 1px, transparent 1px);
  background-size: 96px 96px;
  opacity: 0.3;
}

button,
select,
input {
  font: inherit;
}

button {
  border: 0;
  cursor: pointer;
}

a {
  color: inherit;
}

.viewer-root {
  position: relative;
  min-height: 100vh;
  padding: 18px;
}

.viewer-shell {
  position: relative;
  z-index: 1;
  display: flex;
  flex-direction: column;
  gap: 16px;
  min-height: calc(100vh - 36px);
}

.viewer-shell-loaded {
  min-height: calc(100vh - 36px);
  max-height: calc(100vh - 36px);
  overflow: auto;
}

.surface {
  border: 1px solid var(--line);
  background: linear-gradient(180deg, rgba(26, 42, 56, 0.96) 0%, rgba(15, 26, 37, 0.96) 100%);
  box-shadow:
    0 10px 30px var(--shadow),
    inset 0 1px 0 rgba(255, 255, 255, 0.04);
  border-radius: 24px;
  backdrop-filter: blur(18px);
}

.surface-header {
  display: flex;
  justify-content: space-between;
  align-items: baseline;
  gap: 12px;
  margin-bottom: 14px;
}

.surface-title {
  margin: 0;
  font-size: 0.88rem;
  letter-spacing: 0.14em;
  text-transform: uppercase;
  color: var(--muted-strong);
}

.surface-note {
  color: var(--muted);
  font-size: 0.88rem;
}

.hero {
  overflow: hidden;
  position: relative;
  padding: 30px;
}

.hero::after {
  content: "";
  position: absolute;
  inset: auto -16% -42% 42%;
  height: 220px;
  background: radial-gradient(circle, rgba(240, 181, 106, 0.22) 0%, transparent 72%);
  pointer-events: none;
}

.hero-grid {
  position: relative;
  z-index: 1;
  display: grid;
  grid-template-columns: minmax(0, 1.6fr) minmax(280px, 0.9fr);
  gap: 24px;
}

.hero-kicker {
  margin: 0 0 10px;
  font-size: 0.82rem;
  letter-spacing: 0.18em;
  text-transform: uppercase;
  color: var(--accent-strong);
}

.hero-title {
  margin: 0;
  font-family: var(--serif);
  font-size: clamp(2.2rem, 4vw, 4rem);
  line-height: 0.95;
  letter-spacing: 0.01em;
}

.hero-copy {
  max-width: 52rem;
  margin: 14px 0 0;
  color: var(--muted);
  font-size: 1rem;
  line-height: 1.6;
}

.hero-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  margin-top: 22px;
}

.button-primary,
.button-secondary,
.button-ghost,
.control-button {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  min-height: 38px;
  padding: 0 14px;
  border-radius: 999px;
}

.button-primary,
.control-button.is-primary {
  background: linear-gradient(180deg, #f1c17f 0%, #dd9f4c 100%);
  color: #21160d;
  font-weight: 700;
}

.button-secondary {
  background: rgba(124, 201, 189, 0.14);
  border: 1px solid rgba(124, 201, 189, 0.32);
  color: var(--text);
}

.button-ghost,
.control-button {
  background: rgba(255, 255, 255, 0.03);
  border: 1px solid var(--line);
  color: var(--text);
}

.button-ghost.is-active,
.panel-toggle-button.is-active {
  background: rgba(240, 181, 106, 0.12);
  border-color: rgba(240, 181, 106, 0.32);
  color: var(--accent-strong);
}

.hero-meta {
  padding: 22px;
  background: linear-gradient(180deg, rgba(17, 28, 39, 0.92) 0%, rgba(11, 19, 28, 0.92) 100%);
}

.hero-meta-grid {
  display: grid;
  gap: 14px;
}

.hero-meta-row {
  display: grid;
  gap: 6px;
}

.meta-label {
  font-size: 0.75rem;
  letter-spacing: 0.12em;
  text-transform: uppercase;
  color: var(--muted-strong);
}

.meta-value {
  font-family: var(--serif);
  font-size: 1.2rem;
}

.meta-copy {
  color: var(--muted);
  font-size: 0.92rem;
  line-height: 1.6;
}

.load-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 16px;
}

.load-card {
  padding: 22px;
}

.load-card-body {
  display: grid;
  gap: 14px;
}

.sample-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 12px;
}

.sample-card {
  padding: 16px;
  border-radius: 18px;
  border: 1px solid var(--line);
  background: rgba(255, 255, 255, 0.025);
  display: grid;
  gap: 8px;
  text-align: left;
}

.sample-card:hover {
  border-color: rgba(240, 181, 106, 0.45);
  background: rgba(240, 181, 106, 0.08);
}

.sample-title {
  font-weight: 700;
}

.sample-copy {
  color: var(--muted);
  font-size: 0.9rem;
}

.shell-top {
  display: grid;
  gap: 14px;
}

.summary-bar {
  padding: 12px 16px;
  display: grid;
  grid-template-columns: minmax(0, 1.4fr) auto;
  gap: 12px;
  align-items: center;
}

.summary-main {
  display: grid;
  gap: 8px;
}

.summary-title {
  margin: 0;
  font-family: var(--serif);
  font-size: clamp(1.32rem, 2vw, 1.8rem);
  line-height: 0.95;
}

.summary-subtitle {
  color: var(--muted-strong);
  font-size: 0.96rem;
}

.summary-inline-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.summary-inline-pill {
  display: inline-flex;
  align-items: center;
  min-height: 26px;
  padding: 0 9px;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.035);
  border: 1px solid var(--line);
  color: var(--muted-strong);
  font-size: 0.78rem;
}

.summary-source {
  color: var(--muted);
  font-size: 0.8rem;
}

.badge-row,
.stat-row,
.meta-pill-row,
.jump-row {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
}

.badge,
.stat-pill,
.meta-pill {
  display: inline-flex;
  align-items: center;
  min-height: 34px;
  padding: 0 12px;
  border-radius: 999px;
  border: 1px solid transparent;
}

.stat-pill,
.meta-pill {
  background: rgba(255, 255, 255, 0.035);
  border-color: var(--line);
  color: var(--muted-strong);
}

.badge.tone-accent {
  background: rgba(240, 181, 106, 0.14);
  color: var(--accent-strong);
  border-color: rgba(240, 181, 106, 0.3);
}

.badge.tone-good {
  background: var(--good-soft);
  color: var(--good);
  border-color: rgba(136, 223, 182, 0.3);
}

.badge.tone-warn {
  background: var(--warn-soft);
  color: var(--warn);
  border-color: rgba(255, 211, 108, 0.28);
}

.badge.tone-bad {
  background: var(--danger-soft);
  color: var(--danger);
  border-color: rgba(255, 136, 115, 0.32);
}

.badge.tone-muted {
  background: rgba(255, 255, 255, 0.035);
  color: var(--muted);
  border-color: var(--line);
}

.summary-side {
  display: grid;
  gap: 6px;
  justify-items: end;
  text-align: right;
}

.summary-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
}

.status-copy {
  color: var(--muted);
  font-size: 0.8rem;
  max-width: 24rem;
}

.summary-bar.is-compact .badge-row {
  gap: 8px;
}

.summary-bar.is-compact .badge {
  min-height: 28px;
  padding: 0 10px;
  font-size: 0.8rem;
}

.summary-bar.is-compact .summary-actions {
  gap: 8px;
}

.summary-bar.is-compact .button-primary,
.summary-bar.is-compact .button-ghost {
  min-height: 34px;
  padding: 0 12px;
}

.warning-stack {
  display: grid;
  gap: 10px;
}

.warning-card {
  padding: 14px 16px;
  border-radius: 18px;
  border: 1px solid transparent;
}

.warning-card h3 {
  margin: 0 0 6px;
  font-size: 0.92rem;
  letter-spacing: 0.06em;
  text-transform: uppercase;
}

.warning-card p {
  margin: 0;
  color: var(--muted-strong);
  line-height: 1.5;
  font-size: 0.93rem;
}

.warning-card.tone-warn {
  background: var(--warn-soft);
  border-color: rgba(255, 211, 108, 0.3);
}

.warning-card.tone-bad {
  background: var(--danger-soft);
  border-color: rgba(255, 136, 115, 0.3);
}

.warning-card.tone-muted {
  background: rgba(255, 255, 255, 0.03);
  border-color: var(--line);
}

.controls-bar {
  padding: 8px 10px;
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
  justify-content: space-between;
}

.controls-group {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
  min-width: 0;
}

.controls-group-playback {
  margin-left: auto;
}

.control-select {
  min-height: 34px;
  border-radius: 999px;
  border: 1px solid var(--line);
  background: rgba(255, 255, 255, 0.03);
  color: var(--text);
  padding: 0 12px;
}

.control-speed {
  display: grid;
  grid-template-columns: auto minmax(120px, 180px) auto;
  gap: 10px;
  align-items: center;
  min-height: 34px;
  padding: 0 12px;
  border-radius: 999px;
  border: 1px solid var(--line);
  background: rgba(255, 255, 255, 0.03);
  color: var(--text);
}

.control-speed-label {
  color: var(--muted-strong);
  font-size: 0.76rem;
  letter-spacing: 0.1em;
  text-transform: uppercase;
  white-space: nowrap;
}

.control-range {
  width: 100%;
  margin: 0;
  accent-color: var(--accent);
  background: transparent;
}

.control-speed-number {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.control-number {
  width: 4rem;
  min-height: 28px;
  padding: 0 8px;
  border-radius: 999px;
  border: 1px solid var(--line);
  background: rgba(7, 12, 18, 0.56);
  color: var(--text);
  text-align: right;
}

.control-number::-webkit-outer-spin-button,
.control-number::-webkit-inner-spin-button {
  margin: 0;
}

.control-suffix {
  color: var(--muted);
  font-size: 0.82rem;
}

.viewer-drawer {
  position: fixed;
  top: 18px;
  right: 18px;
  bottom: 18px;
  z-index: 25;
  width: min(42vw, 560px);
  max-width: calc(100vw - 36px);
  display: flex;
  flex-direction: column;
  border-radius: 28px;
  border: 1px solid rgba(240, 181, 106, 0.18);
  background:
    linear-gradient(180deg, rgba(21, 34, 46, 0.98) 0%, rgba(11, 19, 28, 0.98) 100%),
    radial-gradient(circle at top right, rgba(240, 181, 106, 0.12), transparent 42%);
  box-shadow:
    0 24px 60px rgba(0, 0, 0, 0.34),
    inset 0 1px 0 rgba(255, 255, 255, 0.04);
  overflow: hidden;
}

.viewer-shell-loaded.has-overlay .workspace {
  grid-template-areas: "left board";
  grid-template-columns: minmax(180px, 220px) minmax(0, 1fr);
}

.viewer-shell-loaded.has-overlay .panel-right {
  display: none;
}

.viewer-drawer-header {
  display: flex;
  justify-content: space-between;
  gap: 18px;
  padding: 20px 22px 16px;
  border-bottom: 1px solid var(--line);
  background: linear-gradient(180deg, rgba(30, 46, 60, 0.88) 0%, rgba(21, 34, 46, 0.78) 100%);
}

.viewer-drawer-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  align-items: start;
  justify-content: flex-end;
}

.drawer-kicker {
  color: var(--accent-strong);
  font-size: 0.74rem;
  letter-spacing: 0.14em;
  text-transform: uppercase;
}

.drawer-title {
  margin: 4px 0 0;
  font-family: var(--serif);
  font-size: clamp(1.4rem, 2vw, 2rem);
  line-height: 0.98;
}

.drawer-copy {
  margin-top: 8px;
  color: var(--muted);
  font-size: 0.9rem;
  line-height: 1.5;
}

.viewer-drawer-body {
  flex: 1 1 auto;
  overflow: auto;
  padding: 18px 22px 22px;
}

.drawer-content-stack {
  display: grid;
  gap: 18px;
}

.drawer-section {
  display: grid;
  gap: 12px;
}

.drawer-section-header {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  align-items: end;
}

.drawer-section-title {
  margin: 0;
  font-size: 0.84rem;
  letter-spacing: 0.14em;
  text-transform: uppercase;
  color: var(--muted-strong);
}

.drawer-inline-note {
  color: var(--muted);
  font-size: 0.82rem;
}

.drawer-empty {
  padding: 18px;
  border-radius: 20px;
  border: 1px dashed var(--line);
  color: var(--muted);
  line-height: 1.6;
}

.drawer-empty-inline {
  padding: 12px;
  margin-top: 8px;
}

.review-grid,
.card-group-list,
.card-instance-list,
.card-play-list,
.chain-list {
  display: grid;
  gap: 12px;
}

.review-grid-metrics {
  grid-template-columns: repeat(2, minmax(0, 1fr));
}

.review-grid-leaders,
.review-grid-keycards {
  grid-template-columns: repeat(2, minmax(0, 1fr));
}

.review-card {
  padding: 14px 16px;
  border-radius: 22px;
  border: 1px solid var(--line);
  background: linear-gradient(180deg, rgba(34, 52, 67, 0.92) 0%, rgba(18, 28, 39, 0.92) 100%);
  box-shadow: 0 12px 28px rgba(0, 0, 0, 0.22);
  display: grid;
  gap: 10px;
}

.review-card.tone-good {
  border-color: rgba(136, 223, 182, 0.32);
}

.review-card.tone-warn {
  border-color: rgba(255, 211, 108, 0.32);
}

.review-card.tone-bad {
  border-color: rgba(255, 136, 115, 0.34);
}

.review-card.tone-sea {
  border-color: rgba(124, 201, 189, 0.34);
}

.review-card-top {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  align-items: start;
}

.review-card-kicker {
  color: var(--muted-strong);
  font-size: 0.72rem;
  letter-spacing: 0.12em;
  text-transform: uppercase;
}

.review-card-value,
.review-card-title {
  font-family: var(--serif);
  font-size: 1.18rem;
  line-height: 1.05;
}

.review-card-value {
  font-size: 1.84rem;
}

.review-card-copy {
  color: var(--muted);
  line-height: 1.55;
  font-size: 0.9rem;
}

.review-card-meta {
  color: var(--muted);
  font-size: 0.78rem;
}

.review-card-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.review-pill-row {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.review-pill {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-height: 28px;
  padding: 0 10px;
  border-radius: 999px;
  border: 1px solid var(--line);
  background: rgba(255, 255, 255, 0.035);
  color: var(--muted-strong);
  font-size: 0.78rem;
}

.review-pill strong {
  color: var(--text);
  font-size: 0.82rem;
}

.review-pill.tone-good {
  border-color: rgba(136, 223, 182, 0.32);
}

.review-pill.tone-bad {
  border-color: rgba(255, 136, 115, 0.34);
}

.review-share-pill,
.review-tag-pill {
  display: inline-flex;
  align-items: center;
  min-height: 30px;
  padding: 0 10px;
  border-radius: 999px;
  border: 1px solid rgba(240, 181, 106, 0.28);
  background: rgba(240, 181, 106, 0.12);
  color: var(--accent-strong);
  font-size: 0.8rem;
  white-space: nowrap;
}

.review-tag-pill.tone-warn {
  border-color: rgba(255, 211, 108, 0.28);
  background: rgba(255, 211, 108, 0.12);
  color: var(--warn);
}

.diagnostic-table-wrap {
  overflow: auto;
  border-radius: 20px;
  border: 1px solid var(--line);
}

.diagnostic-table {
  width: 100%;
  border-collapse: collapse;
  min-width: 640px;
  background: rgba(11, 18, 26, 0.58);
}

.diagnostic-table th,
.diagnostic-table td {
  padding: 12px 14px;
  border-bottom: 1px solid var(--line);
  text-align: left;
  font-size: 0.84rem;
}

.diagnostic-table thead th {
  color: var(--muted-strong);
  font-size: 0.74rem;
  letter-spacing: 0.1em;
  text-transform: uppercase;
}

.diagnostic-table tbody tr.is-highlighted {
  background: rgba(255, 136, 115, 0.08);
}

.card-group-card.is-zero {
  opacity: 0.88;
}

.card-group-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  justify-content: flex-end;
  align-items: center;
}

.card-instance-list,
.card-play-list {
  margin-top: 4px;
}

.workspace {
  display: grid;
  grid-template-areas: "left board right";
  grid-template-columns: minmax(180px, 220px) minmax(0, 1fr) minmax(260px, 340px);
  gap: 14px;
  flex: 1 1 auto;
  min-height: 0;
  overflow: hidden;
  align-items: start;
  position: relative;
}

.workspace.is-left-collapsed {
  grid-template-areas: "board right";
  grid-template-columns: minmax(0, 1fr) minmax(260px, 340px);
}

.workspace.is-right-collapsed {
  grid-template-areas: "left board";
  grid-template-columns: minmax(180px, 220px) minmax(0, 1fr);
}

.workspace.is-left-collapsed.is-right-collapsed {
  grid-template-areas: "board";
  grid-template-columns: minmax(0, 1fr);
}

.panel {
  min-height: 0;
  min-width: 0;
  padding: 16px;
  display: flex;
  flex-direction: column;
  gap: 12px;
  overflow: hidden;
}

.panel-left {
  grid-area: left;
}

.board-panel {
  grid-area: board;
}

.panel-right {
  grid-area: right;
}

.scroll-panel {
  overflow: auto;
  min-height: 0;
}

.panel-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.panel-toggle-button,
.panel-dock-button {
  min-height: 32px;
  padding: 0 12px;
  border-radius: 999px;
  border: 1px solid var(--line);
  background: rgba(255, 255, 255, 0.04);
  color: var(--text);
}

.panel-dock {
  position: sticky;
  left: 0;
  bottom: 12px;
  z-index: 3;
  align-self: end;
  width: fit-content;
  display: grid;
  gap: 8px;
  margin-top: auto;
  padding: 10px 12px;
  border-radius: 18px;
  border: 1px solid var(--line);
  background: rgba(7, 12, 18, 0.82);
  box-shadow: 0 12px 24px rgba(0, 0, 0, 0.22);
}

.panel-dock-label {
  color: var(--muted-strong);
  font-size: 0.72rem;
  letter-spacing: 0.1em;
  text-transform: uppercase;
}

.panel-dock-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.timeline-list,
.moments-list,
.jump-list {
  display: grid;
  gap: 8px;
}

.section-note {
  color: var(--muted);
  font-size: 0.82rem;
  line-height: 1.45;
}

.timeline-item,
.moment-item,
.jump-item {
  width: 100%;
  text-align: left;
  padding: 12px 14px;
  border-radius: 16px;
  border: 1px solid var(--line);
  background: rgba(255, 255, 255, 0.025);
}

.timeline-item:hover,
.moment-item:hover,
.jump-item:hover {
  border-color: rgba(240, 181, 106, 0.42);
  background: rgba(240, 181, 106, 0.08);
}

.timeline-item.is-active,
.moment-item.is-active,
.jump-item.is-active {
  border-color: rgba(124, 201, 189, 0.48);
  background: rgba(124, 201, 189, 0.14);
}

.timeline-overline,
.moment-overline {
  color: var(--muted-strong);
  font-size: 0.68rem;
  letter-spacing: 0.12em;
  text-transform: uppercase;
  margin-bottom: 6px;
}

.timeline-title,
.moment-title,
.jump-title {
  font-weight: 700;
  line-height: 1.25;
}

.timeline-copy,
.moment-copy,
.jump-copy {
  margin-top: 4px;
  color: var(--muted);
  line-height: 1.45;
  font-size: 0.85rem;
}

.board-panel {
  gap: 10px;
  align-content: start;
}

.arena {
  display: grid;
  gap: 10px;
}

.battle-top-grid {
  display: grid;
  grid-template-columns: minmax(0, 1.8fr) minmax(220px, 0.85fr);
  gap: 10px;
  align-items: start;
}

.actor-stack,
.support-stack {
  display: grid;
  gap: 10px;
  align-content: start;
}

.enemy-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
  gap: 8px;
}

.enemy-card,
.actor-card,
.zone-card,
.resource-card,
.detail-card {
  border-radius: 22px;
  border: 1px solid var(--line);
  background: linear-gradient(180deg, rgba(33, 52, 68, 0.94) 0%, rgba(18, 29, 40, 0.94) 100%);
  box-shadow: 0 12px 28px rgba(0, 0, 0, 0.24);
}

.enemy-card,
.actor-card {
  padding: 12px;
  display: grid;
  gap: 8px;
}

.player-card {
  padding: 12px;
}

.enemy-card.is-dead,
.actor-card.is-dead {
  opacity: 0.72;
}

.actor-top,
.enemy-top {
  display: flex;
  justify-content: space-between;
  gap: 10px;
  align-items: baseline;
}

.actor-name,
.enemy-name {
  font-size: 1rem;
  font-weight: 700;
}

.actor-subline,
.enemy-subline {
  color: var(--muted);
  font-size: 0.8rem;
}

.intent-chip {
  display: inline-flex;
  align-items: center;
  min-height: 28px;
  padding: 0 9px;
  border-radius: 999px;
  border: 1px solid rgba(255, 255, 255, 0.12);
  background: rgba(255, 255, 255, 0.04);
  color: var(--muted-strong);
  font-size: 0.82rem;
}

.hp-bar {
  position: relative;
  height: 10px;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.08);
  overflow: hidden;
}

.hp-fill {
  position: absolute;
  inset: 0 auto 0 0;
  background: linear-gradient(90deg, #cf5d45 0%, #f0b56a 100%);
  border-radius: inherit;
}

.hp-copy {
  color: var(--muted-strong);
  font-size: 0.8rem;
}

.mini-row {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.mini-pill {
  display: inline-flex;
  align-items: center;
  min-height: 24px;
  padding: 0 8px;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.04);
  border: 1px solid var(--line);
  color: var(--muted-strong);
  font-size: 0.78rem;
}

.mini-pill.is-block {
  border-color: rgba(124, 173, 232, 0.28);
  color: #93c0f0;
}

.mini-pill.is-energy {
  border-color: rgba(240, 181, 106, 0.28);
  color: var(--accent-strong);
}

.mini-pill.is-dead {
  border-color: rgba(255, 136, 115, 0.3);
  color: var(--danger);
}

.resource-grid {
  display: grid;
  gap: 8px;
}

.zone-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(152px, 1fr));
  gap: 8px;
}

.resource-card,
.zone-card,
.detail-card {
  padding: 12px;
  display: grid;
  gap: 6px;
}

.resource-title,
.zone-title,
.detail-title {
  margin: 0;
  font-size: 0.76rem;
  letter-spacing: 0.12em;
  text-transform: uppercase;
  color: var(--muted-strong);
}

.resource-copy,
.zone-copy,
.detail-copy {
  color: var(--muted);
  line-height: 1.45;
  font-size: 0.84rem;
}

.resource-section {
  display: grid;
  gap: 6px;
  max-height: 140px;
  overflow-y: auto;
  scrollbar-width: thin;
  scrollbar-color: var(--line) transparent;
}

.resource-section-label {
  color: var(--muted-strong);
  font-size: 0.74rem;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.zone-card-button {
  width: 100%;
  text-align: left;
  color: inherit;
}

.zone-card-button:disabled {
  cursor: default;
}

.zone-card-button.is-expanded {
  border-color: rgba(124, 201, 189, 0.44);
  background: linear-gradient(180deg, rgba(32, 53, 67, 0.96) 0%, rgba(17, 30, 41, 0.96) 100%);
}

.zone-hint {
  color: var(--accent-strong);
  font-size: 0.72rem;
}

.pile-detail {
  margin-top: 4px;
  padding-top: 10px;
  border-top: 1px solid var(--line);
  display: grid;
  gap: 8px;
}

.pile-detail-label {
  color: var(--muted-strong);
  font-size: 0.72rem;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.pile-list {
  max-height: 120px;
  overflow: auto;
  display: grid;
  gap: 6px;
}

.pile-item {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr);
  gap: 8px;
  align-items: start;
  color: var(--muted-strong);
  font-size: 0.82rem;
}

.pile-index {
  color: var(--muted);
}

.hand-panel {
  padding: 12px;
  border-radius: 24px;
  border: 1px solid rgba(240, 181, 106, 0.16);
  background:
    linear-gradient(180deg, rgba(46, 64, 83, 0.5) 0%, rgba(19, 28, 38, 0.72) 100%),
    radial-gradient(circle at top, rgba(240, 181, 106, 0.12), transparent 52%);
}

.hand-panel-embedded {
  margin-top: 2px;
}

.hand-header {
  display: flex;
  justify-content: space-between;
  gap: 10px;
  align-items: baseline;
  margin-bottom: 10px;
}

.hand-grid {
  display: grid;
  grid-auto-flow: column;
  grid-auto-columns: minmax(96px, 1fr);
  gap: 8px;
  overflow-x: auto;
  padding-bottom: 4px;
  scrollbar-width: thin;
  scrollbar-color: var(--line) transparent;
}

.hand-empty {
  padding: 20px;
  border-radius: 18px;
  border: 1px dashed var(--line);
  color: var(--muted);
  text-align: center;
}

.hand-card {
  position: relative;
  overflow: hidden;
  min-height: 108px;
  padding: 10px;
  border-radius: 20px;
  border: 1px solid var(--line-strong);
  background:
    linear-gradient(180deg, rgba(58, 79, 97, 0.96) 0%, rgba(28, 41, 54, 0.96) 100%);
  display: grid;
  gap: 6px;
}

.hand-card::before {
  content: "";
  position: absolute;
  inset: auto -12% -38% auto;
  width: 96px;
  height: 96px;
  border-radius: 50%;
  background: radial-gradient(circle, rgba(240, 181, 106, 0.22) 0%, transparent 70%);
}

.card-cost {
  display: inline-grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border-radius: 50%;
  background: rgba(10, 17, 24, 0.74);
  border: 1px solid rgba(255, 255, 255, 0.18);
  font-weight: 800;
}

.card-title {
  font-family: var(--serif);
  font-size: 0.88rem;
  line-height: 1.1;
}

.card-meta {
  color: var(--muted);
  font-size: 0.7rem;
  line-height: 1.45;
}

.chip-row {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.tag-chip {
  display: inline-flex;
  align-items: center;
  min-height: 26px;
  padding: 0 9px;
  border-radius: 999px;
  border: 1px solid rgba(255, 255, 255, 0.12);
  background: rgba(255, 255, 255, 0.04);
  color: var(--muted-strong);
  font-size: 0.78rem;
}

.detail-stack {
  display: grid;
  gap: 12px;
}

.detail-headline {
  margin: 0;
  font-family: var(--serif);
  font-size: 1.16rem;
  line-height: 1.08;
}

.detail-copy {
  margin: 0;
}

.detail-toggle {
  width: fit-content;
  min-height: 32px;
  padding: 0 12px;
  border-radius: 999px;
  border: 1px solid var(--line);
  background: rgba(255, 255, 255, 0.04);
  color: var(--text);
}

.code-block {
  margin: 0;
  padding: 12px;
  border-radius: 16px;
  border: 1px solid var(--line);
  background: rgba(7, 12, 18, 0.82);
  color: #d2dde7;
  font-family: var(--mono);
  font-size: 0.8rem;
  line-height: 1.55;
  max-height: 220px;
  overflow: auto;
}

.mono {
  font-family: var(--mono);
}

.hidden-input {
  position: absolute;
  width: 1px;
  height: 1px;
  opacity: 0;
  pointer-events: none;
}

.footer-note {
  color: var(--muted);
  font-size: 0.88rem;
  text-align: center;
  padding-bottom: 4px;
}

@media (max-width: 1400px) {
  .workspace {
    grid-template-columns: minmax(170px, 200px) minmax(0, 1fr) minmax(240px, 300px);
  }

  .battle-top-grid {
    grid-template-columns: minmax(0, 1.5fr) minmax(180px, 0.7fr);
  }
}

@media (max-width: 1520px) {
  .workspace {
    grid-template-areas:
      "board board"
      "left right";
    grid-template-columns: minmax(0, 1fr) minmax(280px, 340px);
    overflow: auto;
    align-content: start;
  }

  .workspace.is-left-collapsed {
    grid-template-areas:
      "board board"
      "right right";
    grid-template-columns: minmax(0, 1fr);
  }

  .workspace.is-right-collapsed {
    grid-template-areas:
      "board board"
      "left left";
    grid-template-columns: minmax(0, 1fr);
  }

  .workspace.is-left-collapsed.is-right-collapsed {
    grid-template-areas: "board";
    grid-template-columns: minmax(0, 1fr);
  }

  .panel-left,
  .panel-right {
    max-height: 34vh;
  }

  .battle-top-grid {
    grid-template-columns: minmax(0, 1fr);
  }

  .support-stack {
    grid-template-columns: minmax(220px, 280px) minmax(0, 1fr);
  }
}

@media (max-width: 1180px) {
  .viewer-drawer {
    top: 12px;
    right: 12px;
    bottom: 12px;
    left: 12px;
    width: auto;
    max-width: none;
  }

  .viewer-drawer-header {
    flex-direction: column;
    align-items: start;
  }

  .viewer-drawer-actions {
    width: 100%;
    justify-content: flex-start;
  }

  .review-grid-metrics,
  .review-grid-leaders,
  .review-grid-keycards {
    grid-template-columns: minmax(0, 1fr);
  }

  .viewer-shell-loaded.has-overlay .workspace {
    grid-template-areas:
      "board"
      "left";
    grid-template-columns: minmax(0, 1fr);
  }

  .workspace {
    grid-template-areas:
      "board"
      "left"
      "right";
    grid-template-columns: minmax(0, 1fr);
    overflow: visible;
  }

  .workspace.is-left-collapsed {
    grid-template-areas:
      "board"
      "right";
  }

  .workspace.is-right-collapsed {
    grid-template-areas:
      "board"
      "left";
  }

  .workspace.is-left-collapsed.is-right-collapsed {
    grid-template-areas: "board";
  }

  .summary-bar {
    grid-template-columns: minmax(0, 1fr);
  }

  .summary-side {
    justify-items: start;
    text-align: left;
  }

  .summary-bar {
    grid-template-columns: minmax(0, 1fr);
  }

  .controls-group-playback {
    margin-left: 0;
  }

  .control-speed {
    min-width: min(100%, 320px);
  }

  .battle-top-grid {
    grid-template-columns: minmax(0, 1fr);
  }

  .support-stack {
    grid-template-columns: minmax(0, 1fr);
  }

  .panel-left,
  .panel-right {
    max-height: none;
  }

  .panel-dock {
    bottom: 8px;
  }
}

@media (max-width: 860px) {
  .viewer-drawer {
    top: 8px;
    right: 8px;
    bottom: 8px;
    left: 8px;
    border-radius: 22px;
  }

  .viewer-drawer-body {
    padding: 14px;
  }

  .controls-bar {
    justify-content: flex-start;
  }

  .controls-group-playback {
    width: 100%;
  }

  .control-speed {
    width: 100%;
    grid-template-columns: minmax(0, 1fr);
    justify-items: stretch;
    border-radius: 18px;
    padding: 10px 12px;
  }

  .control-speed-number {
    justify-content: flex-end;
  }

  .panel-toolbar {
    align-items: start;
    flex-direction: column;
  }

  .panel-dock {
    width: 100%;
  }
}

/* ── Minimal flow-indicating animations ─────────────────── */

/* 1. Damage: card flashes red, HP bar pulses */
@keyframes anim-hp-damage-card {
  0%   { background-color: rgba(220, 38, 38, 0.18); box-shadow: inset 0 0 20px rgba(220, 38, 38, 0.25); }
  100% { background-color: transparent; box-shadow: none; }
}
@keyframes anim-hp-damage-bar {
  0%   { transform: scaleX(1); filter: brightness(1.4); }
  50%  { transform: scaleX(1.02); filter: brightness(1.6); }
  100% { transform: scaleX(1); filter: brightness(1); }
}
@keyframes anim-hp-damage-text {
  0%   { color: #f87171; transform: scale(1.08); }
  100% { color: inherit; transform: scale(1); }
}
.anim-hp-damage {
  animation: anim-hp-damage-card 220ms ease-out;
}
.anim-hp-damage .hp-bar,
.anim-hp-damage .hp-fill {
  animation: anim-hp-damage-bar 220ms ease-out;
}
.anim-hp-damage .hp-copy {
  animation: anim-hp-damage-text 220ms ease-out;
}

/* 2. Block change: border flashes cyan */
@keyframes anim-block-change {
  0% { box-shadow: 0 0 0 2px rgba(6, 182, 212, 0.5); }
  100% { box-shadow: none; }
}
.anim-block-change .is-block {
  animation: anim-block-change 120ms ease-out;
}

/* 3. Entity death: fade out + desaturate */
@keyframes anim-entity-die {
  from { opacity: 1; filter: grayscale(0); }
  to   { opacity: 0.35; filter: grayscale(0.8); }
}
.anim-entity-die {
  animation: anim-entity-die 200ms ease-in-out forwards;
}

/* 4. Card played: focused floating card + target impact */
.card-play-overlay {
  position: fixed;
  margin: 0;
  pointer-events: none;
  z-index: 9999;
  will-change: transform, opacity, filter;
  box-shadow:
    0 20px 44px rgba(0, 0, 0, 0.38),
    0 0 36px rgba(240, 181, 106, 0.26);
}
@keyframes anim-card-play-impact {
  0%   { box-shadow: 0 0 0 0 rgba(240, 181, 106, 0); transform: scale(1); }
  45%  { box-shadow: 0 0 0 3px rgba(240, 181, 106, 0.42), 0 0 34px rgba(240, 181, 106, 0.3); transform: scale(1.03); }
  100% { box-shadow: 0 0 0 0 rgba(240, 181, 106, 0); transform: scale(1); }
}
.anim-card-play-impact {
  animation: anim-card-play-impact 220ms ease-out;
}

/* 5. Power applied/removed: slide-in + fade */
@keyframes anim-power-change {
  from { opacity: 0; transform: translateX(-8px); }
  to   { opacity: 1; transform: translateX(0); }
}
.anim-power-change .mini-row:last-of-type .mini-pill {
  animation: anim-power-change 150ms cubic-bezier(0, 0, 0.2, 1);
}

/* 6. Orb evoked: brief glow flash on orb section */
@keyframes anim-orb-evoke {
  0%   { box-shadow: inset 0 0 12px rgba(168, 85, 247, 0.4); }
  100% { box-shadow: none; }
}
.anim-orb-evoke .resource-section:nth-child(2) {
  animation: anim-orb-evoke 150ms ease-out;
}

/* 7. HP number tick (independent of damage): subtle scale pulse */
@keyframes anim-hp-tick {
  0%, 100% { transform: scale(1); }
  50%      { transform: scale(1.06); }
}
.anim-hp-tick .hp-copy {
  animation: anim-hp-tick 200ms ease-in-out;
}

/* Reduced motion: disable all animations */
@media (prefers-reduced-motion: reduce) {
  .anim-hp-damage,
  .anim-block-change,
  .anim-entity-die,
  .anim-card-play-impact,
  .anim-power-change,
  .anim-orb-evoke,
  .anim-hp-tick {
    animation: none !important;
  }

  .card-play-overlay {
    display: none !important;
  }
}
`;

export function renderViewerDocument(options: ViewerDocumentOptions): string {
  return `<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>STS2 Battle Replay</title>
    <style>${VIEWER_STYLES}</style>
  </head>
  <body>
    <div id="app" class="viewer-root"></div>
    <noscript>This viewer needs JavaScript enabled.</noscript>
    <script type="module" src="${escapeAttribute(options.clientScriptPath)}"></script>
  </body>
</html>`;
}
