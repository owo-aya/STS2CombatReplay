import { join } from "node:path";
import { loadBattleDir, loadMetadata } from "../parser/loader";
import type { Snapshot } from "../types/snapshot";
import { buildViewerClientBundle } from "./bundle";
import { renderViewerDocument } from "./document";

const DEFAULT_PORT = 3000;
const FIXTURES_DIR = join(process.cwd(), "fixtures");

interface FixtureDescriptor {
  id: string;
  label: string;
  note?: string;
}

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
  const port = Number.parseInt(rawPort, 10);
  return Number.isFinite(port) ? port : DEFAULT_PORT;
}

function serializeBattleData(
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

async function listFixtures(): Promise<FixtureDescriptor[]> {
  const entries = await Array.fromAsync(
    new Bun.Glob("*/metadata.json").scan({ cwd: FIXTURES_DIR }),
  );

  const fixtureIds = entries
    .map((entry) => entry.split("/")[0])
    .filter((value, index, values) => values.indexOf(value) === index)
    .sort((left, right) => left.localeCompare(right));

  return await Promise.all(
    fixtureIds.map(async (id) => {
      const metadata = await loadMetadata(join(FIXTURES_DIR, id));
      return {
        id,
        label: metadata.battle.encounter_name ?? id,
        note: [
          metadata.battle.character_name ?? metadata.battle.character_id,
          metadata.battle.result ?? "fixture battle",
        ].join(" · "),
      };
    }),
  );
}

function jsonResponse(data: unknown): Response {
  return new Response(JSON.stringify(data, null, 2), {
    headers: {
      "content-type": "application/json; charset=utf-8",
      "cache-control": "no-store",
    },
  });
}

async function handleRequest(request: Request): Promise<Response> {
  const url = new URL(request.url);

  if (url.pathname === "/" || url.pathname === "/index.html") {
    return new Response(
      renderViewerDocument({
        clientScriptPath: "./assets/viewer.js",
      }),
      {
        headers: {
          "content-type": "text/html; charset=utf-8",
          "cache-control": "no-store",
        },
      },
    );
  }

  if (url.pathname === "/assets/viewer.js") {
    const bundle = await buildViewerClientBundle();
    return new Response(bundle, {
      headers: {
        "content-type": "application/javascript; charset=utf-8",
        "cache-control": "no-store",
      },
    });
  }

  if (url.pathname === "/api/fixtures") {
    return jsonResponse(await listFixtures());
  }

  if (url.pathname.startsWith("/api/fixtures/")) {
    const fixtureId = decodeURIComponent(url.pathname.slice("/api/fixtures/".length));
    const battle = await loadBattleDir(join(FIXTURES_DIR, fixtureId));
    return jsonResponse(serializeBattleData(battle));
  }

  return new Response("Not found", { status: 404 });
}

const port = getPort();

Bun.serve({
  port,
  fetch(request) {
    return handleRequest(request);
  },
  error(error) {
    return new Response(error instanceof Error ? error.message : String(error), {
      status: 500,
      headers: {
        "content-type": "text/plain; charset=utf-8",
      },
    });
  },
});

console.log(`Viewer dev server running at http://localhost:${port}`);
