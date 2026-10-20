#!/usr/bin/env bash
# =============================================================================
# classify-change-set.sh —— 按“这次 PR 改了哪些文件”决定该跑哪套 Unity 测试
#
# 【作用】
#   把 PR 的变更文件清单分成四层，并给出两条结论：`edit`（是否跑 EditMode）与
#   `play`（是否跑 PlayMode）。unity-tests.yml 用这份结论决定两个测试 job 是否执行。
#
#     文档层：docs/**、任意 *.md、AGENTS.md、.gitattributes、issue 模板  → 两套都不跑
#     表现层：Runtime/UI|View|Feedback|Debug、UI/**（uxml/uss/prefab）、Scenes/**、
#             Tests/PlayMode/**                                        → 只跑 PlayMode
#     规则层：Core/**、Generated/**、Config/*.xlsx、Editor/**、Tests/EditMode/**
#                                                                      → 两套都跑
#     交叉层：Runtime/Cards|Config|Game、Fonts/**、*.meta、*.asmdef、ProjectSettings/**、
#             Packages/**、Assets/Plugins/**、.github/workflows/**、scripts/**、
#             以及**一切未识别的路径**                                 → 两套都跑
#
#   “规则层也跑 PlayMode”是刻意选择：规则改动常常连带视图契约（快照字段、动作序列），
#   单跑 EditMode 抓不到“日志说移动了、界面却没动”这类回归。真正的省钱点在文档层与表现层。
#
# 【输入】
#   默认：从 CI 环境变量 BASE / HEAD（或 --base/--head）取比较范围，用 git diff 取文件清单。
#   --files <路径>：直接读一份文件清单（每行一个路径），不碰 git（自检与本地调试用）。
#   --summary     ：输出人读的 Markdown（供 $GITHUB_STEP_SUMMARY）。
#   --comment     ：把同一份 Markdown 贴成 PR 上的 sticky 评论（靠隐藏标记原地更新）。
#   --self-test   ：只跑本脚本内置的分层/聚合用例，不读仓库。
#
# 【输出】
#   默认模式打印三行 GITHUB_OUTPUT 内容：edit=... / play=... / reason=...（reason 单行）。
#   脚本永远 exit 0（分类器自己出问题绝不能让流水线“跳过测试还显示绿色”）；
#   只有 --summary / --comment 之外的致命用法错误（未知参数）才 exit 2。
#
# 【为什么必须有这个脚本】
#   1) 不能用 workflow 级的 paths 过滤：必需检查是 ruleset 按字符串精确匹配的，
#      workflow 没触发时对应检查会永久停在 Pending，PR 直接锁死。
#      所以“这次不跑”必须表现为 job 级跳过（skipped 对 ruleset 等同通过）。
#   2) 判定逻辑必须是代码而不是人肉：漏跑一次测试不会有任何报错，只会安静地少测一层。
#   3) 判定必须 fail-safe：任何读不到 diff 的情况（无 PR 上下文、git 报错、空变更集、
#      没见过的目录）一律两套都跑——多花 4 分钟，比漏测安全。
#
# 【注意】
#   路径规则只在本文件的 `_file_layer` 里维护。新增目录时要同步这里的 case 表与
#   `--self-test` 用例；repo-hygiene.yml 每次 PR 都会跑 --self-test，改坏规则会当场红。
# =============================================================================
set -uo pipefail

# 本脚本自身的绝对路径，供自检递归调用
_self_path=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")

