#!/usr/bin/env bash
# =============================================================================
# check-ci-config.sh —— GitHub Actions 触发配置守卫
#
# 【作用】
#   检查 .github/workflows/*.yml 的 `on:` 触发块是否符合本仓库的既定策略：
#
#   1. 必须包含 `pull_request:`     —— PR 是唯一的必跑入口，缺了就等于没有检查。
#   2. 必须包含 `workflow_dispatch:` —— 保留“需要时手动在默认分支上复现一次”的口子。
#   3. 不得包含 `push:`             —— main 已被分支规则集「main」锁死
#      （必须走 PR、禁 force push、禁删除、bypass 为空、strict 必需检查开启），
#      PR 阶段被测的合并结果就是最终落到 main 的结果，push 触发是纯冗余：
#      既多花分钟数，又会额外占用一个 Unity 授权席位（个人版席位制，席位不足
#      会误报 no available seats，表现为与代码无关的红灯）。
#
# 【输入】
#   无参数：扫描仓库内 .github/workflows/*.yml（相对仓库根目录）。
#   --self-test：只跑本脚本自身的用例，不检查当前仓库。
#   --check-ruleset：额外把各 workflow 会产生的 job 名与 GitHub 分支规则集里
#     声明的必需检查名对比。需要 gh 已登录且对仓库有 admin 权限，因此只在本地
#     推送前跑；未登录时跳过并提示，不当作失败。
#
# 【输出】
#   逐文件的检查结论；有问题时列出文件名与违反的条目，退出码 1；全部通过退出码 0。
#
# 【为什么必须有这个脚本】
#   `push:` 触发的删除是一次“看起来删了、以后又会被加回来”的决定：新同事按
#   直觉加回 push 是最自然的写法，而重复跑 CI 的代价（时间 + 授权席位）不会
#   当场报错，只会慢慢消耗。把策略写成可执行的守卫，比写在文档里可靠。
#   另外，规则集里的必需检查名与 workflow 的 job 名是字符串精确匹配的，
#   这也是本仓库容易踩的坑，故在每个 workflow 顶部都要求写明该耦合关系。
#
# 【注意】
#   本脚本只做纯文本解析，不依赖 python / yaml 库，因此在任意 Ubuntu runner 上
#   都能跑。代价是只认块状（block）写法：`on:` 下面逐行缩进列出的触发项。
#   若将来有人把 `on:` 写成行内流式（如 `on: [push, pull_request]`），
#   本脚本会因为解析不到缩进块而按“缺少必需触发项”报错——这是故意的，
#   宁可报错也不要静默放过。
# =============================================================================
set -uo pipefail

# 本脚本自身的绝对路径，供自检递归调用
_self_path=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")

# -----------------------------------------------------------------------------
# _on_block —— 取出一个 workflow 文件里 `on:` 触发块的每一行
# 输入：workflow 文件路径
# 输出：该块的所有行（不含 `on:` 自身）；`on:` 之后的第一个顶层行即视为块结束
# 为什么用 awk：不需要 yaml 解析器，且对“缩进即层级”的 Workflow 语法足够稳。
# -----------------------------------------------------------------------------
_on_block() {
  awk '
    /^on:/ { inside = 1; next }
    inside && /^[^[:space:]]/ { inside = 0 }
    inside { print }
  ' "$1"
}

# -----------------------------------------------------------------------------
# _check_file —— 校验单个 workflow 文件的触发策略
# 输入：workflow 文件路径
# 输出：标准输出打印结论；返回 0 表示合规，1 表示存在违规
# -----------------------------------------------------------------------------
_check_file() {
  local _file="$1" _block _rc=0

  _block=$(_on_block "$_file")

  if ! grep -q '^[[:space:]]*pull_request:' <<<"$_block"; then
    echo "  ❌ $_file：缺少 pull_request 触发 —— PR 阶段不会跑这个 workflow"
    _rc=1
  fi

  if ! grep -q '^[[:space:]]*workflow_dispatch:' <<<"$_block"; then
    echo "  ❌ $_file：缺少 workflow_dispatch 触发 —— 失去了手动复现入口"
    _rc=1
  fi

  if grep -q '^[[:space:]]*push:' <<<"$_block"; then
    echo "  ❌ $_file：存在 push 触发 —— main 已被规则集锁死，push 只会重复执行"
    _rc=1
  fi

  return "$_rc"
}

