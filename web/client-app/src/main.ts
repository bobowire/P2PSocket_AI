import { createApp } from "vue";
import { createPinia } from "pinia";
import ElementPlus from "element-plus";
import "element-plus/dist/index.css";
import App from "./App.vue";
import { router } from "./router";

// MSW 接入（M1-03）：VITE_USE_MSW=1 时启用 mock（前端独立开发联调，06 §5）
async function enableMswIfRequested() {
  if (import.meta.env.VITE_USE_MSW === "1") {
    const { worker } = await import("./mocks/browser");
    await worker.start({ onUnhandledRequest: "bypass" });
  }
}

await enableMswIfRequested();

const app = createApp(App);
app.use(createPinia());
app.use(ElementPlus);
app.use(router);
app.mount("#app");
