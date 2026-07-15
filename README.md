# Minutar

**Tracker de timp automat pentru Windows — 100% local, zero cloud.**

Minutar înregistrează singur pe ce îți pleacă timpul (aplicații, site-uri, proiecte),
îți arată totul într-un dashboard local și te avertizează politicos când aluneci în
distrageri. Datele nu părăsesc NICIODATĂ calculatorul tău — vezi [PRIVACY.md](PRIVACY.md).

> **EN**: Minutar is a fully local, automatic time tracker for Windows (apps, sites,
> projects, distraction warnings, local dashboard — zero cloud, zero telemetry).
> The UI is currently Romanian-only; English is planned.

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

---

## 📥 Instalare (recomandat — pentru oricine)

1. Deschide pagina **[Releases](https://github.com/agentuldigital-AI/minutar/releases/latest)**
   și descarcă **`Minutar-Setup-x.y.z.exe`**.
2. Dă dublu-click pe el.
   - Windows poate arăta un ecran albastru **„Windows protected your PC"** (fiindcă
     aplicația e nouă și încă nesemnată) → apasă **More info → Run anyway**.
   - Instalatorul pune singur și **.NET Desktop Runtime** dacă lipsește (o singură dată).
3. Gata. Iconița **Minutar** apare lângă ceas (system tray), dashboard-ul se deschide
   la **[http://localhost:5601](http://localhost:5601)**, iar aplicația pornește singură
   la fiecare logare.

Cerință: **Windows 10 (22H2) sau Windows 11**. Nimic altceva de instalat.

### Configurare (proiecte, reguli)

Deschide dashboard-ul → tab-ul **Setări**: adaugi proiectele tale, regulile de
clasificare (ce e productiv/neproductiv) și pragurile. Totul se salvează local în
`%LOCALAPPDATA%\TimeTracker\tracker.toml`.

### Extensia de browser (Chrome / Edge) — recomandată

Extensia raportează site-ul/tab-ul activ ca aplicația să știe pe ce proiect lucrezi și
să aplice corect regula pentru video. Instalatorul o pune deja pe disc; o încarci așa:

1. Deschide `chrome://extensions` (sau `edge://extensions`).
2. Activează **Developer mode** (comutatorul din dreapta-sus).
3. **Load unpacked** → alege folderul:
   `%LOCALAPPDATA%\time-tracker\extension`
   (lipește calea în bara Explorer ca s-o găsești rapid).
4. Click pe iconița extensiei → **Options** → scrie un nume pentru profil (ex. „muncă",
   „personal") — același nume îl pui la **browser_profiles** în proiectul potrivit din
   pagina Setări, ca tot ce faci în acel profil să se contorizeze pe acel proiect.
5. Repetă în fiecare profil de browser pe care îl folosești.

> Curând extensia va fi în Chrome Web Store și Edge Add-ons — atunci se va instala
> cu un singur click, fără pașii de mai sus.

### Dezinstalare

Din **Setări Windows → Aplicații → Minutar → Uninstall** (datele tale se păstrează).
Ca să ștergi și datele, rulează dezinstalatorul cu `-RemoveData` (vezi mai jos).

---

## 🛠️ Instalare din sursă (pentru dezvoltatori)

Cerințe: Windows 10 (22H2)/11, [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0),
[Node.js LTS](https://nodejs.org).

```powershell
git clone https://github.com/agentuldigital-AI/minutar.git
cd minutar

# build UI (o singură dată per versiune)
cd src/dashboard && npm ci && npm run build && cd ../..
cd src/extension && npm ci && npm run build && cd ../..

# instalează + pornește (PowerShell CA ADMINISTRATOR)
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1
```

Extensia (build din sursă) se încarcă din `src\extension\dist`.

Hook-urile Claude Code (opțional, pentru metricile per-proiect):
`node hooks\install-claude-hooks.mjs`.

Dezinstalare din sursă:
```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall.ps1             # păstrează datele
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall.ps1 -RemoveData # șterge tot
```

## Arhitectură (pe scurt)

| Componentă | Rol |
|---|---|
| `Tracker.Watcher` (.NET 10) | fereastra activă + AFK → heartbeat-uri REST |
| `Tracker.Daemon` (.NET 10) | store SQLite propriu, motorul de reguli, popup WPF, coach, API + dashboard |
| `Tracker.Supervisor` (.NET 10) | tray, watchdog cu restart, autostart |
| `src/dashboard` (React + Vite) | dashboard-ul servit de daemon pe :5601 |
| `src/extension` (MV3, TypeScript) | url/titlu/audio/canal YouTube per profil → daemon |

## Status

MVP funcțional, folosit zilnic. Instalator per-user gata (fără cerință de SDK).
Urmează: extensia în Chrome Web Store / Edge Add-ons, semnare de cod (elimină
avertismentul SmartScreen), UI în engleză.

## Licență

[MIT](LICENSE) © 2026 Iustin Aionese
