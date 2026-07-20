import { Fragment, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { assignClassForDay, assignMinutesForDay, assignToProject, assignToProjectForDay, createProject, fetchClaudeCwds, fetchConfig, fetchDay, fetchReport, focusStart, focusStop, reclassify, removeDayAssignment, saveConfig, saveDay, unassignFromProject, type ConfigData, type DayAssignment, type DayState, type HeatmapRow, type NamedSeconds, type Report } from "./api";

type Mode = "day" | "week" | "month";
type ClassName = "productive" | "neutral" | "unproductive";

const CLASS_LABEL: Record<ClassName, string> = {
  productive: "Productiv",
  neutral: "Neutru",
  unproductive: "Neproductiv",
};
const CLASS_VAR: Record<ClassName, string> = {
  productive: "var(--cls-productive)",
  neutral: "var(--cls-neutral)",
  unproductive: "var(--cls-unproductive)",
};

function fmt(s: number): string {
  if (s >= 3600) return `${Math.floor(s / 3600)}h ${Math.round((s % 3600) / 60)}m`;
  if (s >= 60) return `${Math.round(s / 60)}m`;
  return `${Math.round(s)}s`;
}

/**
 * Bară cu procent „stil baterie": lungimea = ponderea în TOTALUL listei (nu în maxim),
 * procentul stă în bară când încape (≥12%), altfel imediat după capăt; tooltip cu detaliu.
 */
function TrackBar({ seconds, denom, color, opacity, suffix }: {
  seconds: number; denom: number; color: string; opacity?: number;
  /** ex. „din Productiv", „din timpul de browser" — completează tooltip-ul */
  suffix: string;
}) {
  const pct = denom > 0 ? (seconds / denom) * 100 : 0;
  const pctStr = pct >= 1 ? `${Math.round(pct)}%` : "<1%";
  const label = pct >= 1 ? `${Math.round(pct)}%` : null;
  // pragul e la 40%: sub el eticheta stă DUPĂ bară — la procente mici umplutura are
  // prea puțini pixeli pe cardurile înguste și eticheta din interior s-ar tăia
  const inside = pct >= 40;
  return (
    <span className="track" title={`${fmt(seconds)} — ${pctStr} ${suffix}`}>
      <span className="bar" style={{ width: `${Math.min(100, pct)}%`, background: color, opacity }}>
        {label && inside && <i className="pct-in">{label}</i>}
      </span>
      {label && !inside && (
        <i className="pct-out" style={{ left: `calc(${Math.min(100, pct)}% + 3px)` }}>{label}</i>
      )}
    </span>
  );
}

/* sequential single-hue ramp (dataviz): 0 = surface, 3600s = full accent */
function hmColor(activeSeconds: number): string {
  if (activeSeconds <= 0) return "var(--card-2)";
  const p = Math.round(12 + 88 * Math.min(1, activeSeconds / 3600));
  return `color-mix(in srgb, var(--bar-window) ${p}%, var(--card-2))`;
}

function hmDate(iso: string): string {
  return new Date(iso + "T00:00:00").toLocaleDateString("ro-RO", { weekday: "short", day: "2-digit" });
}

function hmValue(row: HeatmapRow, h: number, cls: ClassName | null): number {
  if (cls === "productive") return row.productive[h];
  if (cls === "unproductive") return row.unproductive[h];
  if (cls === "neutral") return Math.max(0, row.active[h] - row.productive[h] - row.unproductive[h]);
  return row.active[h];
}

function computeRange(mode: Mode, anchor: Date): [Date, Date] {
  const from = new Date(anchor);
  from.setHours(0, 0, 0, 0);
  const to = new Date(from);
  if (mode === "day") {
    to.setDate(to.getDate() + 1);
  } else if (mode === "week") {
    const dow = (from.getDay() + 6) % 7; // Monday-first
    from.setDate(from.getDate() - dow);
    to.setTime(from.getTime());
    to.setDate(to.getDate() + 7);
  } else {
    from.setDate(1);
    to.setTime(from.getTime());
    to.setMonth(to.getMonth() + 1);
  }
  return [from, to];
}

function rangeLabel(mode: Mode, from: Date, to: Date): string {
  const optsDay: Intl.DateTimeFormatOptions = { day: "numeric", month: "short" };
  if (mode === "day") return from.toLocaleDateString("ro-RO", { weekday: "short", day: "numeric", month: "long" });
  if (mode === "week") {
    const end = new Date(to.getTime() - 1);
    return `${from.toLocaleDateString("ro-RO", optsDay)} – ${end.toLocaleDateString("ro-RO", optsDay)}`;
  }
  return from.toLocaleDateString("ro-RO", { month: "long", year: "numeric" });
}

interface Tooltip {
  x: number;
  y: number;
  text: string;
}

/** Cheia calendaristică locală (yyyy-MM-dd) a unei date. */
const localDayKey = (d: Date) =>
  `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;

export default function Dashboard() {
  const [mode, setMode] = useState<Mode>("day");
  const [anchor, setAnchor] = useState(() => new Date());
  const [report, setReport] = useState<Report | null>(null);
  const [error, setError] = useState("");
  const [classFilter, setClassFilter] = useState<ClassName | null>(null);
  const [projectFilter, setProjectFilter] = useState<string | null>(null);
  // click pe „Timp activ total": desfășurătorul TUTUROR claselor, stivuit (cererea 2026-07-13);
  // fiecare secțiune e collapsed implicit și se expandează individual
  const [allDetail, setAllDetail] = useState(false);
  const [openCls, setOpenCls] = useState<Record<string, boolean>>({});
  const [tooltip, setTooltip] = useState<Tooltip | null>(null);
  // pin-urile + atribuirile pe zi, pentru badge-urile persistente din cardurile de clasă
  const [cfgData, setCfgData] = useState<ConfigData | null>(null);
  useEffect(() => {
    fetchConfig().then(setCfgData).catch(() => setCfgData(null));
  }, []);

  const [from, to] = useMemo(() => computeRange(mode, anchor), [mode, anchor]);

  const load = useCallback(() => {
    fetchReport(from, to)
      .then((r) => {
        setReport(r);
        setError("");
      })
      .catch((e) => setError(String(e)));
  }, [from, to]);
  // referință mereu-proaspătă pentru timeout-urile de refetch (1.5s): un closure vechi
  // ar reîncărca ziua VECHE peste cea curent afișată la navigare rapidă
  const loadRef = useRef(load);
  loadRef.current = load;

  useEffect(() => {
    load();
    const id = setInterval(load, 60_000);
    return () => clearInterval(id);
  }, [load]);

  // peste miezul nopții: dacă eram ancorați pe „azi", alunecăm pe ziua nouă — altfel
  // intenția/prioritățile și alocările de după 00:00 s-ar scrie tăcut pe ziua VECHE
  const [todayKey, setTodayKey] = useState(() => localDayKey(new Date()));
  const todayKeyRef = useRef(todayKey);
  todayKeyRef.current = todayKey;
  useEffect(() => {
    const id = setInterval(() => {
      const nowKey = localDayKey(new Date());
      if (nowKey === todayKeyRef.current) return;
      const prevKey = todayKeyRef.current;
      setTodayKey(nowKey);
      setAnchor((a) => (localDayKey(a) === prevKey ? new Date() : a));
    }, 30_000);
    return () => clearInterval(id);
  }, []);

  const shift = (dir: -1 | 1) => {
    const a = new Date(anchor);
    if (mode === "day") a.setDate(a.getDate() + dir);
    else if (mode === "week") a.setDate(a.getDate() + 7 * dir);
    else a.setMonth(a.getMonth() + dir);
    setAnchor(a);
  };

  const totals = report?.totals;
  const active = totals?.activeSeconds ?? 0;

  // day view: fereastra timeline-ului = plaja orelor cu activitate, ca la heatmap
  // (prima → ultima oră, fără ore din viitor); fallback 07–24 cât nu există date
  const dayWin = useMemo(() => {
    if (mode !== "day") return { start: from.getTime(), end: to.getTime(), startH: 0, endH: 24 };
    let startH = 7, endH = 24;
    const row = report?.heatmap?.[0];
    if (row) {
      const firstH = row.active.findIndex((v) => v > 0);
      let lastH = -1;
      row.active.forEach((v, h) => { if (v > 0) lastH = h; });
      if (firstH >= 0) {
        startH = firstH;
        endH = Math.min(24, lastH + 1);
      }
    }
    return { start: from.getTime() + startH * 3600e3, end: from.getTime() + endH * 3600e3, startH, endH };
  }, [mode, from, to, report]);
  const winStartMs = dayWin.start;
  const spanMs = dayWin.end - winStartMs;
  const tickHours = useMemo(() => {
    if (mode !== "day") return null;
    const span = Math.max(1, dayWin.endH - dayWin.startH);
    // ferestre scurte: câte o etichetă pe fiecare oră (fără dubluri de rotunjire)
    if (span <= 6) return Array.from({ length: span + 1 }, (_, k) => dayWin.startH + k);
    return Array.from({ length: 6 }, (_, k) => Math.round(dayWin.startH + (k * span) / 5));
  }, [mode, dayWin]);

  const timeline = useMemo(() => {
    if (!report) return [];
    return report.timeline.filter(
      (s) =>
        (!classFilter || s.cls === classFilter) &&
        (!projectFilter || s.project === projectFilter),
    );
  }, [report, classFilter, projectFilter]);

  const projects = report?.byProject.filter((p) => p.seconds + p.claudeWorkSeconds >= 30) ?? [];
  // barele proiectelor = pondere din totalul atribuit (fereastră, respectiv claude-work)
  const projWindowTotal = Math.max(1, projects.reduce((s, p) => s + p.seconds, 0));
  const projClaudeTotal = Math.max(1, projects.reduce((s, p) => s + p.claudeWorkSeconds, 0));

  // opțiunile de proiect = config ∪ raport: un proiect NOU (0s azi) trebuie să fie
  // selectabil în dropdown-uri și în modalul de minute chiar fără timp măsurat încă
  const projectOptions = useMemo(() => {
    const names = new Map<string, string>();
    for (const p of cfgData?.projects ?? []) if (p.name) names.set(p.name.toLowerCase(), p.name);
    for (const p of report?.byProject ?? []) names.set(p.name.toLowerCase(), p.name);
    return [...names.values()];
  }, [cfgData, report]);

  // proiectele „nesalvate" = venite doar din date (raportul le marchează cu configured:false),
  // minus cele deja adoptate între timp. Selectarea lor cere adopție explicită.
  const unsavedProjects = useMemo(() => {
    const cfgNames = new Set((cfgData?.projects ?? []).map((p) => p.name.toLowerCase()));
    return new Set(
      (report?.byProject ?? [])
        .filter((p) => p.configured === false && !cfgNames.has(p.name.toLowerCase()))
        .map((p) => p.name.toLowerCase()),
    );
  }, [cfgData, report]);

  const dayDate = mode === "day"
    ? `${from.getFullYear()}-${String(from.getMonth() + 1).padStart(2, "0")}-${String(from.getDate()).padStart(2, "0")}`
    : undefined;

  // săptămână/lună: agregare pe zi din segmentele (deja filtrate pe clasă/proiect)
  const dayBars = useMemo(() => {
    if (mode === "day") return [];
    const key = (d: Date) =>
      `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
    const map = new Map<string, { p: number; n: number; u: number }>();
    for (const s of timeline) {
      const k = key(new Date(s.t));
      const e = map.get(k) ?? { p: 0, n: 0, u: 0 };
      if (s.cls === "productive") e.p += s.d;
      else if (s.cls === "unproductive") e.u += s.d;
      else e.n += s.d;
      map.set(k, e);
    }
    const days: { key: string; label: string; full: string; p: number; n: number; u: number }[] = [];
    for (const t = new Date(from); t < to; t.setDate(t.getDate() + 1)) {
      const k = key(t);
      const e = map.get(k) ?? { p: 0, n: 0, u: 0 };
      days.push({
        key: k,
        label: mode === "week"
          ? t.toLocaleDateString("ro-RO", { weekday: "short", day: "numeric" })
          : String(t.getDate()),
        full: t.toLocaleDateString("ro-RO", { weekday: "long", day: "numeric", month: "long" }),
        ...e,
      });
    }
    return days;
  }, [mode, timeline, from, to]);

  return (
    <main>
      <div className="controls">
        <div className="seg" role="tablist" aria-label="Interval">
          {(["day", "week", "month"] as Mode[]).map((m) => (
            <button key={m} className={mode === m ? "active" : ""} onClick={() => setMode(m)}>
              {m === "day" ? "Zi" : m === "week" ? "Săptămână" : "Lună"}
            </button>
          ))}
        </div>
        <div className="nav">
          <button onClick={() => shift(-1)} aria-label="Înapoi">←</button>
          <span className="range-label">{rangeLabel(mode, from, to)}</span>
          <button onClick={() => shift(1)} aria-label="Înainte">→</button>
          <button className="today" onClick={() => setAnchor(new Date())}>Azi</button>
        </div>
        <div
          className="nav"
          onMouseMove={(e) =>
            setTooltip({
              x: e.clientX + 12,
              y: e.clientY + 12,
              text: "Focus mode: site-urile neproductive se închid instant, aplicațiile după un countdown anulabil.",
            })
          }
          onMouseLeave={() => setTooltip(null)}
        >
          <button className="btn" onClick={() => void focusStart(25)}>🎯 Focus 25′</button>
          <button className="btn ghost" onClick={() => void focusStop()}>Stop</button>
        </div>
      </div>

      {error && <p className="error">Nu pot citi raportul: {error}. Verifică daemon-ul (:5601).</p>}

      <TodayCard projects={projectOptions} todayKey={todayKey} />

      <BrowserSuggest report={report} />

      <div className="tiles">
        <div
          // radio-grup cu cardurile de clasă: "Total" = starea „toate" — selectat când nu
          // există niciun filtru, estompat când e activă o clasă/un proiect (cererea userului)
          className={`tile hero clickable ${classFilter || projectFilter ? "dim" : "selected"}${allDetail ? " expanded" : ""}`}
          onClick={() => {
            setClassFilter(null);
            setProjectFilter(null);
            setAllDetail((v) => {
              if (!v) setOpenCls({}); // la deschidere, secțiunile pornesc collapsed
              return !v;
            });
          }}
          title={allDetail ? "Click: închide desfășurătorul claselor" : "Click: desfășoară toate clasele (Productiv + Neutru + Neproductiv) și resetează filtrele"}
        >
          <div className="label">
            Timp activ total
            <span className="chev" aria-hidden="true">{allDetail ? "▾" : "▸"}</span>
          </div>
          <div className="value">{fmt(active)}</div>
          <div className="meter" aria-hidden="true" title={`browser ${fmt(totals?.browserSeconds ?? 0)} · local ${fmt(Math.max(0, active - (totals?.browserSeconds ?? 0)))}`}>
            <span style={{ width: `${active > 0 ? Math.round(((totals?.browserSeconds ?? 0) / active) * 100) : 0}%`, background: "var(--bar-window)" }} />
          </div>
          <div className="sub">browser {fmt(totals?.browserSeconds ?? 0)} · local {fmt(Math.max(0, active - (totals?.browserSeconds ?? 0)))}</div>
          {mode === "day" && (totals?.presenceSeconds ?? 0) > 0 && (
            <div className="sub" title="prezent = doar timp înregistrat (activ + AFK); cât e PC-ul oprit nu se contorizează nicăieri">
              prezent {fmt(totals!.presenceSeconds!)} · AFK {fmt(totals!.afkSeconds ?? 0)}
            </div>
          )}
          <div className="sub hint-click">
            {allDetail ? "click: închide detaliile" : "click: vezi detaliile pe clase"}
          </div>
        </div>
        {(Object.keys(CLASS_LABEL) as ClassName[]).map((cls) => {
          const v = totals?.byClass?.[cls] ?? 0;
          const on = classFilter === cls;
          const pct = active > 0 ? Math.round((v / active) * 100) : 0;
          return (
            <div
              key={cls}
              className={`tile clickable ${classFilter && !on ? "dim" : ""} ${on ? "selected" : ""}`}
              onClick={() => { setClassFilter(on ? null : cls); setAllDetail(false); }}
              title="Click: filtrează timeline-ul"
            >
              <div className="label">
                <span className="dot" style={{ background: CLASS_VAR[cls] }} />
                {CLASS_LABEL[cls]}
              </div>
              <div className="value">{fmt(v)}</div>
              <div className="meter" aria-hidden="true">
                <span style={{ width: `${pct}%`, background: CLASS_VAR[cls] }} />
              </div>
              <div className="sub">{pct}% din activ</div>
            </div>
          );
        })}
        <div className="tile">
          <div className="label">
            <span className="dot" style={{ background: "var(--bar-claude)" }} />
            Claude work
          </div>
          <div className="value">{fmt(totals?.claudeWorkSeconds ?? 0)}</div>
          <div className="sub">independent de focus</div>
        </div>
        <div
          className="tile"
          onMouseMove={(e) =>
            setTooltip({
              x: e.clientX + 12,
              y: e.clientY + 12,
              text: `Scorul combină două lucruri:\n\n1) PE CE ți-ai petrecut timpul — ${report?.focus?.classScore ?? 0}/100 (70% din scor)\nCât din timpul activ a fost productiv. Neutrul contează pe jumătate, neproductivul deloc.\n\n2) CÂT DE CONCENTRAT ai lucrat — ${report?.focus?.flowScore ?? 0}/100 (30% din scor)\nCât de des sari între aplicații: sub 30 de schimbări pe oră = punctaj maxim; de la 150/oră scorul ajunge la 0.`,
            })
          }
          onMouseLeave={() => setTooltip(null)}
        >
          <div className="label">
            <span className="dot" style={{ background: "var(--accent)" }} />
            Focus Score
          </div>
          <div className="value">
            {report?.focus?.score ?? "–"}
            <span className="unit">/100</span>
          </div>
          <div className="meter" aria-hidden="true">
            <span style={{ width: `${report?.focus?.score ?? 0}%`, background: "var(--accent)" }} />
          </div>
          <div className="sub">{report?.focus?.switches ?? 0} schimbări context · {report?.focus?.switchesPerHour ?? 0}/h</div>
        </div>
      </div>

      {(() => {
        // desfășurătorul unei clase — refolosit și la filtrul pe o singură clasă (expandat),
        // și stivuit ×3 la click pe „Timp activ total" (acordeon, collapsed implicit)
        const classSection = (cls: ClassName, collapsible = false) => {
          const detail = report?.classDetail?.[cls];
          if (!report || !detail) return null;
          const open = !collapsible || !!openCls[cls];
          const clsSec = totals?.byClass?.[cls] ?? 0;
          const changed = () => {
            setTimeout(() => loadRef.current(), 1500);
            setTimeout(() => fetchConfig().then(setCfgData).catch(() => undefined), 1500);
          };
          return (
            <section className="card" key={cls}>
              <h2
                className={collapsible ? "acc-head" : undefined}
                onClick={collapsible ? () => setOpenCls((o) => ({ ...o, [cls]: !o[cls] })) : undefined}
                title={collapsible ? (open ? "Click: închide secțiunea" : "Click: expandează secțiunea") : undefined}
              >
                <span className="dot" style={{ background: CLASS_VAR[cls], marginRight: 6 }} />
                Activități clasificate „{CLASS_LABEL[cls]}"
                {collapsible && (
                  <span className="acc-sum">
                    {fmt(clsSec)}{active > 0 ? ` · ${Math.round((clsSec / active) * 100)}% din activ` : ""}
                  </span>
                )}
                {collapsible && <span className="chev" aria-hidden="true">{open ? "▾" : "▸"}</span>}
              </h2>
              {open && (
              <>
              <p className="hint">
                apasă un buton colorat ca să reclasifici aplicația/domeniul, sau alege un proiect —
                regula se aplică în ~1s și RETROACTIV în toate rapoartele
              </p>
              <div className="columns">
                <ReclassList
                  title="Aplicații"
                  match="app"
                  items={detail.apps}
                  current={cls}
                  projects={projectOptions}
                  unsaved={unsavedProjects}
                  dayDate={dayDate}
                  cfg={cfgData}
                  allTotals={report.byApp}
                  projectDetail={report.projectDetail}
                  onTip={setTooltip}
                  onChanged={changed}
                />
                <ReclassList
                  title="Domenii"
                  match="domain"
                  items={detail.domains}
                  current={cls}
                  projects={projectOptions}
                  unsaved={unsavedProjects}
                  dayDate={dayDate}
                  cfg={cfgData}
                  allTotals={report.byDomain}
                  projectDetail={report.projectDetail}
                  onTip={setTooltip}
                  browserTotal={detail.apps
                    .filter((a) => (cfgData?.browser?.processes ?? [])
                      .some((p) => p.toLowerCase() === a.name.split(" → ")[0].toLowerCase()))
                    .reduce((s, a) => s + a.seconds, 0)}
                  onChanged={changed}
                />
              </div>
              </>
              )}
            </section>
          );
        };
        if (classFilter) return classSection(classFilter);
        if (allDetail) return (Object.keys(CLASS_LABEL) as ClassName[]).map((c) => classSection(c, true));
        return null;
      })()}

      <section className="card">
        <h2>Heatmap pe ore</h2>
        <p className="hint">
          intensitate = {classFilter ? `timp ${CLASS_LABEL[classFilter].toLowerCase()}` : "timp activ"} în ora
          respectivă (mai închis = mai mult){classFilter ? " — urmează filtrul de clasă selectat" : " — click pe un card de clasă ca să filtrezi"}
        </p>
        {(() => {
          // ca la Timeline: se afișează doar plaja orelor cu activitate (prima → ultima),
          // nu toate cele 24 (cerința userului, 2026-07-12)
          const rows = report?.heatmap ?? [];
          let lo = 24, hi = -1;
          for (const r of rows)
            r.active.forEach((v, h) => { if (v > 0) { if (h < lo) lo = h; if (h > hi) hi = h; } });
          if (hi < 0) { lo = 0; hi = 23; }
          const hours = Array.from({ length: hi - lo + 1 }, (_, i) => lo + i);
          const cols = { gridTemplateColumns: `repeat(${hours.length}, 1fr)` };
          // în day view data din fața rândului e redundantă (e deja sus în pagină)
          const noDate = mode === "day";
          return (
            <>
              <div className={noDate ? "hm-axis no-date" : "hm-axis"}>
                {!noDate && <span />}
                <div className="hm-cells" style={{ ...cols, position: "relative" }}>
                  {hours.map((h) => (
                    <span key={h} className="hm-axis-label">
                      {h === lo || h === hi || h % 3 === 0 ? h : ""}
                    </span>
                  ))}
                  <span className="hm-axis-end">{hi + 1}</span>
                </div>
              </div>
              <div className="heatmap">
                {rows.map((row) => (
                  <div key={row.date} className={noDate ? "hm-row no-date" : "hm-row"}>
                    {!noDate && <span className="hm-date">{hmDate(row.date)}</span>}
                    <div className="hm-cells" style={cols}>
                      {hours.map((h) => (
                        <div
                          key={h}
                          className="hm-cell"
                          style={{ background: hmColor(hmValue(row, h, classFilter)) }}
                          title={`${row.date} ${String(h).padStart(2, "0")}:00 — activ ${fmt(row.active[h])}${row.productive[h] > 0 ? ` · productiv ${fmt(row.productive[h])}` : ""}${row.unproductive[h] > 0 ? ` · neproductiv ${fmt(row.unproductive[h])}` : ""}`}
                        />
                      ))}
                    </div>
                  </div>
                ))}
                {rows.length === 0 && <div className="empty">Fără date în interval.</div>}
              </div>
            </>
          );
        })()}
        <div className="hm-scale">
          <span>puțin</span>
          {[12, 30, 52, 75, 100].map((p) => (
            <i key={p} style={{ background: `color-mix(in srgb, var(--bar-window) ${p}%, var(--card-2))` }} />
          ))}
          <span>mult</span>
        </div>
      </section>

      <section className="card">
        <h2>Timeline</h2>
        <p className="hint">
          Timp activ clasificat{classFilter ? ` — doar ${CLASS_LABEL[classFilter]}` : ""}
          {projectFilter ? `, proiect: ${projectFilter}` : ""}
          {report?.timelineTruncated ? " (trunchiat la ultimele 2000 de segmente)" : ""}
        </p>
        {mode === "day" ? (
          <div
            className="timeline"
            style={{ backgroundImage: `repeating-linear-gradient(to right, var(--border) 0 1px, transparent 1px calc(100% / ${Math.max(1, dayWin.endH - dayWin.startH)}))` }}
            onMouseLeave={() => setTooltip(null)}
            // peste fundalul gri (fără segment) tooltip-ul vechi trebuie să dispară,
            // altfel rămâne agățat textul ultimei liniuțe survolate (bug raportat)
            onMouseMove={(e) => {
              if (e.target === e.currentTarget) setTooltip(null);
            }}
          >
            {/* AFK: hașură pală SUB segmentele de clasă — „plecat de la birou", nu „PC oprit" */}
            {(report?.afkTimeline ?? []).map((s, i) => {
              const start = new Date(s.t).getTime();
              if (start + s.d * 1000 < winStartMs || start > winStartMs + spanMs) return null;
              const left = Math.max(0, ((start - winStartMs) / spanMs) * 100);
              const width = Math.min(((s.d * 1000) / spanMs) * 100, 100 - left);
              return (
                <div
                  key={`afk-${i}`}
                  className="segment afk"
                  style={{ left: `${left}%`, width: `${Math.max(width, 0.05)}%` }}
                  onMouseMove={(e) =>
                    setTooltip({
                      x: e.clientX + 12,
                      y: e.clientY + 12,
                      text: `${new Date(s.t).toLocaleTimeString("ro-RO")} · ${fmt(s.d)} · AFK (pauză)`,
                    })
                  }
                />
              );
            })}
            {timeline.map((s, i) => {
              const start = new Date(s.t).getTime();
              if (start + s.d * 1000 < winStartMs || start > winStartMs + spanMs) return null;
              const left = Math.max(0, ((start - winStartMs) / spanMs) * 100);
              const width = Math.min(((s.d * 1000) / spanMs) * 100, 100 - left);
              const cls = (s.cls as ClassName) in CLASS_VAR ? (s.cls as ClassName) : "neutral";
              return (
                <div
                  key={i}
                  className="segment"
                  style={{
                    left: `${left}%`,
                    width: `${Math.max(width, 0.05)}%`,
                    background: CLASS_VAR[cls],
                  }}
                  onMouseMove={(e) =>
                    setTooltip({
                      x: e.clientX + 12,
                      y: e.clientY + 12,
                      text: `${new Date(s.t).toLocaleTimeString("ro-RO")} · ${fmt(s.d)} · ${CLASS_LABEL[cls]}${s.project ? ` · ${s.project}` : ""}`,
                    })
                  }
                />
              );
            })}
          </div>
        ) : (
          // săptămână/lună: bare stivuite pe zi (banda continuă e ilizibilă la 7+ zile);
          // click pe o zi = drill-down în vizualizarea zilei
          <div className="daybars" onMouseLeave={() => setTooltip(null)}>
            {dayBars.every((d) => d.p + d.n + d.u === 0) && <div className="empty">Fără date în interval.</div>}
            {dayBars.map((d, i) => {
              const total = d.p + d.n + d.u;
              const maxSec = Math.max(1, ...dayBars.map((x) => x.p + x.n + x.u));
              const seg = (v: number) => ({ height: `${(v / maxSec) * 100}%` });
              return (
                <div
                  key={d.key}
                  className="daybar-col"
                  onClick={() => {
                    setAnchor(new Date(d.key + "T12:00:00"));
                    setMode("day");
                  }}
                  onMouseMove={(e) =>
                    setTooltip({
                      x: e.clientX + 12,
                      y: e.clientY + 12,
                      text: `${d.full} — activ ${fmt(total)}\nproductiv ${fmt(d.p)} · neutru ${fmt(d.n)} · neproductiv ${fmt(d.u)}\nclick = deschide ziua`,
                    })
                  }
                >
                  {mode === "week" && <span className="daybar-total">{total >= 60 ? fmt(total) : ""}</span>}
                  <div className="daybar-stack">
                    <span style={{ ...seg(d.u), background: CLASS_VAR.unproductive }} />
                    <span style={{ ...seg(d.n), background: CLASS_VAR.neutral }} />
                    <span style={{ ...seg(d.p), background: CLASS_VAR.productive }} />
                  </div>
                  <span className="daybar-label">{mode === "week" || i % 2 === 0 ? d.label : ""}</span>
                </div>
              );
            })}
          </div>
        )}
        {tickHours && (
          <div className="tick-labels" aria-hidden="true">
            {tickHours.map((h, i) => <span key={i}>{String(h).padStart(2, "0")}</span>)}
          </div>
        )}
        <div className="legend">
          {(Object.keys(CLASS_LABEL) as ClassName[]).map((cls) => (
            <span key={cls}>
              <span className="dot" style={{ background: CLASS_VAR[cls] }} />
              {CLASS_LABEL[cls]}
            </span>
          ))}
        </div>
      </section>

      <section className="card">
        <h2>Proiecte</h2>
        <p className="hint">bara groasă = timp fereastră atribuit · bara subțire = claude-work · click = filtrează timeline-ul</p>
        <div className="barlist">
          {projects.length === 0 && <div className="empty">Nimic atribuit în interval.</div>}
          {projects.map((p) => {
            const on = projectFilter === p.name;
            return (
              <div
                key={p.name}
                className={`row two-series clickable ${projectFilter && !on ? "dim" : ""}`}
                onClick={() => setProjectFilter(on ? null : p.name)}
              >
                <span className="name">
                  {p.name}
                  {unsavedProjects.has(p.name.toLowerCase()) && (
                    <span
                      className="day-chip"
                      style={{ marginLeft: 6 }}
                      title="Proiect dedus din numele folderului unei sesiuni Claude — nu există în config. Alege-l dintr-un dropdown de atribuire ca să-l creezi."
                    >nesalvat</span>
                  )}
                </span>
                <span
                  className="track"
                  title={`${p.name}: ${fmt(p.seconds)} — ${Math.round((p.seconds / projWindowTotal) * 100)}% din timpul atribuit` +
                    (p.claudeWorkSeconds > 0 ? ` · claude-work ${fmt(p.claudeWorkSeconds)} — ${Math.round((p.claudeWorkSeconds / projClaudeTotal) * 100)}% din claude-work` : "")}
                >
                  <span className="bar" style={{ width: `${(p.seconds / projWindowTotal) * 100}%`, background: "var(--bar-window)" }}>
                    {p.seconds / projWindowTotal >= 0.4 && (
                      <i className="pct-in">{Math.round((p.seconds / projWindowTotal) * 100)}%</i>
                    )}
                  </span>
                  <span className="bar stack2" style={{ width: `${(p.claudeWorkSeconds / projClaudeTotal) * 100}%`, background: "var(--bar-claude)" }} />
                </span>
                <span className="val">
                  {fmt(p.seconds)}
                  {p.claudeWorkSeconds > 0 ? ` / ${fmt(p.claudeWorkSeconds)}` : ""}
                </span>
              </div>
            );
          })}
        </div>
        <div className="legend">
          <span><span className="dot" style={{ background: "var(--bar-window)" }} /> timp fereastră</span>
          <span><span className="dot" style={{ background: "var(--bar-claude)" }} /> claude-work</span>
        </div>
      </section>

      {projectFilter && report?.projectDetail?.[projectFilter] && (
        <div className="columns">
          <BarList
            title={`Aplicații — proiect „${projectFilter}"`}
            hint="timp activ atribuit acestui proiect"
            items={report.projectDetail[projectFilter].apps}
            onTip={setTooltip}
          />
          <BarList
            title={`Domenii — proiect „${projectFilter}"`}
            hint="site-uri pe acest proiect"
            items={report.projectDetail[projectFilter].domains}
            onTip={setTooltip}
          />
        </div>
      )}

      <div className="columns">
        <BarList title="Aplicații" hint="timp activ per aplicație" items={report?.byApp ?? []} suffix="din timpul activ" onTip={setTooltip} />
        <BarList
          title="Domenii" hint="site-uri, din extensie" items={report?.byDomain ?? []}
          denom={report?.totals?.browserSeconds} suffix="din timpul de browser" onTip={setTooltip}
        />
        {(report?.byProfile?.length ?? 0) > 0 && (
          <BarList title="Profile browser" hint="profil → proiect" items={report?.byProfile ?? []} suffix="din timpul pe profile" onTip={setTooltip} />
        )}
      </div>

      {tooltip && (
        <div
          className="tooltip"
          style={{
            // clamp inside the viewport so long tooltips never hug the screen edge
            left: Math.max(8, Math.min(tooltip.x, window.innerWidth - 360)),
            top: Math.max(8, Math.min(tooltip.y, window.innerHeight - 190)),
          }}
        >
          {tooltip.text}
        </div>
      )}

      <footer>
        <span>reîmprospătare automată la 60s</span>
      </footer>
    </main>
  );
}

