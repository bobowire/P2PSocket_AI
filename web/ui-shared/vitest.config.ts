// M1-31 ui-shared 测试配置：组件挂载用 happy-dom（WS/剪贴板经注入替身，无需真环境）。
import { defineConfig } from "vitest/config";
import vue from "@vitejs/plugin-vue";

export default defineConfig({
  plugins: [vue()],
  test: {
    environment: "happy-dom",
    include: ["src/**/*.test.ts"],
  },
});
