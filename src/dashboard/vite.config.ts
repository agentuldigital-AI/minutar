import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// build lands in the daemon's wwwroot — Kestrel serves it at :5601 (decision #11)
export default defineConfig({
  plugins: [react()],
  build: { outDir: "../Tracker.Daemon/wwwroot", emptyOutDir: true },
  server: {
    proxy: {
      "/api": "http://127.0.0.1:5601",
      "/state": "http://127.0.0.1:5601",
    },
  },
});
