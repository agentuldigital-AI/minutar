import { useEffect, useState } from "react";
import {
  classifyPhoneApps, fetchPhoneWeeks, fetchUnclassifiedPhoneApps, savePhoneWeek,
  type PhoneApp, type PhoneWeek, type UnclassifiedPhone,
} from "./api";

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
- "from" și "to" = intervalul afișat în capturi, în format yyyy-MM-dd.
- "totalMinutes" = Total Screen Time al intervalului, convertit în minute (ex. 20h 0m = 1200).
- "apps" = fiecare aplicație din lista Most Used, cu timpul convertit în minute (ex. 5h 0m = 300, 45m = 45).
- Include toate aplicațiile vizibile, în ordinea din listă.
- "pickups" și "notifications" = totalurile, dacă apar în capturi; altfel omite-le.
- Dacă o captură continuă lista din precedenta, contopește-le fără să repeți aplicații.
- Nu inventa valori: dacă o cifră nu se vede clar, omite aplicația respectivă.`;


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
      <section className="card">
        <h2>Timp pe telefon</h2>
        <p className="hint">
          iPhone-ul nu permite exportul datelor din Screen Time — nicio aplicație nu le poate
          scoate de pe telefon. Soluția: faci capturi de ecran, le dai unui asistent AI (orice
          cont, inclusiv gratuit) și aduci înapoi rezultatul. Minutar nu trimite nimic nicăieri;
          tu decizi ce arăți și ce imporți.
        </p>

        <ol className="suggest-steps">
          <li>Pe telefon: <b>Setări → Screen Time → See All App &amp; Website Activity</b>. Fă 2-3 capturi, derulând lista de aplicații.</li>
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
                {parsed.apps.slice(0, 12).map((a) => (
                  <div className="phone-list-row" key={a.name}>
                    <span>{a.name}</span>
                    <span className="val">{hm(a.minutes)}</span>
                  </div>
                ))}
                {parsed.apps.length > 12 ? (
                  <p className="hint">…și încă {parsed.apps.length - 12} aplicații</p>
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
              <button className="btn" onClick={() => setRaw("")}>Renunță</button>
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

          <div className="phone-list" style={{ marginTop: 12 }}>
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
                  {hm(w.totalMinutes)} · {hm(w.avgDailyMinutes)}/zi
                  {w.apps?.length ? ` · ${w.apps.length} aplicații` : ""}
                </span>
              </div>
            ))}
          </div>
        )}
        <p className="hint" style={{ marginTop: 10 }}>
          Timpul de pe telefon se ține separat de cel măsurat pe calculator și nu se adună la el:
          unul e cronometrat la secundă, celălalt e un total raportat de Apple. Retrimiterea
          aceleiași perioade o corectează, nu o dublează.
        </p>
      </section>
    </main>
  );
}
