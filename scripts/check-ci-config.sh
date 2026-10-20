#!/usr/bin/env bash
# =============================================================================
# check-ci-config.sh —— GitHub Actions 触发与 Unity 串行配置守卫
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
#   4. unity-tests.yml 的两套 Unity 必须串行（详见 CheckUnitySerialization）：
#      原先是 matrix + max-parallel: 1；为了让“不需要跑的那套”显示成 Skipped（job 级 if）
#      矩阵被拆成两个字面名 job（job 级 if 读不到 matrix 上下文），串行改由
#      playmode-tests 的 needs 链（包含 editmode-tests）保证。它只保证单次运行内串行，
#      跨运行（不同 PR 各自一个 run）的账号级互斥靠下一条。
#
#   4b. unity-tests.yml 不得使用 workflow 级 paths / paths-ignore 过滤（详见 CheckNoPathFilter）：
#      必需检查按 job 名精确匹配，workflow 没触发时检查会永久停在 Pending，PR 直接锁死。
#      “这次少跑”只能表现为 job 级跳过（skipped 对 ruleset 等同通过）。
#
#   4c. unity-tests.yml 必须调用 scripts/classify-change-set.sh，且两个测试 job 的
#      needs 要包含 classify、if 要带 always() 与“分类失败就照跑”的兜底（详见 CheckClassificationWiring）：
#      否则分类 job 自己出错时两个测试 job 会被静默跳过，PR 依旧全绿。
#
#   5. unity-tests.yml 顶层的 concurrency 必须是仓库级固定组名（详见 CheckUnityConcurrency）：
#      Personal 授权是账号级“席位”，同一时刻只能有一个 Unity 进程。不同 PR 跑在各自的
#      refs/pull/N/merge 上，按 ref 分组会让两个 run 落在不同组里同时 --activate-all，
#      输的一方拿到 “0 free entitlements” 而整轮假失败。
#      同时不得设置 cancel-in-progress: true（中途杀掉 Unity 会泄漏席位）。
#
#   6. 一旦 workflow 里出现打包/归档动作（zip / 7z / tar，或引用 build/ 产物目录），
#      就必须同时保留删除 *_BurstDebugInformation_DoNotShip 的兜底行（详见 CheckBuildArtifactCleanup）：
#      Burst 调试目录里是构建机的绝对路径，整目录压进 nightly 包就把开发期信息发出去了。
#      目前唯一有打包动作的是 release-*.yml（手动发版），所以这条对它恒生效。
#
#   7. release-*.yml 是发版专用 workflow，规则与前三条相反（详见 CheckReleaseWorkflow）：
#      只允许 workflow_dispatch（禁止 pull_request / push）、必须声明 contents: write、
#      必须走 scripts/next-release-version.sh 算版本、且同样要与测试共用 unity-seat 席位组。
#      为什么不能沿用“必须有 pull_request”那条：一次发版要跑两轮 Unity 并创建不可回滚的
#      tag/Release，绝不能由提交自动触发；而“给它加上 pull_request 触发以便过守卫”这种
#      为了过检查而改行为的做法，正是这条规则要拦的事。
#
# 【输入】
#   无参数：扫描仓库内 .github/workflows/*.yml（相对仓库根目录）。
#   --self-test：只跑本脚本自身的用例，不检查当前仓库。
#   --check-ruleset：额外比对 GitHub 分支规则集，包含两项：
#     1) 各 workflow 会产生的 job 名 ↔ 规则集里声明的必需检查名（同名精确匹配）；
#     2) 规则集不得要求人工审阅（approval / Code Owner review / required_reviewers）。
#     需要 gh 已登录且对仓库有 admin 权限，因此只在本地推送前跑；未登录时跳过
#     并提示，不当作失败。
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
#   同理，合并规则约定为“必需检查全绿 + 分支最新即可自行合并、不设人工审阅
#   门禁”；规则集界面上的 approval 选项很容易被顺手打开而无任何提示，故一并
#   纳入本脚本的规则集比对。
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
# _job_block —— 取出某个 job 的配置块（去掉 job 自身那行）
# 输入：workflow 文件路径、job 键名（如 playmode-tests）；输出：该 job 下所有缩进更深的行
# 为什么需要：串行与接线约束都必须“只看这个 job 内部”，否则别的 job/注释里出现同名字样
#   就会误满足检查（历史上 max-parallel 的检查就专门为此按缩进定位过 strategy）。
# 为什么按缩进：本仓库 workflow 固定是 `jobs:` → 两空格 job 键 → 四空格键；
#   只依赖缩进可以避开值或注释里出现同名文本造成的误匹配。
# -----------------------------------------------------------------------------
_job_block() {
  awk -v k="$2" '
    $0 ~ "^  " k ":[[:space:]]*(#.*)?$" { inside = 1; next }
    inside && /^  [^[:space:]#]/ { inside = 0 }
    inside { print }
  ' "$1"
}

# -----------------------------------------------------------------------------
# CheckUnitySerialization —— 校验两套 Unity 测试不会同时启动
# 输入：unity-tests.yml 文件路径；输出：失败时打印原因，返回 0 合规 / 1 违规。
# 为什么需要：个人版授权是账号级“席位”，两个 Unity 进程同时 --activate-all 时，
#   输的一方会拿到 “0 free entitlements” 而整轮假失败（PR #9 实测过）。矩阵拆开后
#   不再有 max-parallel: 1，串行只能靠 playmode-tests 依赖 editmode-tests 表达，
#   而“把 needs 里的依赖删掉”是一个看起来毫无影响的改动，必须做成可执行守卫。
# -----------------------------------------------------------------------------
CheckUnitySerialization() {
  local _file="$1" _block
  _block=$(_job_block "$_file" playmode-tests)
  if [ -z "$_block" ]; then
    echo "  ❌ $_file：找不到 playmode-tests job —— 串行守卫按 job 键名定位，改名时守卫要同步更新"
    return 1
  fi
  # 两种合法写法：内联 `needs: [classify, editmode-tests]` 与块状列表 `- editmode-tests`。
  if grep -Eq '^    needs:.*editmode-tests' <<<"$_block" \
    || grep -Eq '^      - editmode-tests[[:space:]]*(#.*)?$' <<<"$_block"; then
    return 0
  fi
  echo "  ❌ $_file：playmode-tests 的 needs 必须包含 editmode-tests"
  echo "     ↑ 矩阵拆开后没有 max-parallel: 1，两套 Unity 并行会各自 --activate-all 抢同一个席位。"
  return 1
}

# -----------------------------------------------------------------------------
# CheckNoPathFilter —— 校验 unity-tests.yml 没有用 workflow 级 paths 过滤
# 输入：unity-tests.yml 文件路径；输出：失败时打印原因，返回 0 合规 / 1 违规。
# 为什么需要：必需检查按 job 名精确匹配；workflow 因路径过滤未触发时，对应检查会永久
#   停在 Pending（官方文档 “Skipping workflow runs” 明写），PR 既不能合并也不会报错。
#   “按变更范围少跑”必须走 job 级跳过（Skipped 等同通过），不能走触发过滤。
# -----------------------------------------------------------------------------
CheckNoPathFilter() {
  local _file="$1" _block
  _block=$(_on_block "$_file")
  if grep -Eq '^[[:space:]]*(paths|paths-ignore):' <<<"$_block"; then
    echo "  ❌ $_file：unity-tests.yml 不得使用 paths / paths-ignore 过滤触发"
    echo "     ↑ 没触发就没检查结论，必需检查会永久 Pending、PR 被锁死。"
    echo "       需要按变更范围少跑时用 scripts/classify-change-set.sh 做 job 级跳过。"
    return 1
  fi
  return 0
}

# -----------------------------------------------------------------------------
# CheckClassificationWiring —— 校验变更分类接到了两个测试 job 上，且失败时不会静默跳过
# 输入：unity-tests.yml 文件路径；输出：失败时打印原因，返回 0 合规 / 1 违规。
# 校验项：
#   1) 文件里真的调用了 scripts/classify-change-set.sh（分层唯一判定入口）；
#   2) 两个测试 job 的 needs 都包含 classify；
#   3) 两个测试 job 的 if 都以 always() 开头并带 needs.classify.result != 'success' 兜底。
# 为什么需要：分类器算错只会表现为“少跑了一层测试”，没有任何红灯；
#   而 if 缺少 always() 时，上游 job 被跳过会让下游一起被跳过——同样全绿，同样危险。
# -----------------------------------------------------------------------------
CheckClassificationWiring() {
  local _file="$1" _job _block _rc=0

  if ! grep -q 'scripts/classify-change-set.sh' "$_file"; then
    echo "  ❌ $_file：没有调用 scripts/classify-change-set.sh —— 分层判定的唯一入口缺失"
    return 1
  fi

  for _job in editmode-tests playmode-tests; do
    _block=$(_job_block "$_file" "$_job")
    if [ -z "$_block" ]; then
      echo "  ❌ $_file：找不到 $_job job（分类接线守卫按 job 键名定位）"
      _rc=1
      continue
    fi
    if ! grep -Eq '^    needs:.*classify' <<<"$_block" && ! grep -Eq '^      - classify[[:space:]]*(#.*)?$' <<<"$_block"; then
      echo "  ❌ $_file：$_job 的 needs 必须包含 classify"
      _rc=1
    fi
    if ! grep -q 'always()' <<<"$_block"; then
      echo "  ❌ $_file：$_job 的 if 必须以 always() 开头"
      echo "     ↑ 否则 needs 里的 job 被跳过时它也会被跳过，PR 显示全绿但什么都没跑。"
      _rc=1
    fi
    if ! grep -Fq "needs.classify.result != 'success'" <<<"$_block"; then
      echo "  ❌ $_file：$_job 的 if 缺少 needs.classify.result != 'success' 兜底"
      echo "     ↑ 分类 job 自己出错时应当照跑两套，而不是静默跳过。"
      _rc=1
    fi
  done

  return "$_rc"
}

# -----------------------------------------------------------------------------
# CheckUnityConcurrency —— 校验顶层 concurrency 是“仓库级单组”且不中途取消
# 输入：unity-tests.yml 文件路径；输出：失败时打印原因，返回 0 合规 / 1 违规。
# 校验项：
#   1) 存在顶层 `concurrency:` 块且里面有 `group:`。
#   2) `group:` 的值是固定名称（不含 ${{ }} 变量），不能按 github.ref / github.head_ref 分组。
#   3) 不得出现 `cancel-in-progress: true`。
# 为什么需要：Personal 授权是账号级的“席位”，同一时刻只能跑一个 Unity 进程；
# 不同 PR 天然是不同 ref（refs/pull/N/merge），按 ref 分组等于放弃互斥。
# 而“把组名改回按 ref、让每个 PR 各跑各的”是一个很自然、看起来无害的改动，
# 只会表现为偶发一轮 0 free entitlements 假失败，根本不像是回归——所以做成守卫。
# 只接受块状写法（顶层 `concurrency:` + 缩进 2 的键），与脚本其余部分保持一致。
# -----------------------------------------------------------------------------
CheckUnityConcurrency() {
  local _file="$1" _block

  _block=$(awk '
    /^concurrency:[[:space:]]*$/ { inside = 1; next }
    inside && /^[^[:space:]]/ { inside = 0 }
    inside { sub(/^ +/, ""); print }
  ' "$_file")

  if [ -z "$_block" ] || ! grep -Eq '^group:' <<<"$_block"; then
    echo "  ❌ $_file：缺少顶层 concurrency.group —— 多个运行会同时抢同一个 Unity 席位"
    return 1
  fi

  if grep -Eq '^group:.*\$\{\{' <<<"$_block"; then
    echo "  ❌ $_file：concurrency.group 含模板变量 —— 必须是不含变量的仓库级固定组名"
    echo "     ↑ 按 ref 分组会让两个 PR 同时启动，各自 --activate-all 抢席位。"
    return 1
  fi

  if ! grep -Eq '^group:[[:space:]]*[A-Za-z0-9._-]+[[:space:]]*$' <<<"$_block"; then
    echo "  ❌ $_file：concurrency.group 格式不对（期望形如 group: unity-seat）"
    return 1
  fi

  if grep -Eq '^cancel-in-progress:[[:space:]]*true' <<<"$_block"; then
    echo "  ❌ $_file：不得设置 cancel-in-progress: true —— 中途杀掉 Unity 会泄漏授权席位"
    echo "     ↑ CLI 只在 EXIT trap 能执行完时归还席位，泄漏会让该账号之后所有运行报 no available seats。"
    return 1
  fi

  return 0
}

# -----------------------------------------------------------------------------
# CheckBuildArtifactCleanup —— 校验“打包 workflow 必须清掉 Burst 调试目录”
# 输入：workflow 文件路径；输出：失败时打印原因，返回 0 合规 / 1 违规。
# 为什么需要：Unity 会在输出目录旁生成 <产品名>_BurstDebugInformation_DoNotShip/，
# 里面记录 Temp/Burst、Library/Burst 与编辑器安装路径；打包步骤 cp 到 runner 后再 zip，
# 泄露的就是 runner 的 /github/workspace/... 路径。目录名里的 DoNotShip 说明 Unity 自己也
# 认为它不该发布，但“压缩整个产物目录”这个写法很自然会把它一起带上，且不会有任何报错。
# 目前唯一带打包动作的是 release-*.yml（手动发版），因此这条对它恒生效。
# -----------------------------------------------------------------------------
CheckBuildArtifactCleanup() {
  local _file="$1"

  # 只有真的存在打包/归档动作时才要求兜底清理，避免给纯测试 workflow 加无意义的行。
  if ! grep -Eq '(^|[[:space:]])(zip|7z|tar)[[:space:]]|build/' "$_file"; then
    return 0
  fi

  if grep -Eq '_BurstDebugInformation_DoNotShip' "$_file"; then
    return 0
  fi

  echo "  ❌ $_file：存在打包/归档步骤，但缺少删除 *_BurstDebugInformation_DoNotShip 的兜底行"
  echo "     ↑ 该目录里是构建机绝对路径（Temp/Burst、Library/Burst、编辑器安装位置）。"
  echo "       压缩前加一行：find build/StandaloneWindows64 -maxdepth 1 -name \"*_BurstDebugInformation_DoNotShip\" -exec rm -rf {} +"
  return 1
}

# -----------------------------------------------------------------------------
# CheckReleaseWorkflow —— 校验发版 workflow（release-*.yml）的触发与权限
# 输入：workflow 文件路径；输出：失败时打印原因，返回 0 合规 / 1 违规。
# 校验项：
#   1) 必须有 workflow_dispatch —— 手动是它唯一合法的触发入口。
#   2) 不得有 pull_request / push —— 发版要跑两轮 Unity 并创建不可回滚的 tag 与 Release。
#   3) 必须有 permissions.contents: write —— gh release create 需要写权限，
#      缺了不会在配置阶段报错，而是在最后一步 403，白等十几分钟。
#   4) 必须调用 scripts/next-release-version.sh —— 版本号只允许一处运算，
#      否则“Release 标签 / 包内 bundleVersion / Android versionCode”三处会分叉。
#   5) 复用 CheckUnityConcurrency —— 发版同样要占 Unity 个人版席位，必须与测试共用 unity-seat。
# 为什么按文件名识别：workflow_dispatch 里读不到“这是不是发版”的语义，
#   而文件名是唯一稳定、可被静态检查的标识；命名约定写在 AGENTS.md 第 7 节。
# -----------------------------------------------------------------------------
CheckReleaseWorkflow() {
  local _file="$1" _block _rc=0

  _block=$(_on_block "$_file")

  if ! grep -q '^[[:space:]]*workflow_dispatch:' <<<"$_block"; then
    echo "  ❌ $_file：缺少 workflow_dispatch 触发 —— 发版只能手动触发"
    _rc=1
  fi

  if grep -Eq '^[[:space:]]*(pull_request|push):' <<<"$_block"; then
    echo "  ❌ $_file：发版 workflow 不得自动触发 —— pull_request / push 都会创建不可回滚的 Release"
    _rc=1
  fi

  if ! grep -Eq '^[[:space:]]*contents:[[:space:]]*write' "$_file"; then
    echo "  ❌ $_file：缺少 permissions.contents: write —— gh release create 会在最后一步 403"
    _rc=1
  fi

  if ! grep -q 'scripts/next-release-version.sh' "$_file"; then
    echo "  ❌ $_file：没有调用 scripts/next-release-version.sh —— 版本号必须由它统一计算"
    echo "     ↑ 自己写一套 sed/awk 版本运算会让 tag、包内 bundleVersion、versionCode 三处分叉。"
    _rc=1
  fi

  CheckUnityConcurrency "$_file" || _rc=1

  return "$_rc"
}

# -----------------------------------------------------------------------------
# _check_file —— 校验单个 workflow 文件的触发策略与 Unity 串行约束
# 输入：workflow 文件路径
# 输出：标准输出打印结论；返回 0 表示合规，1 表示存在违规
# 为什么先按文件名分流：release-*.yml 的触发规则与测试 workflow 正好相反
#   （只许手动、禁止 PR），两条规则不能同时套在同一个文件上。
# -----------------------------------------------------------------------------
_check_file() {
  local _file="$1" _block _rc=0 _is_release=0

  case "${_file##*/}" in
    release-*.yml) _is_release=1 ;;
  esac

  _block=$(_on_block "$_file")

  if [ "$_is_release" -eq 1 ]; then
    CheckReleaseWorkflow "$_file" || _rc=1
  else
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
  fi

  if [ "${_file##*/}" = "unity-tests.yml" ]; then
    CheckUnitySerialization "$_file" || _rc=1
    CheckUnityConcurrency "$_file" || _rc=1
    CheckNoPathFilter "$_file" || _rc=1
    CheckClassificationWiring "$_file" || _rc=1
  fi

  CheckBuildArtifactCleanup "$_file" || _rc=1

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
# _check_review_gate —— 判定规则集是否重新要求了人工审阅
# 输入：标准输入逐行的 `approvals|require_code_owner_review|required_reviewers数量`
#       （由 _check_ruleset 从各规则集的 pull_request 规则里提取）
# 输出：有任何一行不满足“0 / false / 0”则逐条打印原因并返回 1；全部合规返回 0
# 为什么单独抽成函数：这段判定不依赖 gh，可以纳入 --self-test，在未登录的机器上
# 也能验证“规则被改回要求审阅时必须拦截”，而不只是依赖人工去 GitHub 界面查看。
# -----------------------------------------------------------------------------
_check_review_gate() {
  local _line _approvals _codeowner _reviewers _rc=0
  while IFS= read -r _line; do
    [ -n "${_line:-}" ] || continue
    IFS='|' read -r _approvals _codeowner _reviewers <<<"$_line"
    if [ "$_approvals" != "0" ] || [ "$_codeowner" != "false" ] || [ "$_reviewers" != "0" ]; then
      echo "  ❌ 规则集要求人工审阅：required_approving_review_count=$_approvals、"
      echo "     require_code_owner_review=$_codeowner、required_reviewers=$_reviewers"
      echo "     ↑ 本仓库约定合并只看“必需检查全部通过 + 分支不是 out of date”，"
      echo "       PR 作者应能自行手动合并；请把上述三项改回 0 / false / 0（并删除 CODEOWNERS）。"
      _rc=1
    fi
  done
  return "$_rc"
}

# -----------------------------------------------------------------------------
# _check_ruleset —— 把 workflow 的 job 名与分支规则集声明的必需检查名对比，
#   并校验规则集不要求任何人工审阅
# 输入：无（自行通过 gh 读取仓库与规则集）；输出：结论；返回 0 合规 / 1 失配 / 2 无法检查
# 为什么必须查这一条：规则集的必需检查名是按**字符串**匹配 job 名的。job 名里嵌了
# `${{ matrix.unityVersion }}` 这类变量，所以“把 CI 的 Unity 版本改一下”就足以让所有
# PR 永久停在 pending——没有任何报错，也没有超时，表现只是永远等不到检查结果。
# 这种故障从流水线日志里完全看不出来，只能靠提交前静态比对。
# 为什么同时校验审阅要求：本仓库约定“必需检查全绿 + 分支最新即可由作者自行合并”，
# 一旦规则集被改回要求 approval / Code Owner review，别人提交的 PR 会卡在“等待审阅”
# 而作者看不出原因（尤其是启用 CODEOWNERS 时，只有 owner 的 approval 才计数）。
# -----------------------------------------------------------------------------
_check_ruleset() {
  if ! command -v gh >/dev/null 2>&1 || ! gh auth status >/dev/null 2>&1; then
    echo "  ⚠️  gh 不可用或未登录，跳过规则集比对（该检查需要 admin 权限，只能在本地跑）"
    return 2
  fi

  local _repo _ids _id _contexts _pr
  _repo=$(gh repo view --json nameWithOwner --jq .nameWithOwner 2>/dev/null) || return 2
  [ -n "$_repo" ] || return 2
  _ids=$(gh api "repos/$_repo/rulesets" --jq '.[].id' 2>/dev/null) || return 2
  [ -n "$_ids" ] || { echo "  ⚠️  仓库未配置任何规则集，跳过比对"; return 2; }

  # 同时收集必需检查名与 pull_request 规则里的人数门槛：
  # 每行的格式为 `approvals|require_code_owner_review|required_reviewers 数量`。
  local _required="" _pr_rules=""
  while IFS= read -r _id; do
    [ -n "$_id" ] || continue
    _contexts=$(gh api "repos/$_repo/rulesets/$_id" \
      --jq '.rules[] | select(.type=="required_status_checks") | .parameters.required_status_checks[].context' 2>/dev/null)
    [ -n "$_contexts" ] && _required="${_required}${_contexts}"$'\n'
    _pr=$(gh api "repos/$_repo/rulesets/$_id" \
      --jq '.rules[] | select(.type=="pull_request") | .parameters | "\(.required_approving_review_count)|\(.require_code_owner_review)|\(.required_reviewers | length)"' 2>/dev/null)
    [ -n "$_pr" ] && _pr_rules="${_pr_rules}${_pr}"$'\n'
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

  # 合并门槛：只允许“必需检查全绿 + 分支最新”，不得出现任何人工审阅要求。
  _check_review_gate <<<"${_pr_rules%$'\n'}" || _fail=1
  if [ "$_fail" -ne 0 ]; then
    return 1
  fi

  echo "  ✅ 规则集要求的必需检查与 workflow job 名全部匹配，且未要求人工审阅"
  return 0
}

# -----------------------------------------------------------------------------
# _self_test —— 本脚本自身的测试用例
# 在临时目录构造触发策略、矩阵展开、Unity 串行约束与席位并发约束的独立夹具，
# 断言该拦的拦住、该放的放行。
# 输入：无；输出：逐项断言结果，返回 0 表示全部通过，否则返回 1。
# 为什么这样测：直接对真实仓库断言会让用例随仓库内容漂移，因此用独立 fixture。
# -----------------------------------------------------------------------------
_self_test() {
  local _tmp _rc=0
  _tmp=$(mktemp -d)
  # shellcheck disable=SC2064
  trap "rm -rf '$_tmp'" RETURN

  # _write_unity_tests_fixture —— 构造一份 unity-tests.yml 夹具
  # 输入：输出路径、串行写法变体（ok / nofollow / block / noalways / nowiring）、
  #       concurrency 变体（global / ref / cancel / missing）、on 块追加行（可为空，%b 解释换行）；
  #       输出：写入文件，返回 0。
  # 为什么抽成函数：串行/接线约束（E–H、W–X）与席位并发约束（P–S）需要同一份骨架，
  # 只在一处构造才能保证“某个用例失败”确实因为目标规则，而不是骨架各自漂移。
  # 每个变体必须能单独关闭，否则测不出“缺了哪一件会被拦”。
  _write_unity_tests_fixture() {
    local _path="$1" _serial="${2:-ok}" _conc="${3:-global}" _on_extra="${4:-}"
    {
      printf '%s\n' 'on:' '  pull_request:'
      [ -n "$_on_extra" ] && printf '%b\n' "$_on_extra"
      printf '%s\n' '  workflow_dispatch:'
      case "$_conc" in
        global) printf '%s\n' 'concurrency:' '  group: unity-seat' ;;
        ref) printf '%s\n' 'concurrency:' '  group: unity-tests-${{ github.ref }}' ;;
        cancel) printf '%s\n' 'concurrency:' '  group: unity-seat' '  cancel-in-progress: true' ;;
        missing) : ;;
      esac
      printf '%s\n' 'jobs:' '  classify:' '    runs-on: ubuntu-latest' '    steps:'
      if [ "$_serial" = "nowiring" ]; then
        printf '%s\n' '      - run: echo 判定'
      else
        printf '%s\n' '      - run: bash scripts/classify-change-set.sh'
      fi
      printf '%s\n' \
        '  editmode-tests:' \
        '    needs: classify' \
        "    if: always() && (needs.classify.result != 'success' || needs.classify.outputs.edit == 'true')" \
        '    steps:' \
        '      - run: echo edit'
      printf '%s\n' '  playmode-tests:'
      case "$_serial" in
        block) printf '%s\n' '    needs:' '      - classify' '      - editmode-tests' ;;
        nofollow) printf '%s\n' '    needs: [classify]' ;;
        *) printf '%s\n' '    needs: [classify, editmode-tests]' ;;
      esac
      if [ "$_serial" = "noalways" ]; then
        printf '%s\n' "    if: needs.classify.outputs.play == 'true'"
      else
        printf '%s\n' "    if: always() && (needs.classify.result != 'success' || needs.classify.outputs.play == 'true')"
      fi
      printf '%s\n' '    steps:' '      - run: echo play'
    } >"$_path"
    return 0
  }

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

  # --- 用例 E–H、W–X：通过脚本公开入口验证“两套 Unity 串行”与“分层接线”约束，
  # 不能仅凭文件中出现某段字符串就放行。
  # 为什么必须覆盖这几条：它们被改坏时的表现都是“PR 全绿但什么都没跑”或“PR 永久 Pending”，
  # 日志里完全看不出是配置回归。
  local _case _serial _expected _actual
  while IFS='|' read -r _case _serial _expected; do
    _write_unity_tests_fixture "$_tmp/unity-tests.yml" "$_serial"
    _actual=0
    bash "$_self_path" "$_tmp/unity-tests.yml" >/dev/null 2>&1 || _actual=$?
    if [ "$_actual" -eq "$_expected" ]; then
      echo "  ✅ 自检 $_case 通过"
    else
      echo "  ❌ 自检 $_case 失败：期望退出码 $_expected，实际 $_actual"
      _rc=1
    fi
  done <<'CASES'
