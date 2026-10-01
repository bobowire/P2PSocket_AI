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
    // 浏览器 API 白名单（client-app 同款）：App 路由/location、Devices 复制 navigator.clipboard、
    // 测试对 document 弹窗定位；jsdom 测试环境同源
    files: ["src/**/*.{ts,vue}"],
    languageOptions: {
      globals: {
        document: "readonly",
        navigator: "readonly",
        location: "readonly",
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
