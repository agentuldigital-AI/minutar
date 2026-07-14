import { useEffect, useState } from "react";
import Dashboard from "./Dashboard";
import Journal from "./Journal";
import Settings from "./Settings";
import Weekly from "./Weekly";

type View = "dash" | "weekly" | "journal" | "settings";

function viewFromHash(): View {
  if (window.location.hash === "#settings") return "settings";
  if (window.location.hash === "#weekly") return "weekly";
  if (window.location.hash === "#journal") return "journal";
  return "dash";
}

export default function App() {
  const [view, setView] = useState<View>(viewFromHash);

  useEffect(() => {
    const onHash = () => setView(viewFromHash());
    window.addEventListener("hashchange", onHash);
    return () => window.removeEventListener("hashchange", onHash);
  }, []);

  const go = (v: View) => {
    setView(v);
    window.location.hash = v === "dash" ? "" : v;
  };

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          <span className="brand-mark">⏱</span>
          <span className="brand-name">Minutar</span>
        </div>
        <nav className="tabs" aria-label="Pagini">
          <button className={view === "dash" ? "active" : ""} onClick={() => go("dash")}>
            Dashboard
          </button>
          <button className={view === "weekly" ? "active" : ""} onClick={() => go("weekly")}>
            Săptămânal
          </button>
          <button className={view === "journal" ? "active" : ""} onClick={() => go("journal")}>
            Jurnal
          </button>
          <button className={view === "settings" ? "active" : ""} onClick={() => go("settings")}>
            Setări
          </button>
        </nav>
      </header>
      {view === "dash" ? <Dashboard /> : view === "weekly" ? <Weekly /> : view === "journal" ? <Journal /> : <Settings />}
    </div>
  );
}