/** Coach v0: intent + top-3 priorities of the day (auto-saved, checkable). */
function TodayCard({ projects, todayKey }: { projects: string[]; todayKey?: string }) {
  const [day, setDay] = useState<DayState | null>(null);
  const [saved, setSaved] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout>>();

  // re-fetch la schimbarea zilei calendaristice: altfel, peste miezul nopții, intenția
  // și prioritățile scrise după 00:00 s-ar salva pe day.date-ul VECHI
  useEffect(() => {
    fetchDay().then(setDay).catch(() => setDay(null));
  }, [todayKey]);
  useEffect(() => () => clearTimeout(timer.current), []);

  const update = (d: DayState) => {
    setDay(d);
    clearTimeout(timer.current);
    timer.current = setTimeout(() => {
      void saveDay(d)
        .then(() => {
          setSaved(true);
          setTimeout(() => setSaved(false), 1500);
        })
        .catch(() => undefined);
    }, 600);
  };

  if (!day) return null;
  const prios = [...day.priorities];
  while (prios.length < 3) prios.push({ text: "", project: "", done: false });

  const setPrio = (i: number, patch: Partial<(typeof prios)[0]>) => {
    const next = prios.map((p, j) => (j === i ? { ...p, ...patch } : p));
    update({ ...day, priorities: next.filter((p) => p.text.trim().length > 0 || p.project.length > 0) });
  };

  return (
    <section className="card today-card">
      <h2>Azi {saved && <span className="ok" style={{ fontWeight: 500 }}>salvat ✓</span>}</h2>
      <input
        className="intent-input"
        value={day.intent}
        placeholder="Ce vrei să termini azi? (intenția zilei)"
        onChange={(e) => update({ ...day, intent: e.target.value })}
      />
      <div className="prio-list">
        {prios.map((p, i) => (
          <div key={i} className="prio-row">
            <input
              type="checkbox"
              checked={p.done}
              disabled={p.text.trim().length === 0}
              onChange={(e) => setPrio(i, { done: e.target.checked })}
            />
            <input
              className={p.done ? "prio-done" : ""}
              value={p.text}
              placeholder={`Prioritatea #${i + 1}`}
              onChange={(e) => setPrio(i, { text: e.target.value })}
            />
            <select value={p.project} onChange={(e) => setPrio(i, { project: e.target.value })} title="Leagă de un proiect (regula „prioritatea #1 neatinsă”)">
              <option value="">— proiect —</option>
              {[...new Set([...projects, p.project].filter(Boolean))].map((n) => (
                <option key={n} value={n}>{n}</option>
              ))}
            </select>
          </div>
        ))}
      </div>
    </section>
  );
}

