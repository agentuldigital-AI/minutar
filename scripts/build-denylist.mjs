#!/usr/bin/env node
/**
 * Genereaza lista neagra din configul REAL, in loc s-o scrie cineva din memorie.
 *
 * DE CE: o lista scrisa de mana prinde doar ce stii deja ca exista. O curatenie facuta asa
 * a ratat un SaaS de facturare aflat in regulile de clasificare, tocmai pentru ca nimeni
 * nu s-a gandit sa-l caute. Configul, in schimb, ESTE sursa adevarului
 * despre ce inseamna „real" pentru utilizatorul asta: fiecare domeniu pe care si l-a
 * clasificat, fiecare aplicatie de pe telefon, fiecare nume de client.
 *
 * Recolteaza domenii, executabile, nume de proiecte si de aplicatii, cuvinte-cheie,
 * etichete de profil si segmente din caile Claude, apoi scade GENERICE — termeni globali
 * care apar legitim in orice repo public. Fara scaderea aia, garda ar tipa la fiecare push
 * si ai invata s-o ocolesti, ceea ce ar anula-o complet.
 *
 * RULEAZA SINGUR inaintea fiecarui push: garda il cheama, ca un client aparut in calendar de
 * la ultimul push sa fie cunoscut fara sa-si aminteasca nimeni sa dea o comanda. Lista se
 * ADUNA peste cea existenta, deci un push fara internet nu o saraceste.
 *
 *   node scripts/build-denylist.mjs            # scrie lista
 *   node scripts/build-denylist.mjs --dry-run  # doar arata ce ar scrie
 *   node scripts/build-denylist.mjs --fresh    # reconstruieste de la zero, fara mostenire
 *   node scripts/build-denylist.mjs --quiet    # o singura linie (cum o cheama garda)
 */

import { existsSync, readFileSync, writeFileSync, mkdirSync, statSync, renameSync } from "node:fs";
import { homedir } from "node:os";
import { createSign } from "node:crypto";
import { execSync } from "node:child_process";
import path from "node:path";

/** Rulat din hook-ul de pre-push: doar o linie de rezumat, nu tot inventarul. */
const quiet = process.argv.includes("--quiet");

/** Info se tace in quiet; AVERTISMENTELE nu — o lista neimprospatata trebuie sa se vada. */
const info = (...a) => { if (!quiet) console.log(...a); };

const LOCAL = process.env.LOCALAPPDATA || path.join(homedir(), "AppData", "Local");
const OUT = process.env.TRACKER_PRIVACY_DENYLIST ||
  path.join(LOCAL, "TimeTracker", "privacy-denylist.txt");

/** Surse de adevar: configul viu, plus orice config din repo-uri private. */
const SOURCES = [
  path.join(LOCAL, "TimeTracker", "tracker.toml"),
  ...(process.argv.filter((a) => a.endsWith(".toml") && existsSync(a))),
];

/**
 * Termeni globali care apar legitim intr-un repo public ca exemple neutre.
 * Orice adaugi aici e o gaura constienta — tine lista scurta si evident-globala.
 */
