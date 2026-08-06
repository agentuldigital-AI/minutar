import { useEffect, useMemo, useState } from "react";
import { fetchReport, type NamedSeconds, type Report } from "./api";
import { CLASS_LABEL, CLASS_VAR, computeRange, fmt, fmtMin, rangeLabel, type ClassName, type Mode } from "./shared";

/**
 * Singurul loc unde calculatorul și telefonul se adună. Stă separat de Dashboard fiindcă
 * acolo totul e măsurat de noi, secundă cu secundă, iar aici jumătate din cifre vin de la
 * Apple, pe perioade întregi. Amestecate pe aceeași pagină, nu se mai știa care card
 * răspunde la ce întrebare.
 *
 * Nu există vedere pe zi: Screen Time raportează pe săptămâni, iar o împărțire pe zile ar
 * inventa o precizie pe care datele nu o au.
 */
export default function Devices() {
  const [mode, setMode] = useState<Exclude<Mode, "day">>("week");
  const [anchor, setAnchor] = useState(() => new Date());
  const [report, setReport] = useState<Report | null>(null);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);

  const [from, to] = useMemo(() => computeRange(mode, anchor), [mode, anchor]);

  useEffect(() => {
    let alive = true;
    setLoading(true);
    fetchReport(from, to)
      .then((r) => {
        if (!alive) return;
        setReport(r);
        setError("");
      })
      .catch((e) => alive && setError(String(e)))
      .finally(() => alive && setLoading(false));
    return () => {
      alive = false;
    };
  }, [from, to]);

  const shift = (dir: number) => {
    const a = new Date(anchor);
    if (mode === "week") a.setDate(a.getDate() + dir * 7);
    else a.setMonth(a.getMonth() + dir);
    setAnchor(a);
  };

  const data = useMemo(() => {
    const ph = report?.phone;
    if (!report) return null;

    const pcSeconds = report.totals.activeSeconds;
    const phoneSeconds = (ph?.totalMinutes ?? 0) * 60;
    const pcByClass = report.totals.byClass ?? {};

    // Apple nu spune cât din total a fost productiv — singurele cifre defalcate sunt cele
    // per aplicație, iar suma lor NU e egală cu totalul (pe date reale a ieșit și în plus,
    // și în minus). Deci împărțim totalul lui după proporțiile aplicațiilor și marcăm „≈".
    const appsSum = ph?.appsSumMinutes ?? 0;
    const shareOf = (minutes: number) => (appsSum > 0 ? minutes / appsSum : 0);
    const estSec = (minutes: number) => Math.round(shareOf(minutes) * (ph?.totalMinutes ?? 0) * 60);

    const classes = (["productive", "neutral", "unproductive"] as ClassName[]).map((cls) => ({
      cls,
      pc: pcByClass[cls] ?? 0,
      phone: estSec(ph?.byClass?.[cls] ?? 0),
      phonePct: Math.round(shareOf(ph?.byClass?.[cls] ?? 0) * 100),
    }));

    // în listele Screen Time site-urile stau lângă aplicații („mediafax.ro" lângă
    // „WhatsApp"); le separăm ca să nu amestecăm două clasamente diferite
    const isSite = (name: string) => name.includes(".") && !name.includes(" ");
    const merge = (pc: NamedSeconds[], phone: { name: string; minutes: number }[]) => {
      const key = (n: string) => n.toLowerCase().replace(/\.exe$/, "").trim();
      const map = new Map<string, { name: string; seconds: number; pc: boolean; phone: boolean }>();
      for (const i of pc) {
        const k = key(i.name);
        const e = map.get(k) ?? { name: i.name, seconds: 0, pc: false, phone: false };
        e.seconds += i.seconds;
        e.pc = true;
        map.set(k, e);
      }
      for (const a of phone) {
        const k = key(a.name);
        const e = map.get(k) ?? { name: a.name, seconds: 0, pc: false, phone: false };
        // brut, exact cum îl dă Apple: clasamentul răspunde la „cât zice Apple că am stat
        // pe X", nu la „ce parte din total" — acolo unde ajustăm, o spunem explicit
        e.seconds += a.minutes * 60;
        e.phone = true;
        map.set(k, e);
      }
      return [...map.values()]
        .filter((e) => e.seconds > 0)
        .sort((a, b) => b.seconds - a.seconds)
        .slice(0, 10);
    };

    const phoneApps = ph?.apps ?? [];
    return {
      hasPhone: (ph?.totalMinutes ?? 0) > 0,
      pcSeconds,
      phoneSeconds,
      totalSeconds: pcSeconds + phoneSeconds,
      classes,
      unclassified: estSec(ph?.unclassifiedMinutes ?? 0),
      unclassifiedPct: Math.round(shareOf(ph?.unclassifiedMinutes ?? 0) * 100),
      gapMinutes: appsSum - (ph?.totalMinutes ?? 0),
      appsSumMinutes: appsSum,
      phoneTotalMinutes: ph?.totalMinutes ?? 0,
      apps: merge(report.byApp ?? [], phoneApps.filter((a) => !isSite(a.name))),
      sites: merge(report.byDomain ?? [], phoneApps.filter((a) => isSite(a.name))),
    };
  }, [report]);

  return (
    <main>
      <div className="controls">
        <div className="seg">
          {(["week", "month"] as const).map((m) => (
            <button key={m} className={mode === m ? "active" : ""} onClick={() => setMode(m)}>
              {m === "week" ? "Săptămână" : "Lună"}
            </button>
          ))}
        </div>
        <div className="nav">
          <button onClick={() => shift(-1)} aria-label="Înapoi">←</button>
          <span className="range-label">{rangeLabel(mode, from, to)}</span>
          <button onClick={() => shift(1)} aria-label="Înainte">→</button>
          <button className="today" onClick={() => setAnchor(new Date())}>Azi</button>
        </div>
      </div>

      {error ? <p className="error">Nu pot citi raportul: {error}</p> : null}

      <section className="card">
        <h2>Total pe dispozitive</h2>
        <p className="hint">
          Calculator + telefon la un loc. Bara plină = PC (cronometrat de noi), partea
          transparentă = telefon (raportat de Apple). Nu există vedere pe zi: Screen Time
          vine pe săptămâni întregi.
        </p>

        {loading && !data ? (
          <div className="empty">Se încarcă…</div>
        ) : (
          <>
            <div className="dev-total">
              <b>{fmt(data?.totalSeconds ?? 0)}</b>
              <span className="range-label">
                PC {fmt(data?.pcSeconds ?? 0)}
                {data?.hasPhone ? ` · telefon ${fmt(data.phoneSeconds)}` : ""}
              </span>
            </div>

            {data?.hasPhone ? (
              <p className="hint" style={{ marginTop: 8 }}>
                Rândurile de mai jos împart totalul telefonului după <b>proporțiile</b>{" "}
                aplicațiilor, de aceea sunt marcate „≈": Apple nu spune cât din total a fost
                productiv sau nu.{" "}
                {data.gapMinutes !== 0 ? (
                  <>
                    Cifrele lui nici nu se închid între ele — aplicațiile adună{" "}
                    <b>{fmtMin(data.appsSumMinutes)}</b> față de totalul de{" "}
                    <b>{fmtMin(data.phoneTotalMinutes)}</b>
                    {" ("}
                    {data.gapMinutes > 0 ? "+" : ""}
                    {Math.round((data.gapMinutes / data.phoneTotalMinutes) * 100)}%). Nu le
                    „reparăm": totalul rămâne cifra lui, iar în clasamente aplicațiile rămân
                    exact cum le dă el.
                  </>
                ) : null}
              </p>
            ) : (
              <p className="hint" style={{ marginTop: 8 }}>
                Fără date de telefon în acest interval — cifrele sunt doar de pe calculator.{" "}
                <a href="#phone">Importă Screen Time</a> ca să le vezi la un loc.
              </p>
            )}

            <div className="barlist split-vals" style={{ marginTop: 12 }}>
              {(data?.classes ?? []).map((r) => {
                const tot = r.pc + r.phone;
                const denom = data?.totalSeconds ?? 0;
                const pct = denom > 0 ? (tot / denom) * 100 : 0;
                const pcW = denom > 0 ? (r.pc / denom) * 100 : 0;
                return (
                  <div className="row" key={r.cls}>
                    <span className="name">
                      <span className="dot" style={{ background: CLASS_VAR[r.cls] }} />
                      {CLASS_LABEL[r.cls]}
                    </span>
                    <span className="track">
                      <span
                        className="bar"
                        title={`Calculator — ${fmt(r.pc)}, adică ${tot > 0 ? Math.round((r.pc / tot) * 100) : 0}% din tot ce e ${CLASS_LABEL[r.cls].toLowerCase()}`}
                        style={{
                          left: 0, width: `${pcW}%`, background: CLASS_VAR[r.cls],
                          borderRadius: r.phone > 0 ? "5px 0 0 5px" : undefined,
                        }}
                      />
                      <span
                        className="bar seg-phone"
                        title={`Telefon — ≈${fmt(r.phone)}, adică ${tot > 0 ? Math.round((r.phone / tot) * 100) : 0}% din tot ce e ${CLASS_LABEL[r.cls].toLowerCase()}`}
                        style={{
                          left: `${pcW}%`,
                          width: `${denom > 0 ? (r.phone / denom) * 100 : 0}%`,
                          background: CLASS_VAR[r.cls], opacity: 0.42,
                        }}
                      />
                    </span>
                    <span className="val val-split">
                      <b>{r.phone > 0 ? "≈" : ""}{fmt(tot)}</b>
                      <span className="meta">{Math.round(pct)}% din total</span>
                      {r.phone > 0 ? (
                        <span className="meta">
                          {Math.round((r.pc / tot) * 100)}% PC · {Math.round((r.phone / tot) * 100)}% telefon
                        </span>
                      ) : null}
                    </span>
                  </div>
                );
              })}
              {(data?.unclassified ?? 0) > 0 ? (
                <div className="row">
                  <span className="name range-label">neclasificat (telefon)</span>
                  <span className="track">
                    <span
                      className="bar"
                      style={{
                        left: 0,
                        width: `${(data!.totalSeconds > 0 ? (data!.unclassified / data!.totalSeconds) : 0) * 100}%`,
                        background: "var(--text-secondary)", opacity: 0.35,
                      }}
                    />
                  </span>
                  <span
                    className="val"
                    title={`${data!.unclassifiedPct}% din timpul de telefon, în aplicații fără clasă`}
                  >
                    ≈{fmt(data!.unclassified)}
                  </span>
                </div>
              ) : null}
            </div>

            {data?.hasPhone ? (
              <div className="legend" style={{ marginTop: 10 }}>
                <span><i className="swatch-seg" /> calculator (cronometrat)</span>
                <span><i className="swatch-seg faded" /> telefon (raportat de Apple)</span>
                <span className="meta">treci cu mouse-ul peste o felie pentru detalii</span>
              </div>
            ) : null}

            <div className="columns" style={{ marginTop: 18 }}>
              <DeviceList title="Top aplicații" items={data?.apps ?? []} />
              <DeviceList title="Top site-uri" items={data?.sites ?? []} />
            </div>
          </>
        )}
      </section>
    </main>
  );
}

/**
 * Clasament peste ambele dispozitive. Badge-ul spune de unde vine timpul: fără el,
 * „WhatsApp 5h" ar putea fi de pe PC, de pe telefon sau amândouă, iar diferența schimbă
 * ce faci cu informația.
 */
function DeviceList({ title, items }: {
  items: { name: string; seconds: number; pc: boolean; phone: boolean }[];
  title: string;
}) {
  const max = Math.max(1, ...items.map((i) => i.seconds));
  return (
    <div className="dev-col">
      <h3>{title}</h3>
      {items.length === 0 ? (
        <div className="empty">Fără date în interval.</div>
      ) : (
        <div className="barlist">
          {items.map((i) => (
            <div className="row" key={i.name}>
              <span className="name" title={i.name}>
                <span className="dev-badge" title={i.pc && i.phone ? "PC și telefon" : i.phone ? "telefon" : "PC"}>
                  {i.pc && i.phone ? "pc+tel" : i.phone ? "tel" : "pc"}
                </span>
                {i.name}
              </span>
              <span className="track">
                <span className="bar" style={{ width: `${(i.seconds / max) * 100}%`, background: "var(--bar-window)" }} />
              </span>
              <span className="val">{fmt(i.seconds)}</span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
