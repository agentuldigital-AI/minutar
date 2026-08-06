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
- Dacă în capturi scrie „Last Week", „This Week" sau altceva relativ în loc de date, OMITE
  complet "from" și "to". Nu ghici si nu calcula din data de azi — nu ai de unde sti cand
  au fost facute capturile. Aplicatia cere utilizatorului sa aleaga saptamana.
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

/**
 * Săptămânile pe care le poate alege utilizatorul când capturile scriu „Last Week" în loc
 * de date. Le calculăm noi din ziua curentă și i le arătăm scrise pe litere: dacă l-am pune
 * să tasteze intervalul, ar trebui să nimerească și ziua de start (luni), și convenția de
 * capăt exclusiv — două ocazii de greșit pentru o informație pe care o știm oricum.
 */
function weekOptions(today: Date): { key: string; label: string; from: string; to: string }[] {
  const iso = (d: Date) =>
    `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
  const monday = new Date(today);
  monday.setHours(0, 0, 0, 0);
  monday.setDate(monday.getDate() - ((monday.getDay() + 6) % 7));

  const fmt = (d: Date) => d.toLocaleDateString("ro-RO", { day: "numeric", month: "short" });
  const opts: { key: string; label: string; from: string; to: string }[] = [];
  for (const [back, name] of [[1, "Săptămâna trecută"], [0, "Săptămâna curentă"], [2, "Acum două săptămâni"]] as const) {
    const start = new Date(monday);
    start.setDate(start.getDate() - back * 7);
    const end = new Date(start);
    end.setDate(end.getDate() + 7);
    const last = new Date(end);
    last.setDate(last.getDate() - 1);
    opts.push({
      key: String(back),
      label: `${name} — ${fmt(start)} → ${fmt(last)} (luni→duminică)`,
      from: iso(start),
      to: iso(end),
    });
  }
  return opts;
}

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
  // Ecranul iPhone-ului scrie uneori „Last Week" în loc de date, iar asistentul AI n-are de
  // unde ști ce zi e azi. Atunci lipsesc from/to și le alege utilizatorul dintr-o listă de
  // săptămâni gata calculate — nu le scrie de mână, unde ar greși și capătul, și ziua de start.
  const hasRange = typeof o.from === "string" && iso.test(o.from) && typeof o.to === "string" && iso.test(o.to);
  if (hasRange && new Date(o.to as string) <= new Date(o.from as string)) {
    return { error: "„to” trebuie să fie după „from”." };
  }
  const total = Number(o.totalMinutes);
  if (!Number.isFinite(total) || total <= 0) return { error: "Lipsește „totalMinutes” (număr de minute)." };

  if (hasRange) {
    const days = Math.round((new Date(o.to as string).getTime() - new Date(o.from as string).getTime()) / 86400000);
    if (total > days * 1440) {
      return { error: `„totalMinutes” (${total}) depășește câte minute are intervalul (${days * 1440}).` };
    }
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
      from: hasRange ? (o.from as string) : "",
      to: hasRange ? (o.to as string) : "",
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
  // săptămâna aleasă când capturile scriau „Last Week": implicit cea trecută, care e cazul
  // în care apare eticheta aia pe telefon
  const [weekChoice, setWeekChoice] = useState("1");
  // sub-taburi: „Screen Time" e ce vrei să vezi zilnic, „Import" e o operație rară
  const [tab, setTab] = useState<"screentime" | "import">("screentime");
  const [mode, setMode] = useState<"week" | "month">("week");
  const [anchor, setAnchor] = useState(() => new Date());
  const [phoneReport, setPhoneReport] = useState<Report | null>(null);
  const [showAllInReport, setShowAllInReport] = useState(false);
  const [ruleBusy, setRuleBusy] = useState("");
  /** Reguli aplicate în interfață, dar neconfirmate încă de server. */
  const [optimistic, setOptimistic] = useState<Record<string, { cls: string; project: string }>>({});

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

  /**
   * Schimbă clasa sau proiectul unei aplicații de telefon. Regula e întotdeauna PERMANENTĂ
   * și se aplică retroactiv peste toate perioadele importate: Screen Time dă doar totaluri
   * pe săptămână, deci nu există „doar în ziua asta" ca pe calculator. Reîncărcăm imediat
   * raportul, fiindcă daemonul aplică schimbarea sincron la scriere.
   */
  const applyRule = async (name: string, cls: string, project: string) => {
    setRuleBusy(name);
    // Arătăm schimbarea PE LOC, înainte de confirmarea serverului: recalcularea raportului
    // durează secunde bune pe o lună, iar până acum singurul semn că s-a întâmplat ceva era
    // că două butoane deveneau inactive. Utilizatorul a raportat, pe bună dreptate, că „nu
    // se întâmplă nimic". Dacă salvarea eșuează, revenim și spunem de ce.
    setOptimistic((o) => ({ ...o, [name]: { cls, project } }));
    try {
      await classifyPhoneApps([{ name, class: cls, project }]);
      const [r, unc] = await Promise.all([fetchReport(from, to), fetchUnclassifiedPhoneApps()]);
      setPhoneReport(r);
      setPending(unc);
      setError("");
      setOptimistic((o) => {
        const { [name]: _, ...rest } = o;
        return rest;
      });
    } catch (e) {
      setOptimistic((o) => {
        const { [name]: _, ...rest } = o;
        return rest;
      });
      setError(`Nu am putut salva „${name}": ${e instanceof Error ? e.message : e}`);
    } finally {
      setRuleBusy("");
    }
  };

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

  const { data: rawParsed, error: parseError } = validate(raw);

  const weeks7 = useMemo(() => weekOptions(new Date()), []);
  const needsRange = !!rawParsed && !rawParsed.from;
  /** Ce se importă efectiv: intervalul din JSON dacă există, altfel cel ales de utilizator. */
  const parsed = useMemo(() => {
    if (!rawParsed) return rawParsed;
    if (!needsRange) return rawParsed;
    const w = weeks7.find((x) => x.key === weekChoice) ?? weeks7[0];
    return { ...rawParsed, from: w.from, to: w.to };
  }, [rawParsed, needsRange, weekChoice, weeks7]);

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
      setWeekChoice("1");
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
          projects={pending.projects}
          onRule={applyRule}
          busy={ruleBusy}
          optimistic={optimistic}
          error={error}
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

            {needsRange ? (
              <div className="week-pick">
                <p className="hint" style={{ margin: "0 0 8px" }}>
                  Capturile nu conțineau datele intervalului — pe telefon scria „Last Week"
                  sau „This Week", iar asistentul AI n-avea de unde ști ce zi e azi.{" "}
                  <b>Alege săptămâna</b>, ca importul să nu ajungă peste altă perioadă:
                </p>
                {weeks7.map((w) => (
                  <label key={w.key} className={weekChoice === w.key ? "week-opt active" : "week-opt"}>
                    <input
                      type="radio"
                      name="week-choice"
                      checked={weekChoice === w.key}
                      onChange={() => setWeekChoice(w.key)}
                    />
                    {w.label}
                  </label>
                ))}
              </div>
            ) : null}

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
              <button className="btn" onClick={() => { setRaw(""); setShowAllApps(false);
      setWeekChoice("1"); }}>Renunță</button>
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
  projects, onRule, busy, optimistic, error,
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
  projects: string[];
  onRule: (name: string, cls: string, project: string) => void | Promise<void>;
  busy: string;
  optimistic: Record<string, { cls: string; project: string }>;
  error: string;
}) {
  // cardul apăsat filtrează listele de dedesubt, exact ca pe Dashboard; null = toate
  const [filter, setFilter] = useState<ClassName | "none" | null>(null);
  const ph = report?.phone;
  const has = (ph?.totalMinutes ?? 0) > 0;
  const appsSum = ph?.appsSumMinutes ?? 0;
  const pct = (minutes: number) => (appsSum > 0 ? Math.round((minutes / appsSum) * 100) : 0);
  const days = Math.max(1, Math.round((to.getTime() - from.getTime()) / 86400000));
  // ridicările vin per perioadă importată, nu agregat — le însumăm pe cele din interval
  const pickups = (ph?.periods ?? []).reduce((sum, p) => sum + (p.pickups ?? 0), 0);
  // ce a apăsat utilizatorul bate ce a apucat să răspundă serverul, până se confirmă
  const apps = (ph?.apps ?? []).map((a) =>
    optimistic[a.name]
      ? { ...a, cls: optimistic[a.name].cls, project: optimistic[a.name].project || null }
      : a,
  );

  // Screen Time amestecă site-urile printre aplicații („stiri.example" lângă „WhatsApp");
  // le separăm ca pe calculator, unde Aplicații și Domenii sunt liste distincte
  const isSite = (n: string) => n.includes(".") && !n.includes(" ");
  const inClass = (a: { cls?: string | null }) =>
    filter === null ? true : filter === "none" ? !a.cls : a.cls === filter;
  const visible = apps.filter(inClass);
  const appRows = visible.filter((a) => !isSite(a.name));
  const siteRows = visible.filter((a) => isSite(a.name));

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

      {error ? <p className="error">{error}</p> : null}

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

            <div className="tiles phone-tiles">
              {([
                [null, "Tot timpul", ph!.totalMinutes, null],
                ["productive", CLASS_LABEL.productive, ph!.byClass?.productive ?? 0, CLASS_VAR.productive],
                ["neutral", CLASS_LABEL.neutral, ph!.byClass?.neutral ?? 0, CLASS_VAR.neutral],
                ["unproductive", CLASS_LABEL.unproductive, ph!.byClass?.unproductive ?? 0, CLASS_VAR.unproductive],
                ["none", "Neclasificat", ph!.unclassifiedMinutes, null],
              ] as [ClassName | "none" | null, string, number, string | null][])
                .filter(([key, , min]) => key !== "none" || min > 0)
                .map(([key, label, min, color]) => (
                  <button
                    key={label}
                    className={`tile clickable${filter === key ? " selected" : ""}${filter !== null && filter !== key ? " dim" : ""}`}
                    onClick={() => setFilter(filter === key ? null : key)}
                  >
                    <div className="label">
                      {color ? <span className="dot" style={{ background: color }} /> : null}
                      {label}
                    </div>
                    <div className="value">
                      {key === null ? fmtMin(min) : `${pct(min)}%`}
                    </div>
                    <div className="sub">
                      {key === null
                        ? `${fmtMin(Math.round(min / days))} pe zi, în medie`
                        : `${fmtMin(min)} după cifrele Apple`}
                    </div>
                    <div className="tile-action">
                      {filter === key
                        ? (key === null ? "toate activitățile" : "✓ filtru activ — click ca să-l scoți")
                        : (key === null ? "click: arată tot" : "click: vezi doar astea")}
                    </div>
                  </button>
                ))}
            </div>

            {pickups > 0 ? (
              <p className="hint" style={{ marginTop: 10, marginBottom: 0 }}>
                <b>{pickups}</b> ridicări ale telefonului — <b>{Math.round(pickups / days)}</b> pe
                zi, adică una la <b>~{Math.round((16 * 60) / Math.max(1, pickups / days))} min</b>{" "}
                de veghe. Cifra asta spune cât de <i>des</i>, nu cât de <i>mult</i>.
              </p>
            ) : null}

            {Object.keys(ph!.byProject).length > 0 ? (
              <>
                <div className="reclass-title" style={{ marginTop: 18 }}>Pe proiecte</div>
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

            <div className="columns" style={{ marginTop: 18 }}>
              <PhoneEditor
                title="Aplicații"
                rows={appRows}
                total={appsSum}
                projects={projects}
                onRule={onRule}
                busy={busy}
                showAll={showAll}
                onShowAll={onShowAll}
              />
              <PhoneEditor
                title="Site-uri"
                rows={siteRows}
                total={appsSum}
                projects={projects}
                onRule={onRule}
                busy={busy}
                showAll={showAll}
                onShowAll={onShowAll}
              />
            </div>

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

/**
 * Lista editabilă de aplicații (sau site-uri) de pe telefon. Aceleași gesturi ca pe
 * Dashboard: butoanele colorate mută activitatea în altă clasă, dropdown-ul o pune pe un
 * proiect. Diferența față de calculator: regula e mereu permanentă și se aplică peste
 * TOATE perioadele importate — Screen Time dă totaluri pe săptămână, nu evenimente cu ore,
 * deci „doar în ziua asta" n-ar avea unde să se aplice.
 */
function PhoneEditor({ title, rows, total, projects, onRule, busy, showAll, onShowAll }: {
  title: string;
  rows: { name: string; minutes: number; cls?: string | null; project?: string | null }[];
  total: number;
  projects: string[];
  onRule: (name: string, cls: string, project: string) => void | Promise<void>;
  busy: string;
  showAll: boolean;
  onShowAll: (v: boolean) => void;
}) {
  const LIMIT = 12;
  const shown = showAll ? rows : rows.slice(0, LIMIT);
  const classes: ClassName[] = ["productive", "neutral", "unproductive"];

  return (
    <div className="dev-col">
      <h3>{title}</h3>
      {rows.length === 0 ? (
        <div className="empty">Nimic aici în intervalul afișat.</div>
      ) : (
        <div className="barlist">
          {shown.map((a) => {
            const cls = (a.cls as ClassName | undefined) ?? null;
            const saving = busy === a.name;
            return (
              <div className={`row reclass-row phone-row${saving ? " saving" : ""}`} key={a.name}>
                <span className="name" title={a.name}>
                  {cls ? (
                    <span className="dot" style={{ background: CLASS_VAR[cls] }} />
                  ) : (
                    <span className="dot" style={{ background: "var(--track)" }} title="fără clasă" />
                  )}
                  {a.name}
                </span>
                <span className="track" title={`${fmtMin(a.minutes)} din ${fmtMin(total)}`}>
                  <span
                    className="bar"
                    style={{
                      width: `${total > 0 ? (a.minutes / total) * 100 : 0}%`,
                      background: cls ? CLASS_VAR[cls] : "var(--text-secondary)",
                      opacity: cls ? 1 : 0.35,
                    }}
                  />
                </span>
                <span className="val">
                  {saving ? <span className="saving-tag">se salvează…</span> : fmtMin(a.minutes)}
                </span>
                <span className="mini-btns">
                  {classes
                    .filter((c) => c !== cls)
                    .map((c) => (
                      <button
                        key={c}
                        disabled={busy === a.name}
                        title={`Mută „${a.name}" în ${CLASS_LABEL[c]} — în toate perioadele`}
                        style={{ background: CLASS_VAR[c] }}
                        onClick={() => void onRule(a.name, c, a.project ?? "")}
                      />
                    ))}
                </span>
                <select
                  className="proj-select"
                  value={a.project ?? ""}
                  disabled={busy === a.name}
                  title="Pune timpul acestei aplicații pe un proiect"
                  onChange={(e) => void onRule(a.name, cls ?? "neutral", e.target.value)}
                >
                  <option value="">— fără proiect —</option>
                  {projects.map((p) => (
                    <option key={p} value={p}>{p}</option>
                  ))}
                </select>
              </div>
            );
          })}
          {rows.length > LIMIT ? (
            <button className="link-btn" onClick={() => onShowAll(!showAll)}>
              {showAll ? "arată mai puțin" : `…și încă ${rows.length - LIMIT} — arată-le`}
            </button>
          ) : null}
        </div>
      )}
    </div>
  );
}