const GENERIC = new Set([
  "youtube.com", "netflix.com", "facebook.com", "instagram.com", "tiktok.com",
  "reddit.com", "twitter.com", "x.com", "linkedin.com", "github.com",
  "stackoverflow.com", "google.com", "docs.google.com", "meet.google.com",
  "drive.google.com", "mail.google.com", "wikipedia.org", "amazon.com",
  "spotify.com", "twitch.tv", "discord.com", "slack.com", "notion.so",
  "figma.com", "openai.com", "claude.ai", "anthropic.com", "localhost",
  "127.0.0.1", "example.com",
  "chrome.exe", "msedge.exe", "brave.exe", "firefox.exe", "opera.exe",
  "opera_gx.exe", "vivaldi.exe", "arc.exe", "librewolf.exe", "waterfox.exe",
  "explorer.exe", "code.exe", "claude.exe", "steam.exe", "zoom.exe",
  "cmd.exe", "curl.exe", "dotnet.exe", "notepad.exe", "outlook.exe",
  "whatsapp.exe", "teams.exe", "slack.exe", "spotify.exe", "discord.exe",
  "general", "time-tracker", "minutar", "activitywatch", "aw-server",
  "youtube", "netflix", "facebook", "instagram", "tiktok", "reddit",
  "github", "slack", "teams", "notion", "figma", "spotify", "safari",
  "chrome", "edge", "firefox", "whatsapp", "telegram", "gmail", "outlook",
  "claude", "tutorial", "dotnet", "programming", "documentar", "documentary",
  // aplicatii de sistem iOS/Android — le are oricine, nu spun nimic despre nimeni
  "phone", "settings", "photos", "clock", "camera", "messages", "wallet",
  "weather", "calendar", "mail", "maps", "notes", "reminders", "files",
  "app store", "screenshots", "safari", "siri", "home", "music", "podcasts",
  "chatgpt", "gemini", "zoom", "brave", "drive", "docs", "meet", "booking",
  "google maps", "google photos", "authenticator", "uber", "temu", "glovo",
  // cuvinte prea comune ca sa fie semnal, oricat de real ar fi contextul
  "dash", "dashboard", "accounts", "business", "extensions", "downloads",
  "newtab", "login", "office", "workplace", "myportal", "preview", "user",
  "user authentication", "password-manager", "tracker.supervisor.exe",
  "whatsapp.root.exe", "dotnet.microsoft.com", "www.facebook.com",
  // companii si produse la scara globala: apar inevitabil intr-un tracker de timp,
  // iar prezenta lor nu spune nimic despre nimeni anume
  "google", "apple", "microsoft", "timecamp", "rescuetime", "toggl", "clockify",
  "iustin", "aionese",
  // furnizori de email si de infrastructura: apar in orice config care are un cont,
  // iar domeniul in sine nu identifica pe nimeni — partea dinaintea lui @ o face.
  "gmail.com", "outlook.com", "hotmail.com", "yahoo.com", "icloud.com", "proton.me",
  "googleapis.com", "gserviceaccount.com", "oauth2.googleapis.com",
  "calendar.google.com",
  // gazdele de apel video: stau in config ca reguli de recunoastere, nu ca date despre tine.
  // Lasate afara, garda ar tipa chiar la commitul care le adauga in sablon — exact felul de
  // alarma falsa care te invata s-o ocolesti.
  "zoom.us", "us02web.zoom.us", "us06web.zoom.us", "teams.microsoft.com", "teams.live.com",
  "whereby.com", "webex.com", "gotomeeting.com", "skype.com", "hangouts.google.com",
]);

// Un domeniu generic isi face generic si numele: daca „anthropic.com" e acceptat, atunci
// si „anthropic" trebuie sa fie — altfel radacina extrasa mai jos reintra pe usa din dos.
for (const g of [...GENERIC]) {
  const labels = g.split(".");
  if (labels.length >= 2) GENERIC.add(labels[labels.length - 2]);
}

const isGeneric = (s) => GENERIC.has(s.toLowerCase());

/** Prea scurt sau prea comun = alarme false care te invata sa ocolesti garda. */
const tooWeak = (s) => s.length < 4 || /^[\d.\s-]+$/.test(s);

const found = new Map(); // termen -> de unde vine

function add(term, why) {
  const t = String(term).trim();
  if (!t || tooWeak(t) || isGeneric(t)) return;
  if (!found.has(t)) found.set(t, why);
}

for (const src of SOURCES) {
  if (!existsSync(src)) continue;
  const toml = readFileSync(src, "utf8");

  // value = "..." din regulile de clasificare si alocari — cel mai bogat filon
  for (const m of toml.matchAll(/^\s*value\s*=\s*"([^"]+)"/gm)) add(m[1], "regula");
  // name = "..." din projects si phone_apps
  for (const m of toml.matchAll(/^\s*name\s*=\s*"([^"]+)"/gm)) add(m[1], "nume");
  // liste: keywords, domains, apps, browser_profiles, processes, channels, title_keywords
  for (const m of toml.matchAll(
    /^\s*(keywords|domains|apps|browser_profiles|processes|channels|title_keywords)\s*=\s*\[([^\]]*)\]/gm)) {
    for (const item of m[2].matchAll(/["']([^"']+)["']/g)) add(item[1], m[1]);
  }
  // claude_dirs: numele folderului identifica proiectul/clientul
  for (const m of toml.matchAll(/^\s*claude_dirs\s*=\s*\[([^\]]*)\]/gm)) {
    for (const item of m[1].matchAll(/["']([^"']+)["']/g)) {
      for (const seg of item[1].split(/[\\/]/)) {
        if (!/^(c:|users|projects|personal|clients|home)$/i.test(seg)) add(seg, "cale claude");
      }
    }
  }
  // Adrese de email: intra in config odata cu calendarul, si sunt cel mai direct
  // identificator din tot fisierul. Domeniul nu spune nimic — gmail.com il are jumatate
  // de planeta, de-aia e printre GENERICE — dar partea dinaintea lui @ e unica si
  // trebuie sa nu apara niciodata intr-un repo public. Pana la calendar, configul n-avea
  // nicio adresa, deci tiparul asta lipsea cu totul: 0 din 152 de termeni o acopereau.
  for (const m of toml.matchAll(/\b([a-z0-9._%+-]+)@([a-z0-9.-]+\.[a-z]{2,})\b/gi)) {
    add(m[0], "email");
    add(m[1], "email (nume)");
  }
  // orice domeniu care apare oriunde in config, chiar si in comentarii
  for (const m of toml.matchAll(/\b([a-z0-9][a-z0-9-]*(?:\.[a-z0-9-]+)*\.(?:com|ro|eu|net|org|io|app|dev|tv|so|ai))\b/gi)) {
    add(m[1], "domeniu");
    // Numele inregistrabil e penultima eticheta, NU prima. Prima da gunoi: dintr-un
    // „dash.exemplu.com" ar iesi „dash", care apoi ar tipa la fiecare dashboard din cod.
    // Penultima da „exemplu", adica exact numele care conteaza.
    const labels = m[1].split(".");
    if (labels.length >= 2) add(labels[labels.length - 2], "domeniu (nume)");
  }
}

