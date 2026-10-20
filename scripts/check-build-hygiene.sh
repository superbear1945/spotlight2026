#!/usr/bin/env bash
# =============================================================================
# check-build-hygiene.sh —— 发布包体去调试化检查
#
# 【作用】
#   扫描一份 Unity Windows 构建产物（默认工程根下的 Build/），确认开发期信息没有
#   被编译进必需资产、也没有随目录一起发出去。两类检查：
#
#   1. 目录断言：不存在 *_BurstDebugInformation_DoNotShip。该目录里是 Burst 编译记录，
#      带着构建机的绝对路径（Temp/Burst、Library/Burst、编辑器安装位置）。
#      Unity 自己在目录名里写了 DoNotShip，但打包脚本会把整个输出目录压缩上传。
#   2. 字符串断言：包内任何文件都不得包含下列开发期文本——
#      · 调试配置面板：配置/调试、应用并重开、ConfigDebugPanel、ConfigFieldBuilder、DebugConfigDraft
#      · 配置元数据：Spotlight.xlsx、_sourceWorkbook、_sourceHash、_compiledAtUtc
#      · Burst 符号：lib_burst_generated.pdb
#      这些内容一旦出现在 Managed/*.dll、sharedassets*.assets 或 globalgamemanagers.assets 里，
#      就只能靠改代码/改编译产物解决，删文件是删不掉的，所以必须有个可重复跑的检查。
#
# 【输入】
#   [输出目录]    默认 Build（相对仓库根）；目录不存在时判定为“没有可检查的产物”并退出 0。
#   --self-test   只跑本脚本自身的隔离用例，不检查真实产物。
#
# 【输出】
#   逐条列出命中的文件与命中的文本；全部干净时打印扫描文件数与结论。
#   退出码：0 干净 / 跳过，1 发现开发期信息，2 用法错误。
#
# 【为什么必须有这个脚本】
#   这类泄露不会让游戏崩、不会让测试红，只有在“拆包看字符串”时才暴露，所以它天然不会被
#   任何功能测试覆盖。把它写成脚本而不是一次性调查，下次改完代码/UXML 可以一条命令复核。
#
# 【注意】
#   · lib_burst_generated.dll 与托管程序集里的 “.pdb” 字样属于正常内容，**不要**用宽泛的
#     `grep pdb` 做门禁；这里只匹配确切的 lib_burst_generated.pdb 与 _BurstDebugInformation。
#   · 文本按字节匹配（grep -a），不假设文件是文本；中文用 UTF-8 字面量，与 UXML/C# 源文件一致。
# =============================================================================
set -uo pipefail
export LC_ALL=C

_self_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
_repo_root=$(cd "$_self_dir/.." && pwd)

# 需要拦截的开发期文本（每行一条，供 grep -F -f 使用）。
readonly _needles=(
  '配置/调试'
  '配置 / 调试'
  '应用并重开'
  'ConfigDebugPanel'
  'ConfigFieldBuilder'
  'DebugConfigDraft'
  'Spotlight.xlsx'
  '_sourceWorkbook'
  '_sourceHash'
  '_compiledAtUtc'
  'lib_burst_generated.pdb'
)

readonly _burst_suffix='_BurstDebugInformation_DoNotShip'

