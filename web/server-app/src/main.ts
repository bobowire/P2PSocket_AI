import { createApp } from "vue";
import { createPinia } from "pinia";
import ElementPlus from "element-plus";
import "element-plus/dist/index.css";
import App from "./App.vue";
import { router } from "./router";

// 服务端 Web 后台全功能属 M3（任务清单非目标表），M1-03 仅建立骨架
const app = createApp(App);
app.use(createPinia());
app.use(ElementPlus);
app.use(router);
app.mount("#app");