// ---------------------------------------------------------------- calendarul
//
// Configul acopera ce ai clasificat tu. Calendarul acopera pe CINE cunosti — clienti cu care
// ai doar sedinte, un doctor, o programare la o adresa. Nimic din alea nu apare in config,
// deci pana acum garda nu le stia: singura protectie era ca nu le scrie nimeni in cod. Adica
// disciplina, nu mecanism.
//
// Capcana, si de-aia codul de mai jos nu e o simpla adunare de cuvinte: titlurile de sedinte
// sunt pline de cuvinte care apar legitim in cod — Video, Status, Title, Review, Start.
// Adaugate ca termeni, garda ar tipa la fiecare push si te-ar invata s-o ocolesti.
//
// Deci: frazele intregi si sirurile de 2+ cuvinte cu majuscula intra direct (un nume propriu
// aproape niciodata nu apare in cod). Un cuvant singur intra doar daca NU e deja in codul
// propriu. Iar cand e — cazul in care numele a ajuns deja in repo — nu se sare in tacere, se
// RAPORTEAZA: ala nu e un termen de sarit, e o scurgere de investigat.

/**
 * Tot textul urmarit de git, intr-o singura bucata, cu litere mici.
 *
 * De ce textul intreg si nu o lista de cuvinte: garda cauta SUBSIRURI, deci scaderea trebuie
 * sa foloseasca aceeasi masura. Un cuvant de cinci litere dintr-un titlu de calendar care
 * incepe cu „retur" nu e cuvant in cod, dar se aprinde in fiecare `return` din script — 15
 * alarme false dintr-un singur termen, exact cum s-a intamplat prima data. Comparat ca subsir,
 * dispare de la sursa.
 */
function repoText() {
  let files = [];
  try {
    files = execSync("git ls-files", { encoding: "utf8", maxBuffer: 64 * 1024 * 1024 })
      .split(/\r?\n/).filter(Boolean);
  } catch {
    return ""; // rulat in afara unui repo — atunci nu scadem nimic
  }
  const parts = [];
  for (const f of files) {
    // bundle-ul minificat si lock-urile ar aduce megabytes fara sens
    if (/wwwroot[\\/]assets|package-lock\.json|\.(png|jpg|ico|dll|exe|zip|pdf)$/i.test(f)) continue;
    try {
      if (statSync(f).size > 2 * 1024 * 1024) continue;
      parts.push(readFileSync(f, "utf8").toLowerCase());
    } catch { /* binar sau ilizibil */ }
  }
  return parts.join("\n");
}

/** Token de acces pentru contul de serviciu. Acelasi mecanism ca in daemon, fara pachete. */
async function calendarToken(keyFile) {
  const k = JSON.parse(readFileSync(keyFile, "utf8"));
  const b64 = (o) => Buffer.from(typeof o === "string" ? o : JSON.stringify(o)).toString("base64url");
  const now = Math.floor(Date.now() / 1000);
  const head = b64({ alg: "RS256", typ: "JWT" });
  const body = b64({
    iss: k.client_email, scope: "https://www.googleapis.com/auth/calendar.readonly",
    aud: k.token_uri, iat: now, exp: now + 3600,
  });
  const sig = createSign("RSA-SHA256").update(`${head}.${body}`).sign(k.private_key).toString("base64url");
  const res = await fetch(k.token_uri, {
    method: "POST", headers: { "content-type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      grant_type: "urn:ietf:params:oauth:grant-type:jwt-bearer",
      assertion: `${head}.${body}.${sig}`,
    }),
    // ruleaza inaintea fiecarui push: o retea care atarna n-are voie sa-ti blocheze pushul
    signal: AbortSignal.timeout(NET_TIMEOUT),
  });
  const j = await res.json();
  return j.access_token || null;
}

