#!/usr/bin/env bash
# =============================================================================
# next-release-version.sh —— 发版版本号计算（.github/workflows/release-build.yml 的唯一版本来源）
#
# 【作用】
#   给定“上一个 Release 的版本号”，算出本次发版的版本号，并同时给出
#   Release 标签、Unity 的 PlayerSettings.bundleVersion 与 Android versionCode。
#
#   版本规则（AGENTS.md 第 7 节的发版约定）：
#     · 默认 bump=patch：在上一个版本基础上 +0.0.1（0.2.0 → 0.2.1 → 0.2.2 …）；
#     · bump=minor / major 仅供确实需要跳段位时使用（默认路径永远是 patch）；
#     · 仓库里还没有任何 Release 时，基线是 0.0.0，第一次发版就是 v0.0.1。
#
#   versionCode = major*10000 + minor*100 + patch（0.2.1 → 201），
#   这样同一个版本在 Android 侧的 versionCode 单调递增，且不依赖构建次数。
#
# 【输入】
#   --latest <tag>   上一个 Release 的标签（可带 v 前缀，可为空字符串表示“还没有 Release”）。
#   --bump <kind>    patch（默认）/ minor / major。
#   --self-test      只跑本脚本自身的隔离用例，不读任何外部状态。
#   --help           打印用法。
#
# 【输出】（逐行 key=value，可直接 `>> "$GITHUB_OUTPUT"`）
#   previous=<上一个版本，去掉 v 前缀；没有时为空>
#   version=<本次版本，不带 v>
#   tag=v<本次版本>
#   versionCode=<Android versionCode>
#
# 【退出码】0 成功 / 2 用法或版本号非法（信息写 stderr，CI 里会直接变成红灯）
#
# 【为什么必须单独成脚本】
#   版本号算错只会在很晚才暴露：打出来的包体版本与 Release 标签对不上、
#   Android versionCode 不递增导致覆盖安装失败、或者两个 tag 撞车让发布步骤 403。
#   这些都不是“测试会红”的问题，因此把算法收敛成一处纯函数并配自检用例，
#   workflow 里不允许自己再写一套 sed/awk 版本运算。
# =============================================================================
set -uo pipefail

_self_path=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")

# -----------------------------------------------------------------------------
# _usage —— 打印用法
# 输入：无；输出：用法文本写到 stderr。为什么写到 stderr：GITHUB_OUTPUT 只允许 key=value。
# -----------------------------------------------------------------------------
_usage() {
  cat >&2 <<'EOF'
用法：
  next-release-version.sh [--latest <上一个 Release 标签>] [--bump patch|minor|major]
  next-release-version.sh --self-test

输出（逐行 key=value，可直接写入 $GITHUB_OUTPUT）：
  previous=<上一版本，无 Release 时为空>
  version=<本次版本>
  tag=v<本次版本>
  versionCode=<Android versionCode>
EOF
}

# -----------------------------------------------------------------------------
# _normalize_version —— 去掉 v 前缀并校验 x.y.z
# 输入：$1 原始标签（如 "v0.2.0" / "0.2.0"）；输出：stdout 打印 "0.2.0"。
# 返回：0 合法 / 1 非法（调用方负责报错文案）。
# 为什么严格三段：Release 标签同时被写进 PlayerSettings.bundleVersion（Unity 只认 x.y.z 这种格式）
#   和 Android versionName，缺段或多段会在打包侧被静默截断。
# -----------------------------------------------------------------------------
_normalize_version() {
  local _raw="${1:-}"
  _raw="${_raw#"${_raw%%[![:space:]]*}"}"   # 去掉前导空白
  _raw="${_raw%"${_raw##*[![:space:]]}"}"   # 去掉尾部空白
  _raw="${_raw#v}"
  _raw="${_raw#V}"
  [[ "$_raw" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)$ ]] || return 1
  printf '%s' "$_raw"
}

# -----------------------------------------------------------------------------
# _bump_version —— 按段位递增版本号
# 输入：$1 合法版本（x.y.z）；$2 段位（patch / minor / major）；
# 输出：stdout 打印递增后的版本；返回 0 成功 / 1 段位非法。
# 注意 patch+1 就是“比上一个 Release 多 0.0.1”，因此默认路径不需要任何特殊处理。
# -----------------------------------------------------------------------------
_bump_version() {
  local _version="$1" _kind="$2" _major _minor _patch
  IFS=. read -r _major _minor _patch <<<"$_version"
  case "$_kind" in
    major) printf '%s.0.0' "$((_major + 1))" ;;
    minor) printf '%s.%s.0' "$_major" "$((_minor + 1))" ;;
    patch) printf '%s.%s.%s' "$_major" "$_minor" "$((_patch + 1))" ;;
    *) return 1 ;;
  esac
}

# -----------------------------------------------------------------------------
# _version_code —— 由版本号算出 Android versionCode
# 输入：$1 合法版本（x.y.z）；输出：stdout 打印整数；返回 0 成功 / 1 段位过大。
# 为什么限定 minor / patch < 100：versionCode 用 100 进位，段位超过 99 会与相邻版本撞号，
#   撞号之后 Android 会拒绝覆盖安装（新包被判定为同一版本），所以宁可在这里直接报错。
# -----------------------------------------------------------------------------
_version_code() {
  local _version="$1" _major _minor _patch
  IFS=. read -r _major _minor _patch <<<"$_version"
  if [ "$_minor" -gt 99 ] || [ "$_patch" -gt 99 ]; then
    echo "❌ 版本 $_version 的次版本号/修订号超过 99：Android versionCode 用 100 进位，会与相邻版本撞号。" >&2
    echo "   请改用 --bump minor 换段位，而不是继续 +0.0.1。" >&2
    return 1
  fi
  printf '%s' "$((_major * 10000 + _minor * 100 + _patch))"
}

