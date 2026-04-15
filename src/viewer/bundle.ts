import { join } from "node:path";

interface BuildViewerClientOptions {
  minify?: boolean;
}

export async function buildViewerClientBundle(
  options: BuildViewerClientOptions = {},
): Promise<string> {
  const result = await Bun.build({
    entrypoints: [join(import.meta.dir, "client.ts")],
    target: "browser",
    format: "esm",
    sourcemap: "external",
    write: false,
    minify: options.minify ?? false,
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
