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
);
