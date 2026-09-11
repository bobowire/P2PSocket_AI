import { defineConfig } from "vitest/config";
import vue from "@vitejs/plugin-vue";

// M1-32 走查测试：真实页面 + MSW node 拦截（06 §5）。
// VITE_API_BASE 经 define 注入 http://localhost（node 下 axios 需绝对地址；MSW path-only 模式任意源匹配）。
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
        // element-plus 外置时其 import 'async-validator' 走 Node ESM 解析 → CJS 构建
        // （module.exports = { default: Schema }，无 __esModule）→ 默认导入是模块对象而非类，
        // new AsyncValidator() 同步抛 TypeError，form-item 校验链断裂且 form.validate() 恒真（校验形同虚设）。
        // 内联后由 vite 解析 → "module" 字段 → dist-web ESM，默认导入即 Schema 类，与浏览器行为一致。
        inline: ["element-plus"],
      },
    },
  },
});
