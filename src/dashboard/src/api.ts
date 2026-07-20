export interface NamedSeconds {
  name: string;
  seconds: number;
}

export interface ProjectRow {
  name: string;
  seconds: number;
  claudeWorkSeconds: number;
  claudeAttentionSeconds: number;
  /** false = proiect „nesalvat": nume apărut doar din date (folderul unei sesiuni
   *  Claude fără claude_dirs configurat), fără intrare în tracker.toml */
  configured?: boolean;
}

export interface TimelineSegment {
  t: string;
  d: number;
  project: string;
  cls: string;
}

export interface FocusInfo {
  score: number | null;
  classScore: number;
  flowScore: number;
  switches: number;
  switchesPerHour: number;
}

export interface HeatmapRow {
  date: string;
  active: number[];
  productive: number[];
  unproductive: number[];
}

export interface Report {
  from: string;
  to: string;
  totals: {
    activeSeconds: number;
    claudeWorkSeconds: number;
    browserSeconds: number;
    presenceSeconds?: number;
    afkSeconds?: number;
    byClass: Record<string, number>;
  };
  afkTimeline?: { t: string; d: number }[];
  classDetail: Record<string, { apps: NamedSeconds[]; domains: NamedSeconds[] }>;
  projectDetail: Record<string, { apps: NamedSeconds[]; domains: NamedSeconds[] }>;
  byProject: ProjectRow[];
  byApp: NamedSeconds[];
  byDomain: NamedSeconds[];
  byProfile: NamedSeconds[];
  focus: FocusInfo;
  heatmap: HeatmapRow[];
  timeline: TimelineSegment[];
  timelineTruncated: boolean;
}

export async function fetchReport(from: Date, to: Date): Promise<Report> {
  const qs = new URLSearchParams({ from: from.toISOString(), to: to.toISOString() });
  const resp = await fetch(`/api/report?${qs}`);
  if (!resp.ok) throw new Error(`report ${resp.status}`);
  return resp.json();
}

// ---- weekly (F2) ----

export interface WeekDay {
  date: string;
  productive: number;
  neutral: number;
  unproductive: number;
}

export interface WeekSummary {
  from: string;
  to: string;
  activeSeconds: number;
  byClass: Record<string, number>;
  focus: FocusInfo;
  days: WeekDay[];
  topProjects: NamedSeconds[];
  topApps: NamedSeconds[];
}

export interface StreakInfo {
  current: number;
  best: number;
  thresholdMinutes: number;
  last14: { date: string; productiveSeconds: number; met: boolean }[];
}

export interface WeeklyData {
  current: WeekSummary;
  previous: WeekSummary;
  streak: StreakInfo;
}

export async function fetchWeekly(anchor: Date): Promise<WeeklyData> {
  const resp = await fetch(`/api/weekly?anchor=${encodeURIComponent(anchor.toISOString())}`);
  if (!resp.ok) throw new Error(`weekly ${resp.status}`);
  return resp.json();
}

/** Recategorize an app/domain going FORWARD (replaces any existing rule for the same target). */
export async function reclassify(match: "app" | "domain", value: string, cls: string): Promise<void> {
  const cfg = await fetchConfig();
  cfg.classification.rules = cfg.classification.rules.filter(
    (r) => !(r.match === match && r.value.toLowerCase() === value.toLowerCase()),
  );
  cfg.classification.rules.push({ class: cls, match, value });
  await saveConfig(cfg);
}

/** Assign an app/domain to a project for ONE calendar day only (beats every global rule that day). */
export async function assignToProjectForDay(
  date: string, match: "app" | "domain", value: string, projectName: string,
): Promise<void> {
  await postDayAssign({ date, match, value, project: projectName });
}

/** Override the CLASS of an app/domain for one day only (ex. WhatsApp productive doar azi). */
export async function assignClassForDay(
  date: string, match: "app" | "domain", value: string, cls: string,
): Promise<void> {
  await postDayAssign({ date, match, value, class: cls });
}

/** Assign only a TIME SLICE of one day ("Zoom 14:00–15:30 → ClientX"): project and/or class,
 *  applied inside [from, to) only — beats the whole-day assignment in the overlap. */
export async function assignIntervalForDay(
  date: string, match: "app" | "domain", value: string,
  from: string, to: string, project?: string, cls?: string,
): Promise<void> {
  await postDayAssign({
    date, match, value, from, to,
    ...(project ? { project } : {}),
    ...(cls ? { class: cls } : {}),
  });
}

