import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";

// 06 §6：hash 路由 + base './'（嵌入式静态托管）；产物直写宿主 wwwroot（08 §2②）
export default defineConfig({
  base: "./",
  plugins: [vue()],
  build: {
    outDir: "../../src/P2P.Client/wwwroot",
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      "/api": { target: "http://127.0.0.1:7100", changeOrigin: true },
      "/ws": { target: "ws://127.0.0.1:7100", ws: true },
    },
  },
});
