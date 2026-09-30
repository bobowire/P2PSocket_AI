import { defineConfig } from "vitest/config";
import vue from "@vitejs/plugin-vue";

// M3-09 走查测试（client-app M1-32 同款装配）：真实页面 + MSW node 拦截。
// VITE_API_BASE 经 define 注入 http://localhost（node 下 axios 需绝对地址；MSW path-only 任意源匹配）。
export default defineConfig({
  plugins: [vue()],
  define: {
    "import.meta.env.VITE_API_BASE": JSON.stringify("http://localhost"),
  },
  test: {
    environment: "happy-dom",
    include: ["src/**/*.test.ts"],
    setupFiles: ["src/__tests__/setup.ts"],
    server: {
      deps: {
        // element-plus 内联（M1-32 教训：外置时 async-validator 走 Node ESM 解析 CJS 构建，
        // form.validate() 恒真——校验形同虚设）
        inline: ["element-plus"],
      },
    },
  },
});