/** Secțiunea [calendar] din TOML, fara sa aducem un parser intreg pentru trei chei. */
function calendarConfig(toml) {
  const i = toml.indexOf("\n[calendar]");
  if (i < 0) return null;
  const rest = toml.slice(i + 1);
  const end = rest.indexOf("\n[", 1);
  const sect = end < 0 ? rest : rest.slice(0, end);
  const val = (key) => {
    const m = sect.match(new RegExp(`^\\s*${key}\\s*=\\s*(?:'([^']*)'|"([^"]*)")`, "m"));
    return m ? (m[1] ?? m[2] ?? "").trim() : "";
  };
  const enabled = /^\s*enabled\s*=\s*true/m.test(sect);
  return { enabled, keyFile: val("key_file"), calendarId: val("calendar_id") };
}

const CAL_BACK_DAYS = 180;
const CAL_FWD_DAYS = 90;

/** Rulam inaintea fiecarui push, deci reteaua nu are voie sa tina ostatic pushul. */
const NET_TIMEOUT = 10_000;

const raportate = [];

async function harvestCalendar() {
  const src = SOURCES.find((s) => existsSync(s));
  if (!src) return;
  const cfg = calendarConfig(readFileSync(src, "utf8"));
  if (!cfg || !cfg.enabled || !cfg.keyFile || !cfg.calendarId) return;

  const keyFile = cfg.keyFile.replace(/%(\w+)%/g, (_, v) => process.env[v] || "");
  if (!existsSync(keyFile)) { console.log("  (calendar: cheia nu exista la calea din config, sar)"); return; }

  let items;
  try {
    const tok = await calendarToken(keyFile);
    if (!tok) { console.log("  (calendar: Google a refuzat cheia, sar)"); return; }
    const from = new Date(Date.now() - CAL_BACK_DAYS * 864e5).toISOString();
    const to = new Date(Date.now() + CAL_FWD_DAYS * 864e5).toISOString();
    const url = `https://www.googleapis.com/calendar/v3/calendars/${encodeURIComponent(cfg.calendarId)}/events`
      + `?timeMin=${encodeURIComponent(from)}&timeMax=${encodeURIComponent(to)}`
      + "&singleEvents=true&maxResults=2500";
    const res = await fetch(url, {
      headers: { authorization: `Bearer ${tok}` },
      signal: AbortSignal.timeout(NET_TIMEOUT),
    });
    if (!res.ok) { console.log(`  (calendar: citirea a esuat, ${res.status}, sar)`); return; }
    items = (await res.json()).items || [];
  } catch (e) {
    console.log("  (calendar: nu am putut citi, sar —", e.message, ")");
    return;
  }

  const cod = repoText();
  const MAJ = /[A-ZĂÂÎȘȚ][\wĂÂÎȘȚăâîșț-]*/g;

  /** Un termen care apare deja in codul propriu n-are ce cauta in lista: ar da alarme false. */
  const inCod = (s) => cod.length > 0 && cod.includes(s.toLowerCase());

  const pune = (s, why) => {
    if (!s || tooWeak(s) || isGeneric(s)) return;
    if (inCod(s)) { raportate.push(s); return; }
    add(s, why);
  };

  /** Fraza intreaga + sirurile de 2+ cuvinte cu majuscula + cuvintele singure sigure. */
  const harvestText = (text, why) => {
    const t = (text || "").replace(/https?:\/\/\S+/g, " ").replace(/\s+/g, " ").trim();
    if (!t) return;
    if (t.includes(" ")) pune(t, why);

    // siruri consecutive: „Nume Client SRL" ca intreg
    for (const r of t.match(/(?:[A-ZĂÂÎȘȚ][\wĂÂÎȘȚăâîșț-]*(?:\s+|$)){2,}/g) || [])
      pune(r.trim(), why + " (nume)");

    for (const m of t.matchAll(MAJ)) pune(m[0], why + " (cuvant)");
  };

  for (const e of items) {
    harvestText(e.summary, "calendar");
    if (e.location && !/https?:\/\//i.test(e.location)) harvestText(e.location, "calendar (loc)");
    for (const a of e.attendees || []) {
      if (a.displayName) harvestText(a.displayName, "invitat");
      if (a.email) { pune(a.email, "invitat (email)"); pune(a.email.split("@")[0], "invitat (nume)"); }
    }
    if (e.organizer?.displayName) harvestText(e.organizer.displayName, "organizator");
  }
  info(`  (calendar: ${items.length} evenimente citite)`);
}

await harvestCalendar();

// ------------------------------------------------------- reuniune cu ce era
//
// Lista nu se rescrie de la zero, se ADUNA peste cea veche. Doua motive, si al doilea e cel
// care conteaza de cand generatorul ruleaza automat inainte de fiecare push:
//
// 1. Un client cu care nu mai lucrezi ramane un om al carui nume n-are ce cauta intr-un repo
//    public. Iesirea lui din calendar nu-l face public.
// 2. Un push fara internet nu poate citi calendarul. Fara reuniune, ar rescrie lista FARA
//    termenii de acolo, si ai ramane cu o garda mai slaba fara sa afli — exact felul de
//    esec tacut pe care garda exista ca sa-l previna.
//
// GENERICELE se scad si din mostenire, ca reglajele de zgomot sa aiba efect si retroactiv.
// `--fresh` reconstruieste de la zero, pentru cand chiar vrei sa scapi de termeni vechi.
if (!process.argv.includes("--fresh") && existsSync(OUT)) {
  let mostenite = 0;
  for (const line of readFileSync(OUT, "utf8").split(/\r?\n/)) {
    const t = line.trim();
    if (!t || t.startsWith("#") || found.has(t)) continue;
    if (tooWeak(t) || isGeneric(t)) continue;
    found.set(t, "mostenit");
    mostenite++;
  }
  if (mostenite) info(`  (${mostenite} termeni pastrati din lista anterioara)`);
}

const terms = [...found.keys()].sort((a, b) => a.localeCompare(b, "ro"));
const header = `# GENERAT de scripts/build-denylist.mjs — nu edita de mana, se suprascrie.
#
# Sursa: configul real (${SOURCES.length} fisier(e)) plus calendarul, cand e configurat.
# Termenii globali sunt scazuti, ca garda sa nu tipe la fiecare push si sa te invete
# s-o ocolesti.
#
# FISIERUL ASTA NU AJUNGE NICIODATA INTR-UN REPO: contine exact numele pe care le
# protejeaza. Pus in repo-ul public, ar publica ce trebuia sa ascunda.
#
# ${terms.length} termeni, regenerat cand iti schimbi configul.

`;

const body = terms.map((t) => t).join("\n") + "\n";

if (process.argv.includes("--dry-run")) {
  console.log(header + body);
  console.log(`# ${terms.length} termeni (dry-run, nu am scris nimic)`);
} else {
  mkdirSync(path.dirname(OUT), { recursive: true });
  // scriere in doi timpi: un fisier taiat la jumatate de o intrerupere ar insemna o lista
  // mai scurta, adica o garda mai slaba — si nimic nu te-ar anunta
  const tmp = OUT + ".tmp";
  writeFileSync(tmp, header + body, "utf8");
  renameSync(tmp, OUT);
  if (quiet) {
    console.log(`lista neagra: ${terms.length} termeni`);
  } else {
    console.log(`${terms.length} termeni scrisi in ${OUT}`);
    const byWhy = new Map();
    for (const why of found.values()) byWhy.set(why, (byWhy.get(why) || 0) + 1);
    for (const [why, n] of [...byWhy].sort((a, b) => b[1] - a[1])) console.log(`  ${why}: ${n}`);
    raportSarite();
  }
}

/**
 * Cuvintele din calendar care apar DEJA in codul propriu nu se pot pune in lista: ar face garda
 * sa tipe la fiecare push. Dar nici nu se sar in tacere — printre ele, majoritatea sunt cuvinte
 * obisnuite („Programare", „Discutie"), insa un NUME aparut aici inseamna ca a ajuns deja in
 * repo. De-aia se afiseaza: e singurul loc din care poti afla asta.
 */
function raportSarite() {
  const u = [...new Set(raportate)].sort((a, b) => a.localeCompare(b, "ro"));
  if (u.length === 0) return;
  console.log(`\n  ${u.length} cuvinte din calendar apar deja in codul propriu, deci nu au intrat`);
  console.log("  in lista (ar da alarme false). Majoritatea sunt cuvinte obisnuite — dar daca");
  console.log("  vezi un NUME printre ele, a ajuns deja in repo si trebuie scos:");
  console.log("    " + u.join(", "));
}