# _scan_dir —— 扫描一个构建输出目录
# 输入：输出目录绝对路径；输出：命中行打印到标准输出，返回值 0 干净 / 1 有命中
# 为什么把“命中”与“目录断言”分开写：两者失败原因不同，报错要能直接告诉人改代码还是删文件。
# -----------------------------------------------------------------------------
_scan_dir() {
  local _dir="$1" _needle_file _hits=0 _files=0 _path _found
  _needle_file=$(mktemp)
  printf '%s
' "${_needles[@]}" >"$_needle_file"
  echo "扫描目录：$_dir"
  while IFS= read -r -d '' _path; do
    _files=$((_files + 1))
    # -a 让二进制也被当作文本；-F 固定字符串，避免卡名里的正则元字符引发误判。
    _found=$(grep -a -F -o -f "$_needle_file" "$_path" 2>/dev/null | sort -u | tr '
' ' ')
    if [ -n "$_found" ]; then
      echo "  ❌ ${_path#"$_dir"/} → $_found"
      _hits=$((_hits + 1))
    fi
  done < <(find "$_dir" -type f -print0)
  rm -f "$_needle_file"

  local _burst
  _burst=$(find "$_dir" -name "*${_burst_suffix}" -print)
  if [ -n "$_burst" ]; then
    while IFS= read -r _path; do
      echo "  ❌ 残留 Burst 调试信息目录：${_path#"$_dir"/}"
      _hits=$((_hits + 1))
    done <<<"$_burst"
  fi

  # 符号文件按“文件名”判定：PDB 内容不一定包含自身文件名，只看字节会漏。
  local _stray_pdb
  _stray_pdb=$(find "$_dir" -name 'lib_burst_generated.pdb' -print)
  if [ -n "$_stray_pdb" ]; then
    while IFS= read -r _path; do
      echo "  ❌ 残留 Burst 符号文件：${_path#"$_dir"/}"
      _hits=$((_hits + 1))
    done <<<"$_stray_pdb"
  fi

  if [ "$_hits" -ne 0 ]; then
    echo "共 $_hits 处开发期信息，来自 $_files 个文件。"
    return 1
  fi
  echo "✅ 未发现开发期信息（扫描 $_files 个文件，且无 ${_burst_suffix} 目录与 Burst 符号文件）"
  return 0
}

# -----------------------------------------------------------------------------
# _self_test —— 隔离用例：干净包放行、各类泄露拦截、目录断言生效
# 输入：无；输出：逐项断言结果，返回 0 全部通过 / 1 有用例失败
# 为什么用临时目录：直接依赖真实 Build/ 会让用例随产物漂移（没打过包就永远“通过”）。
# -----------------------------------------------------------------------------
_self_test() {
  local _tmp _rc=0 _case_dir _scan_out
  _tmp=$(mktemp -d)
  # shellcheck disable=SC2064
  trap "rm -rf '$_tmp'" RETURN

  # 干净产物：可执行文件与运行时 DLL。
  _case_dir="$_tmp/clean"
  mkdir -p "$_case_dir/Game_Data/Managed" "$_case_dir/Game_Data/Plugins/x86_64"
  printf 'MZ' >"$_case_dir/Game.exe"
  printf 'managed' >"$_case_dir/Game_Data/Managed/Spotlight.Runtime.dll"
  printf 'burst' >"$_case_dir/Game_Data/Plugins/x86_64/lib_burst_generated.dll"
  if _scan_dir "$_case_dir" >/dev/null 2>&1; then
    echo "  ✅ 自检 A 通过：干净产物被放行"
  else
    echo "  ❌ 自检 A 失败：干净产物被误报"
    _rc=1
  fi

  # 调试面板文案泄露（模拟 sharedassets 里的 UXML 字符串）。
  _case_dir="$_tmp/debug-uxml"
  mkdir -p "$_case_dir/Game_Data"
  printf 'xx 配置/调试 应用并重开 xx' >"$_case_dir/Game_Data/sharedassets0.assets"
  if _scan_dir "$_case_dir" >/dev/null 2>&1; then
    echo "  ❌ 自检 B 失败：调试面板文案没有被拦截"
    _rc=1
  else
    echo "  ✅ 自检 B 通过：调试面板文案被拦截"
  fi

  # 调试面板类型名泄露（模拟 Managed dll 的元数据）。
  _case_dir="$_tmp/debug-type"
  mkdir -p "$_case_dir/Game_Data/Managed"
  printf 'Spotlight.ConfigDebugPanel' >"$_case_dir/Game_Data/Managed/Spotlight.Runtime.dll"
  if _scan_dir "$_case_dir" >/dev/null 2>&1; then
    echo "  ❌ 自检 C 失败：调试类型名没有被拦截"
    _rc=1
  else
    echo "  ✅ 自检 C 通过：调试类型名被拦截"
  fi

  # 工作簿路径泄露（模拟主配置资产里的 _sourceWorkbook）。
  _case_dir="$_tmp/workbook"
  mkdir -p "$_case_dir/Game_Data"
  printf '_sourceWorkbook: Assets/Spotlight/Config/Spotlight.xlsx' >"$_case_dir/Game_Data/sharedassets0.assets"
  if _scan_dir "$_case_dir" >/dev/null 2>&1; then
    echo "  ❌ 自检 D 失败：工作簿路径没有被拦截"
    _rc=1
  else
    echo "  ✅ 自检 D 通过：工作簿路径被拦截"
  fi

  # 仅剩 Burst 调试目录（没有任何文本命中）：目录断言必须独立生效。
  _case_dir="$_tmp/burst-dir"
  mkdir -p "$_case_dir/Game${_burst_suffix}/Data/Plugins/x86_64"
  printf 'clean payload' >"$_case_dir/Game${_burst_suffix}/Data/Plugins/x86_64/lib_burst_generated.txt"
  if _scan_dir "$_case_dir" >/dev/null 2>&1; then
    echo "  ❌ 自检 E 失败：Burst 调试目录没有被拦截"
    _rc=1
  else
    echo "  ✅ 自检 E 通过：Burst 调试目录被拦截"
  fi

  # 单个 Burst 符号文件（插件目录里的 .pdb）。
  _case_dir="$_tmp/burst-pdb"
  mkdir -p "$_case_dir/Game_Data/Plugins/x86_64"
  printf 'pdb' >"$_case_dir/Game_Data/Plugins/x86_64/lib_burst_generated.pdb"
  if _scan_dir "$_case_dir" >/dev/null 2>&1; then
    echo "  ❌ 自检 F 失败：Burst 符号文件没有被拦截"
    _rc=1
  else
    echo "  ✅ 自检 F 通过：Burst 符号文件被拦截"
  fi

  # 不存在的目录属于“没有产物可查”，必须放行而不是报错（CI 上本来就没有 Build/）。
  if bash "$_self_dir/check-build-hygiene.sh" "$_tmp/does-not-exist" >/dev/null 2>&1; then
    echo "  ✅ 自检 G 通过：产物目录不存在时跳过"
  else
    echo "  ❌ 自检 G 失败：产物目录不存在时不应失败"
    _rc=1
  fi

  # 托管程序集里的普通 “.pdb” 字样与 lib_burst_generated.dll 不能触发误报。
  _case_dir="$_tmp/benign-pdb"
  mkdir -p "$_case_dir/Game_Data/Managed" "$_case_dir/Game_Data/Plugins/x86_64"
  printf 'D:\\build\\Library\\Bee\\artifacts\\Spotlight.Runtime.pdb' >"$_case_dir/Game_Data/Managed/Spotlight.Runtime.dll"
  printf 'burst' >"$_case_dir/Game_Data/Plugins/x86_64/lib_burst_generated.dll"
  if _scan_dir "$_case_dir" >/dev/null 2>&1; then
    echo "  ✅ 自检 H 通过：普通 .pdb 路径与 Burst DLL 不误报"
  else
    echo "  ❌ 自检 H 失败：正常内容被误报（会让人直接把门禁关掉）"
    _rc=1
  fi

  return "$_rc"
}

# -----------------------------------------------------------------------------
# 入口
# -----------------------------------------------------------------------------
if [ "${1:-}" = "--self-test" ]; then
  echo "== check-build-hygiene.sh 自检 =="
  if _self_test; then
    echo "自检结果：8/8 通过"
    exit 0
  fi
  echo "自检结果：存在失败用例"
  exit 1
fi

case "${1:-}" in
  --help | -h)
    echo "用法：bash scripts/check-build-hygiene.sh [输出目录，默认 Build]"
    echo "      bash scripts/check-build-hygiene.sh --self-test"
    exit 0
    ;;
  -*) echo "未知参数：$1" >&2; exit 2 ;;
esac

_target="${1:-Build}"
case "$_target" in
  /* | [A-Za-z]:*) _target_dir="$_target" ;;
  *) _target_dir="$_repo_root/$_target" ;;
esac

if [ ! -d "$_target_dir" ]; then
  echo "== 发布包体卫生检查：未找到产物目录 $_target_dir，跳过（先执行 Tools/Spotlight/打包 Windows 版本）=="
  exit 0
fi

echo "== 发布包体卫生检查 =="
if _scan_dir "$_target_dir"; then
  exit 0
fi
echo
echo "发布包体卫生检查：未通过。"
echo "  调试面板文案/类型名 → 检查 Runtime/Debug 三个文件是否整文件包在 UNITY_EDITOR || DEVELOPMENT_BUILD 里，"
echo "  以及 Spotlight.uxml 是否又混入了调试节点；"
echo "  工作簿路径/摘要 → 检查 SpotlightConfigAsset / CardDefinitionSO 是否又存了溯源字段；"
echo "  Burst 调试目录 → 重新打包（Editor/BuildArtifactCleanup 会在构建后自动删除）。"
exit 1