E（playmode 依赖 editmode → 放行）|ok|0
F（playmode 不依赖 editmode → 拦截）|nofollow|1
G（块状 needs 列表 → 放行）|block|0
H（if 缺 always() 兜底 → 拦截）|noalways|1
W（没调用分类脚本 → 拦截）|nowiring|1
CASES

  # --- 用例 X：unity-tests.yml 不得用 workflow 级 paths 过滤（否则必需检查永久 Pending）。
  _write_unity_tests_fixture "$_tmp/unity-tests.yml" ok global '    paths:\n      - "docs/**"'
  _actual=0
  bash "$_self_path" "$_tmp/unity-tests.yml" >/dev/null 2>&1 || _actual=$?
  if [ "$_actual" -eq 1 ]; then
    echo "  ✅ 自检 X 通过：unity-tests.yml 出现 paths 过滤 → 已拦截"
  else
    echo "  ❌ 自检 X 失败：期望退出码 1，实际 $_actual"
    _rc=1
  fi

  # --- 用例 P–S：席位并发约束（顶层 concurrency 必须是仓库级单组，且不得中途取消）。
  # 为什么必须覆盖：把组名改回按 ref 分组是“看起来能让 PR 跑得更快”的自然改动，
  # 它只在两个 PR 同时启动时表现为 0 free entitlements，日志里根本看不出是配置回归。
  local _ccase _cconc _cexpected _cactual
  while IFS='|' read -r _ccase _cconc _cexpected; do
    _write_unity_tests_fixture "$_tmp/unity-tests.yml" ok "$_cconc"
    _cactual=0
    bash "$_self_path" "$_tmp/unity-tests.yml" >/dev/null 2>&1 || _cactual=$?
    if [ "$_cactual" -eq "$_cexpected" ]; then
      echo "  ✅ 自检 $_ccase 通过"
    else
      echo "  ❌ 自检 $_ccase 失败：期望退出码 $_cexpected，实际 $_cactual"
      _rc=1
    fi
  done <<'CONCCASES'
