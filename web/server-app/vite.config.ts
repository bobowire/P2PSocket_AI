import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";

// 06 §6：hash 路由 + base './'；产物直写宿主 wwwroot（08 §2②）
export default defineConfig({
  base: "./",
  plugins: [vue()],
  build: {
    outDir: "../../src/P2P.Server/wwwroot",
    emptyOutDir: true,
  },
  server: {
    port: 5174,
    proxy: {
      "/api": { target: "http://127.0.0.1:7500", changeOrigin: true },
      "/ws": { target: "ws://127.0.0.1:7500", ws: true },
    },
  },
});
