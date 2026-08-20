#!/bin/sh
# 本地构建全链（08 §2 ①→④ 顺序：build → export-ts → pnpm build → test）
# 用法：sh build/build.sh [quick]   quick = 跳过前端阶段
# 依赖：dotnet SDK 10、node ≥20、pnpm（corepack enable pnpm）
set -e
cd "$(dirname "$0")/.."

echo "==> [1/4] dotnet build"
dotnet build AI-P2P.sln -c Release

# export-ts：反射导出 API 类型 → ui-shared/types/api.d.ts（06 §5，M1-31 落地生成器）
if [ -d tools/ExportTs ]; then
  echo "==> [2/4] export-ts"
  dotnet run --project tools/ExportTs -c Release
else
  echo "==> [2/4] export-ts：生成器未建（M1-31），跳过"
fi

# 前端：pnpm workspace 构建产物直写两宿主 wwwroot（08 §2②，M1-03 起）
if [ -f web/pnpm-workspace.yaml ] && [ "$1" != "quick" ]; then
  echo "==> [3/4] pnpm build"
  pnpm --filter @p2p/ui-shared --filter @p2p/client-app --filter @p2p/server-app build
else
  echo "==> [3/4] pnpm build：workspace 未建（M1-03）或 quick 模式，跳过"
fi

echo "==> [4/4] dotnet test"
dotnet test AI-P2P.sln -c Release --no-build

echo "==> 全链完成"