P（仓库级单组 unity-seat → 放行）|global|0
Q（按 ref 分组 → 拦截）|ref|1
R（cancel-in-progress: true → 拦截）|cancel|1
S（缺 concurrency 块 → 拦截）|missing|1
CONCCASES

  # --- 用例 T–V：打包 workflow 必须清掉 Burst 调试目录（当前无打包 job，这条在加回打包时生效）。
  # 为什么必须覆盖："把整个输出目录压缩上传"是最自然的写法，它会把 Unity 自己标了
  # DoNotShip 的 Burst 调试目录一起带上，泄露构建机绝对路径，而且全程不会报错。
  local _pcase _pstep _pexpected _pactual
  while IFS='|' read -r _pcase _pstep _pexpected; do
    [ -n "${_pcase:-}" ] || continue
    {
      printf '%s\n' \
        'on:' \
        '  pull_request:' \
        '  workflow_dispatch:' \
        'jobs:' \
        '  package:' \
        '    steps:'
      [ -n "$_pstep" ] && printf '      - run: %b\n' "$_pstep"
    } >"$_tmp/case-pack.yml"
    _pactual=0
    bash "$_self_path" "$_tmp/case-pack.yml" >/dev/null 2>&1 || _pactual=$?
    if [ "$_pactual" -eq "$_pexpected" ]; then
      echo "  ✅ 自检 $_pcase 通过"
    else
      echo "  ❌ 自检 $_pcase 失败：期望退出码 $_pexpected，实际 $_pactual"
      _rc=1
    fi
  done <<'PACKCASES'
