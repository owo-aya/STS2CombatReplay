# STS2CombatReplay

Public snapshot of the Slay the Spire 2 combat replay project.

Live viewer:

- [https://owo-aya.github.io/STS2CombatReplay/](https://owo-aya.github.io/STS2CombatReplay/)

Included here:

- `recorder/`: the battle recorder mod source
- `src/`: parser, replay, and viewer source
- root build and package files needed for the minimal productization path

Intentionally omitted from this public snapshot:

- large local reference exports and third-party reference repos
- internal workflow assets
- local machine configuration
- documentation drafts, test suites, and fixture corpora

This repository is currently focused on the minimal public code path needed to
move the project toward a stable single-battle replay MVP.

## Viewer

The static viewer is designed for battle-folder import.

Expected battle container contents:

- `metadata.json`
- `events.ndjson`
- optional `snapshots/*.json`

For local development:

```bash
bun run viewer
```

For a static export:

```bash
bun run viewer:build
```