# -----------------------------------------------------------------------------
# _matrix_pairs —— 取出 workflow 里 matrix 区块的内联数组定义
# 输入：workflow 文件路径
# 输出：每行一条 `键=值1,值2`（已去掉空格与引号）
# 为什么需要：job 名里可以嵌 `${{ matrix.X }}`，不展开就无法与规则集比对。
# 只认写成 `X: [ a, b ]` 的内联数组——本仓库全部是这种写法；遇到其他写法直接
# 不产出对应键，后续比对会因为“必需检查名找不到对应 job”而报错，不会静默放过。
# -----------------------------------------------------------------------------
_matrix_pairs() {
  awk '
    match($0, /^[[:space:]]*/) { ind = RLENGTH }
    /^[[:space:]]*matrix:[[:space:]]*$/ { base = ind; inside = 1; next }
    inside {
      if ($0 ~ /^[[:space:]]*$/) next
      if (ind <= base) { inside = 0; next }
      print
    }
  ' "$1" | sed -n 's/^[[:space:]]*\([A-Za-z_][A-Za-z0-9_]*\):[[:space:]]*\[\(.*\)\][[:space:]]*$/\1=\2/p' \
    | tr -d "'\" "
}

# -----------------------------------------------------------------------------
# _expand_template —— 把 job 名模板里的矩阵占位符展开成所有组合
# 输入：名称模板，以及若干 `键=值1,值2`；输出：逐行打印展开后的名称
# 为什么递归：矩阵键最多两三个，笛卡尔积用递归写最短且无需额外工具。
# -----------------------------------------------------------------------------
_expand_template() {
  local _name="$1"; shift
  if [ "$#" -eq 0 ]; then printf '%s\n' "$_name"; return 0; fi
  local _pair="$1"; shift
  # 键值格式不对时（例如上游把矩阵写成了 YAML 多行列表）不做替换也不报错：
  # 结果会是未展开的 job 名，后续比对自然会把它当成“不存在”而报错，不会静默放过。
  case "$_pair" in
    *=*) ;;
    *) _expand_template "$_name" "$@"; return 0 ;;
  esac
  local _key="${_pair%%=*}" _vals="${_pair#*=}" _v _pat _old_ifs
  _pat='${{ matrix.'"$_key"' }}'
  _old_ifs="$IFS"; IFS=','
  for _v in $_vals; do
    _expand_template "${_name//"$_pat"/$_v}" "$@"
  done
  IFS="$_old_ifs"
}

# -----------------------------------------------------------------------------
# _workflow_job_names —— 列出某 workflow 会产生的全部 job 名（已展开矩阵）
# 输入：workflow 文件路径；输出：逐行打印 job 名
# 为什么用 awk 按缩进定位：只有 jobs 区块下、紧贴 job 键一层的 `name:`
# 才是 job 名；步骤里 `with.name`（如 upload-artifact 的产物名）层级更深，必须排除，
# 否则它们会污染比对结果，让真正的失配被掩盖过去。
# -----------------------------------------------------------------------------
_workflow_job_names() {
  local _file="$1" _pairs=() _line
  while IFS= read -r _line; do
    [ -n "$_line" ] && _pairs+=("$_line")
  done < <(_matrix_pairs "$_file")

  awk '
    /^jobs:[[:space:]]*$/ { match($0, /^[[:space:]]*/); jind = RLENGTH; inside = 1; next }
    inside && /^[[:space:]]*$/ { next }
    inside {
      match($0, /^[[:space:]]*/); ind = RLENGTH
      if (ind <= jind) { inside = 0; next }
      if (ind == jind + 4 && $0 ~ /^[[:space:]]*name:/) {
        sub(/^[[:space:]]*name:[[:space:]]*/, ""); sub(/[[:space:]]*$/, ""); print
      }
    }
  ' "$_file" | while IFS= read -r _n; do
    _expand_template "$_n" ${_pairs[@]+"${_pairs[@]}"}
  done
}

