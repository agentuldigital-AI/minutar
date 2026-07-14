# Politica de confidențialitate / Privacy Policy

**RO** — pe scurt: **toate datele rămân pe calculatorul tău. Punct.**

- Minutar înregistrează local: titlurile ferestrelor active, numele aplicațiilor, URL-urile
  și titlurile taburilor de browser, canalul video-ului de YouTube vizionat și perioadele
  de activitate/inactivitate (AFK).
- Totul se stochează exclusiv pe discul tău, în `%LOCALAPPDATA%\TimeTracker\events.db`
  (SQLite) — cu backup nocturn automat, tot local.
- **Zero cloud. Zero telemetrie. Zero reclame. Zero conturi.** Aplicația nu face nicio
  conexiune la internet; serverul intern ascultă DOAR pe `127.0.0.1` (localhost).
- Extensia de browser („Minutar Reporter") citește tabul activ (URL, titlu, stare audio)
  și trimite aceste date **exclusiv la `http://127.0.0.1:5601`** — daemonul local Minutar.
  Nimic nu părăsește calculatorul.
- Dezinstalarea (`scripts/uninstall.ps1`) păstrează implicit datele; cu `-RemoveData`
  se șterg complet și ele.

**EN** — in short: **all data stays on your computer. Period.**

- Minutar records locally: active window titles, application names, browser tab URLs and
  titles, the YouTube channel being watched, and active/AFK periods.
- Everything is stored only on your disk, in `%LOCALAPPDATA%\TimeTracker\events.db`
  (SQLite), with automatic nightly local backups.
- **No cloud. No telemetry. No ads. No accounts.** The app makes no internet connections;
  the internal server listens ONLY on `127.0.0.1` (localhost).
- The browser extension ("Minutar Reporter") reads the active tab (URL, title, audio
  state) and sends this data **exclusively to `http://127.0.0.1:5601`** — the local
  Minutar daemon. Nothing ever leaves your machine.
- Uninstalling (`scripts/uninstall.ps1`) keeps your data by default; `-RemoveData`
  deletes it completely.

Contact: deschide un issue pe GitHub / open a GitHub issue.
