import { defineConfig } from "vite";

export default defineConfig({
  base: "/acs-participant/",
  build: {
    outDir: "../wwwroot/acs-participant",
    emptyOutDir: true
  }
});
