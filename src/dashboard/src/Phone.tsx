import { useEffect, useMemo, useState } from "react";
import {
  classifyPhoneApps, fetchPhoneWeeks, fetchReport, fetchUnclassifiedPhoneApps, savePhoneWeek,
  type PhoneApp, type PhoneWeek, type Report, type UnclassifiedPhone,
} from "./api";
import { CLASS_LABEL, CLASS_VAR, computeRange, fmtMin, rangeLabel, type ClassName } from "./shared";

/**
 * Import pentru timpul de pe telefon. iOS nu are export de Screen Time — datele
 * există doar pe ecran — așa că utilizatorul face capturi, le duce la LLM-ul lui
 * (orice cont, inclusiv gratuit) și aduce înapoi JSON-ul. Aplicația nu vorbește cu
 * niciun LLM și nu are nevoie de chei: rămâne locală, primește doar rezultatul.
 */

const PROMPT = `Ai mai jos capturi de ecran din Screen Time (iPhone, Setări → Screen Time → See All App & Website Activity).

Citește-le și răspunde DOAR cu un JSON valid, fără text în jur, în formatul:

{
  "device": "iPhone",
  "from": "2026-01-06",
  "to": "2026-01-12",
  "totalMinutes": 1200,
  "pickups": 400,
  "notifications": 600,
  "apps": [
    { "name": "Safari", "minutes": 300 },
    { "name": "WhatsApp", "minutes": 180 }
  ]
}

Reguli:
- "from" și "to" = intervalul afișat în capturi, în format yyyy-MM-dd. Apple scrie capătul
  EXCLUSIV: „Jul 13–20" înseamnă from 2026-07-13, to 2026-07-20 (adică zilele 13..19).
- "totalMinutes" = Total Screen Time al intervalului, convertit în minute (ex. 20h 0m = 1200).
- "apps" = fiecare aplicație din lista Most Used, cu timpul convertit în minute (ex. 5h 0m = 300, 45m = 45).
- Include toate aplicațiile vizibile, în ordinea din listă.
- "name" = numele SCURT al aplicației. Taie sufixul descriptiv de după „:" sau „-" — scrie
  „9GAG", nu „9GAG: Best LOL Pics & GIFs". Numele trebuie să fie identic de la o săptămână
  la alta, altfel aceeași aplicație ajunge socotită de două ori.
- "pickups" = DOAR numărul scris explicit „Total Pickups". Dacă vezi doar o medie pe zi,
  omite complet câmpul.
- "notifications" = la fel: doar dacă apare un TOTAL scris. Ecranul de Notifications arată
  de obicei doar media pe zi — în cazul ăla omite câmpul.
- NU calcula și NU deduce nimic: media × 7 nu e un total. Mai bine lipsește un câmp decât
  să conțină o cifră pe care nu ai citit-o.
- Dacă o captură continuă lista din precedenta, contopește-le fără să repeți aplicații.
- Dacă o cifră nu se vede clar, omite intrarea respectivă.`;


/** Propuneri pentru nume larg cunoscute. Sunt doar preselecții: utilizatorul poate
 *  schimba orice înainte de salvare, iar ce nu e aici pornește de la „neutru". */
const SUGGESTED: Record<string, string> = {
  "9gag": "unproductive", tiktok: "unproductive", facebook: "unproductive",
  instagram: "unproductive", youtube: "unproductive", reddit: "unproductive",
  netflix: "unproductive", twitter: "unproductive", x: "unproductive",
  snapchat: "unproductive", pinterest: "unproductive", twitch: "unproductive",
  slack: "productive", teams: "productive", outlook: "productive", gmail: "productive",
  chatgpt: "productive", claude: "productive", gemini: "productive", notion: "productive",
  zoom: "productive", "google meet": "productive", drive: "productive", excel: "productive",
  word: "productive", figma: "productive", github: "productive", linkedin: "productive",
  revolut: "productive", "yahoo mail": "productive",
};

function suggestClass(name: string): string {
  const n = name.toLowerCase();
  for (const [key, cls] of Object.entries(SUGGESTED)) {
    if (n === key || n.startsWith(key + " ") || n.startsWith(key + ":")) return cls;
  }
  return "neutral";
}