# -----------------------------------------------------------------------------
# _file_layer —— 判定单个文件属于哪一层
# 输入：仓库相对路径（如 Assets/Spotlight/Core/GameSession.cs）；输出：doc / ui / logic / cross
# 为什么先判交叉层：*.meta、asmdef 这类文件本身就是“跨层”的（改 GUID 能让 prefab 引用静默失效），
#   必须优先于它所在目录的表现层规则命中，否则 UI 目录下的 .meta 会被误判成“只跑 PlayMode”。
# 为什么末尾默认 cross：未识别路径一律当成最危险的层，宁多跑不漏跑。
# -----------------------------------------------------------------------------
_file_layer() {
  local _p="$1"

  # —— 交叉层（优先）——
  case "$_p" in
    *.meta|*.asmdef) echo cross; return 0 ;;
    Assets/Spotlight/Runtime/Cards/*|Assets/Spotlight/Runtime/Config/*|Assets/Spotlight/Runtime/Game/*) echo cross; return 0 ;;
    Assets/Spotlight/Runtime/AssemblyInfo.cs|Assets/Spotlight/Fonts/*) echo cross; return 0 ;;
    ProjectSettings/*|Packages/*|Assets/Plugins/*) echo cross; return 0 ;;
    .github/workflows/*|scripts/*) echo cross; return 0 ;;
  esac

  # —— 文档层 ——
  case "$_p" in
    docs/*|*.md|*.MD) echo doc; return 0 ;;
    AGENTS.md|LICENSE|LICENSE.*|.gitattributes|.gitignore|.editorconfig) echo doc; return 0 ;;
    .github/ISSUE_TEMPLATE/*) echo doc; return 0 ;;
  esac

  # —— 规则层 ——
  case "$_p" in
    Assets/Spotlight/Core/*|Assets/Spotlight/Generated/*|Assets/Spotlight/Config/*) echo logic; return 0 ;;
    Assets/Spotlight/Editor/*|Assets/Spotlight/Tests/EditMode/*) echo logic; return 0 ;;
  esac

  # —— 表现层 ——
  case "$_p" in
    Assets/Spotlight/Runtime/UI/*|Assets/Spotlight/Runtime/View/*|Assets/Spotlight/Runtime/Feedback/*|Assets/Spotlight/Runtime/Debug/*) echo ui; return 0 ;;
    Assets/Spotlight/UI/*|Assets/Spotlight/Scenes/*|Assets/Spotlight/Tests/PlayMode/*) echo ui; return 0 ;;
  esac

  echo cross
}

# -----------------------------------------------------------------------------
# _read_decision —— 读文件清单并算出结论
# 输入：标准输入逐行的文件路径；输出：设置全局变量（不打印）
#   _D_TOTAL / _D_DOC / _D_UI / _D_LOGIC / _D_CROSS：各层文件数
#   _D_EDIT / _D_PLAY：true|false
#   _D_REASON：单行中文原因（写进 GITHUB_OUTPUT）
# 为什么用全局变量而不是 echo：调用方除了结论还要拿各层计数去渲染 Markdown 摘要，
#   用管道接echo会让函数落在子 shell 里，计数传不出来。
# -----------------------------------------------------------------------------
_read_decision() {
  local _p _layer
  _D_TOTAL=0; _D_DOC=0; _D_UI=0; _D_LOGIC=0; _D_CROSS=0
  while IFS= read -r _p || [ -n "$_p" ]; do
    [ -n "$_p" ] || continue
    _D_TOTAL=$((_D_TOTAL + 1))
    _layer=$(_file_layer "$_p")
    case "$_layer" in
      doc) _D_DOC=$((_D_DOC + 1)) ;;
      ui) _D_UI=$((_D_UI + 1)) ;;
      logic) _D_LOGIC=$((_D_LOGIC + 1)) ;;
      *) _D_CROSS=$((_D_CROSS + 1)) ;;
    esac
  done

  if [ "$_D_TOTAL" -eq 0 ]; then
    # 空变更集/读不到 diff：保守起见两套都跑（workflow_dispatch 也走这条）。
    _D_EDIT=true; _D_PLAY=true
    _D_REASON="读不到变更清单（无 PR 上下文或空变更集）：按 fail-safe 两套都跑"
    return 0
  fi

  if [ "$_D_CROSS" -gt 0 ] || [ "$_D_LOGIC" -gt 0 ]; then
    _D_EDIT=true; _D_PLAY=true
  elif [ "$_D_UI" -gt 0 ]; then
    _D_EDIT=false; _D_PLAY=true
  else
    _D_EDIT=false; _D_PLAY=false
  fi

  local _parts=""
  [ "$_D_DOC" -gt 0 ] && _parts="文档 ${_D_DOC}"
  [ "$_D_LOGIC" -gt 0 ] && _parts="${_parts:+$_parts + }规则 ${_D_LOGIC}"
  [ "$_D_UI" -gt 0 ] && _parts="${_parts:+$_parts + }表现 ${_D_UI}"
  [ "$_D_CROSS" -gt 0 ] && _parts="${_parts:+$_parts + }交叉 ${_D_CROSS}"
  _D_REASON="共 ${_D_TOTAL} 个文件（${_parts}）"
}

# -----------------------------------------------------------------------------
# _collect_files —— 取本次变更的文件清单
# 输入：全局 _opt_base / _opt_head / _opt_files；输出：逐行打印路径，返回 0 表示成功
# 为什么用 `core.quotepath=false`：仓库里有中文文件名（docs/玩法.md），默认会被转义成 \346...，
#   后面的 case 匹配会全部失效。
# 为什么用 `--no-renames`：重命名拆成“删 + 增”两条，两个路径都会参与分层（否则只看到新路径，
#   旧路径所在层的测试可能被漏掉）。
# 为什么读取端要写 `read -r _p || [ -n "$_p" ]`：git 输出通常带结尾换行，但 `--files`/
#   自检喂进来的清单可能没有最后那个换行；只用 `while read` 会静默丢掉最后一行（少一个文件
#   参与判定 = 可能漏跑一层测试）。
# -----------------------------------------------------------------------------
_collect_files() {
  if [ -n "${_opt_files:-}" ]; then
    [ -r "$_opt_files" ] || return 1
    cat "$_opt_files"
    return 0
  fi
  local _base="${_opt_base:-${BASE:-}}" _head="${_opt_head:-${HEAD:-}}"
  [ -n "$_base" ] && [ -n "$_head" ] || return 1
  git rev-parse --git-dir >/dev/null 2>&1 || return 1
  git -c core.quotepath=false diff --name-only --no-renames \
    --diff-filter=ACDMRT "$_base...$_head" 2>/dev/null
}

# -----------------------------------------------------------------------------
# _render_summary —— 生成人读的 Markdown 摘要
# 输入：文件清单路径（第二个参数是可选的说明前缀）；输出：标准输出的 Markdown
# 为什么单独渲染：$GITHUB_STEP_SUMMARY（Actions 运行页）与 PR sticky 评论用同一份文案，
#   避免两处口径不一致；“跳过了什么、为什么”必须写清楚，否则灰色 Skipped 只会让人猜。
# -----------------------------------------------------------------------------
_render_summary() {
  local _files="$1"
  local _title
  if [ "$_D_TOTAL" -eq 0 ]; then
    _title="本次变更分类：读不到变更清单（两套都跑）"
  elif [ "$_D_CROSS" -gt 0 ] || [ "$_D_LOGIC" -gt 0 ]; then
    _title="本次变更分类：规则层/交叉层改动（两套都跑）"
  elif [ "$_D_UI" -gt 0 ]; then
    _title="本次变更分类：仅表现层改动（只跑 PlayMode）"
  else
    _title="本次变更分类：仅文档改动（两套都跳过）"
  fi

  printf '### %s\n\n' "$_title"
  printf '| 套件 | 是否运行 | 说明 |\n|---|---|---|\n'
  if [ "$_D_EDIT" = "true" ]; then
    printf '| EditMode 测试 | ✅ 运行 | 变更集包含规则层或交叉层文件 |\n'
  else
    printf '| EditMode 测试 | ⏭ 跳过 | 变更集只落在文档层/表现层，EditMode 覆盖不到这些改动 |\n'
  fi
  if [ "$_D_PLAY" = "true" ]; then
    printf '| PlayMode 测试 | ✅ 运行 | 变更集包含表现层/规则层/交叉层文件 |\n'
  else
    printf '| PlayMode 测试 | ⏭ 跳过 | 变更集只落在文档层 |\n'
  fi
  printf '| 大文件 / LFS 一致性 | ✅ 运行 | 与分层无关，始终执行（秒级、不占 Unity 席位） |\n\n'

  printf '判定依据：%s\n\n' "$_D_REASON"
  printf '分层口径见 `docs/tasks/completed/CI测试分层计划.md`；需要强制两套全量时用 Actions 页面的 **Run workflow** 手动触发。\n\n'

  if [ -s "$_files" ]; then
    local _count
    _count=$(grep -c . "$_files" 2>/dev/null || echo 0)
    printf '<details><summary>变更文件（%s）</summary>\n\n' "$_count"
    head -n 50 "$_files" | sed 's/^/- `/; s/$/`/'
    if [ "$_count" -gt 50 ]; then printf -- '- …（其余 %s 个省略）\n' "$((_count - 50))"; fi
    printf '\n</details>\n'
  fi
}

# -----------------------------------------------------------------------------
# _pr_number —— 从 CI 环境推断当前 PR 编号
# 输入：无（读 GITHUB_REF）；输出：编号或空字符串
# 为什么不用事件 JSON：GITHUB_REF 在 pull_request 事件里固定是 refs/pull/<N>/merge，
#   一行 sed 就够，且不需要 jq。非 PR 运行（workflow_dispatch）返回空 → 跳过评论。
# -----------------------------------------------------------------------------
_pr_number() {
  sed -n 's#^refs/pull/\([0-9]\{1,\}\)/.*#\1#p' <<<"${GITHUB_REF:-}" | head -1
}

# -----------------------------------------------------------------------------
# _post_comment —— 把摘要贴成 PR 上的 sticky 评论（有则原地更新）
# 输入：Markdown 文本；输出：无（失败只警告，不改变退出码）
# 为什么 sticky：每次 push 都新发一条评论会把 PR 对话刷成一堵墙；靠隐藏标记
#   `<!-- spotlight-ci-classify -->` 找到自己上一条并 PATCH。
# 为什么失败不退出非零：fork PR 的 GITHUB_TOKEN 是只读的，评论发不出去是预期内的；
#   评论只是辅助信息，不能因为它把 CI 弄红（结论同时写在运行摘要与 job 的 Skipped 状态里）。
# -----------------------------------------------------------------------------
_post_comment() {
  local _body="$1" _pr _repo _id
  _pr=$(_pr_number)
  _repo="${GITHUB_REPOSITORY:-}"
  if [ -z "$_pr" ] || [ -z "$_repo" ]; then
    echo "（非 PR 运行或缺少 GITHUB_REPOSITORY，跳过 PR 评论）" >&2
    return 0
  fi
  if ! command -v gh >/dev/null 2>&1; then
    echo "（未安装 gh，跳过 PR 评论）" >&2
    return 0
  fi
  if ! _id=$(gh api "repos/$_repo/issues/$_pr/comments" --paginate \
      --jq '.[] | select(.body | contains("<!-- spotlight-ci-classify -->")) | .id' 2>/dev/null | head -1); then
    echo "（读取 PR 评论失败，跳过）" >&2
    return 0
  fi
  if [ -n "${_id:-}" ]; then
    gh api -X PATCH "repos/$_repo/issues/comments/$_id" -f body="$_body" >/dev/null 2>&1 \
      && echo "已更新 PR 评论 #$_id" >&2 \
      || echo "（更新 PR 评论失败，跳过）" >&2
  else
    gh api -X POST "repos/$_repo/issues/$_pr/comments" -f body="$_body" >/dev/null 2>&1 \
      && echo "已创建 PR 评论（#$_pr）" >&2 \
      || echo "（创建 PR 评论失败，跳过；fork PR 的令牌是只读的）" >&2
  fi
  return 0
}

# -----------------------------------------------------------------------------
# _self_test —— 内置用例
# 输入：无；输出：逐项断言结果，返回 0 表示全部通过
# 为什么用独立夹具：直接断言当前仓库的目录结构，会让用例随仓库漂移；
#   这里只喂路径字符串给 _file_layer / _read_decision，验证“规则本身”而不是“仓库现状”。
# -----------------------------------------------------------------------------
_self_test() {
  local _rc=0 _case _path _want _got
  echo "== classify-change-set.sh 自检 =="

  # --- A. 单文件分层 ---
  while IFS='|' read -r _case _path _want; do
    [ -n "${_case:-}" ] || continue
    _got=$(_file_layer "$_path")
    if [ "$_got" = "$_want" ]; then
      echo "  ✅ 自检 $_case 通过（$_path → $_got）"
    else
      echo "  ❌ 自检 $_case 失败：$_path 期望 $_want，实际 $_got"
      _rc=1
    fi
  done <<'FILECASES'
A1（docs 下的文档）|docs/玩法.md|doc
A2（根目录 AGENTS.md）|AGENTS.md|doc
A3（图纸 JSON 也是文档）|docs/diagrams/01-modules.json|doc
A4（任意层级 README）|Assets/Spotlight/README.md|doc
A5（规则内核）|Assets/Spotlight/Core/GameSession.cs|logic
A6（生成的配置资产）|Assets/Spotlight/Generated/Cards/ge1.prefab|logic
A7（Excel 源工作簿）|Assets/Spotlight/Config/Spotlight.xlsx|logic
A8（编译器）|Assets/Spotlight/Editor/Excel/ConfigCompiler.cs|logic
A9（EditMode 测试）|Assets/Spotlight/Tests/EditMode/GameSessionTests.cs|logic
A10（HUD 视图）|Assets/Spotlight/Runtime/UI/HandView.cs|ui
A11（UXML）|Assets/Spotlight/UI/Spotlight.uxml|ui
A12（演示场景）|Assets/Spotlight/Scenes/Spotlight.unity|ui
A13（PlayMode 测试）|Assets/Spotlight/Tests/PlayMode/BoardMovePlayModeTests.cs|ui
A14（卡牌视图=跨层）|Assets/Spotlight/Runtime/Cards/Card.cs|cross
A15（引导器=跨层）|Assets/Spotlight/Runtime/Game/GameBootstrap.cs|cross
A16（UI 的 .meta 也必须跨层）|Assets/Spotlight/UI/BoardCell.prefab.meta|cross
A17（asmdef 跨层）|Assets/Spotlight/Core/Spotlight.Core.asmdef|cross
A18（工程设置）|ProjectSettings/ProjectVersion.txt|cross
A19（CI 脚本自身）|scripts/check-ci-config.sh|cross
A20（工作流自身）|.github/workflows/unity-tests.yml|cross
A21（没见过的目录=保守）|Assets/Spotlight/NewThing/x.cs|cross
A22（字体资产两边都有覆盖）|Assets/Spotlight/Fonts/SimHei SDF.asset|cross
A23（LFS 规则）|.gitattributes|doc
FILECASES

  # --- B. 聚合结论（edit/play）---
  local _tmp
  _tmp=$(mktemp -d)
  # shellcheck disable=SC2064
  trap "rm -rf '$_tmp'" RETURN

  while IFS='|' read -r _case _files _want; do
    [ -n "${_case:-}" ] || continue
    printf '%b\n' "${_files//,/$'\n'}" > "$_tmp/list"
    _read_decision < "$_tmp/list"
    _got="edit=$_D_EDIT play=$_D_PLAY"
    if [ "$_got" = "$_want" ]; then
      echo "  ✅ 自检 $_case 通过（$_got）"
    else
      echo "  ❌ 自检 $_case 失败：期望 $_want，实际 $_got"
      _rc=1
    fi
  done <<'SETCASES'
B1（纯文档 → 两套都不跑）|docs/玩法.md,AGENTS.md,docs/diagrams/01-modules.json|edit=false play=false
B2（纯表现层 → 只 PlayMode）|Assets/Spotlight/Runtime/UI/HandView.cs,Assets/Spotlight/UI/Spotlight.uxml|edit=false play=true
B3（纯规则层 → 两套都跑）|Assets/Spotlight/Core/GameSession.cs|edit=true play=true
B4（规则+表现 → 两套都跑）|Assets/Spotlight/Core/GameSession.cs,Assets/Spotlight/Runtime/UI/HandView.cs|edit=true play=true
B5（文档+规则 → 两套都跑）|AGENTS.md,Assets/Spotlight/Core/GameSession.cs|edit=true play=true
B6（文档+表现 → 只 PlayMode）|docs/玩法.md,Assets/Spotlight/Scenes/Spotlight.unity|edit=false play=true
B7（交叉层 → 两套都跑）|ProjectSettings/ProjectVersion.txt|edit=true play=true
B8（未知路径 → 两套都跑）|Assets/Spotlight/NewThing/x.cs|edit=true play=true
B9（空变更集 → 两套都跑）||edit=true play=true
SETCASES

  # --- C. 纯文档时原因文案必须能说明“为什么没跑” ---
  printf 'docs/玩法.md\n' > "$_tmp/list"
  _read_decision < "$_tmp/list"
  if [ "$_D_REASON" = "共 1 个文件（文档 1）" ]; then
    echo "  ✅ 自检 C 通过：原因文案包含层计数"
  else
    echo "  ❌ 自检 C 失败：原因文案为 '$_D_REASON'"
    _rc=1
  fi

  return "$_rc"
}

# -----------------------------------------------------------------------------
# 入口
# -----------------------------------------------------------------------------
_opt_base=""; _opt_head=""; _opt_files=""; _mode="output"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --base) _opt_base="${2:-}"; shift 2 || shift ;;
    --head) _opt_head="${2:-}"; shift 2 || shift ;;
    --files) _opt_files="${2:-}"; shift 2 || shift ;;
    --summary) _mode="summary"; shift ;;
    --comment) _mode="comment"; shift ;;
    --self-test) _mode="self-test"; shift ;;
    -h|--help)
      sed -n '2,30p' "$_self_path" | sed 's/^# \{0,1\}//'
      exit 0 ;;
    *)
      echo "未知参数：$1（可用：--base/--head/--files/--summary/--comment/--self-test）" >&2
      exit 2 ;;
  esac
done

if [ "$_mode" = "self-test" ]; then
  if _self_test; then
    echo "自检结果：全部用例通过"
    exit 0
  fi
  echo "自检结果：存在失败用例"
  exit 1
fi

# 取清单 → 判定；取不到时用空清单走 fail-safe（两套都跑）
_tmp_files=$(mktemp)
trap 'rm -f "$_tmp_files"' EXIT
if ! _collect_files > "$_tmp_files"; then
  : > "$_tmp_files"
fi
_read_decision < "$_tmp_files"

case "$_mode" in
  summary)
    _render_summary "$_tmp_files"
    ;;
  comment)
    _body=$'<!-- spotlight-ci-classify -->\n'"$(_render_summary "$_tmp_files")"
    _post_comment "$_body"
    ;;
  *)
    printf 'edit=%s\n' "$_D_EDIT"
    printf 'play=%s\n' "$_D_PLAY"
    # GITHUB_OUTPUT 单行限制：把换行与 % 之外的字符压平，避免破坏 key=value 解析。
    printf 'reason=%s\n' "$(printf '%s' "$_D_REASON" | tr '\n' ' ')"
    ;;
esac

exit 0
