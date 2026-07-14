// Service worker — dumb reporter (architecture §1.4, decision #5).
// No keep-alive tricks: every heartbeat fetch resets the SW idle timer, chrome.alarms
// is the backstop, and durable state lives in chrome.storage.local (per-profile).

const DAEMON = "http://127.0.0.1:5601/browser/heartbeat";
const HEARTBEAT_ALARM = "tracker-heartbeat";

// channel per tab, filled by the YouTube content script (decision #8).
// Mirrored to chrome.storage.session: the MV3 service worker dies after ~30s idle and
// an in-memory-only map would lose every channel until the next content-script push.
const channelByTab = new Map<number, string>();

function persistChannels(): void {
  void chrome.storage.session
    .set({ channels: Object.fromEntries(channelByTab) })
    .catch(() => undefined);
}

async function restoreChannels(): Promise<void> {
  try {
    const st = await chrome.storage.session.get(["channels"]);
    for (const [k, v] of Object.entries((st.channels ?? {}) as Record<string, string>)) {
      channelByTab.set(Number(k), v);
    }
  } catch {
    // storage.session unavailable — degrade to memory-only (old behavior)
  }
}

// retry queue: heartbeats that failed to reach the daemon (restart/crash) are kept with
// their ORIGINAL timestamps and replayed on the next successful contact (bounded).
const QUEUE_KEY = "hbQueue";
const QUEUE_MAX = 60; // ~30 min at the 30s cadence — covers a daemon restart comfortably

async function enqueue(payload: unknown): Promise<void> {
  try {
    const st = await chrome.storage.session.get([QUEUE_KEY]);
    const q = ((st[QUEUE_KEY] ?? []) as unknown[]).concat([payload]);
    await chrome.storage.session.set({ [QUEUE_KEY]: q.slice(-QUEUE_MAX) });
  } catch {
    // no queue available — old behavior (drop)
  }
}

async function flushQueue(): Promise<void> {
  try {
    const st = await chrome.storage.session.get([QUEUE_KEY]);
    const q = (st[QUEUE_KEY] ?? []) as unknown[];
    if (q.length === 0) return;
    await chrome.storage.session.set({ [QUEUE_KEY]: [] });
    for (let i = 0; i < q.length; i++) {
      try {
        await fetch(DAEMON, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(q[i]),
        });
      } catch {
        // daemon down again — put the unsent remainder back (newest-last, still bounded)
        const st2 = await chrome.storage.session.get([QUEUE_KEY]);
        const cur = (st2[QUEUE_KEY] ?? []) as unknown[];
        await chrome.storage.session.set({ [QUEUE_KEY]: q.slice(i).concat(cur).slice(-QUEUE_MAX) });
        return;
      }
    }
  } catch {
    // best effort
  }
}

let focused = true;
let unfocusDebounce: ReturnType<typeof setTimeout> | undefined;

// F4: focus-mode blocklist, refreshed from every heartbeat response (two-way channel, decision #5)
let focusBlock: { active: boolean; blockedDomains: string[] } = { active: false, blockedDomains: [] };

function domainBlocked(url: string | undefined): boolean {
  if (!focusBlock.active || !url) return false;
  try {
    const host = new URL(url).hostname.toLowerCase();
    return focusBlock.blockedDomains.some(
      (d) => host === d.toLowerCase() || host.endsWith("." + d.toLowerCase()),
    );
  } catch {
    return false;
  }
}

function enforceTab(tab: chrome.tabs.Tab): void {
  if (tab.id != null && domainBlocked(tab.url)) {
    void chrome.tabs.remove(tab.id).catch(() => undefined);
  }
}

chrome.runtime.onInstalled.addListener(async () => {
  const st = await chrome.storage.local.get(["instanceId"]);
  if (!st.instanceId) {
    await chrome.storage.local.set({ instanceId: crypto.randomUUID() });
  }
  chrome.alarms.create(HEARTBEAT_ALARM, { periodInMinutes: 0.5 });
});
chrome.runtime.onStartup.addListener(() => {
  chrome.alarms.create(HEARTBEAT_ALARM, { periodInMinutes: 0.5 });
});

