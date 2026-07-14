import { useEffect, useState, type KeyboardEvent } from "react";
import { fetchConfig, saveConfig, type ConfigData, type GoalItem, type ProjectCfg, type RuleCfg } from "./api";

const STYLES = [
  { value: "coach", label: "Coach" },
  { value: "direct", label: "Direct" },
  { value: "funny", label: "Funny" },
  { value: "calm", label: "Calm" },
  { value: "data_driven", label: "Data-driven" },
];

const CLASSES = [
  { value: "productive", label: "Productiv" },
  { value: "neutral", label: "Neutru" },
  { value: "unproductive", label: "Neproductiv" },
];
const MATCHES = [
  { value: "domain", label: "Domeniu (site)" },
  { value: "app", label: "Aplicație (exe)" },
  { value: "title", label: "Titlu (conține)" },
];

export default function Settings() {
  const [cfg, setCfg] = useState<ConfigData | null>(null);
  const [error, setError] = useState("");
  const [status, setStatus] = useState<"" | "saving" | "saved">("");
  const [dirty, setDirty] = useState(false);

  useEffect(() => {
    fetchConfig().then(setCfg).catch((e) => setError(String(e)));
  }, []);

  const update = (next: ConfigData) => {
    setCfg(next);
    setDirty(true);
    setStatus("");
  };

  const save = async () => {
    if (!cfg) return;
    setStatus("saving");
    setError("");
    try {
      await saveConfig(cfg);
      // refetch: aduce versiunea nouă (tokenul de concurență) + forma canonică de pe disc
      const fresh = await fetchConfig().catch(() => null);
      if (fresh) setCfg(fresh);
      setStatus("saved");
      setDirty(false);
    } catch (e) {
      setStatus("");
      setError(String(e instanceof Error ? e.message : e));
    }
  };

  if (error && !cfg) return <main><p className="error">Nu pot citi configul: {error}</p></main>;
  if (!cfg) return <main><p className="empty">Se încarcă…</p></main>;

  const updGoal = (i: number, patch: Partial<GoalItem>) => {
    const objectiveList = cfg.profile.objectiveList.map((g, j) => (j === i ? { ...g, ...patch } : g));
    update({ ...cfg, profile: { ...cfg.profile, objectiveList } });
  };

  return (
    <main>
      <section className="card">
        <h2>Clasificare — ce e productiv și ce nu</h2>
        <p className="hint">
          Regulile se evaluează: aplicație → domeniu → titlu. Ce nu se potrivește primește clasa implicită.
        </p>
        <div className="field-row">
          <label>Clasă implicită</label>
          <select
            value={cfg.classification.default}
            onChange={(e) => update({ ...cfg, classification: { ...cfg.classification, default: e.target.value } })}
          >
            {CLASSES.map((c) => <option key={c.value} value={c.value}>{c.label}</option>)}
          </select>
        </div>
        <div className="rules">
          {cfg.classification.rules.map((r, i) => (
            <RuleRow
              key={i}
              rule={r}
              onChange={(nr) => {
                const rules = cfg.classification.rules.map((x, j) => (j === i ? nr : x));
                update({ ...cfg, classification: { ...cfg.classification, rules } });
              }}
              onRemove={() => {
                const rules = cfg.classification.rules.filter((_, j) => j !== i);
                update({ ...cfg, classification: { ...cfg.classification, rules } });
              }}
            />
          ))}
        </div>
        <button
          className="btn ghost"
          onClick={() =>
            update({
              ...cfg,
              classification: {
                ...cfg.classification,
                rules: [...cfg.classification.rules, { class: "unproductive", match: "domain", value: "" }],
              },
            })
          }
        >
          + Adaugă regulă
        </button>
      </section>

      <section className="card">
        <h2>Excepții YouTube</h2>
        <p className="hint">
          Un video al cărui titlu conține un cuvânt de mai jos, sau al cărui canal e în listă,
          devine productiv și nu declanșează popup — chiar dacă youtube.com e neproductiv.
        </p>
        <div className="field-col">
          <label>Cuvinte în titlu</label>
          <ChipInput
            values={cfg.youtubeExceptions.titleKeywords}
            placeholder="ex. documentar, tutorial…  (Enter adaugă)"
            onChange={(titleKeywords) => update({ ...cfg, youtubeExceptions: { ...cfg.youtubeExceptions, titleKeywords } })}
          />
        </div>
        <div className="field-col">
          <label>Canale permise</label>
          <ChipInput
            values={cfg.youtubeExceptions.channels}
            placeholder="numele exact al canalului  (Enter adaugă)"
            onChange={(channels) => update({ ...cfg, youtubeExceptions: { ...cfg.youtubeExceptions, channels } })}
          />
        </div>
      </section>

      <section className="card">
        <h2>Proiecte</h2>
        <p className="hint">
          Keywords: cuvinte din titlurile ferestrelor/URL-uri care atribuie timpul pe proiect.
          Profile browser: eticheta setată în Options-ul extensiei — bate keywords pentru taburi.
          Foldere Claude: cwd-urile sesiunilor Claude Code.
        </p>
        {cfg.projects.map((p, i) => (
          <ProjectCard
            key={i}
            project={p}
            onChange={(np) => update({ ...cfg, projects: cfg.projects.map((x, j) => (j === i ? np : x)) })}
            onRemove={() => update({ ...cfg, projects: cfg.projects.filter((_, j) => j !== i) })}
          />
        ))}
        <button
          className="btn ghost"
          onClick={() =>
            update({
              ...cfg,
              projects: [...cfg.projects, { name: "", keywords: [], claudeDirs: [], browserProfiles: [], apps: [], domains: [] }],
            })
          }
        >
          + Adaugă proiect
        </button>
      </section>

      {cfg.profile && (
        <section className="card">
          <h2>Profil (Coach)</h2>
          <p className="hint">obiectivele și stilul tău — folosite în mesajele coach-ului</p>
          <div className="field-col">
            <label>De ce muncesc?</label>
            <input
              className="intent-input"
              value={cfg.profile.why}
              placeholder="ex. pentru independență financiară și familia mea"
              onChange={(e) => update({ ...cfg, profile: { ...cfg.profile, why: e.target.value } })}
            />
          </div>
          <div className="field-col">
            <label>Stiluri de motivație</label>
            <div className="chips">
              {STYLES.map((s) => (
                <label key={s.value} className="check-label chip">
                  <input
                    type="checkbox"
                    checked={cfg.profile.motivationStyles.includes(s.value)}
                    onChange={(e) => {
                      const styles = e.target.checked
                        ? [...cfg.profile.motivationStyles, s.value]
                        : cfg.profile.motivationStyles.filter((x) => x !== s.value);
                      update({ ...cfg, profile: { ...cfg.profile, motivationStyles: styles } });
                    }}
                  />
                  {s.label}
                </label>
              ))}
            </div>
          </div>
          <div className="field-col">
            <label>Ce NU-mi place (folosit de stratul AI, v1)</label>
            <ChipInput
              values={cfg.profile.dislikes}
              placeholder="ex. nu mă certa, fără sarcasm  (Enter adaugă)"
              onChange={(dislikes) => update({ ...cfg, profile: { ...cfg.profile, dislikes } })}
            />
          </div>
          <div className="field-row" style={{ flexWrap: "wrap", gap: 16 }}>
            <label>Program: </label>
            <input style={{ width: 70 }} value={cfg.profile.workStart} onChange={(e) => update({ ...cfg, profile: { ...cfg.profile, workStart: e.target.value } })} />
            <span>–</span>
            <input style={{ width: 70 }} value={cfg.profile.workEnd} onChange={(e) => update({ ...cfg, profile: { ...cfg.profile, workEnd: e.target.value } })} />
            <label>Prânz:</label>
            <input style={{ width: 110 }} value={cfg.profile.lunch} onChange={(e) => update({ ...cfg, profile: { ...cfg.profile, lunch: e.target.value } })} />
            <label className="check-label">
              <input type="checkbox" checked={cfg.profile.workWeekends} onChange={(e) => update({ ...cfg, profile: { ...cfg.profile, workWeekends: e.target.checked } })} />
              lucrez și în weekend
            </label>
          </div>
          <div className="field-col">
            <label>Intervale de concentrare (fără nudge-uri dacă ești productiv)</label>
            <ChipInput
              values={cfg.profile.focusIntervals}
              placeholder="ex. 09:00-11:00  (Enter adaugă)"
              onChange={(focusIntervals) => update({ ...cfg, profile: { ...cfg.profile, focusIntervals } })}
            />
          </div>
          <div className="field-col">
            <label>Obiective (cu deadline și proiect opțional)</label>
            {cfg.profile.objectiveList.map((g, i) => (
              <div key={i} className="goal-row">
                <input value={g.title} placeholder="obiectiv, ex. lansez MVP-ul" onChange={(e) => updGoal(i, { title: e.target.value })} />
                <select value={g.kind} onChange={(e) => updGoal(i, { kind: e.target.value })}>
                  <option value="professional">profesional</option>
                  <option value="personal">personal</option>
                </select>
                <input type="date" value={g.deadline} onChange={(e) => updGoal(i, { deadline: e.target.value })} />
                <select value={g.project} onChange={(e) => updGoal(i, { project: e.target.value })}>
                  <option value="">— proiect —</option>
                  {cfg.projects.map((p) => <option key={p.name} value={p.name}>{p.name}</option>)}
                </select>
                <button className="btn icon" onClick={() => update({ ...cfg, profile: { ...cfg.profile, objectiveList: cfg.profile.objectiveList.filter((_, j) => j !== i) } })}>✕</button>
              </div>
            ))}
            <button
              className="btn ghost"
              onClick={() => update({ ...cfg, profile: { ...cfg.profile, objectiveList: [...cfg.profile.objectiveList, { title: "", kind: "professional", deadline: "", project: "" }] } })}
            >
              + Adaugă obiectiv
            </button>
          </div>
        </section>
      )}

      {cfg.coach && (
        <section className="card">
          <h2>Coach — reguli</h2>
          <p className="hint">
            toate locale, zero API; tăcere totală în Flow (≥{cfg.coach.flowMinutes} min productiv continuu) și max un nudge la {cfg.coach.minMinutesBetweenNudges} min
          </p>
          <label className="check-label" style={{ marginBottom: 10 }}>
            <input type="checkbox" checked={cfg.coach.enabled} onChange={(e) => update({ ...cfg, coach: { ...cfg.coach, enabled: e.target.checked } })} />
            <b>Coach activ</b>
          </label>
          <div className="coach-rule">
            <input type="checkbox" checked={cfg.coach.ruleUnproductive} onChange={(e) => update({ ...cfg, coach: { ...cfg.coach, ruleUnproductive: e.target.checked } })} />
            <span>Timp neproductiv continuu (minute)</span>
            <input type="number" value={cfg.coach.unproductiveMinutes} onChange={(e) => update({ ...cfg, coach: { ...cfg.coach, unproductiveMinutes: +e.target.value } })} />
          </div>
          <div className="coach-rule">
            <input type="checkbox" checked={cfg.coach.ruleContextSwitching} onChange={(e) => update({ ...cfg, coach: { ...cfg.coach, ruleContextSwitching: e.target.checked } })} />
            <span>Prea multe schimbări de aplicație pe oră</span>
            <input type="number" value={cfg.coach.maxSwitchesPerHour} onChange={(e) => update({ ...cfg, coach: { ...cfg.coach, maxSwitchesPerHour: +e.target.value } })} />
          </div>
          <div className="coach-rule">
            <input type="checkbox" checked={cfg.coach.ruleMainNotStarted} onChange={(e) => update({ ...cfg, coach: { ...cfg.coach, ruleMainNotStarted: e.target.checked } })} />
            <span>Prioritatea #1 neîncepută până la ora</span>
            <input value={cfg.coach.mainProjectCheckAt} onChange={(e) => update({ ...cfg, coach: { ...cfg.coach, mainProjectCheckAt: e.target.value } })} />
          </div>
          <div className="coach-rule">
            <input type="checkbox" checked={cfg.coach.ruleNoBreak} onChange={(e) => update({ ...cfg, coach: { ...cfg.coach, ruleNoBreak: e.target.checked } })} />
            <span>Ore de lucru fără pauză</span>
            <input type="number" step="0.5" value={cfg.coach.noBreakHours} onChange={(e) => update({ ...cfg, coach: { ...cfg.coach, noBreakHours: +e.target.value } })} />
          </div>
          <div className="coach-rule">
            <input type="checkbox" checked={cfg.coach.ruleDeadlineDrift} onChange={(e) => update({ ...cfg, coach: { ...cfg.coach, ruleDeadlineDrift: e.target.checked } })} />
            <span>Deadline aproape (zile) dar timpul merge în altă parte</span>
            <input type="number" value={cfg.coach.deadlineDriftDays} onChange={(e) => update({ ...cfg, coach: { ...cfg.coach, deadlineDriftDays: +e.target.value } })} />
          </div>
        </section>
      )}

      <div className="savebar">
        {error && <span className="error">{error}</span>}
        {status === "saved" && <span className="ok">Salvat ✓ — se aplică în ~1s</span>}
        <button className="btn primary" disabled={!dirty || status === "saving"} onClick={save}>
          {status === "saving" ? "Se salvează…" : "Salvează modificările"}
        </button>
      </div>
    </main>
  );
}