function ReclassList({
  title, match, items, current, projects, unsaved, onChanged, dayDate, cfg, allTotals, projectDetail, browserTotal, onTip,
}: {
  title: string;
  match: "app" | "domain";
  items: NamedSeconds[];
  current: ClassName;
  projects: string[];
  /** nume (lowercase) de proiecte care există doar în date, nu și în tracker.toml */
  unsaved?: Set<string>;
  onChanged: () => void;
  /** yyyy-MM-dd — set only in day view; enables the "doar ziua asta" assignment group */
  dayDate?: string;
  /** config-ul curent — pentru badge-urile de atribuire persistente */
  cfg: ConfigData | null;
  /** byApp/byDomain complet (toate clasele) — bugetul din modalul de subîmpărțire */
  allTotals?: NamedSeconds[];
  /** defalcarea pe proiecte din raport — tooltip-ul „atribuit: X · Y" de pe nume */
  projectDetail?: Record<string, { apps: NamedSeconds[]; domains: NamedSeconds[] }>;
  /** doar pentru match=domain: timpul de browser al clasei — numitorul barelor
   *  (domeniile sunt o subdiviziune a timpului de browser, nu al întregii clase) */
  browserTotal?: number;
  /** tooltip-ul custom al paginii (clamped la marginea ecranului) — pentru titluri */
  onTip?: (t: { x: number; y: number; text: string } | null) => void;
}) {
  const [busy, setBusy] = useState<string | null>(null);
  const [hidden, setHidden] = useState<string[]>([]);
  // rândurile ascunse după o reclasificare sunt per-ZI: la navigarea pe altă zi se văd iar
  useEffect(() => setHidden([]), [dayDate]);
  const [msg, setMsg] = useState("");
  const [msgFading, setMsgFading] = useState(false);
  const [clsAsk, setClsAsk] = useState<{ name: string; cls: ClassName; x: number; y: number } | null>(null);
  // modal „împarte pe MINUTE" (click pe numele rândului, doar în day view): userul dă
  // doar minutele + proiect/clasă; serverul alege singur ferestrele reale de rulare
  const [splitAsk, setSplitAsk] = useState<string | null>(null);
  // dialog de adopție: proiectele „nesalvate" (nume fabricat din folderul unei sesiuni
  // Claude) trebuie create explicit în tracker.toml înainte de a fi folosite într-o regulă
  const [adoptAsk, setAdoptAsk] = useState<{ value: string; proj: string; scope: "day" | "perm" } | null>(null);
  const [adoptDir, setAdoptDir] = useState("");
  const [adoptBusy, setAdoptBusy] = useState(false);
  const [spRows, setSpRows] = useState<{ min: string; proj: string; cls: string; reqId?: string }[]>([]);
  const [spErr, setSpErr] = useState("");
  const msgTimers = useRef<number[]>([]);
  // rândurile-variantă („zoom.exe → ClientX") vin din backend gata separate; le grupăm
  // sub rândul de bază ca sub-rânduri informative (cerința userului, 2026-07-12 #5)
  const SEP = " → ";
  const baseOf = (n: string) => n.split(SEP)[0];
  const visible = items.filter((i) => !hidden.includes(i.name) && !hidden.includes(baseOf(i.name)));
  // barele = pondere din TOTAL (nu din maxim): aplicații → totalul clasei;
  // domenii → timpul de browser al clasei (subdiviziune, nu însumează 100%)
  const listSum = items.reduce((s, i) => s + i.seconds, 0);
  const barDenom = match === "domain" ? Math.max(browserTotal ?? 0, listSum) : listSum;
  const barSuffix = match === "domain"
    ? `din timpul de browser ${CLASS_LABEL[current]}`
    : `din ${CLASS_LABEL[current]}`;
  const targets = (Object.keys(CLASS_LABEL) as ClassName[]).filter((c) => c !== current);
  const baseRows = visible.filter((i) => !i.name.includes(SEP));
  const orphanBases = [...new Set(
    visible.filter((i) => i.name.includes(SEP)).map((i) => baseOf(i.name))
      .filter((b) => !baseRows.some((x) => x.name.toLowerCase() === b.toLowerCase())),
  )];
  const displayGroups = [
    ...baseRows,
    ...orphanBases.map((b) => ({ name: b, seconds: 0 })),
  ].map((base) => ({
    base,
    variants: visible.filter((i) =>
      i.name.includes(SEP) && baseOf(i.name).toLowerCase() === base.name.toLowerCase()),
  }));

  // confirmarea e tranzitorie: vizibilă 4s, apoi fade 0.5s (cererea userului, 2026-07-10)
  const showMsg = (text: string) => {
    msgTimers.current.forEach(clearTimeout);
    setMsg(text);
    setMsgFading(false);
    msgTimers.current = [
      window.setTimeout(() => setMsgFading(true), 4000),
      window.setTimeout(() => setMsg(""), 4500),
    ];
  };
  useEffect(() => () => msgTimers.current.forEach(clearTimeout), []);

  const done = (name: string, text: string) => {
    setHidden((h) => [...h, name]);
    showMsg(text);
    onChanged();
  };

  const apply = (value: string, cls: ClassName) => {
    setBusy(value);
    void reclassify(match, value, cls)
      .then(() => done(value, `✓ ${value} → ${CLASS_LABEL[cls]} — cardurile se actualizează în ~2s`))
      .catch((e) => showMsg(`Eroare: ${e}`))
      .finally(() => setBusy(null));
  };

  const assign = (value: string, proj: string) => {
    if (!proj) return;
    setBusy(value);
    void assignToProject(match, value, proj)
      .then(() => { showMsg(`✓ ${value} → proiect „${proj}" — permanent`); onChanged(); })
      .catch((e) => showMsg(`Eroare: ${e}`))
      .finally(() => setBusy(null));
  };

  const dayLabel = dayDate
    ? new Date(dayDate + "T00:00:00").toLocaleDateString("ro-RO", { day: "numeric", month: "short" })
    : "";
  const todayStr = (() => {
    const n = new Date();
    return `${n.getFullYear()}-${String(n.getMonth() + 1).padStart(2, "0")}-${String(n.getDate()).padStart(2, "0")}`;
  })();
  // „azi" doar când ziua afișată chiar e azi — pe zilele trecute scrie data (ex. „11 iul.")
  const scopeWord = dayDate === todayStr ? "azi" : dayLabel;

  /** Starea de PROIECT a rândului: atribuirea pe ziua afișată bate pin-ul permanent —
   *  dar pin-ul rămâne vizibil când ziua are doar override de CLASĂ (bug reparat 12 iul). */
  const projectStateOf = (value: string): { project: string; day: boolean } | null => {
    if (!cfg) return null;
    const day = dayDate
      ? cfg.assignments?.find((a) => a.date === dayDate && a.match === match && !a.from && a.value.toLowerCase() === value.toLowerCase())
      : undefined;
    if (day?.project) return { project: day.project, day: true };
    const pin = cfg.projects.find((p) =>
      ((match === "app" ? p.apps : p.domains) ?? []).some((x) => x.toLowerCase() === value.toLowerCase()));
    return pin ? { project: pin.name, day: false } : null;
  };

  /** Browserele sunt doar coajă: site-urile decid clasa/proiectul; regulile pe aplicație
   *  ating numai timpul FĂRĂ site identificat (hint cerut de user, 2026-07-12). */
  const isBrowser = (value: string) =>
    match === "app" && !!cfg?.browser?.processes?.some((p) => p.toLowerCase() === value.toLowerCase());
  const browserHint = " — la un browser afectează DOAR timpul fără site identificat; site-urile decid singure";

  /** Override-ul de CLASĂ valabil doar pe ziua afișată (chip „azi" lângă buline). */
  const classDayOf = (value: string): string | null => {
    if (!cfg || !dayDate) return null;
    const day = cfg.assignments?.find((a) => a.date === dayDate && a.match === match && !a.from && a.value.toLowerCase() === value.toLowerCase());
    return day?.class || null;
  };

  /** Atribuirile pe INTERVAL orar ale rândului, pe ziua afișată (chips „14:00–15:30 → X"). */
  const intervalsOf = (value: string): DayAssignment[] =>
    (cfg?.assignments ?? [])
      .filter((a) => a.date === dayDate && a.match === match && !!a.from && a.value.toLowerCase() === value.toLowerCase())
      .sort((a, b) => a.from.localeCompare(b.from));

  /** Eticheta variantei — TREBUIE să coincidă cu ce compune backend-ul în cheile
   *  „app → variantă" (ReportService: project + clasa în română, unite cu „ · "). */
  const labelOf = (a: DayAssignment): string =>
    [a.project, a.class === "productive" ? "productiv" : a.class === "neutral" ? "neutru" : a.class === "unproductive" ? "neproductiv" : ""]
      .filter(Boolean).join(" · ");

  /** Defalcarea CALCULATĂ pe proiecte a rândului (profil/pin/keywords/alocări la un loc),
   *  sortată descrescător — alimentează tooltip-ul și eticheta „· auto" din dropdown. */
  const computedProjectsOf = (value: string): [string, number][] => {
    if (!projectDetail) return [];
    const low = value.toLowerCase();
    const per: [string, number][] = [];
    for (const [proj, det] of Object.entries(projectDetail)) {
      const rows = (match === "app" ? det.apps : det.domains) ?? [];
      const sec = rows
        .filter((r) => r.name.toLowerCase() === low || r.name.toLowerCase().startsWith(low + " → "))
        .reduce((s, r) => s + r.seconds, 0);
      if (sec >= 30) per.push([proj, sec]);
    }
    return per.sort((a, b) => b[1] - a[1]);
  };

  /** Eticheta stării calculate pentru dropdown, când NU există atribuire explicită:
   *  un proiect domină (≥90% din timpul rândului) → „Proiect · auto"; împărțit → „mixt · auto". */
  const autoLabelOf = (value: string): string | null => {
    const per = computedProjectsOf(value).filter(([, s]) => s >= 60);
    if (per.length === 0) return null;
    const total = budgetOf(value).total;
    if (total > 0 && per[0][1] >= total * 0.9) return `auto · ${per[0][0]}`;
    return "auto · mixt";
  };

  /** Rezumatul de hover al rândului: cum e setat ACUM, fără niciun click. */
  const summaryOf = (value: string): string => {
    const parts: string[] = [];
    // „unde s-a dus timpul acestei aplicații?" — fără drill-down (cerința 2026-07-12)
    const per = computedProjectsOf(value);
    if (per.length) {
      const rest = budgetOf(value).total - per.reduce((s, [, v]) => s + v, 0);
      const bits = per.map(([p, s]) => `${p} ${fmt(s)}`);
      if (rest >= 60) bits.push(`fără proiect ${fmt(rest)}`);
      parts.push(`atribuit (${dayDate ? scopeWord : "perioada afișată"}): ${bits.join(" · ")}`);
    }
    const ps = projectStateOf(value);
    if (ps) parts.push(`proiect: ${ps.project} (${ps.day ? scopeWord : "permanent"})`);
    const cd = classDayOf(value);
    if (cd) parts.push(`clasă ${scopeWord}: ${CLASS_LABEL[cd as ClassName] ?? cd}`);
    const allocs = allocationsOf(value);
    if (allocs.length) {
      parts.push(`alocări: ${allocs.map((g) => {
        const sec = measuredOf(value, g.label);
        return `${g.label}${sec !== null ? ` · ${Math.round(sec / 60)}m` : ""}`;
      }).join(", ")}`);
    }
    return parts.join("\n");
  };

  const unassign = (value: string, kind: "dayproj" | "daycls" | "pin") => {
    setBusy(value);
    const op = kind === "pin" || !dayDate
      ? unassignFromProject(match, value)
      : removeDayAssignment(dayDate, match, value, kind === "dayproj" ? "project" : "class");
    void op
      .then(() => { showMsg(`✓ ${value} — atribuirea a fost scoasă`); onChanged(); })
      .catch((e) => showMsg(`Eroare: ${e}`))
      .finally(() => setBusy(null));
  };

  const assignDay = (value: string, proj: string) => {
    if (!proj || !dayDate) return;
    setBusy(value);
    void assignToProjectForDay(dayDate, match, value, proj)
      .then(() => { showMsg(`✓ ${value} → „${proj}" doar pe ${dayLabel}`); onChanged(); })
      .catch((e) => showMsg(`Eroare: ${e}`))
      .finally(() => setBusy(null));
  };

  const assignClsDay = (value: string, cls: ClassName) => {
    if (!dayDate) return;
    setBusy(value);
    void assignClassForDay(dayDate, match, value, cls)
      .then(() => done(value, `✓ ${value} → ${CLASS_LABEL[cls]} doar pe ${dayLabel}`))
      .catch((e) => showMsg(`Eroare: ${e}`))
      .finally(() => setBusy(null));
  };

  /** Proiect existent doar în date (fără intrare în tracker.toml) — vezi ReportService. */
  const isUnsaved = (proj: string) => !!unsaved?.has(proj.toLowerCase());
  const optLabel = (proj: string) => (isUnsaved(proj) ? `${proj} · nesalvat` : proj);

  /** Selectarea unui proiect nesalvat nu aplică regula direct: deschide dialogul de
   *  adopție, precompletat cu cwd-ul real al sesiunii Claude (dacă daemonul îl știe). */
  const openAdopt = (value: string, proj: string, scope: "day" | "perm") => {
    setAdoptDir("");
    setAdoptAsk({ value, proj, scope });
    void fetchClaudeCwds()
      .then((m) => {
        const hit = Object.entries(m).find(([k]) => k.toLowerCase() === proj.toLowerCase());
        if (hit) setAdoptDir(hit[1]);
      })
      .catch(() => undefined);
  };

  const confirmAdopt = () => {
    if (!adoptAsk) return;
    const { value, proj, scope } = adoptAsk;
    setAdoptBusy(true);
    void createProject(proj, adoptDir.trim() ? [adoptDir.trim()] : [])
      .then(() => {
        setAdoptAsk(null);
        if (scope === "day") assignDay(value, proj);
        else assign(value, proj);
      })
      .catch((e) => showMsg(`Eroare: ${e}`))
      .finally(() => setAdoptBusy(false));
  };

  /** Bugetul de timp al țintei pe ziua afișată, din byApp/byDomain: rândul simplu =
   *  „standard", rândurile „nume → X" = feliile deja alocate pe intervale. */
  const budgetOf = (value: string) => {
    const rows = allTotals ?? [];
    const low = value.toLowerCase();
    const standard = rows.find((r) => r.name.toLowerCase() === low)?.seconds ?? 0;
    const custom = rows.filter((r) => r.name.toLowerCase().startsWith(low + " → ")).reduce((s, r) => s + r.seconds, 0);
    return { total: standard + custom, custom, standard };
  };

  /** null = ok; altfel mesajul de eroare. Disponibilitatea exactă o decide serverul. */
  const validateRows = (rows: { min: string; proj: string; cls: string }[]): string | null => {
    const filled = rows.filter((r) => r.min || r.proj || r.cls);
    if (filled.length === 0) return "adaugă cel puțin o alocare";
    for (const r of filled) {
      const m = Number(r.min);
      if (!r.min || !Number.isInteger(m) || m < 1 || m > 1440) return "minutele trebuie să fie un număr întreg (1-1440)";
      if (!r.proj && !r.cls) return `alege proiect și/sau clasă pentru alocarea de ${r.min} min`;
    }
    return null;
  };

  const applyMinutes = async () => {
    if (!splitAsk || !dayDate) return;
    if (busy) return; // gardă de reintrare: dublu-click ar porni două bucle de salvare
    const name = splitAsk;
    const err = validateRows(spRows);
    if (err) { setSpErr(err); return; }
    // id stabil per rând: serverul deduplică pe el, deci un retry după eșec parțial
    // (sau un răspuns pierdut) nu mai alocă A DOUA OARĂ minutele rândurilor deja salvate
    const rows = spRows.map((r) => (r.reqId ? r : { ...r, reqId: crypto.randomUUID() }));
    setSpRows(rows);
    const filled = rows.filter((r) => r.min || r.proj || r.cls);
    setBusy(name);
    setSpErr("");
    try {
      for (const r of filled)
        await assignMinutesForDay(dayDate, match, name, Number(r.min), r.proj || undefined, r.cls || undefined, r.reqId);
      setSplitAsk(null);
      const total = filled.reduce((s, r) => s + Number(r.min), 0);
      showMsg(`✓ ${name}: ${total} min mutate (${scopeWord}); restul rămâne pe standard`);
      onChanged();
    } catch (e) {
      setSpErr(String(e instanceof Error ? e.message : e));
    } finally {
      setBusy(null);
    }
  };

  /** Alocările existente ale rândului, grupate pe destinație („ClientX", „neproductiv"…). */
  const allocationsOf = (value: string): { label: string; entries: DayAssignment[] }[] => {
    const m = new Map<string, DayAssignment[]>();
    for (const a of intervalsOf(value)) {
      const k = labelOf(a);
      m.set(k, [...(m.get(k) ?? []), a]);
    }
    return [...m.entries()].map(([label, entries]) => ({ label, entries }));
  };

  /** Minutele MĂSURATE ale unei alocări (rândul-variantă din byApp/byDomain). */
  const measuredOf = (value: string, label: string): number | null => {
    const row = (allTotals ?? []).find((r) => r.name.toLowerCase() === `${value} → ${label}`.toLowerCase());
    return row ? row.seconds : null;
  };

  /** Scoate o alocare întreagă (toate ferestrele ei din config). */
  const removeAllocation = async (value: string, label: string) => {
    if (!dayDate) return;
    const group = allocationsOf(value).find((g) => g.label === label);
    if (!group) return;
    setBusy(value);
    try {
      for (const a of group.entries)
        await removeDayAssignment(dayDate, match, value, undefined, a.from, a.to);
      showMsg(`✓ ${value} — alocarea „${label}" a fost scoasă; timpul revine pe standard`);
      onChanged();
    } catch (e) {
      showMsg(`Eroare: ${e instanceof Error ? e.message : e}`);
    } finally {
      setBusy(null);
    }
  };

  return (
    <div>
      {(() => {
        const tipText = match === "app"
          ? `Aplicațiile cu timp în clasa ${CLASS_LABEL[current]} (doar porția lor din această clasă). Bulinele reclasifică, dropdown-ul atribuie la proiect, click pe nume împarte timpul pe minute.`
          : `Site-urile (din extensie) cu timp în clasa ${CLASS_LABEL[current]} — o subdiviziune a timpului de browser; restul aplicațiilor nu au domenii. Barele se raportează la timpul de browser al clasei.`;
        return (
          <h3
            className="reclass-title"
            onMouseMove={(e) => onTip?.({ x: e.clientX + 12, y: e.clientY + 12, text: tipText })}
            onMouseLeave={() => onTip?.(null)}
          >{title}</h3>
        );
      })()}
      {msg && (
        <div className={`${msg.startsWith("Eroare") ? "error" : "ok"} reclass-msg${msgFading ? " fade" : ""}`}>
          {msg}
        </div>
      )}
      <div className="barlist">
        {visible.length === 0 && <div className="empty">Nimic în această clasă.</div>}
        {displayGroups.map(({ base: i, variants }) => (
          <Fragment key={i.name}>
          <div className="row reclass-row">
            <span
              className={dayDate ? "name clickable" : "name"}
              title={[i.name, summaryOf(i.name), dayDate ? `click: împarte pe intervale orare (${scopeWord})` : ""].filter(Boolean).join("\n")}
              onClick={() => {
                if (!dayDate) return;
                setSpRows([{ min: "", proj: "", cls: "" }]);
                setSpErr("");
                setSplitAsk(i.name);
              }}
            >
              {i.name}
              {(() => {
                const cd = classDayOf(i.name);
                return cd ? (
                  <span className="day-chip" title={`${CLASS_LABEL[cd as ClassName] ?? cd} — doar pe ${dayLabel}`}>{scopeWord}</span>
                ) : null;
              })()}
            </span>
            <TrackBar seconds={i.seconds} denom={barDenom} color={CLASS_VAR[current]} suffix={barSuffix} />
            <span className="val">{fmt(i.seconds)}</span>
            <span className="mini-btns">
              {targets.map((c) => (
                <button
                  key={c}
                  disabled={busy === i.name}
                  title={`Mută în ${CLASS_LABEL[c]}` + (isBrowser(i.name) ? browserHint : "")}
                  style={{ background: CLASS_VAR[c] }}
                  onClick={(e) => {
                    if (dayDate) setClsAsk({ name: i.name, cls: c, x: e.clientX, y: e.clientY });
                    else apply(i.name, c);
                  }}
                />
              ))}
            </span>
            <select
              className="proj-select"
              value=""
              disabled={busy === i.name}
              title={(dayDate
                ? "Atribuie unui proiect — doar pe ziua afișată sau permanent"
                : "Pinuiește pe un proiect (bate profilul și keywords)") + (isBrowser(i.name) ? browserHint : "")}
              onChange={(e) => {
                const v = e.target.value;
                if (v === "un:dayproj") unassign(i.name, "dayproj");
                else if (v === "un:daycls") unassign(i.name, "daycls");
                else if (v === "un:pin") unassign(i.name, "pin");
                else if (v.startsWith("day:")) {
                  const p = v.slice(4);
                  if (isUnsaved(p)) openAdopt(i.name, p, "day");
                  else assignDay(i.name, p);
                } else if (v.startsWith("perm:")) {
                  const p = v.slice(5);
                  if (isUnsaved(p)) openAdopt(i.name, p, "perm");
                  else assign(i.name, p);
                }
                else if (v.startsWith("cls:")) assignClsDay(i.name, v.slice(4) as ClassName);
              }}
            >
              {(() => {
                const ps = projectStateOf(i.name);
                const cd = classDayOf(i.name);
                return (
                  <>
                    <option value="">
                      {ps ? `${ps.project} · ${ps.day ? scopeWord : "permanent"}` : (autoLabelOf(i.name) ?? "→ proiect…")}
                    </option>
                    {ps && (
                      <option value={ps.day ? "un:dayproj" : "un:pin"}>
                        ✕ scoate proiectul ({ps.day ? scopeWord : "permanent"})
                      </option>
                    )}
                    {cd && (
                      <option value="un:daycls">✕ scoate „{CLASS_LABEL[cd as ClassName]?.toLowerCase() ?? cd} · {scopeWord}”</option>
                    )}
                  </>
                );
              })()}
              {(() => {
                // proiectele nesalvate stau în grupuri separate: selectarea lor deschide
                // dialogul de adopție, nu aplică regula direct
                const saved = projects.filter((p) => !isUnsaved(p));
                const fresh = projects.filter(isUnsaved);
                if (!dayDate) {
                  return (
                    <>
                      <optgroup label="Proiecte">
                        {saved.map((p) => <option key={p} value={`perm:${p}`}>{p}</option>)}
                      </optgroup>
                      {fresh.length > 0 && (
                        <optgroup label="Nesalvate (se creează la selectare)">
                          {fresh.map((p) => <option key={`n-${p}`} value={`perm:${p}`}>{optLabel(p)}</option>)}
                        </optgroup>
                      )}
                    </>
                  );
                }
                return (
                  <>
                    <optgroup label={`Doar ziua asta (${dayLabel})`}>
                      {saved.map((p) => <option key={`d-${p}`} value={`day:${p}`}>{p}</option>)}
                    </optgroup>
                    {fresh.length > 0 && (
                      <optgroup label={`Doar ziua asta · nesalvate (se creează)`}>
                        {fresh.map((p) => <option key={`dn-${p}`} value={`day:${p}`}>{optLabel(p)}</option>)}
                      </optgroup>
                    )}
                    <optgroup label="Permanent (tot istoricul)">
                      {saved.map((p) => <option key={`p-${p}`} value={`perm:${p}`}>{p}</option>)}
                    </optgroup>
                    {fresh.length > 0 && (
                      <optgroup label="Permanent · nesalvate (se creează)">
                        {fresh.map((p) => <option key={`pn-${p}`} value={`perm:${p}`}>{optLabel(p)}</option>)}
                      </optgroup>
                    )}
                  </>
                );
              })()}
            </select>
          </div>
          {variants.map((v) => {
            const label = v.name.slice(i.name.length + SEP.length);
            const canRemove = dayDate && allocationsOf(i.name).some((g) => g.label === label);
            // bulină informativă cu clasa alocării (dedusă din etichetă) — view-only
            const subCls: ClassName | null =
              label === "productiv" || label.endsWith(" · productiv") ? "productive"
              : label === "neutru" || label.endsWith(" · neutru") ? "neutral"
              : label === "neproductiv" || label.endsWith(" · neproductiv") ? "unproductive"
              : null;
            return (
              <div key={v.name} className="row reclass-row sub">
                <span className="name" title={`${i.name} → ${label} — timp alocat manual (doar pe ${dayLabel})`}>
                  <span className="sub-arrow">↳</span>
                  {subCls && <span className="dot-mini" style={{ background: CLASS_VAR[subCls] }} title={CLASS_LABEL[subCls]} />}
                  {" "}{label}
                  {canRemove && (
                    <span className="chip-x sub-x" title={`Scoate alocarea „${label}"`}
                      onClick={() => void removeAllocation(i.name, label)}>×</span>
                  )}
                </span>
                <TrackBar seconds={v.seconds} denom={barDenom} color={CLASS_VAR[current]} opacity={0.55} suffix={barSuffix} />
                <span className="val">{fmt(v.seconds)}</span>
                <span className="mini-btns" />
                <span />
              </div>
            );
          })}
          </Fragment>
        ))}
      </div>
      {clsAsk && (
        <>
          <div className="popover-overlay" onClick={() => setClsAsk(null)} />
          <div
            className="cls-popover"
            style={{
              left: Math.max(8, Math.min(clsAsk.x - 60, window.innerWidth - 230)),
              top: Math.min(clsAsk.y + 10, window.innerHeight - 120),
            }}
          >
            <div className="cls-popover-title">
              {clsAsk.name} → {CLASS_LABEL[clsAsk.cls]}
            </div>
            {isBrowser(clsAsk.name) && (
              <div className="hint" style={{ marginBottom: 8 }}>
                Browser: afectează doar timpul fără site identificat — site-urile își păstrează clasa lor.
              </div>
            )}
            <div className="suggest-actions">
              <button className="btn primary" onClick={() => { assignClsDay(clsAsk.name, clsAsk.cls); setClsAsk(null); }}>
                Doar azi
              </button>
              <button className="btn ghost" onClick={() => { apply(clsAsk.name, clsAsk.cls); setClsAsk(null); }}>
                Permanent
              </button>
            </div>
          </div>
        </>
      )}
      {adoptAsk && (
        <div className="modal-overlay" onClick={() => setAdoptAsk(null)}>
          <div className="split-modal" onClick={(e) => e.stopPropagation()}>
            <div className="split-head">
              <div>
                <div className="split-title">Creezi proiectul „{adoptAsk.proj}"?</div>
                <div className="split-sub">
                  apare în rapoarte, dar nu există în config — a fost dedus din numele folderului
                  unei sesiuni Claude Code
                </div>
              </div>
              <button className="split-close" title="Închide" onClick={() => setAdoptAsk(null)}>×</button>
            </div>
            <div className="field-col">
              <label>Folder Claude (claude_dirs) — opțional</label>
              <input
                value={adoptDir}
                placeholder="ex. C:\Users\…\yt-cowork"
                onChange={(e) => setAdoptDir(e.target.value)}
              />
              <div className="hint">
                completat = sesiunile viitoare din acel folder intră direct pe proiect, fără nume dedus.
                Gol = proiectul se creează oricum, dar numele va continua să vină din folder.
              </div>
            </div>
            <div className="suggest-actions split-actions">
              <button className="btn primary" disabled={adoptBusy} onClick={confirmAdopt}>
                Creează și atribuie {adoptAsk.scope === "day" ? `(${scopeWord})` : "(permanent)"}
              </button>
              <button className="btn ghost" onClick={() => setAdoptAsk(null)}>Renunță</button>
            </div>
          </div>
        </div>
      )}
      {splitAsk && dayDate && (
        <div className="modal-overlay" onClick={() => setSplitAsk(null)}>
          <div className="split-modal" onClick={(e) => e.stopPropagation()}>
            <div className="split-head">
              <div>
                <div className="split-title" title={splitAsk}>{splitAsk}</div>
                <div className="split-sub">împarte timpul pe minute — {scopeWord}</div>
              </div>
              <button className="split-close" title="Închide" onClick={() => setSplitAsk(null)}>×</button>
            </div>

            {(() => {
              const b = budgetOf(splitAsk);
              // bugetul reflectă LIVE și rândurile încă nesalvate de mai jos
              const pending = spRows.reduce((s, r) => s + (Number(r.min) || 0), 0) * 60;
              const remaining = b.standard - pending;
              return b.total > 0 ? (
                <div className="split-budget">
                  <span>Total {scopeWord}: <b>{fmt(b.total)}</b></span>
                  <span>alocat{pending > 0 ? " (cu rândurile noi)" : ""}: <b>{fmt(b.custom + pending)}</b></span>
                  <span>
                    rămas pe standard:{" "}
                    <b style={remaining < 0 ? { color: "#e5484d" } : undefined}>
                      {remaining < 0 ? `−${fmt(-remaining)} (peste disponibil!)` : fmt(remaining)}
                    </b>
                  </span>
                </div>
              ) : null;
            })()}

            {isBrowser(splitAsk) && (
              <div className="hint" style={{ margin: "0 0 10px" }}>
                Browser: alocarea afectează doar timpul fără site identificat.
              </div>
            )}

            {allocationsOf(splitAsk).length > 0 && (
              <div className="split-exist">
                {allocationsOf(splitAsk).map((g) => {
                  const sec = measuredOf(splitAsk, g.label);
                  return (
                    <span key={g.label} className="day-chip interval-chip">
                      {sec !== null ? `${Math.round(sec / 60)}m → ` : ""}{g.label}
                      <span className="chip-x" title={`Scoate alocarea „${g.label}"`}
                        onClick={() => void removeAllocation(splitAsk, g.label)}>×</span>
                    </span>
                  );
                })}
              </div>
            )}

            <div className="split-rows">
              {spRows.map((r, idx) => (
                <div key={idx} className="split-row">
                  <input
                    className="time-in" placeholder="10" maxLength={4} inputMode="numeric"
                    value={r.min}
                    onChange={(e) => setSpRows((rs) => rs.map((x, j) => j === idx ? { ...x, min: e.target.value.replace(/\D/g, "") } : x))}
                  />
                  <span className="split-dash">min</span>
                  <select
                    value={r.proj}
                    onChange={(e) => setSpRows((rs) => rs.map((x, j) => j === idx ? { ...x, proj: e.target.value } : x))}
                  >
                    <option value="">proiect…</option>
                    {projects.filter((p) => !isUnsaved(p)).map((p) => <option key={p} value={p}>{p}</option>)}
                    {projects.some(isUnsaved) && (
                      <optgroup label="Nesalvate (doar eticheta zilei)">
                        {projects.filter(isUnsaved).map((p) => <option key={`n-${p}`} value={p}>{optLabel(p)}</option>)}
                      </optgroup>
                    )}
                  </select>
                  <select
                    value={r.cls}
                    onChange={(e) => setSpRows((rs) => rs.map((x, j) => j === idx ? { ...x, cls: e.target.value } : x))}
                  >
                    <option value="">clasă…</option>
                    {(Object.keys(CLASS_LABEL) as ClassName[]).map((c) => (
                      <option key={c} value={c}>{CLASS_LABEL[c]}</option>
                    ))}
                  </select>
                  <button
                    className="split-del" title="Scoate rândul"
                    disabled={spRows.length === 1}
                    onClick={() => setSpRows((rs) => rs.filter((_, j) => j !== idx))}
                  >×</button>
                </div>
              ))}
            </div>
            <button className="btn ghost split-add" onClick={() => setSpRows((rs) => [...rs, { min: "", proj: "", cls: "" }])}>
              + Adaugă alocare
            </button>

            {spErr && <div className="error split-err">{spErr}</div>}

            <div className="suggest-actions split-actions">
              <button className="btn primary" disabled={busy === splitAsk} onClick={() => void applyMinutes()}>
                Salvează
              </button>
              <button className="btn ghost" onClick={() => setSplitAsk(null)}>Renunță</button>
            </div>
            <div className="hint" style={{ marginTop: 8 }}>
              Minutele se iau din timpul rulat efectiv, cronologic de la începutul zilei —
              suma alocărilor nu poate depăși totalul. Restul rămâne pe standard.
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

function BarList({ title, hint, items, denom, suffix, onTip }: {
  title: string; hint: string; items: NamedSeconds[];
  /** numitorul barelor; implicit = suma listei (barele însumează 100%) */
  denom?: number;
  /** textul tooltip-ului de pe bară; implicit „din totalul listei" */
  suffix?: string;
  /** tooltip-ul custom al paginii (clamped la marginea ecranului) — pentru titlu */
  onTip?: (t: { x: number; y: number; text: string } | null) => void;
}) {
  const listSum = items.reduce((s, i) => s + i.seconds, 0);
  const barDenom = Math.max(denom ?? 0, listSum);
  const barSuffix = suffix ?? "din totalul listei";
  // variantele („zoom.exe → ClientX") se grupează ca sub-rânduri sub rândul lor de bază;
  // o variantă fără rând de bază (tot timpul e alocat pe intervale) se afișează cu numele întreg
  const SEP = " → ";
  const bases = items.filter((i) => !i.name.includes(SEP));
  const ordered: { item: NamedSeconds; label: string | null }[] = [];
  for (const b of bases) {
    ordered.push({ item: b, label: null });
    for (const v of items.filter((x) => x.name.toLowerCase().startsWith(b.name.toLowerCase() + SEP)))
      ordered.push({ item: v, label: v.name.slice(b.name.length + SEP.length) });
  }
  for (const v of items.filter((x) =>
    x.name.includes(SEP) && !bases.some((b) => x.name.toLowerCase().startsWith(b.name.toLowerCase() + SEP))))
    ordered.push({ item: v, label: null });
  return (
    <section className="card">
      <h2
        onMouseMove={(e) => onTip?.({
          x: e.clientX + 12, y: e.clientY + 12,
          text: `${hint} — bara arată ponderea (${barSuffix}); sub-rândurile „↳" sunt felii alocate manual`,
        })}
        onMouseLeave={() => onTip?.(null)}
      >{title}</h2>
      <p className="hint">{hint}</p>
      <div className="barlist">
        {ordered.length === 0 && <div className="empty">Fără date în interval.</div>}
        {ordered.map(({ item: i, label }) => (
          <div key={i.name} className={label !== null ? "row sub" : "row"}>
            <span className="name" title={i.name}>
              {label !== null ? <><span className="sub-arrow">↳</span> {label}</> : i.name}
            </span>
            <TrackBar seconds={i.seconds} denom={barDenom} color="var(--bar-window)" opacity={label !== null ? 0.55 : 1} suffix={barSuffix} />
            <span className="val">{fmt(i.seconds)}</span>
          </div>
        ))}
      </div>
    </section>
  );
}

/** Browsere cunoscute care pot apărea în byApp fără să fie urmărite (idee user, 2026-07-10). */
const KNOWN_BROWSERS = [
  "chrome.exe", "msedge.exe", "brave.exe", "firefox.exe", "opera.exe",
  "opera_gx.exe", "vivaldi.exe", "arc.exe", "librewolf.exe", "waterfox.exe",
];
const CHROMIUM_ONLY_NOTE = ["firefox.exe", "librewolf.exe", "waterfox.exe"];

/**
 * Card discret: (1) browser cunoscut cu timp semnificativ dar neconfigurat în
 * [browser].processes → ofertă de adăugare + ghid extensie; (2) acoperire slabă a
 * tab-urilor (domenii << timp de browser) → probabil extensia lipsește dintr-un profil.
 */
function BrowserSuggest({ report }: { report: Report | null }) {
  const [procs, setProcs] = useState<string[] | null>(null);
  const [added, setAdded] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [, bump] = useState(0);

  useEffect(() => {
    fetchConfig().then((c) => setProcs(c.browser?.processes ?? [])).catch(() => setProcs(null));
  }, []);

  if (!report || !procs) return null;

  const dismissed = (k: string) => localStorage.getItem(`browser-suggest:${k}`) === "1";
  const dismiss = (k: string) => {
    localStorage.setItem(`browser-suggest:${k}`, "1");
    bump((t) => t + 1);
  };

  const candidate = report.byApp.find(
    (a) =>
      KNOWN_BROWSERS.includes(a.name.toLowerCase()) &&
      !procs.some((p) => p.toLowerCase() === a.name.toLowerCase()) &&
      a.seconds >= 300 &&
      !dismissed(a.name.toLowerCase()),
  );

  const browserSec = report.totals?.browserSeconds ?? 0;
  const domSec = report.byDomain.reduce((s, d) => s + d.seconds, 0);
  const lowCoverage = browserSec >= 900 && domSec < browserSec * 0.3 && !dismissed("coverage");

  const addBrowser = async (name: string) => {
    setBusy(true);
    try {
      const cfg = await fetchConfig();
      if (!cfg.browser.processes.some((p) => p.toLowerCase() === name.toLowerCase()))
        cfg.browser.processes.push(name);
      await saveConfig(cfg);
      setProcs(cfg.browser.processes);
      setAdded(name);
    } catch (e) {
      alert(`Nu am putut salva: ${e instanceof Error ? e.message : e}`);
    } finally {
      setBusy(false);
    }
  };

  const extensionSteps = (
    <ol className="suggest-steps">
      <li>Deschide <code>chrome://extensions</code> (sau echivalentul: <code>edge://</code>, <code>brave://</code>…) în <b>fiecare profil</b> al browserului.</li>
      <li>Activează <b>Developer mode</b> (dreapta sus).</li>
      <li><b>Load unpacked</b> → <code>&lt;folder Minutar&gt;\src\extension\dist</code></li>
      <li>Click pe iconița extensiei → setează <b>label-ul profilului</b> (ex. General, ClientX) ca site-urile să se lege de proiect.</li>
    </ol>
  );

  if (added)
    return (
      <section className="card suggest">
        <h2>✓ {added} e acum urmărit ca browser</h2>
        <p className="hint">Ca să vezi și site-urile (nu doar timpul total), instalează extensia în el:</p>
        {extensionSteps}
        <button className="btn primary" onClick={() => { dismiss(added.toLowerCase()); setAdded(null); }}>
          Am instalat-o ✓
        </button>
      </section>
    );

  if (candidate)
    return (
      <section className="card suggest">
        <h2>Browser detectat: {candidate.name} · {fmt(candidate.seconds)}</h2>
        <p className="hint">
          Nu e configurat ca browser, deci site-urile lui nu apar în rapoarte — timpul cade doar pe aplicație.
          {CHROMIUM_ONLY_NOTE.includes(candidate.name.toLowerCase()) &&
            " Atenție: extensia de tab-uri merge doar pe browsere Chromium — pe Firefox rămâne doar timpul per aplicație."}
        </p>
        <div className="suggest-actions">
          <button className="btn primary" disabled={busy} onClick={() => void addBrowser(candidate.name)}>
            {busy ? "Salvez…" : "Adaugă ca browser"}
          </button>
          <button className="btn ghost" onClick={() => dismiss(candidate.name.toLowerCase())}>Ignoră</button>
        </div>
      </section>
    );

  if (lowCoverage)
    return (
      <section className="card suggest">
        <h2>Extensia pare inactivă în unele profile</h2>
        <p className="hint">
          Doar {fmt(domSec)} din {fmt(browserSec)} de browser au tab-uri identificate ({Math.round((domSec / Math.max(1, browserSec)) * 100)}%).
          De obicei asta înseamnă că extensia lipsește sau e dezactivată într-un profil folosit des:
        </p>
        {extensionSteps}
        <div className="suggest-actions">
          <button className="btn ghost" onClick={() => dismiss("coverage")}>Ignoră definitiv</button>
        </div>
      </section>
    );

  return null;
}
