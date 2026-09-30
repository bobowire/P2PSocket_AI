import js from "@eslint/js";
import pluginVue from "eslint-plugin-vue";
import tseslint from "typescript-eslint";

export default tseslint.config(
  { ignores: ["dist/**", "node_modules/**", "../../src/**/wwwroot/**"] },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  ...pluginVue.configs["flat/recommended"],
  {
    files: ["src/pages/**/*.vue"],
    rules: { "vue/multi-word-component-names": "off" },
  },
  {
    files: ["**/*.vue"],
    languageOptions: { parserOptions: { parser: tseslint.parser } },
  },
  {
    // 浏览器 API 白名单（App 的 location/useWs 回调、store 轮询与速率图采样定时器、
    // Logs 查询串与导出新窗口直下；happy-dom 测试环境同源）
    files: ["src/**/*.{ts,vue}"],
    languageOptions: {
      globals: {
        document: "readonly",
        navigator: "readonly",
        location: "readonly",
        WebSocket: "readonly",
        window: "readonly",
        URLSearchParams: "readonly",
        setTimeout: "readonly",
        clearTimeout: "readonly",
        setInterval: "readonly",
        clearInterval: "readonly",
      },
    },
  },
);
