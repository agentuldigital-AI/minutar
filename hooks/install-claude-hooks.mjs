#!/usr/bin/env node
// install-claude-hooks.mjs — merges the tracker's Claude Code hooks into ~/.claude/settings.json
// (M4, decision #7). Idempotent: skips events that already have the tracker command.
// Run manually:  node hooks/install-claude-hooks.mjs
// Revert:        restore the printed .bak file.

import { readFileSync, writeFileSync, copyFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

const SETTINGS = join(homedir(), ".claude", "settings.json");
const COMMAND =
  'curl.exe -s --max-time 2 -X POST -H "Content-Type: application/json" --data-binary @- http://127.0.0.1:5601/claude/event';
const TRACKER_HOOK = { type: "command", command: COMMAND, async: true };

// PostToolUse needs a matcher; lifecycle events don't.
const EVENTS = {
  PostToolUse: { matcher: "*" },
  SessionStart: {},
  UserPromptSubmit: {},
  Stop: {},
  SessionEnd: {},
  Notification: {},
};

const raw = readFileSync(SETTINGS, "utf8");
const settings = JSON.parse(raw);
settings.hooks ??= {};

const stamp = new Date().toISOString().slice(0, 19).replace(/[:T]/g, "-");
const backup = `${SETTINGS}.bak-${stamp}`;
copyFileSync(SETTINGS, backup);

let added = 0;
for (const [event, extra] of Object.entries(EVENTS)) {
  const groups = (settings.hooks[event] ??= []);
  const already = groups.some((g) =>
    (g.hooks ?? []).some((h) => h.command === COMMAND)
  );
  if (already) {
    console.log(`= ${event}: tracker hook already installed`);
    continue;
  }
  groups.push({ ...extra, hooks: [TRACKER_HOOK] });
  console.log(`+ ${event}: tracker hook added`);
  added++;
}

writeFileSync(SETTINGS, JSON.stringify(settings, null, 2) + "\n");
JSON.parse(readFileSync(SETTINGS, "utf8")); // sanity: file is valid JSON

console.log(`\nDone. ${added} event(s) updated. Backup: ${backup}`);
console.log("Hooks load at session start — restart Claude Code sessions to activate.");