/** Move N MINUTES of one day's usage to a project and/or class — the daemon picks the real
 *  usage windows itself (chronological, skipping existing allocations) and stores them as
 *  interval assignments, so exactly N minutes move. */
export async function assignMinutesForDay(
  date: string, match: "app" | "domain", value: string, minutes: number,
  project?: string, cls?: string, requestId?: string,
): Promise<void> {
  const resp = await fetch("/api/assign-minutes", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      date, match, value, minutes,
      ...(project ? { project } : {}),
      ...(cls ? { class: cls } : {}),
      // id stabil per rând: serverul deduplică — un retry nu mai alocă încă o dată
      ...(requestId ? { requestId } : {}),
    }),
  });
  if (!resp.ok) {
    const body = await resp.json().catch(() => null);
    throw new Error(body?.error ?? `assign-minutes ${resp.status}`);
  }
}

async function postDayAssign(body: object): Promise<void> {
  const resp = await fetch("/api/assign-day", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  if (!resp.ok) {
    const respBody = await resp.json().catch(() => null);
    throw new Error(respBody?.error ?? `assign-day ${resp.status}`);
  }
}

/** Remove a one-day assignment (part: only the project, only the class, or the whole entry).
 *  from/to target an interval entry; omitted = the whole-day entry. */
export async function removeDayAssignment(
  date: string, match: "app" | "domain", value: string, part?: "project" | "class",
  from?: string, to?: string,
): Promise<void> {
  const qs = new URLSearchParams({ date, match, value });
  if (part) qs.set("part", part);
  if (from) qs.set("from", from);
  if (to) qs.set("to", to);
  const resp = await fetch(`/api/assign-day?${qs}`, { method: "DELETE" });
  if (!resp.ok) {
    const body = await resp.json().catch(() => null);
    throw new Error(body?.error ?? `unassign-day ${resp.status}`);
  }
}

/** Remove a permanent project pin for an app/domain (strips it from every project). */
export async function unassignFromProject(match: "app" | "domain", value: string): Promise<void> {
  const cfg = await fetchConfig();
  for (const p of cfg.projects) {
    p.apps = (p.apps ?? []).filter((x) => x.toLowerCase() !== value.toLowerCase());
    p.domains = (p.domains ?? []).filter((x) => x.toLowerCase() !== value.toLowerCase());
  }
  await saveConfig(cfg);
}

/** Adoptă un proiect „nesalvat" (îl scrie în tracker.toml). Aditiv și idempotent pe
 *  server — nu trimite tot config-ul, deci nu poate da 409 și nu suprascrie nimic. */
export async function createProject(name: string, claudeDirs: string[] = []): Promise<void> {
  const resp = await fetch("/api/projects", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ name, claudeDirs }),
  });
  if (!resp.ok) {
    const body = await resp.json().catch(() => null);
    throw new Error(body?.error ?? `create-project ${resp.status}`);
  }
}

/** cwd-urile reale ale pseudo-proiectelor Claude (proiect → cwd), pentru precompletarea
 *  lui claude_dirs la adopție. Gol până la primul hook de după repornirea daemonului. */
export async function fetchClaudeCwds(): Promise<Record<string, string>> {
  const resp = await fetch("/api/claude/cwds");
  if (!resp.ok) return {};
  return resp.json().catch(() => ({}));
}

/** Pin an app/domain to a PROJECT going forward (explicit pins beat profile + keywords). */
export async function assignToProject(match: "app" | "domain", value: string, projectName: string): Promise<void> {
  const cfg = await fetchConfig();
  for (const p of cfg.projects) {
    p.apps = (p.apps ?? []).filter((x) => x.toLowerCase() !== value.toLowerCase());
    p.domains = (p.domains ?? []).filter((x) => x.toLowerCase() !== value.toLowerCase());
  }
  const target = cfg.projects.find((p) => p.name === projectName);
  if (!target) throw new Error(`proiect necunoscut: ${projectName}`);
  (match === "app" ? target.apps : target.domains).push(value);
  await saveConfig(cfg);
}

// ---- focus mode (F4) ----

export async function focusStart(minutes: number): Promise<void> {
  await fetch(`/focus/start?minutes=${minutes}`, { method: "POST" });
}

export async function focusStop(): Promise<void> {
  await fetch(`/focus/stop`, { method: "POST" });
}