function RuleRow({ rule, onChange, onRemove }: { rule: RuleCfg; onChange: (r: RuleCfg) => void; onRemove: () => void }) {
  return (
    <div className="rule-row">
      <select value={rule.class} onChange={(e) => onChange({ ...rule, class: e.target.value })}>
        {CLASSES.map((c) => <option key={c.value} value={c.value}>{c.label}</option>)}
      </select>
      <select value={rule.match} onChange={(e) => onChange({ ...rule, match: e.target.value })}>
        {MATCHES.map((m) => <option key={m.value} value={m.value}>{m.label}</option>)}
      </select>
      <input
        value={rule.value}
        placeholder={rule.match === "domain" ? "ex. 9gag.com" : rule.match === "app" ? "ex. steam.exe" : "text din titlu"}
        onChange={(e) => onChange({ ...rule, value: e.target.value })}
      />
      <button className="btn icon" title="Șterge regula" onClick={onRemove}>✕</button>
    </div>
  );
}

function ProjectCard({ project, onChange, onRemove }: { project: ProjectCfg; onChange: (p: ProjectCfg) => void; onRemove: () => void }) {
  return (
    <div className="project-card">
      <div className="project-head">
        <input
          className="project-name"
          value={project.name}
          placeholder="numele proiectului"
          onChange={(e) => onChange({ ...project, name: e.target.value })}
        />
        <button className="btn icon" title="Șterge proiectul" onClick={onRemove}>✕</button>
      </div>
      <div className="field-col">
        <label>Keywords</label>
        <ChipInput
          values={project.keywords}
          placeholder="ex. proiectul-meu  (Enter adaugă)"
          onChange={(keywords) => onChange({ ...project, keywords })}
        />
      </div>
      <div className="field-col">
        <label>Profile browser</label>
        <ChipInput
          values={project.browserProfiles}
          placeholder="eticheta din extensie, ex. General"
          onChange={(browserProfiles) => onChange({ ...project, browserProfiles })}
        />
      </div>
      <div className="field-col">
        <label>Foldere Claude Code</label>
        <ChipInput
          values={project.claudeDirs}
          placeholder={"ex. C:\\Proiecte\\proiectul-meu"}
          onChange={(claudeDirs) => onChange({ ...project, claudeDirs })}
        />
      </div>
      <div className="field-col">
        <label>Aplicații pinuite (bat profilul + keywords)</label>
        <ChipInput
          values={project.apps ?? []}
          placeholder="ex. Zoom.exe"
          onChange={(apps) => onChange({ ...project, apps })}
        />
      </div>
      <div className="field-col">
        <label>Domenii pinuite (bat profilul + keywords)</label>
        <ChipInput
          values={project.domains ?? []}
          placeholder="ex. meet.google.com"
          onChange={(domains) => onChange({ ...project, domains })}
        />
      </div>
    </div>
  );
}

function ChipInput({ values, placeholder, onChange }: { values: string[]; placeholder: string; onChange: (v: string[]) => void }) {
  const [draft, setDraft] = useState("");

  const commit = () => {
    const v = draft.trim();
    if (!v) return;
    if (!values.some((x) => x.toLowerCase() === v.toLowerCase())) onChange([...values, v]);
    setDraft("");
  };

  const onKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === "Enter" || e.key === ",") {
      e.preventDefault();
      commit();
    } else if (e.key === "Backspace" && draft === "" && values.length > 0) {
      onChange(values.slice(0, -1));
    }
  };

  return (
    <div className="chipinput">
      {values.map((v) => (
        <span key={v} className="chip on">
          {v}
          <button className="chip-x" title="Șterge" onClick={() => onChange(values.filter((x) => x !== v))}>×</button>
        </span>
      ))}
      <input
        value={draft}
        placeholder={placeholder}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={onKey}
        onBlur={commit}
      />
    </div>
  );
}