T（有 zip 打包但无 Burst 清理 → 拦截）|cd build/StandaloneWindows64 && zip -r out.zip .|1
U（压缩前删掉 Burst 目录 → 放行）|cd build/StandaloneWindows64 && find . -maxdepth 1 -name "*_BurstDebugInformation_DoNotShip" -exec rm -rf {} + && zip -r out.zip .|0
V（没有打包步骤 → 放行）|echo test|0
PACKCASES

  # --- 用例 Y–AE：发版 workflow（release-*.yml）的触发与权限约束。
  # 为什么必须覆盖：发版是唯一会创建 tag / Release / 上传资产的流程，且一次要占两轮 Unity 席位。
  # 它改坏时的表现都是“没事发生”或“人工发现时才爆”：
  #   · 加回 pull_request → 每个 PR 都会发一个 Release；
  #   · 漏掉 contents: write → 构建成功后最后一步 403，白烧半小时；
  #   · 自己写一套版本运算 → tag 与包内版本分叉，装上去才知道；
  #   · 按 ref 分组 → 与测试抢同一个 Unity 个人席位，报 no available seats。
  _write_release_fixture() {
    local _path="$1" _variant="${2:-ok}"
    {
      printf '%s\n' 'on:'
      [ "$_variant" = "nodispatch" ] || printf '%s\n' '  workflow_dispatch:'
      case "$_variant" in
        pr | nodispatch) printf '%s\n' '  pull_request:' ;;
        push) printf '%s\n' '  push:' '    branches: [ main ]' ;;
      esac
      printf '%s\n' 'permissions:'
      if [ "$_variant" = "noperm" ]; then
        printf '%s\n' '  contents: read'
      else
        printf '%s\n' '  contents: write'
      fi
      printf '%s\n' 'concurrency:'
      if [ "$_variant" = "refgroup" ]; then
        printf '%s\n' '  group: release-${{ github.ref }}'
      else
        printf '%s\n' '  group: unity-seat'
      fi
      printf '%s\n' 'jobs:' '  release:' '    steps:'
      if [ "$_variant" = "noversion" ]; then
        printf '%s\n' '      - run: echo 打包'
      else
        printf '%s\n' '      - run: bash scripts/next-release-version.sh --latest v0.0.1'
      fi
    } >"$_path"
    return 0
  }

  local _rcase _rvariant _rexpected _ractual
  while IFS='|' read -r _rcase _rvariant _rexpected; do
    [ -n "${_rcase:-}" ] || continue
    _write_release_fixture "$_tmp/release-build.yml" "$_rvariant"
    _ractual=0
    bash "$_self_path" "$_tmp/release-build.yml" >/dev/null 2>&1 || _ractual=$?
    if [ "$_ractual" -eq "$_rexpected" ]; then
      echo "  ✅ 自检 $_rcase 通过"
    else
      echo "  ❌ 自检 $_rcase 失败：期望退出码 $_rexpected，实际 $_ractual"
      _rc=1
    fi
  done <<'RELEASECASES'