// ---- journal (F3) ----

export interface JournalData {
  date: string;
  firstActivity: string | null;
  lastActivity: string | null;
  activeSeconds: number;
  byClass: Record<string, number>;
  focus: FocusInfo;
  topProjects: NamedSeconds[];
  topApps: NamedSeconds[];
  topDomains: NamedSeconds[];
  claudeWorkSeconds: number;
  claudeTopProject: NamedSeconds | null;
  longestFocus: { start: string; seconds: number; project: string | null } | null;
  distraction: NamedSeconds | null;
}

export async function fetchJournal(date: Date): Promise<JournalData> {
  const resp = await fetch(`/api/journal?date=${encodeURIComponent(date.toISOString())}`);
  if (!resp.ok) throw new Error(`journal ${resp.status}`);
  return resp.json();
}

// ---- coach v0: day state ----

export interface DayPriority {
  text: string;
  project: string;
  done: boolean;
}

export interface DayState {
  date: string;
  intent: string;
  priorities: DayPriority[];
  shutdownNotes: string;
  tomorrowPlan: string;
  intentPromptShown: boolean;
  shutdownPromptShown: boolean;
  nudges: { time: string; rule: string; message: string }[];
}

export async function fetchDay(date?: string): Promise<DayState> {
  const resp = await fetch(`/api/day${date ? `?date=${date}` : ""}`);
  if (!resp.ok) throw new Error(`day ${resp.status}`);
  return resp.json();
}

export async function saveDay(state: DayState): Promise<void> {
  const resp = await fetch(`/api/day`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(state),
  });
  if (!resp.ok) throw new Error(`day save ${resp.status}`);
}

// ---- coach v0: profile + coach config ----

export interface GoalItem {
  title: string;
  kind: string;
  deadline: string;
  project: string;
}

export interface ProfileCfg {
  why: string;
  motivationStyles: string[];
  dislikes: string[];
  workStart: string;
  workEnd: string;
  lunch: string;
  workWeekends: boolean;
  focusIntervals: string[];
  objectiveList: GoalItem[];
}

export interface CoachCfg {
  enabled: boolean;
  minMinutesBetweenNudges: number;
  ruleCooldownMinutes: number;
  flowMinutes: number;
  toastSeconds: number;
  ruleUnproductive: boolean;
  unproductiveMinutes: number;
  ruleContextSwitching: boolean;
  maxSwitchesPerHour: number;
  ruleMainNotStarted: boolean;
  mainProjectCheckAt: string;
  ruleNoBreak: boolean;
  noBreakHours: number;
  ruleDeadlineDrift: boolean;
  deadlineDriftDays: number;
}

// ---- settings ----

export interface ProjectCfg {
  name: string;
  keywords: string[];
  claudeDirs: string[];
  browserProfiles: string[];
  apps: string[];
  domains: string[];
}

export interface RuleCfg {
  class: string;
  match: string;
  value: string;
}

export interface DayAssignment {
  date: string;
  match: string;
  value: string;
  project: string;
  class: string;
  /** "HH:mm" — set on interval entries only; "" = the whole day */
  from: string;
  to: string;
}

export interface ConfigData {
  projects: ProjectCfg[];
  classification: { default: string; rules: RuleCfg[] };
  youtubeExceptions: { titleKeywords: string[]; channels: string[] };
  browser: { processes: string[] };
  assignments?: DayAssignment[];
  profile: ProfileCfg;
  coach: CoachCfg;
  configPath?: string;
  /** token de concurență optimistă — trimis înapoi la PUT; nepotrivire = 409 */
  version?: string;
}

export async function fetchConfig(): Promise<ConfigData> {
  const resp = await fetch("/api/config");
  if (!resp.ok) throw new Error(`config ${resp.status}`);
  return resp.json();
}

export async function saveConfig(cfg: ConfigData): Promise<void> {
  const resp = await fetch("/api/config", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      projects: cfg.projects,
      classification: cfg.classification,
      youtubeExceptions: cfg.youtubeExceptions,
      profile: cfg.profile,
      coach: cfg.coach,
      browserProcesses: cfg.browser?.processes ?? null,
      version: cfg.version ?? null,
    }),
  });
  if (!resp.ok) {
    const body = await resp.json().catch(() => null);
    throw new Error(body?.error ?? `save ${resp.status}`);
  }
}
