import { defineConfig } from "vite";

export default defineConfig({
  // "./" keeps asset paths working on GitHub Project Pages
  // (https://embabyty.github.io/MusicSpeedChangerMacOS/) and locally.
  base: "./",
  publicDir: "public",
  build: {
    outDir: "dist",
    emptyOutDir: true,
  },
  server: {
    port: 5173,
  },
  preview: {
    port: 4173,
  },
});