# -----------------------------------------------------------------------------
# _check_ruleset —— 把 workflow 的 job 名与分支规则集声明的必需检查名对比
# 输入：无（自行通过 gh 读取仓库与规则集）；输出：结论；返回 0 合规 / 1 失配 / 2 无法检查
# 为什么必须查这一条：规则集的必需检查名是按**字符串**匹配 job 名的。job 名里嵌了
# `${{ matrix.unityVersion }}` 这类变量，所以“把 CI 的 Unity 版本改一下”就足以让所有
# PR 永久停在 pending——没有任何报错，也没有超时，表现只是永远等不到检查结果。
# 这种故障从流水线日志里完全看不出来，只能靠提交前静态比对。
# -----------------------------------------------------------------------------
_check_ruleset() {
  if ! command -v gh >/dev/null 2>&1 || ! gh auth status >/dev/null 2>&1; then
    echo "  ⚠️  gh 不可用或未登录，跳过规则集比对（该检查需要 admin 权限，只能在本地跑）"
    return 2
  fi

  local _repo _ids _id _contexts
  _repo=$(gh repo view --json nameWithOwner --jq .nameWithOwner 2>/dev/null) || return 2
  [ -n "$_repo" ] || return 2
  _ids=$(gh api "repos/$_repo/rulesets" --jq '.[].id' 2>/dev/null) || return 2
  [ -n "$_ids" ] || { echo "  ⚠️  仓库未配置任何规则集，跳过比对"; return 2; }

  local _required=""
  while IFS= read -r _id; do
    [ -n "$_id" ] || continue
    _contexts=$(gh api "repos/$_repo/rulesets/$_id" \
      --jq '.rules[] | select(.type=="required_status_checks") | .parameters.required_status_checks[].context' 2>/dev/null)
    [ -n "$_contexts" ] && _required="${_required}${_contexts}"$'\n'
  done <<<"$_ids"

  if [ -z "${_required//$'\n'/}" ]; then
    echo "  ⚠️  规则集里没有声明任何必需状态检查，跳过比对"
    return 2
  fi

  local _names="" _f _n
  for _f in "${_targets[@]}"; do
    while IFS= read -r _n; do
      [ -n "$_n" ] && _names="${_names}${_n}"$'\n'
    done < <(_workflow_job_names "$_f")
  done

  local _fail=0
  while IFS= read -r _contexts; do
    [ -n "$_contexts" ] || continue
    if ! grep -Fxq -- "$_contexts" <<<"${_names%$'\n'}"; then
      echo "  ❌ 规则集要求的必需检查在 workflow 里不存在：$_contexts"
      _fail=1
    fi
  done <<<"${_required%$'\n'}"

  if [ "$_fail" -ne 0 ]; then
    echo "     ↑ 这会让所有 PR 永久停在 pending。要么把 workflow 的 job 名改回匹配，"
    echo "       要么同步修改规则集。建议 job 名不要嵌 matrix 里的版本号（如 unityVersion），"
    echo "       让版本升级不再牵动分支规则。"
    return 1
  fi
  echo "  ✅ 规则集要求的必需检查与 workflow job 名全部匹配"
  return 0
}

