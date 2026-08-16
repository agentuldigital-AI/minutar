#!/usr/bin/env node
/**
 * Instalează garda de confidențialitate ca hook `pre-push` în repo-ul curent.
 *
 * Hook-ul nu poate fi versionat direct (git nu urmărește .git/hooks), deci se instalează
 * o dată per clonă. Scriptul e idempotent: rulat de două ori, nu strică nimic.
 *
 *   node scripts/install-privacy-guard.mjs
 */

import { execFileSync } from "node:child_process";
import { chmodSync, existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import path from "node:path";

const root = execFileSync("git", ["rev-parse", "--show-toplevel"], { encoding: "utf8" }).trim();
const gitDir = execFileSync("git", ["rev-parse", "--git-dir"], { encoding: "utf8" }).trim();
const hooksDir = path.resolve(root, gitDir, "hooks");
const hookPath = path.join(hooksDir, "pre-push");

const HOOK = `#!/bin/sh
# Garda de confidentialitate — instalata de scripts/install-privacy-guard.mjs
# Ocolire deliberata: git push --no-verify
exec node "$(git rev-parse --show-toplevel)/scripts/privacy-guard.mjs" "$@"
`;

mkdirSync(hooksDir, { recursive: true });
if (existsSync(hookPath) && !readFileSync(hookPath, "utf8").includes("privacy-guard.mjs")) {
  console.error(`Exista deja un pre-push care nu e al nostru: ${hookPath}`);
  console.error("Nu il suprascriu. Imbina-le manual.");
  process.exit(1);
}
writeFileSync(hookPath, HOOK, { encoding: "utf8" });
try { chmodSync(hookPath, 0o755); } catch { /* pe Windows nu conteaza */ }
console.log(`Hook instalat: ${hookPath}`);

const denylist = path.join(
  process.env.LOCALAPPDATA || path.join(homedir(), "AppData", "Local"),
  "TimeTracker", "privacy-denylist.txt");

if (existsSync(denylist)) {
  const n = readFileSync(denylist, "utf8")
    .split(/\r?\n/).filter((l) => l.trim() && !l.startsWith("#")).length;
  console.log(`Lista neagra: ${denylist} (${n} termeni)`);
} else {
  mkdirSync(path.dirname(denylist), { recursive: true });
  writeFileSync(denylist,
    "# Un termen pe linie. Fisierul asta NU ajunge niciodata intr-un repo.\n" +
    "# Pune valori specifice: nume de clienti, aplicatii pe care le folosesti, device-uri.\n",
    "utf8");
  console.log(`Lista neagra creata goala: ${denylist}`);
  console.log("Completeaz-o — pana atunci ruleaza doar euristicile.");
}

console.log("\nVerifica oricand ce ai pe disc:  node scripts/privacy-guard.mjs --worktree");