// MV3 silent-death fix (diagnosticat 2026-07-10: ~35 min/zi fără heartbeat-uri): dacă
// alarma s-a pierdut (reload/update/restart fără onStartup), NIMENI n-o mai recreează și
// raportarea moare până la următorul eveniment de tab. Acest bloc top-level rulează la
// ORICE trezire a service worker-ului: re-asigură alarma și raportează imediat.
void (async () => {
  const existing = await chrome.alarms.get(HEARTBEAT_ALARM).catch(() => undefined);
  if (!existing) chrome.alarms.create(HEARTBEAT_ALARM, { periodInMinutes: 0.5 });
  await restoreChannels(); // SW woke up fresh — recover the per-tab channels first
  void report();
})();

chrome.alarms.onAlarm.addListener((a) => {
  if (a.name === HEARTBEAT_ALARM) void report();
});

chrome.tabs.onActivated.addListener(() => void report());
chrome.tabs.onUpdated.addListener((tabId, info, tab) => {
  if (info.url || info.status === "loading") enforceTab(tab); // focus-mode block, ANY tab
  // tab navigated off YouTube entirely — its channel is no longer valid
  if (info.url && !/^https?:\/\/([^/]*\.)?youtube\.com\//i.test(info.url) && channelByTab.delete(tabId)) {
    persistChannels();
  }
  // an audible change on ANY tab (background included) must report IMMEDIATELY: the
  // video rule would otherwise keep counting (or miss) up to a full heartbeat interval
  if (info.audible !== undefined) {
    void report();
    return;
  }
  if (tab.active && (info.url || info.title)) void report();
});
chrome.tabs.onRemoved.addListener((tabId) => {
  if (channelByTab.delete(tabId)) persistChannels();
  void report(); // an audible tab may have just closed — drop anyAudible right away
});

chrome.windows.onFocusChanged.addListener((windowId) => {
  if (windowId === chrome.windows.WINDOW_ID_NONE) {
    // Windows quirk: switching between two windows of the SAME browser fires a
    // transient WINDOW_ID_NONE — debounce before reporting blur (chromium bug 171995)
    unfocusDebounce = setTimeout(() => {
      focused = false;
      void report();
    }, 400);
  } else {
    if (unfocusDebounce !== undefined) clearTimeout(unfocusDebounce);
    focused = true;
    void report();
  }
});

chrome.runtime.onMessage.addListener((msg, sender) => {
  if (msg?.type === "yt-channel" && sender.tab?.id != null && typeof msg.channel === "string") {
    // empty channel = the tab LEFT the video (homepage/search) — clear, don't keep it
    if (msg.channel) channelByTab.set(sender.tab.id, msg.channel);
    else channelByTab.delete(sender.tab.id);
    persistChannels();
    if (sender.tab.active) void report();
  }
});

async function report(): Promise<void> {
  try {
    const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
    if (!tab) return;

    // re-derive REAL focus every report: the event-driven flag can go stale (SW death,
    // dropped blur report) and would keep claiming focus from an unfocused profile
    try {
      const w = await chrome.windows.getLastFocused();
      focused = !!w.focused;
    } catch {
      // keep the event-driven value
    }

    const [all, audibleTabs, st] = await Promise.all([
      chrome.tabs.query({}),
      chrome.tabs.query({ audible: true }),
      chrome.storage.local.get(["profileLabel"]),
    ]);

    const payload = {
      url: tab.url ?? "",
      title: tab.title ?? "",
      audible: !!tab.audible,
      anyAudible: audibleTabs.length > 0, // video rule needs background-tab audio too
      incognito: tab.incognito,
      tabCount: all.length,
      channel: tab.id != null ? (channelByTab.get(tab.id) ?? null) : null,
      profile: (st.profileLabel as string | undefined) ?? "",
      focused,
      browser: navigator.userAgent.includes("Edg/") ? "edge" : "chrome",
      // original capture time: replayed queue entries must not be stamped at write time
      timestamp: new Date().toISOString(),
    };

    let resp: Response;
    try {
      resp = await fetch(DAEMON, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      });
    } catch (e) {
      // daemon down (restart/crash) — queue with the original timestamp and replay later
      await enqueue(payload);
      throw e;
    }
    void flushQueue(); // daemon reachable again — drain anything accumulated

    // F4: the response carries the focus-mode blocklist
    const body = (await resp.json().catch(() => null)) as
      | { focus?: { active: boolean; blockedDomains: string[] } }
      | null;
    if (body?.focus) {
      focusBlock = body.focus;
      if (focusBlock.active) {
        const all = await chrome.tabs.query({});
        for (const t of all) enforceTab(t);
      }
    }
  } catch {
    // already queued above when the daemon was unreachable; anything else is best-effort
  }
}