Y（合规的发版 workflow → 放行）|ok|0
Z（发版 workflow 加回 pull_request → 拦截）|pr|1
AA（发版 workflow 加回 push → 拦截）|push|1
AB（发版 workflow 缺 workflow_dispatch → 拦截）|nodispatch|1
AC（缺 permissions.contents: write → 拦截）|noperm|1
AD（自己算版本、没走统一脚本 → 拦截）|noversion|1
AE（按 ref 分组抢 Unity 席位 → 拦截）|refgroup|1
RELEASECASES

  # --- 用例 I–K：合并门槛只允许“检查全绿 + 分支最新”，不得要求人工审阅。
  # 为什么必须在自检里覆盖：这条约定平时看不出问题，一旦规则集被改回要求审阅，
  # 表现只是 PR 卡在等 approval；而校验它的 --check-ruleset 需要 gh 登录才能跑，
  # 因此判定逻辑必须能在离线的自检中被证明有效。
  local _gate _gate_expected _gate_line _gate_actual
  while IFS='|' read -r _gate _gate_expected _gate_line; do
    [ -n "${_gate:-}" ] || continue
    _gate_actual=0
    _check_review_gate <<<"${_gate_line//,/|}" >/dev/null 2>&1 || _gate_actual=$?
    if [ "$_gate_actual" -eq "$_gate_expected" ]; then
      echo "  ✅ 自检 $_gate 通过"
    else
      echo "  ❌ 自检 $_gate 失败：期望退出码 $_gate_expected，实际 $_gate_actual"
      _rc=1
    fi
  done <<'GATES'
