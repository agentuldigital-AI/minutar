// esbuild bundler for the MV3 extension — outputs a load-unpacked-ready dist/.
import { build } from "esbuild";
import { cpSync, mkdirSync } from "node:fs";

mkdirSync("dist", { recursive: true });

await build({
  entryPoints: ["src/sw.ts", "src/content-youtube.ts", "src/options.ts"],
  bundle: true,
  outdir: "dist",
  target: "chrome120",
  format: "iife",
  minify: false,
  logLevel: "info",
});

cpSync("manifest.json", "dist/manifest.json");
cpSync("src/options.html", "dist/options.html");
console.log("Extension built to src/extension/dist — load unpacked in chrome://extensions / edge://extensions");
