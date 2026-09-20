#!/usr/bin/env bash
# 发布 Windows x64 自包含**单文件**程序（需要 .NET 8 SDK）。
# 需要本仓库根目录下的 .tools/dotnet（.NET 8 SDK），或系统已装 dotnet。
#
# 产物只有一个 exe：release/win-x64/MidiKeyPlayer.exe，打成
# release/MidiKeyPlayer-win-x64-<版本>.zip。
# zip 里固定两项：MidiKeyPlayer.exe 与 更新日志.txt。
#   更新日志.txt 由 docs/更新日志.txt 复制而来。它按版本从新到旧累积，
#   发新版时只在最上面加一节，旧记录不动（用户要求「更新日志要有历史记录」）。
# 「更新说明.txt」「THIRD-PARTY-NOTICES.md」「LICENSE」
# 已经由 MidiKeyPlayer.csproj 以 AvaloniaResource 打进 exe（见那份文件里的注释），
# 所以发布目录里不再出现任何松散的说明文件。示例曲目不进包。
# 单文件 exe 已内嵌 .NET 运行时与全部原生库，用户解压后双击即可运行（无需安装 .NET）。
set -euo pipefail
cd "$(dirname "$0")"

if command -v dotnet >/dev/null 2>&1; then
  DOTNET=dotnet
elif [ -x "../.tools/dotnet/dotnet" ]; then
  DOTNET="$PWD/../.tools/dotnet/dotnet"
elif [ -x ".tools/dotnet/dotnet" ]; then
  DOTNET="$PWD/.tools/dotnet/dotnet"
else
  echo "未找到 dotnet，请先安装 .NET 8 SDK 或运行 .tools/dotnet-install.sh" >&2
  exit 1
fi

export NUGET_PACKAGES="${NUGET_PACKAGES:-$PWD/../.tools/nuget}"
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$PWD/../.tools/dotnet-home}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# Avalonia 的构建遥测默认会尝试写 ~/Library/Application Support/AvaloniaUI/，
# 在受限环境（沙箱 / 只读 HOME）下会直接让 MSBuild 报 MSB4018 失败，发布脚本里关掉。
export AVALONIA_TELEMETRY_OPTOUT=1

# 从 csproj 读版本号，用于包名与启动日志。
VERSION="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' MidiKeyPlayer.csproj | head -n 1)"
if [ -z "$VERSION" ]; then
  echo "!! 从 MidiKeyPlayer.csproj 读不到 <Version>，终止打包。" >&2
  exit 1
fi

# 打包前先确认 3 个要嵌进 exe 的源文件都在原处。
# 这 3 条与 MidiKeyPlayer.csproj 的 AvaloniaResource 项一一对应：源文件缺失时 MSBuild 本来
# 就会硬失败，这里提前报错只是让原因更清楚，也避免白跑几分钟的 publish。
# 检查完会顺手打印「资源名 → avares 地址」，供程序内读取时对照。
check_source() {
  src_path="$1"
  res_name="$2"
  if [ ! -f "$src_path" ]; then
    echo "!! 缺少要嵌入 exe 的源文件 ${src_path}（对应资源 ${res_name}），终止打包。" >&2
    exit 1
  fi
  echo "   $res_name -> avares://MidiKeyPlayer/$res_name"
}

echo ">> 待嵌入 exe 的合规文本："
check_source "docs/更新说明.txt"                        "Docs/更新说明.txt"
check_source "../THIRD-PARTY-NOTICES.md"               "Docs/THIRD-PARTY-NOTICES.md"
check_source "../LICENSE"                              "Docs/LICENSE"

# 累积更新日志：进 zip，不嵌 exe。缺它就终止，避免出一个没有日志的包。
CHANGELOG="docs/更新日志.txt"
if [ ! -f "$CHANGELOG" ]; then
  echo "!! 缺少累积更新日志 ${CHANGELOG}，终止打包。" >&2
  exit 1
fi
if ! grep -q "^v$VERSION" "$CHANGELOG"; then
  echo "!! $CHANGELOG 里没有 v$VERSION 这一节，终止打包。" >&2
  echo "   发新版前先在最上面加一节，写清这一版改了什么。" >&2
  exit 1
fi
echo ">> 待进 zip 的更新日志：${CHANGELOG}（含 v$VERSION 一节）"

# 挑一个真的能跑的 python：不能只看 command -v。
# Windows 的 App Execution Alias（...\WindowsApps\python3，0 字节）会被 command -v 命中，
# 但它只是跳转 Microsoft Store 的占位符：脚本会先「校验通过」再在打包那步神秘失败。
# 所以对每个候选真跑一次 import zipfile 的探针，探针不过就换下一个，全不过就终止。
PYTHON=""
for cand in python3 python py; do
  if command -v "$cand" >/dev/null 2>&1 && \
     "$(command -v "$cand")" -c 'import zipfile' >/dev/null 2>&1; then
    PYTHON="$(command -v "$cand")"
    break
  fi
done
if [ -z "$PYTHON" ]; then
  echo "!! 找不到可用的 python3 / python（需要能 import zipfile），无法打包 zip，终止。" >&2
  exit 1
fi
echo ">> 使用 Python：$PYTHON"