# -----------------------------------------------------------------------------
# _emit —— 计算并打印结果
# 输入：$1 上一个 Release 标签（可为空）；$2 段位；输出：四行 key=value。
# 返回：0 成功 / 2 非法版本号或非法段位（调用方直接退出 2）。
# -----------------------------------------------------------------------------
_emit() {
  local _latest="${1:-}" _kind="${2:-patch}" _base _version _code

  if [ -z "${_latest//[[:space:]]/}" ]; then
    # 还没有任何 Release：基线 0.0.0，第一次 bump 得到 0.0.1。
    _base="0.0.0"
  elif ! _base=$(_normalize_version "$_latest"); then
    echo "❌ 上一个 Release 标签 \"$_latest\" 不是 x.y.z 形式，无法计算下一版本。" >&2
    echo "   发版标签必须是 v<major>.<minor>.<patch>（例如 v0.2.0）；请先修正或删除该 Release。" >&2
    return 2
  fi

  if ! _version=$(_bump_version "$_base" "$_kind"); then
    echo "❌ 不支持的递增量 \"$_kind\"（只接受 patch / minor / major）。" >&2
    return 2
  fi

  if ! _code=$(_version_code "$_version"); then
    return 2
  fi

  # previous 保留“去掉 v 前缀的上一版本”，没有 Release 时为空（workflow 靠它写发布说明）。
  if [ -z "${_latest//[[:space:]]/}" ]; then
    echo "previous="
  else
    echo "previous=$_base"
  fi
  echo "version=$_version"
  echo "tag=v$_version"
  echo "versionCode=$_code"
  return 0
}

# -----------------------------------------------------------------------------
# _self_test —— 本脚本自身的隔离用例
# 输入：无；输出：逐项断言结果；返回 0 全部通过 / 1 有用例失败。
# 为什么用固定夹具而不读仓库标签：直接对真实 Release 断言会让用例随发版漂移，
#   而这里要固定的是“+0.0.1 = patch+1”“没有 Release 从 0.0.1 起”“撞号必须报错”这几条规则。
# -----------------------------------------------------------------------------
_self_test() {
  local _rc=0 _case _latest _bump _want _got

  # 表驱动：用例|上一个标签|段位|期望输出（多行用 \n 连接）|期望退出码
  while IFS='|' read -r _case _latest _bump _want; do
    [ -n "${_case:-}" ] || continue
    _got=$(_emit "$_latest" "$_bump" 2>/dev/null | tr '\n' ' ')
    if [ "$_got" = "$_want" ]; then
      echo "  ✅ 自检 $_case 通过"
    else
      echo "  ❌ 自检 $_case 失败"
      printf '     期望：%s\n     实际：%s\n' "$_want" "$_got"
      _rc=1
    fi
  done <<'CASES'
A（没有 Release → 从 v0.0.1 起）||patch|previous= version=0.0.1 tag=v0.0.1 versionCode=1 
B（v0.2.0 + 0.0.1 → v0.2.1）|v0.2.0|patch|previous=0.2.0 version=0.2.1 tag=v0.2.1 versionCode=201 
C（patch 进到两位 → 0.2.10）|v0.2.9|patch|previous=0.2.9 version=0.2.10 tag=v0.2.10 versionCode=210 
D（minor 跳段位并清零修订号）|v0.2.9|minor|previous=0.2.9 version=0.3.0 tag=v0.3.0 versionCode=300 
E（major 跳段位）|v9.9.9|major|previous=9.9.9 version=10.0.0 tag=v10.0.0 versionCode=100000 
F（不带 v 前缀的标签也认）|0.2.0|patch|previous=0.2.0 version=0.2.1 tag=v0.2.1 versionCode=201 
CASES

  # 非法输入必须返回 2，而不是打印一个看起来像版本号的字符串。
  local _bad _rc_bad
  while IFS='|' read -r _case _bad _bump; do
    [ -n "${_case:-}" ] || continue
    _rc_bad=0
    _emit "$_bad" "$_bump" >/dev/null 2>&1 || _rc_bad=$?
    if [ "$_rc_bad" -eq 2 ]; then
      echo "  ✅ 自检 $_case 通过"
    else
      echo "  ❌ 自检 $_case 失败：期望退出码 2，实际 $_rc_bad"
      _rc=1
    fi
  done <<'BADCASES'
G（标签缺段位 → 报错）|v0.2|patch
H（标签含多余内容 → 报错）|v0.2.0-rc1|patch
I（段位不支持 → 报错）|v0.2.0|nightly
J（修订号超过 99 会撞 versionCode → 报错）|v0.99.99|patch
BADCASES

  return "$_rc"
}

# -----------------------------------------------------------------------------
# 入口
# -----------------------------------------------------------------------------
_latest=""
_bump="patch"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --latest)
      [ "$#" -ge 2 ] || { echo "❌ --latest 缺少取值" >&2; _usage; exit 2; }
      _latest="$2"; shift 2 ;;
    --bump)
      [ "$#" -ge 2 ] || { echo "❌ --bump 缺少取值" >&2; _usage; exit 2; }
      _bump="$2"; shift 2 ;;
    --self-test)
      echo "== next-release-version.sh 自检 =="
      if _self_test; then
        echo "自检结果：全部用例通过"
        exit 0
      fi
      echo "自检结果：存在失败用例"
      exit 1 ;;
    -h|--help)
      _usage; exit 0 ;;
    *)
      echo "❌ 未知参数：$1" >&2
      _usage
      exit 2 ;;
  esac
done

_emit "$_latest" "$_bump"
exit $?
