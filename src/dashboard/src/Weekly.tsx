import { useEffect, useState } from "react";
import { fetchWeekly, type NamedSeconds, type WeeklyData } from "./api";

function fmt(s: number): string {
  if (s >= 3600) return `${Math.floor(s / 3600)}h ${Math.round((s % 3600) / 60)}m`;
  if (s >= 60) return `${Math.round(s / 60)}m`;
  return `${Math.round(s)}s`;
}

const DAY_LABELS = ["Lu", "Ma", "Mi", "Jo", "Vi", "Sâ", "Du"];

function Delta({ cur, prev, invert = false }: { cur: number; prev: number; invert?: boolean }) {
  if (prev <= 0 && cur <= 0) return <span className="sub">–</span>;
  const diff = cur - prev;
  const pct = prev > 0 ? Math.round((diff / prev) * 100) : 100;
  const good = invert ? diff < 0 : diff > 0;
  if (diff === 0) return <span className="sub">= săpt. trecută</span>;
  return (
    <span className={`delta ${good ? "up" : "down"}`}>
      {diff > 0 ? "▲" : "▼"} {Math.abs(pct)}% vs săpt. trecută
    </span>
  );
}

export default function Weekly() {
  const [anchor, setAnchor] = useState(() => new Date());
  const [data, setData] = useState<WeeklyData | null>(null);
  const [error, setError] = useState("");

  useEffect(() => {
    fetchWeekly(anchor)
      .then((d) => {
        setData(d);
        setError("");
      })
      .catch((e) => setError(String(e)));
  }, [anchor]);

  const cur = data?.current;
  const prev = data?.previous;
  const maxDay = Math.max(
    1,
    ...(cur?.days ?? []).map((d) => d.productive + d.neutral + d.unproductive),
    ...(prev?.days ?? []).map((d) => d.productive + d.neutral + d.unproductive),
  );

  const weekLabel = cur
    ? `${new Date(cur.from).toLocaleDateString("ro-RO", { day: "numeric", month: "short" })} – ${new Date(new Date(cur.to).getTime() - 1).toLocaleDateString("ro-RO", { day: "numeric", month: "short" })}`
    : "";

  return (
    <main>
      <div className="controls">
        <h2 style={{ margin: 0, fontSize: 15 }}>Raport săptămânal</h2>
        <div className="nav">
          <button onClick={() => setAnchor(new Date(anchor.getTime() - 7 * 864e5))} aria-label="Înapoi">←</button>
          <span className="range-label">{weekLabel}</span>
          <button onClick={() => setAnchor(new Date(anchor.getTime() + 7 * 864e5))} aria-label="Înainte">→</button>
          <button className="today" onClick={() => setAnchor(new Date())}>Azi</button>
        </div>
      </div>

      {error && <p className="error">Nu pot citi raportul săptămânal: {error}</p>}

      <div className="tiles">
        <div className="tile hero">
          <div className="label">Timp activ</div>
          <div className="value">{fmt(cur?.activeSeconds ?? 0)}</div>
          <div className="sub"><Delta cur={cur?.activeSeconds ?? 0} prev={prev?.activeSeconds ?? 0} /></div>
        </div>
        <div className="tile">
          <div className="label"><span className="dot" style={{ background: "var(--cls-productive)" }} />Productiv</div>
          <div className="value">{fmt(cur?.byClass?.productive ?? 0)}</div>
          <div className="sub"><Delta cur={cur?.byClass?.productive ?? 0} prev={prev?.byClass?.productive ?? 0} /></div>
        </div>
        <div className="tile">
          <div className="label"><span className="dot" style={{ background: "var(--cls-unproductive)" }} />Neproductiv</div>
          <div className="value">{fmt(cur?.byClass?.unproductive ?? 0)}</div>
          <div className="sub"><Delta cur={cur?.byClass?.unproductive ?? 0} prev={prev?.byClass?.unproductive ?? 0} invert /></div>
        </div>
        <div className="tile">
          <div className="label"><span className="dot" style={{ background: "var(--accent)" }} />Focus Score</div>
          <div className="value">{cur?.focus?.score ?? "–"}<span className="unit">/100</span></div>
          <div className="sub">săpt. trecută: {prev?.focus?.score ?? "–"}</div>
        </div>
        <div className="tile">
          <div className="label">🔥 Streak</div>
          <div className="value">{data?.streak?.current ?? 0}<span className="unit"> zile</span></div>
          <div className="streak-strip" title="ultimele 14 zile">
            {(data?.streak?.last14 ?? []).map((d) => (
              <span
                key={d.date}
                className="streak-cell"
                style={{ background: d.met ? "var(--cls-productive)" : "var(--track)" }}
                title={`${d.date}: ${fmt(d.productiveSeconds)} productiv${d.met ? " ✓" : ""}`}
              />
            ))}
          </div>
          <div className="sub">record {data?.streak?.best ?? 0} · prag {data?.streak?.thresholdMinutes ?? 60}m productiv/zi</div>
        </div>
      </div>

      <section className="card">
        <h2>Zilele săptămânii</h2>
        <p className="hint">bara plină = săptămâna curentă · bara subțire = săptămâna trecută</p>
        <div className="wk-days">
          {(cur?.days ?? []).map((d, i) => {
            const p = prev?.days?.[i];
            const total = d.productive + d.neutral + d.unproductive;
            const prevTotal = p ? p.productive + p.neutral + p.unproductive : 0;
            return (
              <div key={d.date} className="wk-day">
                <div className="wk-bars">
                  {p && (
                    <div className="wk-bar ghost" style={{ height: `${(prevTotal / maxDay) * 100}%` }} title={`săpt. trecută: ${fmt(prevTotal)}`}>
                      <span className="wk-seg" style={{ height: prevTotal ? `${(p.productive / prevTotal) * 100}%` : 0, background: "var(--cls-productive)" }} />
                      <span className="wk-seg" style={{ height: prevTotal ? `${(p.neutral / prevTotal) * 100}%` : 0, background: "var(--cls-neutral)" }} />
                      <span className="wk-seg" style={{ height: prevTotal ? `${(p.unproductive / prevTotal) * 100}%` : 0, background: "var(--cls-unproductive)" }} />
                    </div>
                  )}
                  <div className="wk-bar" style={{ height: `${(total / maxDay) * 100}%` }}
                    title={`${d.date}: activ ${fmt(total)} · productiv ${fmt(d.productive)} · neutru ${fmt(d.neutral)} · neproductiv ${fmt(d.unproductive)}`}>
                    <span className="wk-seg" style={{ height: total ? `${(d.productive / total) * 100}%` : 0, background: "var(--cls-productive)" }} />
                    <span className="wk-seg" style={{ height: total ? `${(d.neutral / total) * 100}%` : 0, background: "var(--cls-neutral)" }} />
                    <span className="wk-seg" style={{ height: total ? `${(d.unproductive / total) * 100}%` : 0, background: "var(--cls-unproductive)" }} />
                  </div>
                </div>
                <span className="wk-label">{DAY_LABELS[i]}</span>
                <span className="wk-total">{total > 0 ? fmt(total) : ""}</span>
              </div>
            );
          })}
        </div>
        <div className="legend">
          <span><span className="dot" style={{ background: "var(--cls-productive)" }} />Productiv</span>
          <span><span className="dot" style={{ background: "var(--cls-neutral)" }} />Neutru</span>
          <span><span className="dot" style={{ background: "var(--cls-unproductive)" }} />Neproductiv</span>
        </div>
      </section>

      <div className="columns">
        <TopDelta title="Top proiecte" cur={cur?.topProjects ?? []} prev={prev?.topProjects ?? []} />
        <TopDelta title="Top aplicații" cur={cur?.topApps ?? []} prev={prev?.topApps ?? []} />
      </div>
    </main>
  );
}

function TopDelta({ title, cur, prev }: { title: string; cur: NamedSeconds[]; prev: NamedSeconds[] }) {
  const max = Math.max(1, ...cur.map((i) => i.seconds));
  return (
    <section className="card">
      <h2>{title}</h2>
      <p className="hint">cu diferența față de săptămâna trecută</p>
      <div className="barlist">
        {cur.length === 0 && <div className="empty">Fără date.</div>}
        {cur.map((i) => {
          const p = prev.find((x) => x.name.toLowerCase() === i.name.toLowerCase())?.seconds ?? 0;
          return (
            <div key={i.name} className="row">
              <span className="name" title={i.name}>{i.name}</span>
              <span className="track">
                <span className="bar" style={{ width: `${(i.seconds / max) * 100}%`, background: "var(--bar-window)" }} />
              </span>
              <span className="val" title={`săpt. trecută: ${fmt(p)}`}>
                {fmt(i.seconds)} {i.seconds >= p ? "▲" : "▼"}
              </span>
            </div>
          );
        })}
      </div>
    </section>
  );
}
