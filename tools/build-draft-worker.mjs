#!/usr/bin/env node
// Builds a separate experimental CLI without modifying or vendoring production artifacts.
import { createHash } from "node:crypto";
import { readFile, readdir, mkdir, writeFile, realpath } from "node:fs/promises";
import { createRequire } from "node:module";
import { resolve, relative, join, dirname, basename, sep, isAbsolute } from "node:path";

const [sourceArgument, outputArgument, releaseArgument] = process.argv.slice(2);
const release = releaseArgument === "--release";
if (!sourceArgument || !outputArgument || (releaseArgument && !release) || process.argv.length > 5) {
  throw new Error("Usage: node tools/build-draft-worker.mjs AETHER_REPO PRIVATE_OUTPUT_DIRECTORY [--release]");
}
const sourceRoot = await realpath(resolve(sourceArgument));
let ancestor = resolve(outputArgument);
const missing = [];
let realAncestor;
while (!realAncestor) {
  try { realAncestor = await realpath(ancestor); }
  catch (error) {
    if (error.code !== "ENOENT") throw error;
    missing.unshift(basename(ancestor));
    ancestor = dirname(ancestor);
  }
}
let outputRoot = join(realAncestor, ...missing);
const insideSource = (path) => {
  const pathRelative = relative(sourceRoot, path);
  return pathRelative === "" || (pathRelative !== ".." && !pathRelative.startsWith(".." + sep) && !isAbsolute(pathRelative));
};
if (insideSource(outputRoot)) {
  throw new Error("Output must be outside the client repository");
}
const requireWorker = createRequire(join(sourceRoot, "packages/server-analysis-worker/package.json"));
const { build, version } = requireWorker("esbuild");
const hash = (value) => createHash("sha256").update(value).digest("hex");
const before = new Map();
async function capture(directory) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) await capture(path);
    else if (entry.isFile()) before.set(path, hash(await readFile(path)));
  }
}
for (const packageName of ["server-analysis-worker", "perception-engine", "analysis-cache", "core-types"]) {
  await capture(join(sourceRoot, "packages", packageName, "src"));
}
const wrapper = `import { mainDraftStorageCli } from "./src/draft-storage-cli.ts";
mainDraftStorageCli(process.argv.slice(2)).catch(error => {
  const code = typeof error?.code === "string" ? error.code : "storage-offline-job-failed";
  process.stderr.write(JSON.stringify({error: code}) + "\\n");
  process.exitCode = 1;
});
`;
const transforms = {};
const bundle = await build({
  stdin: { contents: wrapper, resolveDir: join(sourceRoot, "packages/server-analysis-worker"), sourcefile: "plugin-draft-main.ts", loader: "ts" },
  bundle: true, platform: "node", format: "cjs", target: "node22", minify: true,
  legalComments: "none", metafile: true, write: false,
  // The CLI's ESM direct-entry guard is replaced by the explicit wrapper above.
  define: { "import.meta.url": "\"plugin-draft-wrapper\"" },
  plugins: release ? [{
    name: "release-ffmpeg-thread-cap",
    setup(builder) {
      builder.onLoad({ filter: /\/draft-storage-job\.ts$/ }, async ({ path }) => {
        const source = await readFile(path, "utf8");
        const anchor = "video = await analyzeDraftVideo(options.input, {";
        if (source.split(anchor).length !== 2) throw new Error("Thread-cap integration anchor changed");
        const contents = source.replace(anchor, anchor + '\n        threads: Number(process.env.AETHER_FFMPEG_THREADS ?? "0"),');
        transforms[relative(sourceRoot, path)] = { recipe: "release-ffmpeg-thread-cap-v1", sha256: hash(contents) };
        return { contents, loader: "ts" };
      });
    },
  }] : [],
});
const sources = {};
for (const input of Object.keys(bundle.metafile.inputs).sort()) {
  if (input.endsWith("plugin-draft-main.ts")) continue;
  const path = resolve(input);
  const digest = hash(await readFile(path));
  if (before.get(path) !== digest) throw new Error("Source changed or was outside the captured package sources: " + input);
  sources[relative(sourceRoot, path)] = digest;
}
sources["plugin-draft-main.ts"] = hash(wrapper);
if (release) sources["release-build-transforms"] = hash(JSON.stringify(transforms));
const producerRevision = "sha256:" + hash(JSON.stringify(sources));
const workerBytes = bundle.outputFiles[0].contents;
const workerSha256 = hash(workerBytes);
await mkdir(outputRoot, { recursive: true, mode: 0o700 });
outputRoot = await realpath(outputRoot);
if (insideSource(outputRoot)) throw new Error("Output must be outside the client repository");
const workerPath = join(outputRoot, release ? "aether-analysis-1.2-worker.cjs" : "aether-analysis-draft-worker.cjs");
await writeFile(workerPath, workerBytes, { mode: 0o600 });
const manifest = {
  status: release ? "release-fresh-components-with-stable-master" : "experimental-unregistered-bundle-not-production-release",
  algorithm: release ? "aether-visual/1.2.0" : "aether-visual/1.2.0-draft",
  workerPath, workerSha256, producerRevision, bytes: workerBytes.byteLength,
  esbuildVersion: version, nodeVersion: process.version, sources,
  ...(release ? { transforms, componentAlgorithm: "aether-visual/1.2.0-draft" } : {}),
};
await writeFile(join(outputRoot, "draft-worker-manifest.json"), JSON.stringify(manifest, null, 2) + "\n", { mode: 0o600 });
process.stdout.write(JSON.stringify({ workerPath, workerSha256, producerRevision, bytes: workerBytes.byteLength }) + "\n");
