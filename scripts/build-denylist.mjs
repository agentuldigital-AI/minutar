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
 *   node scripts/build-denylist.mjs            # scrie lista
 *   node scripts/build-denylist.mjs --dry-run  # doar arata ce ar scrie
 */

import { existsSync, readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { homedir } from "node:os";
import path from "node:path";

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

const terms = [...found.keys()].sort((a, b) => a.localeCompare(b, "ro"));
const header = `# GENERAT de scripts/build-denylist.mjs — nu edita de mana, se suprascrie.
#
# Sursa: configul real (${SOURCES.length} fisier(e)). Termenii globali sunt scazuti,
# ca garda sa nu tipe la fiecare push si sa te invete s-o ocolesti.
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
  writeFileSync(OUT, header + body, "utf8");
  console.log(`${terms.length} termeni scrisi in ${OUT}`);
  const byWhy = new Map();
  for (const why of found.values()) byWhy.set(why, (byWhy.get(why) || 0) + 1);
  for (const [why, n] of [...byWhy].sort((a, b) => b[1] - a[1])) console.log(`  ${why}: ${n}`);
}
