#!/usr/bin/env node
/**
 * Oprește datele personale înainte să ajungă într-un repo public.
 *
 * DE CE MECANIC, NU O REGULĂ: regula „exemplele se inventează" a fost scrisă explicit
 * într-un commit de curățenie... după care au urmat încă zece commituri cu cifre reale în
 * mesaje. Nu din neglijență — când repari un bug găsit în datele tale, cel mai natural
 * lucru e să scrii „cazul real: 709 min" ca justificare. E practică bună într-un repo
 * privat și scurgere într-unul public. Deci verificarea trebuie să fie automată, exact în
 * momentul push-ului.
 *
 * DE CE LA PUSH, NU ÎN CI: un check în CI rulează după ce datele sunt deja publice.
 *
 * DOUĂ STRATURI:
 *   1. lista neagră — nume concrete (clienți, aplicații, device). Exactă, fără alarme false.
 *      Trăiește ÎN AFARA repo-ului: pusă aici, ar publica exact ce protejează.
 *   2. euristici de formă — prind scurgeri NOI, după tiparul care s-a repetat de fiecare
 *      dată: „cazul real", „pe datele reale", „semnalat de utilizator", durate reale.
 *
 * MESAJELE DE COMMIT SUNT SCANATE LA FEL CA FIȘIERELE. Acolo a fost cea mai mare scurgere
 * și e singurul loc pe care nu-l vezi într-un `git diff`.
 *
 * Folosire:
 *   node scripts/privacy-guard.mjs            # ca hook pre-push (citește stdin de la git)
 *   node scripts/privacy-guard.mjs --worktree # scanează ce ai acum pe disc
 *   node scripts/privacy-guard.mjs --range A..B
 *
 * Ocolire deliberată: `git push --no-verify`.
 */

import { execFileSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { homedir } from "node:os";
import path from "node:path";

const DENYLIST_PATH =
  process.env.TRACKER_PRIVACY_DENYLIST ||
  path.join(process.env.LOCALAPPDATA || path.join(homedir(), "AppData", "Local"),
            "TimeTracker", "privacy-denylist.txt");

/** Tipare care semnalează „am copiat asta de pe ecranul meu". */
const PHRASES = [
  /cazul\s+real/i, /caz\s+real/i, /pe\s+datele\s+reale/i, /datele\s+reale/i,
  /date\s+reale/i, /perioada\s+real[ăa]/i, /semnalat\s+de\s+utilizator/i,
  /raportat\s+de\s+utilizator/i, /datele\s+mele/i, /configul\s+meu/i,
  /telefonul\s+meu/i, /din\s+telefonul\s+meu/i, /verificat\s+live\s+pe/i,
  /pe\s+o\s+lun[ăa]\s+real[ăa]/i, /real\s+case/i, /real[- ]world\s+data/i,
  /on\s+my\s+machine/i, /my\s+own\s+data/i,
  // Tipare care au scapat la curatenia din 16 august: o masuratoare datata suna a nota
  // de laborator, nu a documentatie, si de obicei vine din masina autorului.
  /\bmeasured\s+\d{4}-\d{2}-\d{2}/i, /\bverified\s+(live|\d{4}-\d{2}-\d{2})/i,
  /\bobserved\s+(live|\d{1,2}:\d{2}|\d{4}-\d{2}-\d{2})/i,
  /\bseen\s+\d{4}-\d{2}-\d{2}/i, /\bseen\s+in\s+real\s+events/i,
  /\bdiagnostic\s+\d{4}-\d{2}-\d{2}/i,
];

/**
 * Ora de perete e mai intima decat durata: nu spune cat ai lucrat, spune CAND erai la
 * calculator. Dar un interval simplu („Zoom 14:00-15:30 → ClientX") e de obicei un exemplu
 * de documentatie, nu o observatie — daca il semnalezi, garda latra la fiecare push pe
 * aceleasi locuri nevinovate si te invata s-o ignori. Semnalul real e ora lipita de un verb
 * de observare; restul cazurilor sunt deja prinse de PHRASES („observed 19:57").
 */
const WALLCLOCK = [/\b(?:at|exited\s+at|la\s+ora|ora)\s+(?:[01]?\d|2[0-3]):[0-5]\d\b/i];

/** Durate cu aspect de măsurătoare reală: „709 min", „11h 30m", „23h 20m". */
const DURATIONS = [
  /\b\d{2,5}\s*min\b/i,
  /\b\d{1,3}\s*h\s*\d{1,2}\s*m\b/i,
  /\b\d{1,3}h\d{1,2}m\b/i,
];

/** Nu are ce căuta într-un repo public, indiferent de context. */
const ALWAYS = [
  { re: /\b[A-Za-z]:\\Users\\(?!<|%|\.\.\.|user\b|nume\b)[A-Za-z0-9._-]+/,
    why: "cale absolută cu nume de utilizator" },
  { re: /\b\d{8,10}:[A-Za-z0-9_-]{30,}\b/, why: "token de bot Telegram" },
  { re: /DESKTOP-[A-Z0-9]{7}|LAPTOP-[A-Z0-9]{7}/, why: "nume de calculator" },
];

const git = (args) =>
  execFileSync("git", args, { encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });

function loadDenylist() {
  if (!existsSync(DENYLIST_PATH)) return { terms: [], missing: true };
  const terms = readFileSync(DENYLIST_PATH, "utf8")
    .split(/\r?\n/)
    .map((l) => l.trim())
    .filter((l) => l && !l.startsWith("#"));
  return { terms, missing: false };
}

/** Comentariu sau text liber — acolo ajung cifrele reale, nu în cod executabil. */
const isProse = (line) =>
  /^\s*(\/\/|\/\*|\*|#|<!--|--)/.test(line) || /^\s*\/{3}/.test(line);

function scanText(text, terms, { prose = false } = {}) {
  const hits = [];
  for (const t of terms) {
    if (text.toLowerCase().includes(t.toLowerCase())) {
      hits.push({ kind: "listă neagră", detail: t });
    }
  }
  for (const re of PHRASES) {
    const m = text.match(re);
    if (m) hits.push({ kind: "formulare", detail: m[0] });
  }
  if (prose) {
    for (const re of DURATIONS) {
      const m = text.match(re);
      if (m) hits.push({ kind: "durată reală", detail: m[0] });
    }
    for (const re of WALLCLOCK) {
      const m = text.match(re);
      if (m) hits.push({ kind: "oră din ziua ta", detail: m[0] });
    }
  }
  for (const { re, why } of ALWAYS) {
    const m = text.match(re);
    if (m) hits.push({ kind: why, detail: m[0] });
  }
  return hits;
}

function scanCommits(range, terms) {
  const findings = [];
  const shas = git(["rev-list", ...range]).split("\n").filter(Boolean);
  for (const sha of shas) {
    const msg = git(["log", "-1", "--format=%s%n%b", sha]);
    // în mesaje, durata e mereu suspectă: un mesaj n-are nevoie de cifre măsurate
    for (const h of scanText(msg, terms, { prose: true })) {
      findings.push({ where: `commit ${sha.slice(0, 8)}`, ...h });
    }
  }
  return findings;
}

function scanDiff(diffArgs, terms) {
  const findings = [];
  const diff = git(diffArgs);
  let file = "";
  let lineNo = 0;
  for (const line of diff.split("\n")) {
    if (line.startsWith("+++ b/")) { file = line.slice(6); lineNo = 0; continue; }
    if (line.startsWith("@@")) {
      const m = line.match(/\+(\d+)/);
      lineNo = m ? parseInt(m[1], 10) - 1 : 0;
      continue;
    }
    if (!line.startsWith("+") || line.startsWith("+++")) continue;
    lineNo++;
    const body = line.slice(1);
    if (/wwwroot\/assets\//.test(file)) continue; // bundle generat, verificat prin surse
    // Garda insasi: fisierul care DEFINESTE tiparele le contine inevitabil. E singura
    // exceptie, si e ingusta intentionat — aici traiesc doar expresii regulate, nu valori.
    // Lista neagra, singurul loc cu date reale, sta oricum in afara repo-ului.
    if (/scripts\/privacy-guard\.mjs$/.test(file)) continue;
    for (const h of scanText(body, terms, { prose: isProse(body) })) {
      findings.push({ where: `${file}:${lineNo}`, ...h, line: body.trim().slice(0, 110) });
    }
  }
  return findings;
}

/**
 * In CI, detaliile NU se tiparesc. Logurile unui repo public sunt publice, iar o constatare
 * afisata acolo ar publica exact ce a prins garda — „durată reală: 543 min" intr-un log
 * deschis e aceeasi scurgere, doar pe alt canal. CI-ul e o sarma de declansare, nu un
 * raport: spune CATE si unde sa te uiti, iar detaliile le vezi rulandu-l local.
 */
function reportQuiet(findings) {
  if (findings.length === 0) {
    console.log("privacy check: curat");
    return 0;
  }
  const files = new Set(findings.map((f) => f.where.split(":")[0]));
  console.error(`privacy check: ${findings.length} constatări în ${files.size} locuri.`);
  console.error("Detaliile NU se afișează aici — logul e public. Rulează local:");
  console.error("  node scripts/privacy-guard.mjs --range $(git hash-object -t tree /dev/null)..HEAD");
  return 1;
}

function report(findings, denyMissing) {
  if (denyMissing) {
    console.error(`\n  Notă: lista neagră lipsește (${DENYLIST_PATH}).`);
    console.error("  Rulează doar euristicile. Vezi scripts/install-privacy-guard.mjs.\n");
  }
  if (findings.length === 0) return 0;

  console.error("\n═══ PUSH OPRIT: par a fi date personale ═══\n");
  const byWhere = new Map();
  for (const f of findings) {
    if (!byWhere.has(f.where)) byWhere.set(f.where, []);
    byWhere.get(f.where).push(f);
  }
  for (const [where, hits] of byWhere) {
    console.error(`  ${where}`);
    for (const h of hits) {
      console.error(`      ${h.kind}: „${h.detail}"`);
      if (h.line) console.error(`      → ${h.line}`);
    }
    console.error("");
  }
  console.error('  Cifra reală se rescrie generic. „Browserul ≈ suma site-urilor din el”');
  console.error('  spune același lucru ca o măsurătoare de-a ta, fără să te descrie.');
  console.error("  Mesajele de commit se rescriu cu: git rebase -i / git commit --amend");
  console.error("  Dacă e o alarmă falsă: git push --no-verify\n");
  return 1;
}

const { terms, missing } = loadDenylist();
const argv = process.argv.slice(2).filter((a) => a !== "--quiet");
const quiet = process.argv.includes("--quiet");
let findings = [];

if (argv[0] === "--worktree") {
  findings = scanDiff(["diff", "HEAD"], terms);
} else if (argv[0] === "--range") {
  findings = [...scanCommits([argv[1]], terms), ...scanDiff(["diff", argv[1]], terms)];
} else {
  // hook pre-push: git dă pe stdin „<localRef> <localSha> <remoteRef> <remoteSha>"
  const stdin = readFileSync(0, "utf8").trim();
  if (!stdin) process.exit(0);
  const remoteName = argv[0] || "origin";
  const ZERO = /^0+$/;
  for (const row of stdin.split("\n").filter(Boolean)) {
    const [, localSha, , remoteSha] = row.split(/\s+/);
    if (ZERO.test(localSha)) continue; // ștergere de ramură
    const range = ZERO.test(remoteSha)
      ? [localSha, "--not", `--remotes=${remoteName}`]
      : [`${remoteSha}..${localSha}`];
    findings.push(...scanCommits(range, terms));
    const diffRange = ZERO.test(remoteSha)
      ? git(["rev-list", "--max-parents=0", localSha]).trim().split("\n")[0] + "^{tree}"
      : remoteSha;
    try {
      findings.push(...scanDiff(["diff", diffRange, localSha], terms));
    } catch {
      findings.push(...scanDiff(["show", "--format=", localSha], terms));
    }
  }
}

process.exit(quiet ? reportQuiet(findings) : report(findings, missing));
