#!/bin/sh
# 打包脚本（08 §2⑤，M4-01）：前端构建 → dotnet publish 单文件自包含 → out/ 产物目录（+Linux tar.gz 归档）。
# 用法：sh build/package.sh [client-win|client-linux|server|android|all]   缺省 all
# 依赖：dotnet SDK 10、node ≥20、pnpm（corepack enable pnpm）。
# Windows 客户端另需 Wintun.dll（官方 0.14.1 amd64，wintun.net；放 out/wintun/Wintun.dll，
#   脚本自动拷入产物；未放置则警告提示手工分发——08 §3.1 Wintun 走程序目录分发不内嵌）。
# android 目标（M8-05，设计 13 §8）另需 android workload（dotnet workload install android）
#   + keytool（JDK/JRE 随 workload 依赖装）；签名 keystore=deploy/client-android/p2pclient.keystore。
# publish 参数=部署文档验证过的口径（PublishSingleFile+SelfContained；M1-37/M3-18 实机轮沿用）。
set -e
cd "$(dirname "$0")/.."

TARGET="${1:-all}"

# 版本号：src/Directory.Build.props 单一事实源（M4-01 注入，git 短哈希经 ThisAssembly.Metadata 进 exe）
VERSION=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' src/Directory.Build.props | head -1)
[ -n "$VERSION" ] || VERSION="0.0.0"

web_build() { # $*=pnpm --filter 包名
  echo "==> 前端构建 $*"
  pnpm -C web "$@" build
}

publish() { # $1=项目路径 $2=RID $3=产物目录名
  echo "==> publish $1 ($2) → out/$3"
  rm -rf "out/$3"
  dotnet publish "$1" -c Release -r "$2" \
    -p:PublishSingleFile=true -p:SelfContained=true -o "out/$3"
  # 产物校验：可执行体 + 前端入口（wwwroot 由 SDK 自 src 宿主目录拷入）
  test -f "out/$3/p2p-client" || test -f "out/$3/p2p-client.exe" || test -f "out/$3/p2p-server" \
    || { echo "产物校验失败：out/$3 无可执行体"; exit 1; }
  test -f "out/$3/wwwroot/index.html" || { echo "产物校验失败：out/$3/wwwroot/index.html 缺失（前端未构建？）"; exit 1; }
}

archive() { # $1=产物目录名（Linux 分发形态 tar.gz；分发名前缀 P2PSocket-，2026-10-21 命名口径）
  echo "==> 归档 out/$1 → out/P2PSocket-$1-v$VERSION.tar.gz"
  tar -czf "out/P2PSocket-$1-v$VERSION.tar.gz" -C "out/$1" .
  sha256_manifest "out/P2PSocket-$1-v$VERSION.tar.gz"
}

# M5-14/SEC-84（07 §14.4）产物哈希清单：每分发件一份 <文件名>.sha256，内容单行
#   「<小写hex64>␣␣<产物文件名>」（两空格=GNU sha256sum 文本模式口径，sha256sum -c 直接核验）。
sha256_manifest() { # $1=分发件路径（out/ 下）
  local f name
  f="$1"; name=$(basename "$f")
  echo "==> SHA-256 清单 $f.sha256"
  (cd out && sha256sum "$name" > "$name.sha256")
}

# 全产物汇总 SHA256SUMS（同格式多行）：按本次在位分发件全量重建（幂等；单目标运行不重复登记）
write_sums() {
  : > out/SHA256SUMS
  for f in out/P2PSocket-*.tar.gz out/P2PSocket-Client-Setup-*.exe out/P2PSocket-Client-Android-*.apk; do
    [ -f "$f" ] || continue
    [ -f "$f.sha256" ] || sha256_manifest "$f"
    cat "$f.sha256" >> out/SHA256SUMS
  done
  if [ -s out/SHA256SUMS ]; then
    echo "==> 汇总清单 out/SHA256SUMS（$(wc -l < out/SHA256SUMS) 条）"
    STATUS="$STATUS SHA256SUMS"
  fi
}

want() { echo "$TARGET" | grep -qw "$1"; } # all 含全部目标

