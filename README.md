# Minutar

**Tracker de timp automat pentru Windows — 100% local, zero cloud.**

Minutar înregistrează singur pe ce îți pleacă timpul (aplicații, site-uri, proiecte),
îți arată totul într-un dashboard local și te avertizează politicos când aluneci în
distrageri. Datele nu părăsesc NICIODATĂ calculatorul tău — vezi [PRIVACY.md](PRIVACY.md).

> **EN**: Minutar is a fully local, automatic time tracker for Windows (apps, sites,
> projects, distraction warnings, local dashboard — zero cloud, zero telemetry).
> The UI is currently Romanian-only; English is planned. Install steps below work the same.

## Ce face

- **Urmărire automată** — fereastra activă, aplicația, titlul; detecție AFK (pauzele nu
  se contorizează); video-urile cu sunet contează ca timp activ chiar dacă nu miști mouse-ul.
- **Atribuire pe proiecte** — după cuvinte-cheie din titluri, domenii, aplicații dedicate,
  profiluri de browser (fiecare profil Chrome/Edge poate fi alt client) și, în premieră,
  **sesiunile Claude Code per proiect** (2 metrici: „attention" și „claude-work").
- **Clasificare productiv / neutru / neproductiv** cu reguli editabile din dashboard +
  excepții pentru YouTube educațional (cuvinte în titlu / canale whitelistate).
- **Popup anti-distragere** — apare când stai prea mult pe ceva neproductiv: amâni,
  marchezi ca productiv (devine regulă) sau confirmi că știi ce faci.
- **Dashboard local** ([http://localhost:5601](http://localhost:5601)) — zi/săptămână/lună,
  timeline, heatmap pe ore, defalcări pe clase/proiecte/aplicații/domenii, jurnal zilnic,
  focus score, streak-uri, realocare de timp pe minute între proiecte.
- **Mod Focus** — ferestre de lucru stricte, cu blocarea site-urilor neproductive.
- **100% local** — stocare SQLite în `%LOCALAPPDATA%\TimeTracker`, backup nocturn automat,
  serverul ascultă doar pe localhost.

## Instalare (deocamdată din sursă — instalator simplu în lucru)

Cerințe: Windows 10 (22H2) / Windows 11,
[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0),
[Node.js LTS](https://nodejs.org) (doar pentru build-ul dashboard-ului/extensiei).

> Vrei doar să FOLOSEȘTI aplicația, fără build din sursă? Descarcă `Minutar-Setup-x.y.z.exe`
> din [Releases](https://github.com/agentuldigital-AI/minutar/releases) — instalează tot,
> inclusiv .NET Runtime, fără alte cerințe.

```powershell
git clone https://github.com/agentuldigital-AI/minutar.git
cd minutar

# build UI (o singură dată per versiune)
cd src/dashboard  && npm ci && npm run build && cd ../..
cd src/extension  && npm ci && npm run build && cd ../..

# instalează + pornește (PowerShell CA ADMINISTRATOR)
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1
```

La final: iconița Minutar apare în tray, iar dashboard-ul e pe
[http://localhost:5601](http://localhost:5601). Configurarea (proiecte, reguli, praguri)
se face din pagina **Setări** — fișierul viu e `%LOCALAPPDATA%\TimeTracker\tracker.toml`.

### Extensia de browser (Chrome / Edge)

Până la publicarea în Web Store, se încarcă „unpacked":

1. `chrome://extensions` (sau `edge://extensions`) → activează **Developer mode**.
2. **Load unpacked** → alege folderul `<minutar>\src\extension\dist`.
3. Click pe iconița extensiei → **Options** → dă un nume profilului (ex. „muncă") —
   același nume îl pui la `browser_profiles` în proiectul potrivit din Setări.

### Hook-urile Claude Code (opțional)

Pentru metricile per-proiect din Claude Code: `node hooks\install-claude-hooks.mjs`
(scrie hook-urile în `~/.claude/settings.json`, cu backup `.bak`).

### Dezinstalare

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall.ps1            # păstrează datele
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall.ps1 -RemoveData # șterge tot
```

## Arhitectură (pe scurt)

| Componentă | Rol |
|---|---|
| `Tracker.Watcher` (.NET 8) | fereastra activă + AFK → heartbeat-uri REST |
| `Tracker.Daemon` (.NET 8) | store SQLite propriu, motorul de reguli, popup WPF, coach, API + dashboard |
| `Tracker.Supervisor` (.NET 8) | tray, watchdog cu restart, autostart |
| `src/dashboard` (React + Vite) | dashboard-ul servit de daemon pe :5601 |
| `src/extension` (MV3, TypeScript) | url/titlu/audio/canal YouTube per profil → daemon |

## Status

MVP funcțional, folosit zilnic de autor. Roadmap scurt: instalator „next-next-finish"
(Inno Setup, fără cerință de SDK), publicarea extensiei în Chrome Web Store / Edge
Add-ons, UI în engleză.

## Licență

[MIT](LICENSE) © 2026 Iustin Aionese