const CLASS_LABELS: { value: string; label: string }[] = [
  { value: "productive", label: "Productiv" },
  { value: "neutral", label: "Neutru" },
  { value: "unproductive", label: "Neproductiv" },
];

type Parsed = {
  device?: string;
  from: string;
  to: string;
  totalMinutes: number;
  apps?: PhoneApp[];
  pickups?: number | null;
  notifications?: number | null;
};

function hm(minutes: number): string {
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  return h > 0 ? `${h}h ${m}m` : `${m}m`;
}

/** Acceptă și JSON înconjurat de text sau de ```json — LLM-urile adaugă des explicații. */
function extractJson(raw: string): string {
  const fenced = raw.match(/```(?:json)?\s*([\s\S]*?)```/);
  const body = fenced ? fenced[1] : raw;
  const start = body.indexOf("{");
  const end = body.lastIndexOf("}");
  return start >= 0 && end > start ? body.slice(start, end + 1) : body.trim();
}

function validate(text: string): { data?: Parsed; error?: string } {
  if (!text.trim()) return { error: "" };
  let obj: unknown;
  try {
    obj = JSON.parse(extractJson(text));
  } catch {
    return { error: "Nu e JSON valid. Copiază doar răspunsul, fără text în jur." };
  }
  const o = obj as Record<string, unknown>;
  const iso = /^\d{4}-\d{2}-\d{2}$/;
  if (typeof o.from !== "string" || !iso.test(o.from)) return { error: "Lipsește „from” (format yyyy-MM-dd)." };
  if (typeof o.to !== "string" || !iso.test(o.to)) return { error: "Lipsește „to” (format yyyy-MM-dd)." };
  if (new Date(o.to) <= new Date(o.from)) return { error: "„to” trebuie să fie după „from”." };
  const total = Number(o.totalMinutes);
  if (!Number.isFinite(total) || total <= 0) return { error: "Lipsește „totalMinutes” (număr de minute)." };

  const days = Math.round((new Date(o.to).getTime() - new Date(o.from).getTime()) / 86400000);
  if (total > days * 1440) {
    return { error: `„totalMinutes” (${total}) depășește câte minute are intervalul (${days * 1440}).` };
  }

  const apps: PhoneApp[] = Array.isArray(o.apps)
    ? (o.apps as Record<string, unknown>[])
        .filter((a) => typeof a?.name === "string" && Number(a?.minutes) > 0)
        .map((a) => ({ name: String(a.name), minutes: Math.round(Number(a.minutes)) }))
        .sort((a, b) => b.minutes - a.minutes)
    : [];

  return {
    data: {
      device: typeof o.device === "string" ? o.device : undefined,
      from: o.from,
      to: o.to,
      totalMinutes: Math.round(total),
      apps,
      pickups: Number.isFinite(Number(o.pickups)) ? Number(o.pickups) : null,
      notifications: Number.isFinite(Number(o.notifications)) ? Number(o.notifications) : null,
    },
  };
}