# -----------------------------------------------------------------------------
# _self_test —— 本脚本自身的测试用例
# 在临时目录里构造三份 workflow，断言“该拦的拦住、该过的放行”。
# 为什么这样测：直接对真实仓库断言会让用例随仓库内容漂移，因此用独立 fixture。
# 返回 0 表示三个用例全部通过。
# -----------------------------------------------------------------------------
_self_test() {
  local _tmp _rc=0
  _tmp=$(mktemp -d)
  # shellcheck disable=SC2064
  trap "rm -rf '$_tmp'" RETURN

  # --- 用例 A：只写了 push + pull_request → 缺 workflow_dispatch，且 push 冗余 → 必须报错
  cat >"$_tmp/case-a.yml" <<'YAML'
on:
  push:
    branches: [ main ]
  pull_request:
YAML
  if bash "$_self_path" "$_tmp/case-a.yml" >/dev/null 2>&1; then
    echo "  ❌ 自检 A 失败：含 push 且缺 workflow_dispatch 的配置没有被拦截"
    _rc=1
  else
    echo "  ✅ 自检 A 通过：push 冗余 + 缺手动入口 → 已拦截"
  fi

  # --- 用例 B：pull_request + workflow_dispatch → 必须放行
  cat >"$_tmp/case-b.yml" <<'YAML'
on:
  pull_request:
  workflow_dispatch:
YAML
  if bash "$_self_path" "$_tmp/case-b.yml" >/dev/null 2>&1; then
    echo "  ✅ 自检 B 通过：PR + 手动入口 → 放行"
  else
    echo "  ❌ 自检 B 失败：合规的触发配置被误报为违规"
    _rc=1
  fi

  # --- 用例 C：只有 push → 缺少两个必需入口 → 必须报错
  cat >"$_tmp/case-c.yml" <<'YAML'
on:
  push:
    branches: [ main ]
YAML
  if bash "$_self_path" "$_tmp/case-c.yml" >/dev/null 2>&1; then
    echo "  ❌ 自检 C 失败：只有 push 的配置没有被拦截"
    _rc=1
  else
    echo "  ✅ 自检 C 通过：缺少 PR 与手动入口 → 已拦截"
  fi

  # --- 用例 D：job 名里的矩阵占位符展开（规则集比对的基础）
  # 为什么单独测这个：比对完全建立于“能把 ${{ matrix.X }} 展开成实际 job 名”之上。
  # 若展开错（比如漏了一个组合），失配就会被静默掩盖，反而比不检查更危险。
  local _expanded _want
  _expanded=$(_expand_template '${{ matrix.testMode }} 测试（Unity ${{ matrix.unityVersion }}）' \
    'testMode=EditMode,PlayMode' 'unityVersion=2022.3.62f1')
  _want=$'EditMode 测试（Unity 2022.3.62f1）\nPlayMode 测试（Unity 2022.3.62f1）'
  if [ "$_expanded" = "$_want" ]; then
    echo "  ✅ 自检 D 通过：矩阵占位符展开为 2 个 job 名"
  else
    echo "  ❌ 自检 D 失败：矩阵展开结果不对"
    printf '    期望：\n%s\n    实际：\n%s\n' "$_want" "$_expanded"
    _rc=1
  fi

  return "$_rc"
}

# -----------------------------------------------------------------------------
# 入口
# -----------------------------------------------------------------------------

# --self-test：只跑用例，不检查仓库
if [ "${1:-}" = "--self-test" ]; then
  echo "== check-ci-config.sh 自检 =="
  if _self_test; then
    echo "自检结果：4/4 通过"
    exit 0
  fi
  echo "自检结果：存在失败用例"
  exit 1
fi

# 解析开关与目标文件：--check-ruleset 可与文件参数共存
_do_check_ruleset=0
_targets=()
for _a in "$@"; do
  case "$_a" in
    --check-ruleset) _do_check_ruleset=1 ;;
    *) _targets+=("$_a") ;;
  esac
done

# 未显式传入文件时，扫描仓库内全部 workflow
if [ "${#_targets[@]}" -eq 0 ]; then
  _cd_root=$(git rev-parse --show-toplevel 2>/dev/null)
  if [ -z "$_cd_root" ]; then
    echo "当前目录不是 git 仓库" >&2
    exit 2
  fi
  cd "$_cd_root" || exit 2
  shopt -s nullglob
  _targets=(.github/workflows/*.yml)
  shopt -u nullglob
  if [ "${#_targets[@]}" -eq 0 ]; then
    echo "  ⚠️  没有找到任何 .github/workflows/*.yml"
    exit 0
  fi
fi

echo "== CI 触发配置检查：只保留 PR 入口 =="
_fail=0
for _f in "${_targets[@]}"; do
  _check_file "$_f" || _fail=1
done

# 规则集比对：只在显式开启时跑（需要 gh + admin 权限）
if [ "$_do_check_ruleset" -eq 1 ]; then
  echo
  echo "== 规则集必需检查 ↔ workflow job 名比对 =="
  _check_ruleset
  _ruleset_rc=$?
  # 2 = 无法检查（未登录/无规则集），不当作失败
  [ "$_ruleset_rc" -eq 1 ] && _fail=1
fi

echo
if [ "$_fail" -ne 0 ]; then
  echo "CI 触发配置检查：未通过"
  echo "  策略说明：main 由分支规则集「main」保护（必须走 PR、禁 force push、禁删除、"
  echo "  无 bypass，且开启 strict 必需检查），因此 PR 阶段测的就是合并结果，"
  echo "  push 触发只会重复跑。需要时用 workflow_dispatch 手动触发。"
  exit 1
fi

echo "CI 触发配置检查：通过（${#_targets[@]} 个 workflow 均为 PR + 手动入口）"
exit 0