I（不要求任何审阅，0/false/0 → 放行）|0|0,false,0
J（重新要求 1 个 approval → 拦截）|1|1,false,0
K（重新开启 Code Owner 审阅 → 拦截）|1|0,true,0
GATES

  return "$_rc"
}

# -----------------------------------------------------------------------------
# 入口
# -----------------------------------------------------------------------------

# --self-test：只跑用例，不检查仓库
if [ "${1:-}" = "--self-test" ]; then
  echo "== check-ci-config.sh 自检 =="
  if _self_test; then
    echo "自检结果：全部用例通过"
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

echo "== CI 配置检查：PR 入口、Unity 串行与席位互斥 =="
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
  echo "CI 配置检查：未通过"
  echo "  策略说明：main 由分支规则集「main」保护（必须走 PR、禁 force push、禁删除、"
  echo "  无 bypass，开启 strict 必需检查，且不要求人工审阅：approval 为 0、"
  echo "  Code Owner 审阅关闭、无 required_reviewers），因此必需检查全绿且分支最新后"
  echo "  作者可自行合并；PR 阶段测的就是合并结果，push 触发只会重复跑。"
  echo "  需要时用 workflow_dispatch 手动触发。"
  echo "  release-*.yml 是手动发版专用：只允许 workflow_dispatch、禁止 pull_request/push、"
  echo "  必须有 contents: write、必须走 scripts/next-release-version.sh，并与测试共用 unity-seat 席位组。"
  echo "  Unity 测试矩阵还必须设置 max-parallel: 1，避免矩阵内并行授权竞争；"
  echo "  unity-tests.yml 顶层的 concurrency 必须是仓库级固定组名（unity-seat）且不得中途取消，"
  echo "  否则两个 PR 会同时抢同一个 Unity 个人席位。"
  exit 1
fi

echo "CI 配置检查：通过（${#_targets[@]} 个 workflow 的触发策略与 Unity 串行约束均合规）"
exit 0