STATUS=""
run_client_win() {
  web_build --filter @p2p/client-app
  publish src/P2P.Client win-x64 client-win
  if [ -f out/wintun/Wintun.dll ]; then
    cp out/wintun/Wintun.dll out/client-win/
    echo "==> Wintun.dll 已拷入 out/client-win"
  else
    echo "警告：out/wintun/Wintun.dll 不存在——产物未含 Wintun 驱动库，分发前须手工放置（08 §3.1）"
  fi
  STATUS="$STATUS client-win(v$VERSION)"
}
run_client_linux() {
  web_build --filter @p2p/client-app
  publish src/P2P.Client linux-x64 client-linux
  archive client-linux
  STATUS="$STATUS client-linux(v$VERSION)"
}
run_server() {
  web_build --filter @p2p/server-app
  publish src/P2P.Server linux-x64 server
  archive server
  STATUS="$STATUS server(v$VERSION)"
}
run_android() { # M8-05（设计 13 §8）：Android APK——workload/keystore 守卫 + 自签发布件
  if ! dotnet workload list 2>/dev/null | grep -qw android; then
    echo "警告：android workload 未安装（dotnet workload install android）——跳过 android 打包"
    STATUS="$STATUS android(跳过：workload 未装)"
    return
  fi
  local keystore="deploy/client-android/p2pclient.keystore"
  if [ ! -f "$keystore" ]; then
    echo "警告：$keystore 不存在（M8-05 keytool 生成件）——跳过 android 打包"
    STATUS="$STATUS android(跳过：keystore 缺失)"
    return
  fi
  web_build --filter @p2p/client-app # wwwroot 先行（APK 以 AndroidAsset 打包之，设计 13 §3.1）
  echo "==> publish src/P2P.Client.Android → out/client-android"
  rm -rf out/client-android
  dotnet publish src/P2P.Client.Android -c Release -o out/client-android \
    -p:AndroidKeyStore=true \
    -p:AndroidSigningKeyStore="$PWD/$keystore" \
    -p:AndroidSigningKeyAlias=p2pclient \
    -p:AndroidSigningKeyPass=p2psocket-android-2026 \
    -p:AndroidSigningStorePass=p2psocket-android-2026
  local apk
  apk=$(ls out/client-android/*-Signed.apk 2>/dev/null | head -1)
  [ -n "$apk" ] || { echo "产物校验失败：out/client-android 无签名 APK"; exit 1; }
  echo "==> 发布件 $apk → out/P2PSocket-Client-Android-v$VERSION.apk"
  mv "$apk" "out/P2PSocket-Client-Android-v$VERSION.apk"
  sha256_manifest "out/P2PSocket-Client-Android-v$VERSION.apk"
  STATUS="$STATUS android(v$VERSION)"
}
run_win_setup() { # M4-02：Inno Setup 安装包（依赖 client-win 产物；ISCC 缺失=环境依赖如实标注）
  run_client_win
  ISCC="/c/Program Files (x86)/Inno Setup 6/ISCC.exe"
  if [ ! -f "$ISCC" ]; then
    ISCC="/c/Program Files/Inno Setup 6/ISCC.exe"
  fi
  if [ ! -f "$ISCC" ]; then # winget 用户级安装位（JRSoftware.InnoSetup 默认）
    ISCC="$LOCALAPPDATA/Programs/Inno Setup 6/ISCC.exe"
  fi
  if [ -f "$ISCC" ]; then
    echo "==> ISCC 编译 P2PSocket-Client.iss → out/P2PSocket-Client-Setup-$VERSION-x64.exe"
    "$ISCC" //DPkgVersion="$VERSION" deploy/client/win/P2PSocket-Client.iss || { echo "ISCC 编译失败"; exit 1; }
    test -f "out/P2PSocket-Client-Setup-$VERSION-x64.exe" \
      || { echo "产物校验失败：out/P2PSocket-Client-Setup-$VERSION-x64.exe 缺失"; exit 1; }
    sha256_manifest "out/P2PSocket-Client-Setup-$VERSION-x64.exe"
    STATUS="$STATUS win-setup(v$VERSION)"
  else
    echo "警告：ISCC.exe 不存在（Inno Setup 6 未安装，winget install JRSoftware.InnoSetup）——"
    echo "      跳过安装包编译（环境依赖，M4-02 清单 §8.2）；deploy/client/win/P2PClient.iss 为交付物"
    STATUS="$STATUS win-setup(跳过：ISCC 未装)"
  fi
}

case "$TARGET" in
  client-win) run_client_win ;;
  client-linux) run_client_linux ;;
  server) run_server ;;
  android) run_android ;;
  win-setup) run_win_setup ;;
  all) run_client_win; run_client_linux; run_server; run_android ;;
  *) echo "未知目标：$TARGET（支持 client-win|client-linux|server|android|win-setup|all）"; exit 1 ;;
esac

write_sums # M5-14：分发件 .sha256 齐备后汇总（无分发件目标=空文件不报）

echo "==> 打包完成：$STATUS"
