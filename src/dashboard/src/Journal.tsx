import { useEffect, useMemo, useState } from "react";
import { fetchDay, fetchJournal, saveDay, type DayState, type JournalData } from "./api";

function fmt(s: number): string {
  if (s >= 3600) return `${Math.floor(s / 3600)}h ${Math.round((s % 3600) / 60)}m`;
  if (s >= 60) return `${Math.round(s / 60)}m`;
  return `${Math.round(s)}s`;
}

const hm = (iso: string) =>
  new Date(iso).toLocaleTimeString("ro-RO", { hour: "2-digit", minute: "2-digit" });

function buildLines(j: JournalData): string[] {
  const lines: string[] = [];
  const dayName = new Date(j.date + "T00:00:00").toLocaleDateString("ro-RO", {
    weekday: "long", day: "numeric", month: "long", year: "numeric",
  });
  if (!j.activeSeconds || !j.firstActivity) {
    lines.push(`${dayName} — nicio activitate înregistrată.`);
    return lines;
  }
  lines.push(`${dayName} — activitate între ${hm(j.firstActivity)} și ${hm(j.lastActivity!)}.`);

  const p = j.byClass.productive ?? 0;
  const n = j.byClass.neutral ?? 0;
  const u = j.byClass.unproductive ?? 0;
  const pct = (x: number) => (j.activeSeconds > 0 ? Math.round((x / j.activeSeconds) * 100) : 0);
  lines.push(
    `Timp activ ${fmt(j.activeSeconds)}: ${fmt(p)} productiv (${pct(p)}%), ${fmt(n)} neutru (${pct(n)}%), ${fmt(u)} neproductiv (${pct(u)}%).` +
      (j.focus?.score != null ? ` Focus Score ${j.focus.score}/100 (${j.focus.switches} schimbări de context).` : ""),
  );
  if (j.topProjects.length > 0)
    lines.push(`Proiecte: ${j.topProjects.map((x) => `${x.name} (${fmt(x.seconds)})`).join(", ")}.`);
  if (j.claudeWorkSeconds > 0)
    lines.push(
      `Claude Code a lucrat ${fmt(j.claudeWorkSeconds)}${j.claudeTopProject ? `, mai ales pe ${j.claudeTopProject.name} (${fmt(j.claudeTopProject.seconds)})` : ""}.`,
    );
  if (j.longestFocus)
    lines.push(
      `Cel mai lung bloc de focus: ${fmt(j.longestFocus.seconds)}, începând cu ${hm(j.longestFocus.start)}${j.longestFocus.project ? ` (${j.longestFocus.project})` : ""}.`,
    );
  if (j.topApps.length > 0)
    lines.push(`Aplicații principale: ${j.topApps.map((x) => `${x.name} (${fmt(x.seconds)})`).join(", ")}.`);
  if (j.topDomains.length > 0)
    lines.push(`Site-uri principale: ${j.topDomains.map((x) => `${x.name} (${fmt(x.seconds)})`).join(", ")}.`);
  if (j.distraction && j.distraction.seconds >= 60)
    lines.push(`Distragerea principală: ${j.distraction.name} (${fmt(j.distraction.seconds)}).`);
  return lines;
}

export default function Journal() {
  const [anchor, setAnchor] = useState(() => new Date());
  const [journal, setJournal] = useState<JournalData | null>(null);
  const [error, setError] = useState("");
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    fetchJournal(anchor)
      .then((j) => {
        setJournal(j);
        setError("");
      })
      .catch((e) => setError(String(e)));
  }, [anchor]);

  const lines = useMemo(() => (journal ? buildLines(journal) : []), [journal]);

  const copyMarkdown = () => {
    if (!journal) return;
    const md = `## Jurnal ${journal.date}\n\n` + lines.map((l) => `- ${l}`).join("\n");
    void navigator.clipboard.writeText(md).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    });
  };

  const dayLabel = anchor.toLocaleDateString("ro-RO", { weekday: "short", day: "numeric", month: "long" });

  return (
    <main>
      <div className="controls">
        <h2 style={{ margin: 0, fontSize: 15 }}>Jurnalul zilei</h2>
        <div className="nav">
          <button onClick={() => setAnchor(new Date(anchor.getTime() - 864e5))} aria-label="Înapoi">←</button>
          <span className="range-label">{dayLabel}</span>
          <button onClick={() => setAnchor(new Date(anchor.getTime() + 864e5))} aria-label="Înainte">→</button>
          <button className="today" onClick={() => setAnchor(new Date())}>Azi</button>
        </div>
      </div>

      {error && <p className="error">Nu pot genera jurnalul: {error}</p>}

      <section className="card journal">
        {lines.map((l, i) => (
          <p key={i} className={i === 0 ? "journal-head" : ""}>{l}</p>
        ))}
        {lines.length > 0 && (
          <button className="btn" onClick={copyMarkdown}>
            {copied ? "Copiat ✓" : "Copiază (Markdown)"}
          </button>
        )}
      </section>

      <ReviewCard date={anchor} />
    </main>
  );
}

/** Coach v0 — shutdown ritual: 2-minute end-of-day review; tomorrowPlan becomes
 *  tomorrow morning's intent hint. */
function ReviewCard({ date }: { date: Date }) {
  const dateStr = `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
  const [day, setDay] = useState<DayState | null>(null);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    fetchDay(dateStr).then(setDay).catch(() => setDay(null));
  }, [dateStr]);

  if (!day) return null;
  const done = day.priorities.filter((p) => p.done);
  const missed = day.priorities.filter((p) => !p.done && p.text.trim().length > 0);

  const save = () => {
    void saveDay(day).then(() => {
      setSaved(true);
      setTimeout(() => setSaved(false), 1500);
    });
  };

  return (
    <section className="card review">
      <h2>Review de seară {saved && <span className="ok" style={{ fontWeight: 500 }}>salvat ✓</span>}</h2>
      <p className="hint">
        {day.intent ? `Intenția zilei: „${day.intent}”. ` : ""}
        {day.priorities.length > 0
          ? `Priorități: ${done.length}/${day.priorities.length} bifate${missed.length > 0 ? ` — rămase: ${missed.map((p) => p.text).join(", ")}` : " 🎉"}.`
          : "Nicio prioritate setată azi."}
        {day.nudges.length > 0 ? ` Nudge-uri primite: ${day.nudges.length}.` : ""}
      </p>
      <div className="field-col">
        <label>Ce ai realizat / observații</label>
        <textarea
          value={day.shutdownNotes}
          onChange={(e) => setDay({ ...day, shutdownNotes: e.target.value })}
          placeholder="2-3 rânduri: ce a mers, ce nu..."
        />
      </div>
      <div className="field-col">
        <label>Ce muți pe mâine (apare ca sugestie mâine dimineață)</label>
        <textarea
          value={day.tomorrowPlan}
          onChange={(e) => setDay({ ...day, tomorrowPlan: e.target.value })}
          placeholder="primul lucru de atacat mâine..."
        />
      </div>
      <button className="btn primary" onClick={save}>Salvează review-ul</button>
    </section>
  );
}
