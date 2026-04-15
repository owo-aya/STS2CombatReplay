import { mkdir, rm } from "node:fs/promises";
import { join } from "node:path";
import { buildViewerClientBundle } from "./bundle";
import { renderViewerDocument } from "./document";

const DEFAULT_OUTDIR = join(process.cwd(), "dist", "viewer");

function readArg(name: string): string | undefined {
  const direct = Bun.argv.find((value) => value.startsWith(`${name}=`));
  if (direct) {
    return direct.slice(name.length + 1);
  }

  const flagIndex = Bun.argv.indexOf(name);
  if (flagIndex !== -1) {
    return Bun.argv[flagIndex + 1];
  }

  return undefined;
}

function normalizeBasePath(rawBase: string | undefined): string {
  if (!rawBase || rawBase === "." || rawBase === "./") {
    return "./";
  }

  let base = rawBase.trim();
  if (!base.endsWith("/")) {
    base = `${base}/`;
  }
  return base;
}

async function main(): Promise<void> {
  const outDir = readArg("--outdir") ?? DEFAULT_OUTDIR;
  const basePath = normalizeBasePath(readArg("--base"));
  const assetsDir = join(outDir, "assets");

  await rm(outDir, { recursive: true, force: true });
  await mkdir(assetsDir, { recursive: true });

  const bundle = await buildViewerClientBundle({ minify: true });
  await Bun.write(join(assetsDir, "viewer.js"), bundle);
  await Bun.write(
    join(outDir, "index.html"),
    renderViewerDocument({
      clientScriptPath: `${basePath}assets/viewer.js`,
    }),
  );

  console.log(`Built static viewer at ${outDir}`);
  console.log(`Client base path: ${basePath}`);
}

void main();
