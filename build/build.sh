#!/bin/sh
# 本地构建全链（08 §2 ①→④ 顺序：build → export-ts → pnpm build → test）
# 用法：sh build/build.sh [quick]   quick = 跳过前端阶段
# 依赖：dotnet SDK 10、node ≥20、pnpm（corepack enable pnpm）；
#       可选 android workload（M8-05 守卫探测：无则跳过明示，不影响桌面链）
set -e
cd "$(dirname "$0")/.."

echo "==> [1/4] dotnet build"
dotnet build AI-P2P.sln -c Release

# android 壳（M8-05，设计 13 §8）：工程不进 AI-P2P.sln（TD-31——免全构建机装数 GB workload），
# 此步独立构建；workload 探测守卫沿 ExportTs/ISCC 先例——缺则跳过明示不报错（CI 同口径）
if dotnet workload list 2>/dev/null | grep -qw android; then
  echo "==> android 壳构建（src/P2P.Client.Android）"
  dotnet build src/P2P.Client.Android -c Release
else
  echo "==> android 壳：workload 未安装（dotnet workload install android），跳过——桌面链不受影响"
fi

# export-ts：反射导出 API 类型 → ui-shared/types/api.d.ts（06 §5，M1-31 落地生成器）
if [ -d src/Tools/ExportTs ]; then
  echo "==> [2/4] export-ts"
  dotnet run --project src/Tools/ExportTs -c Release
else
  echo "==> [2/4] export-ts：生成器未建（M1-31），跳过"
fi

# 前端：pnpm workspace 构建产物直写两宿主 wwwroot（08 §2②，M1-03 起）
if [ -f web/pnpm-workspace.yaml ] && [ "$1" != "quick" ]; then
  echo "==> [3/4] pnpm build"
  pnpm -C web --filter @p2p/client-app --filter @p2p/server-app build
else
  echo "==> [3/4] pnpm build：workspace 未建（M1-03）或 quick 模式，跳过"
fi

echo "==> [4/4] dotnet test"
dotnet test AI-P2P.sln -c Release --no-build

echo "==> 全链完成"