export default function Phone() {
  const [weeks, setWeeks] = useState<PhoneWeek[]>([]);
  const [raw, setRaw] = useState("");
  const [error, setError] = useState("");
  const [status, setStatus] = useState<"" | "saving" | "saved">("");
  const [copied, setCopied] = useState(false);
  const [manual, setManual] = useState({ from: "", to: "", hours: "", minutes: "" });
  const [pending, setPending] = useState<UnclassifiedPhone>({ apps: [], projects: [] });
  const [choices, setChoices] = useState<Record<string, { class: string; project: string }>>({});
  const [savingRules, setSavingRules] = useState(false);
  const [showAllApps, setShowAllApps] = useState(false);
  // sub-taburi: „Screen Time" e ce vrei să vezi zilnic, „Import" e o operație rară
  const [tab, setTab] = useState<"screentime" | "import">("screentime");
  const [mode, setMode] = useState<"week" | "month">("week");
  const [anchor, setAnchor] = useState(() => new Date());
  const [phoneReport, setPhoneReport] = useState<Report | null>(null);
  const [showAllInReport, setShowAllInReport] = useState(false);

  const [from, to] = useMemo(() => computeRange(mode, anchor), [mode, anchor]);

  useEffect(() => {
    if (tab !== "screentime") return;
    let alive = true;
    fetchReport(from, to)
      .then((r) => alive && setPhoneReport(r))
      .catch(() => alive && setPhoneReport(null));
    return () => {
      alive = false;
    };
  }, [tab, from, to, weeks]);

  const shift = (dir: number) => {
    const a = new Date(anchor);
    if (mode === "week") a.setDate(a.getDate() + dir * 7);
    else a.setMonth(a.getMonth() + dir);
    setAnchor(a);
  };

  const load = () =>
    Promise.all([fetchPhoneWeeks(), fetchUnclassifiedPhoneApps()])
      .then(([w, u]) => {
        setWeeks(w);
        setPending(u);
        // preselectăm propunerea pentru fiecare aplicație nouă, păstrând ce a ales deja
        setChoices((prev) => {
          const next = { ...prev };
          for (const a of u.apps) {
            if (!next[a.name]) next[a.name] = { class: suggestClass(a.name), project: "" };
          }
          return next;
        });
      })
      .catch((e) => setError(String(e)));
  useEffect(() => { load(); }, []);

  const saveRules = async () => {
    setSavingRules(true);
    setError("");
    try {
      await classifyPhoneApps(
        pending.apps.map((a) => ({
          name: a.name,
          class: choices[a.name]?.class ?? "neutral",
          project: choices[a.name]?.project || undefined,
        })),
      );
      await load();
    } catch (e) {
      setError(String(e instanceof Error ? e.message : e));
    } finally {
      setSavingRules(false);
    }
  };

  const { data: parsed, error: parseError } = validate(raw);

  /**
   * Numele dispozitivului face parte din identitatea unei perioade: retrimiterea aceleiași
   * săptămâni o CORECTEAZĂ doar dacă numele coincide. Cu nume diferite („iPhone" vs
   * „iPhone-ul meu") ajung două înregistrări care se adună, iar totalul se dublează în
   * tăcere. E o capcană reală — de aceea o semnalăm înainte de import, nu după.
   */
  const sameRangeOtherDevice = useMemo(() => {
    if (!parsed) return null;
    const dev = (parsed.device || "iPhone").trim().toLowerCase();
    const clash = weeks.find(
      (w) => w.from === parsed.from && w.to === parsed.to && w.device.trim().toLowerCase() !== dev,
    );
    return clash?.device ?? null;
  }, [parsed, weeks]);

  const copyPrompt = async () => {
    try {
      await navigator.clipboard.writeText(PROMPT);
      setCopied(true);
      setTimeout(() => setCopied(false), 2200);
    } catch {
      setError("Nu am putut copia. Selectează textul de mai jos manual.");
    }
  };

  const importParsed = async () => {
    if (!parsed) return;
    setStatus("saving");
    setError("");
    try {
      await savePhoneWeek({ ...parsed, source: "screen-time-llm" });
      setRaw("");
      setShowAllApps(false);
      setStatus("saved");
      await load();
      setTimeout(() => setStatus(""), 2500);
    } catch (e) {
      setStatus("");
      setError(String(e instanceof Error ? e.message : e));
    }
  };

  const importManual = async () => {
    const total = (Number(manual.hours) || 0) * 60 + (Number(manual.minutes) || 0);
    if (!manual.from || !manual.to || total <= 0) {
      setError("Completează perioada și cel puțin un minut.");
      return;
    }
    setStatus("saving");
    setError("");
    try {
      await savePhoneWeek({ from: manual.from, to: manual.to, totalMinutes: total, source: "manual" });
      setManual({ from: "", to: "", hours: "", minutes: "" });
      setStatus("saved");
      await load();
      setTimeout(() => setStatus(""), 2500);
    } catch (e) {
      setStatus("");
      setError(String(e instanceof Error ? e.message : e));
    }
  };

  return (
    <main className="journal">
      <div className="subtabs" role="tablist" aria-label="Secțiuni telefon">
        <button
          role="tab"
          aria-selected={tab === "screentime"}
          className={tab === "screentime" ? "active" : ""}
          onClick={() => setTab("screentime")}
        >
          Screen Time
        </button>
        <button
          role="tab"
          aria-selected={tab === "import"}
          className={tab === "import" ? "active" : ""}
          onClick={() => setTab("import")}
        >
          Import
        </button>
      </div>

      {tab === "screentime" ? (
        <ScreenTime
          report={phoneReport}
          weeks={weeks}
          mode={mode}
          from={from}
          to={to}
          onMode={setMode}
          onShift={shift}
          onToday={() => setAnchor(new Date())}
          showAll={showAllInReport}
          onShowAll={setShowAllInReport}
          onGoImport={() => setTab("import")}
        />
      ) : null}

      {tab === "import" ? (
      <>
      <section className="card">
        <h2>Cum aduci datele</h2>
        <p className="hint">
          iPhone-ul nu permite exportul datelor din Screen Time — nicio aplicație nu le poate
          scoate de pe telefon. Soluția: faci capturi de ecran, le dai unui asistent AI (orice
          cont, inclusiv gratuit) și aduci înapoi rezultatul. Minutar nu trimite nimic nicăieri;
          tu decizi ce arăți și ce imporți.
        </p>

        <ol className="suggest-steps">
          <li>
            Pe telefon: <b>Setări → Screen Time → See All App &amp; Website Activity</b>, cu
            comutatorul pe <b>Week</b>.
          </li>
          <li>
            Fă capturi la, în ordine:
            <ul className="shot-list">
              <li>
                <b>ecranul de sus</b> — trebuie să se vadă intervalul („Jul 20–27") și{" "}
                <b>Total Screen Time</b>;
              </li>
              <li>
                <b>lista Most Used</b>, derulată până la capăt. Apasă <b>Show More</b> și
                continuă: aplicațiile de sub el chiar contează la total;
              </li>
              <li>
                <b>secțiunea Pickups</b> — ai nevoie de rândul <b>Total Pickups</b>. Ăsta
                spune de câte ori ai luat telefonul în mână, nu cât ai stat pe el: 3 ore în
                două sesiuni și 3 ore în optzeci sunt zile complet diferite.
              </li>
            </ul>
          </li>
          <li>
            <b>Notifications nu-ți trebuie.</b> Ecranul ăla arată doar media pe zi, nu un
            total, iar un asistent AI e tentat s-o înmulțească cu 7 și să ți-o dea ca și cum
            ar fi citit-o. Sari peste el.
          </li>
          <li>Apasă <b>Copiază promptul</b> mai jos și lipește-l în ChatGPT, Claude sau Gemini, împreună cu capturile.</li>
          <li>Copiază răspunsul primit și lipește-l în câmpul de mai jos.</li>
          <li>Verifică previzualizarea și apasă <b>Importă</b>.</li>
        </ol>

        <div className="phone-actions">
          <button className="btn primary" onClick={copyPrompt}>
            {copied ? "Copiat ✓" : "Copiază promptul"}
          </button>
        </div>
      </section>

      <section className="card">
        <h2>Lipește răspunsul</h2>
        <textarea
          className="intent-input"
          rows={7}
          placeholder='{ "from": "2026-01-06", "to": "2026-01-12", "totalMinutes": 1200, "apps": [...] }'
          value={raw}
          onChange={(e) => setRaw(e.target.value)}
          spellCheck={false}
        />

        {parseError ? <p className="error">{parseError}</p> : null}

        {parsed ? (
          <>
            <div className="reclass-title" style={{ marginTop: 14 }}>Verifică înainte de import</div>

            {sameRangeOtherDevice ? (
              <p className="warn-note">
                Ai deja aceeași perioadă importată, dar sub alt nume de dispozitiv:{" "}
                <b>„{sameRangeOtherDevice}"</b> față de <b>„{parsed.device || "iPhone"}"</b>. O
                perioadă se suprascrie doar dacă numele coincide — altfel cele două se ADUNĂ, ca
                și cum ai avea două telefoane. Dacă e același telefon, schimbă numele în JSON ca
                să se potrivească, sau șterge perioada veche.
              </p>
            ) : null}
            <div className="phone-preview">
              <span><b>{parsed.from}</b> → <b>{parsed.to}</b></span>
              <span>total <b>{hm(parsed.totalMinutes)}</b></span>
              <span>
                medie{" "}
                <b>
                  {hm(Math.round(parsed.totalMinutes /
                    Math.max(1, Math.round((new Date(parsed.to).getTime() - new Date(parsed.from).getTime()) / 86400000))))}
                </b>{" "}
                pe zi
              </span>
              {parsed.pickups ? <span>{parsed.pickups} ridicări</span> : null}
              {parsed.notifications ? <span>{parsed.notifications} notificări</span> : null}
            </div>

            {parsed.apps?.length ? (
              <div className="phone-list" style={{ marginTop: 12 }}>
                {(showAllApps ? parsed.apps : parsed.apps.slice(0, 12)).map((a) => (
                  <div className="phone-list-row" key={a.name}>
                    <span>{a.name}</span>
                    <span className="val">{hm(a.minutes)}</span>
                  </div>
                ))}
                {parsed.apps.length > 12 ? (
                  <button className="link-btn" onClick={() => setShowAllApps(!showAllApps)}>
                    {showAllApps
                      ? "arată mai puțin"
                      : `…și încă ${parsed.apps.length - 12} aplicații — arată-le`}
                  </button>
                ) : null}
              </div>
            ) : (
              <p className="hint" style={{ marginTop: 10 }}>
                Fără defalcare pe aplicații — se importă doar totalul.
              </p>
            )}

            <div className="phone-actions">
              <button className="btn primary" onClick={importParsed} disabled={status === "saving"}>
                {status === "saving" ? "Se importă…" : "Importă"}
              </button>
              <button className="btn" onClick={() => { setRaw(""); setShowAllApps(false); }}>Renunță</button>
            </div>
          </>
        ) : null}

        {status === "saved" ? <p className="ok">Importat.</p> : null}
        {error ? <p className="error">{error}</p> : null}
      </section>

      <section className="card">
        <h2>Sau scrie direct totalul</h2>
        <p className="hint">Fără AI și fără capturi: doar perioada și cât timp ai stat pe telefon.</p>
        <div className="phone-form">
          <label className="phone-field">
            <span>De la</span>
            <input type="date" value={manual.from} onChange={(e) => setManual({ ...manual, from: e.target.value })} />
          </label>
          <label className="phone-field">
            <span>Până la</span>
            <input type="date" value={manual.to} onChange={(e) => setManual({ ...manual, to: e.target.value })} />
          </label>
          <label className="phone-field">
            <span>Ore</span>
            <input type="number" min="0" style={{ width: 80 }} value={manual.hours}
              onChange={(e) => setManual({ ...manual, hours: e.target.value })} />
          </label>
          <label className="phone-field">
            <span>Minute</span>
            <input type="number" min="0" max="59" style={{ width: 80 }} value={manual.minutes}
              onChange={(e) => setManual({ ...manual, minutes: e.target.value })} />
          </label>
          <button className="btn" onClick={importManual} disabled={status === "saving"}>Adaugă</button>
        </div>
      </section>

      {pending.apps.length > 0 ? (
        <section className="card">
          <h2>Aplicații noi de clasificat ({pending.apps.length})</h2>
          <p className="hint">
            Numele de pe telefon nu se potrivesc cu regulile de pe calculator — „WhatsApp" pe
            iPhone, „WhatsApp.Root.exe" pe PC — așa că au nevoie de o clasă proprie. O alegi o
            dată; la importurile următoare apar doar aplicațiile pe care nu le-am mai văzut.
          </p>

          <div className="phone-classify-list" style={{ marginTop: 12 }}>
            {pending.apps.map((a) => (
              <div className="phone-classify-row" key={a.name}>
                <span className="nm">
                  {a.name}
                  <span className="meta">{hm(a.minutes)}</span>
                </span>
                <div className="seg-choice" role="group" aria-label={`Clasă pentru ${a.name}`}>
                  {CLASS_LABELS.map((c) => (
                    <button
                      key={c.value}
                      className={choices[a.name]?.class === c.value ? "active" : ""}
                      onClick={() =>
                        setChoices({
                          ...choices,
                          [a.name]: { class: c.value, project: choices[a.name]?.project ?? "" },
                        })
                      }
                    >
                      {c.label}
                    </button>
                  ))}
                </div>
                <select
                  value={choices[a.name]?.project ?? ""}
                  onChange={(e) =>
                    setChoices({
                      ...choices,
                      [a.name]: {
                        class: choices[a.name]?.class ?? "neutral",
                        project: e.target.value,
                      },
                    })
                  }
                >
                  <option value="">fără proiect</option>
                  {pending.projects.map((p) => (
                    <option key={p} value={p}>{p}</option>
                  ))}
                </select>
              </div>
            ))}
          </div>

          <div className="phone-actions">
            <button className="btn primary" onClick={saveRules} disabled={savingRules}>
              {savingRules ? "Se salvează…" : "Salvează clasificările"}
            </button>
          </div>
          <p className="hint" style={{ marginTop: 8 }}>
            Aplicațiile cunoscute pornesc cu o propunere; restul rămân pe „neutru". Poți schimba
            oricând din Setări.
          </p>
        </section>
      ) : null}

      </>
      ) : null}
    </main>
  );
}

/**
 * Tot ce ține de telefon, într-un singur loc: cât, în ce clase, pe ce aplicații și ce
 * perioade ai importat. Cifra principală pe clase e PROCENTUL, nu minutele — suma
 * aplicațiilor din Screen Time nu e egală cu totalul afișat de Apple (pe date reale a
 * ieșit și în plus, și în minus), așa că proporțiile sunt partea de încredere.
 */
function ScreenTime({
  report, weeks, mode, from, to, onMode, onShift, onToday, showAll, onShowAll, onGoImport,
}: {
  report: Report | null;
  weeks: PhoneWeek[];
  mode: "week" | "month";
  from: Date;
  to: Date;
  onMode: (m: "week" | "month") => void;
  onShift: (dir: number) => void;
  onToday: () => void;
  showAll: boolean;
  onShowAll: (v: boolean) => void;
  onGoImport: () => void;
}) {
  const ph = report?.phone;
  const has = (ph?.totalMinutes ?? 0) > 0;
  const appsSum = ph?.appsSumMinutes ?? 0;
  const pct = (minutes: number) => (appsSum > 0 ? Math.round((minutes / appsSum) * 100) : 0);
  const days = Math.max(1, Math.round((to.getTime() - from.getTime()) / 86400000));
  // ridicările vin per perioadă importată, nu agregat — le însumăm pe cele din interval
  const pickups = (ph?.periods ?? []).reduce((sum, p) => sum + (p.pickups ?? 0), 0);
  const apps = ph?.apps ?? [];
  const shown = showAll ? apps : apps.slice(0, 10);

  return (
    <>
      <div className="controls">
        <div className="seg">
          {(["week", "month"] as const).map((m) => (
            <button key={m} className={mode === m ? "active" : ""} onClick={() => onMode(m)}>
              {m === "week" ? "Săptămână" : "Lună"}
            </button>
          ))}
        </div>
        <div className="nav">
          <button onClick={() => onShift(-1)} aria-label="Înapoi">←</button>
          <span className="range-label">{rangeLabel(mode, from, to)}</span>
          <button onClick={() => onShift(1)} aria-label="Înainte">→</button>
          <button className="today" onClick={onToday}>Azi</button>
        </div>
      </div>

      <section className="card">
        <h2>Timp pe telefon</h2>
        <p className="hint">
          Din Screen Time, importat manual. Sunt totaluri raportate de Apple pe perioade
          întregi, nu timp cronometrat — de aceea nu se adună la orele de pe calculator.{" "}
          <a href="#devices">Vezi-le împreună</a>.
        </p>

        {!has ? (
          <p className="empty">
            Fără date în acest interval.{" "}
            <button className="link-btn" onClick={onGoImport}>importă o perioadă</button>
          </p>
        ) : (
          <>
            <div className="dev-total">
              <b>{fmtMin(ph!.totalMinutes)}</b>
              <span className="range-label">
                {fmtMin(Math.round(ph!.totalMinutes / days))} pe zi, în medie
              </span>
            </div>

            {pickups > 0 ? (
              <p className="hint" style={{ marginTop: 8 }}>
                <b>{pickups}</b> ridicări ale telefonului —{" "}
                <b>{Math.round(pickups / days)}</b> pe zi, adică una la{" "}
                <b>~{Math.round((16 * 60) / Math.max(1, pickups / days))} min</b> de veghe.
                Cifra asta spune cât de <i>des</i>, nu cât de <i>mult</i>: același total de ore
                fărâmițat în mai multe reprize e altă zi.
              </p>
            ) : null}

            <div className="phone-list" style={{ marginTop: 14 }}>
              {(["productive", "neutral", "unproductive"] as ClassName[]).map((cls) => {
                const v = ph!.byClass?.[cls] ?? 0;
                if (v <= 0) return null;
                return (
                  <div className="phone-list-row" key={cls}>
                    <span>
                      <i className="swatch-inline" style={{ background: CLASS_VAR[cls] }} aria-hidden="true" />
                      {CLASS_LABEL[cls]}
                    </span>
                    <span className="val">
                      <b>{pct(v)}%</b>
                      <span className="meta">{fmtMin(v)} după cifrele Apple</span>
                    </span>
                  </div>
                );
              })}
              {ph!.unclassifiedMinutes > 0 ? (
                <div className="phone-list-row">
                  <span className="range-label">
                    neclasificat
                    <span className="meta">
                      <button className="link-btn" onClick={onGoImport}>clasifică-le</button>
                    </span>
                  </span>
                  <span className="val">
                    <b>{pct(ph!.unclassifiedMinutes)}%</b>
                    <span className="meta">{fmtMin(ph!.unclassifiedMinutes)} după cifrele Apple</span>
                  </span>
                </div>
              ) : null}
            </div>

            {Object.keys(ph!.byProject).length > 0 ? (
              <>
                <div className="reclass-title" style={{ marginTop: 16 }}>Pe proiecte</div>
                <div className="phone-list">
                  {Object.entries(ph!.byProject).map(([name, minutes]) => (
                    <div className="phone-list-row" key={name}>
                      <span>{name}</span>
                      <span className="val">{fmtMin(minutes)}</span>
                    </div>
                  ))}
                </div>
              </>
            ) : null}

            {apps.length > 0 ? (
              <>
                <div className="reclass-title" style={{ marginTop: 16 }}>Aplicații și site-uri</div>
                <div className="phone-list">
                  {shown.map((a) => (
                    <div className="phone-list-row" key={a.name}>
                      <span>{a.name}</span>
                      <span className="val">{fmtMin(a.minutes)}</span>
                    </div>
                  ))}
                  {apps.length > 10 ? (
                    <button className="link-btn" onClick={() => onShowAll(!showAll)}>
                      {showAll ? "arată mai puțin" : `…și încă ${apps.length - 10} — arată-le`}
                    </button>
                  ) : null}
                </div>
              </>
            ) : null}

            {appsSum !== ph!.totalMinutes ? (
              <p className="hint" style={{ marginTop: 12 }}>
                Cifrele Apple nu se închid între ele: aplicațiile adună {fmtMin(appsSum)}, iar
                totalul afișat e {fmtMin(ph!.totalMinutes)}. Diferența nu are un sens constant —
                pe date reale a ieșit și în plus, și în minus — așa că nu o „reparăm": totalul
                rămâne cifra Apple, aplicațiile rămân exact cum le dă Apple, iar procentele de
                mai sus sunt partea în care poți avea încredere.
              </p>
            ) : null}
          </>
        )}
      </section>

      <section className="card">
        <h2>Perioade importate</h2>
        {weeks.length === 0 ? (
          <p className="empty">Nimic încă. Prima importare apare aici.</p>
        ) : (
          <div className="phone-list">
            {weeks.map((w) => (
              <div className="phone-list-row" key={`${w.device}-${w.from}`}>
                <span>
                  {w.from} → {w.to}
                  <span className="meta">
                    {w.device}
                    {w.source === "manual" ? " · scris manual" : ""}
                  </span>
                </span>
                <span className="val">
                  {fmtMin(w.totalMinutes)} · {fmtMin(w.avgDailyMinutes)}/zi
                  {w.apps?.length ? ` · ${w.apps.length} aplicații` : ""}
                </span>
              </div>
            ))}
          </div>
        )}
        <p className="hint" style={{ marginTop: 10 }}>
          Retrimiterea aceleiași perioade o corectează, nu o dublează.
        </p>
      </section>
    </>
  );
}
