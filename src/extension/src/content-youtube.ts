// YouTube content script (decision #8): the tab title has NO channel name, so we read
// it from the page DOM — watch pages, Shorts and embeds — and push it to the SW.

/** Channel is meaningful ONLY on video pages: on home/search/subscriptions the same
 *  selectors would read a random FEED CARD's channel (wrong-channel bug). */
function isVideoPage(): boolean {
  const p = location.pathname;
  return p === "/watch" || p.startsWith("/shorts/") || p.startsWith("/embed/") || p.startsWith("/live/");
}

function getChannel(): string {
  if (!isVideoPage()) return "";
  const selectors = [
    "ytd-video-owner-renderer ytd-channel-name a", // watch page
    "ytd-channel-name#channel-name a",
    "yt-reel-channel-bar-view-model a", // shorts (new UI)
    "ytd-reel-player-header-renderer ytd-channel-name a", // shorts (old UI)
    ".ytp-title-expanded-title a", // embeds
  ];
  for (const sel of selectors) {
    const el = document.querySelector(sel);
    const name = el?.textContent?.trim();
    if (name) return name;
  }
  // structured-data fallback
  const meta = document.querySelector("span[itemprop='author'] link[itemprop='name']");
  return meta?.getAttribute("content") ?? "";
}

let last: string | null = null; // null = nothing sent yet, so the first "" is pushed too
function push(): void {
  const channel = getChannel();
  if (channel !== last) {
    last = channel;
    // an EMPTY channel must be pushed too: navigating from a video to the homepage
    // would otherwise leave the old channel "stuck" on the tab (sticky-channel bug)
    void chrome.runtime.sendMessage({ type: "yt-channel", channel }).catch(() => undefined);
  }
}

// YouTube is a SPA — yt-navigate-finish fires on internal navigation
document.addEventListener("yt-navigate-finish", () => setTimeout(push, 1500));
setInterval(push, 5000);
push();