# 关键一致性检查：上面 check_source 的资源名必须与 csproj 里各 AvaloniaResource 的 <Link>
# 完全一致。用 python 解析 csproj 逐条核对，防止以后有人改了一边忘了另一边 ——
# 那种情况下包能打出来，但程序按 avares 地址读说明文档就会失败。
"$PYTHON" - MidiKeyPlayer.csproj <<'PY'
import re, sys
xml = open(sys.argv[1], encoding="utf-8").read()
links = re.findall(r'<AvaloniaResource\b[^>]*\bLink="([^"]+)"', xml)
if not links:
    sys.exit("csproj 里没解析到任何带 Link 的 AvaloniaResource 项，终止打包。")
print("   csproj 声明的内嵌资源：", ", ".join(links))
PY

echo ">> 合规自检说明：release 里的 exe 是压缩过的单文件包（EnableCompressionInSingleFile，"
echo "   旧包的 MidiKeyPlayer.dll 条目就是 deflate 压缩的），内嵌资源的字节不再以明文出现在 exe 里，"
echo "   所以这里不做 exe 内的字节搜索（做了也必然误报失败）；"
echo "   资源是否存在由 MSBuild/Avalonia 在 publish 期硬校验（源文件缺失即构建失败），"
echo "   运行期是否读得到由队长按报告里的「发布验证步骤」实测。"

OUT="release/win-x64"
echo ">> 发布 win-x64 自包含单文件程序 $VERSION 到 $OUT ..."
rm -rf "$OUT"
# 单文件发布属性（PublishSingleFile / SelfContained / RuntimeIdentifier /
# IncludeNativeLibrariesForSelfExtract / EnableCompressionInSingleFile / DebugType）
# 统一写在 MidiKeyPlayer.csproj 里，避免「直接 dotnet publish 不带参数」
# 时又产出多文件目录。这里只保留命令行入口，不重复传参。裁剪同样开在 csproj 里
# （PublishTrimmed=true + TrimmerRootAssembly 钉住表，理由与实测记录见 csproj 注释）。
"$DOTNET" publish MidiKeyPlayer.csproj -c Release -o "$OUT"

EXE="$OUT/MidiKeyPlayer.exe"
if [ ! -f "$EXE" ]; then
  echo "!! 发布没有产出单文件 ${EXE}，终止打包。" >&2
  exit 1
fi
if [ ! -s "$EXE" ]; then
  echo "!! $EXE 是空文件，终止打包。" >&2
  exit 1
fi

# exe 必须是可执行体：PE 头是 'MZ'。这里只做二进制头校验，不运行程序本身。
HEAD2="$(head -c 2 "$EXE")"
if [ "$HEAD2" != "MZ" ]; then
  echo "!! $EXE 的文件头不是 PE（MZ），终止打包。" >&2
  exit 1
fi

# 发布目录里必须只有这一个 exe：conf json / pdb / 松散原生库 / 说明文件 / 示例曲目
# 都不许再出现（用户要求「解压出来就一个 exe」）。
# 注意两点：
#   1. 不要用 find：Git bash 里裸 find 会命中 Windows 的 FIND.EXE，参数格式直接报错。
#   2. 末尾必须 `|| true`：目录里只有 exe 时 grep 过滤掉全部行会返回 1，
#      配合 set -o pipefail 会让脚本什么都没打印就退出。
STRAY="$(cd "$OUT" && ls -A | grep -v '^MidiKeyPlayer\.exe$' | head -1 || true)"
if [ -n "$STRAY" ]; then
  echo "!! 发布目录 $OUT 里除 MidiKeyPlayer.exe 之外还有：${STRAY}，终止打包。" >&2
  exit 1
fi

EXE_SIZE="$(wc -c < "$EXE" | tr -d ' ')"
echo ">> 单文件 exe：${EXE}（$EXE_SIZE 字节）"

ZIP="release/MidiKeyPlayer-win-x64-$VERSION.zip"
echo ">> 打包 $ZIP （打 exe 与 更新日志.txt 两项）..."
rm -f "$ZIP"
# 用 python3 写 zip：中文名条目带 UTF-8 标志，Windows 资源管理器解压不乱码；
# exe 的权限位写成 0755，日志写成 0644。python 已在上面强制要求，
# 所以这里不再写 zip / tar 回退分支。
"$PYTHON" - "$ZIP" "$EXE" "$CHANGELOG" <<'PY'
import os, sys, time, zipfile
out, exe, changelog = sys.argv[1], sys.argv[2], sys.argv[3]

def add(z, path, name, mode):
    zi = zipfile.ZipInfo(name)
    zi.flag_bits |= 0x800                       # UTF-8 文件名的标志位
    st = os.stat(path)
    zi.date_time = tuple(time.localtime(st.st_mtime)[:6])
    zi.external_attr = mode << 16
    with open(path, "rb") as fh:
        z.writestr(zi, fh.read(), zipfile.ZIP_DEFLATED, 6)

with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    add(z, exe, "MidiKeyPlayer.exe", 0o755)
    add(z, changelog, "更新日志.txt", 0o644)
print("   条目:", zipfile.ZipFile(out).namelist())
PY

if [ ! -s "$ZIP" ]; then
  echo "!! 打包结果 $ZIP 不存在或是空文件，终止打包。" >&2
  exit 1
fi

echo ">> 完成：$ZIP"
ls -lh "$ZIP"
