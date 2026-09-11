<script setup lang="ts">
// M1-33 /groups 占位页（06 §2、FR-C-805）：分组管理（已加入列表/凭码入组/新建/审批，
// 0x55~0x57）属 M2——本页仅骨架 + 哑节点横幅（M1-33 完成判定三页之一）。
// M1 前无 /api/groups 端点（04 §2 全集未含）；占位不发起请求。
import PassiveBanner from "../components/PassiveBanner.vue";
</script>

<template>
  <section class="groups">
    <PassiveBanner />
    <div class="toolbar">
      <h2>分组</h2>
      <el-button
        disabled
        title="凭码入组属 M2（0x50~0x54）"
        data-testid="groups-join"
      >
        凭码入组（M2）
      </el-button>
      <el-button
        disabled
        title="新建分组属 M2（0x55）"
        data-testid="groups-create"
      >
        新建分组（M2）
      </el-button>
    </div>
    <el-empty description="分组管理将在后续版本提供（FR-C-805 → M2）" />
  </section>
</template>

<style scoped>
.toolbar { display: flex; align-items: center; gap: 12px; margin-bottom: 12px; }
.toolbar h2 { margin: 0; font-size: 16px; }
</style>
