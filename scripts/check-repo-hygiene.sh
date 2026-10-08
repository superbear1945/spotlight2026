#!/usr/bin/env bash
# =============================================================================
# check-repo-hygiene.sh —— 仓库体积卫生检查（大文件守卫）
#
# 【作用】
#   在 PR / push 阶段拦住"大二进制文件以普通 blob 形式进入仓库"的提交。
#
# 【两条检查】
#   检查 1（大小阈值）：HEAD 树中任何大于 MAX_FILE_MB 的 blob 都算违规。
#     合规的 LFS 文件入库后是约 130 字节的指针文本，所以天然不会触发。
#   检查 2（LFS 一致性）：凡在 .gitattributes 中声明了 filter=lfs 的文件，
#     都必须真的以 LFS 指针形式入库。否则它仍是一个普通 blob —— 这既会撑大
#     仓库，也会让该文件在工作区永远显示为"已修改"（clean filter 后的指针
#     与 index 里的真实内容不一致）。
#
# 【输入】
#   环境变量 MAX_FILE_MB：大小阈值，单位 MiB，默认 5，允许小数（便于自检）。
#   命令行参数 --self-test：只跑本脚本自身的用例，不检查当前仓库。
#
# 【输出】
#   违规文件清单（人可读）。有违规时退出码 1；无违规 0；用法/环境错误 2。
#
# 【为什么必须有这个脚本】
#   blob 一旦进入历史就无法通过"删文件"回收体积，只能重写历史；而 GitHub
#   还有 100 MB 单文件硬上限，超了就再也推不上去。所以必须在 PR 阶段拦住，
#   而不是等仓库已经变大之后才发现。
#   配套规则见仓库根目录 .gitattributes 的 Git LFS 小节。
# =============================================================================
set -uo pipefail

# 本脚本自身的绝对路径，供自检递归调用
_self_path=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")

# LFS 指针文件的首行标识（Git LFS 规范固定值）
readonly _lfs_magic="version https://git-lfs.github.com/spec/v1"

# -----------------------------------------------------------------------------
# _self_test —— 本脚本的测试用例
# 在临时仓库里构造三种场景，断言"该拦的拦住、该过的放行"。
# 为什么这样测：直接对真实仓库断言会让用例随仓库内容漂移，因此用独立
# fixture 仓库，并把 filter.lfs.* 指向 cat，使测试不依赖 git-lfs 是否安装。
# 返回 0 表示三个用例全部通过。
# -----------------------------------------------------------------------------
_self_test() {
  local _tmp _rc=0
  _tmp=$(mktemp -d)
  # shellcheck disable=SC2064
  trap "rm -rf '$_tmp'" RETURN

  (
    cd "$_tmp" || exit 1
    git init -q .
    git config user.email "selftest@example.com"
    git config user.name "selftest"
    # 让 LFS 过滤器退化成直通，从而在不安装 git-lfs 的环境里也能测指针判定
    git config filter.lfs.clean cat
    git config filter.lfs.smudge cat
    git config filter.lfs.process ""
    git config filter.lfs.required false
  ) || return 1

  # --- 用例 A：声明了 filter=lfs，但入库内容是真实数据 → 必须报错
  printf '*.bin filter=lfs diff=lfs merge=lfs -text\n' >"$_tmp/.gitattributes"
  printf 'REAL-BINARY-DATA\n' >"$_tmp/a.bin"
  (
    cd "$_tmp" || exit 1
    git add -A && git commit -qm "case-a"
  )
  if (cd "$_tmp" && bash "$_self_path" >/dev/null 2>&1); then
    echo "  ❌ 自检 A 失败：声明 LFS 却以真实内容入库的文件没有被检出"
    _rc=1
  else
    echo "  ✅ 自检 A 通过：声明 LFS 却以真实内容入库 → 已拦截"
  fi

  # --- 用例 B：入库内容确实是 LFS 指针 → 必须放行
  printf '%s\noid sha256:%064d\nsize 16\n' "$_lfs_magic" 0 >"$_tmp/a.bin"
  (
    cd "$_tmp" || exit 1
    git add -A && git commit -qm "case-b"
  )
  if (cd "$_tmp" && bash "$_self_path" >/dev/null 2>&1); then
    echo "  ✅ 自检 B 通过：合法 LFS 指针 → 放行"
  else
    echo "  ❌ 自检 B 失败：合法 LFS 指针被误报为违规"
    _rc=1
  fi

  # --- 用例 C：普通大文件超过阈值 → 必须报错（先清空 .gitattributes 隔离检查 2）
  printf '' >"$_tmp/.gitattributes"
  head -c 20000 /dev/zero | tr '\0' 'x' >"$_tmp/big.bin"
  (
    cd "$_tmp" || exit 1
    git add -A && git commit -qm "case-c"
  )
  if (cd "$_tmp" && MAX_FILE_MB=0.001 bash "$_self_path" >/dev/null 2>&1); then
    echo "  ❌ 自检 C 失败：超过阈值的普通 blob 没有被拦住"
    _rc=1
  else
    echo "  ✅ 自检 C 通过：超过阈值的普通 blob → 已拦截"
  fi

  return "$_rc"
}

if [ "${1:-}" = "--self-test" ]; then
  echo "== check-repo-hygiene.sh 自检 =="
  if _self_test; then
    echo "自检结果：3/3 通过"
    exit 0
  fi
  echo "自检结果：存在失败用例"
  exit 1
fi

# 阈值：允许小数，便于自检用极小值构造用例
MAX_FILE_MB="${MAX_FILE_MB:-5}"
if ! awk -v v="$MAX_FILE_MB" 'BEGIN { exit !(v ~ /^[0-9]+(\.[0-9]+)?$/) }'; then
  echo "MAX_FILE_MB 必须是数字（当前：'$MAX_FILE_MB'）" >&2
  exit 2
fi

# 所有命令都相对于仓库根目录执行，避免在子目录里调用时漏扫
_cd_root=$(git rev-parse --show-toplevel 2>/dev/null)
if [ -z "$_cd_root" ]; then
  echo "当前目录不是 git 仓库" >&2
  exit 2
fi
cd "$_cd_root" || exit 2

# 检查对象是 **index（暂存区）** 而不是 HEAD：
#  · 本地场景：能在 commit 之前就验出“我正要提交一个大文件”；
#  · CI 场景：actions/checkout 之后 index 与检出提交完全一致，等价于检查该提交。
_fail=0

# -----------------------------------------------------------------------------
# 检查 1：index 中的超大 blob
# 用 git ls-files -s -z 拿到每个文件的 blob sha，再批量问 cat-file 要大小，
# 全程不读文件内容，很快；用 -z 分隔避免路径带空格时解析错位。
# LFS 指针只有 ~130 字节，所以阈值 5 MiB 不会误伤合规文件。
# -----------------------------------------------------------------------------
echo "== 检查 1/2：超过 ${MAX_FILE_MB} MiB 的非 LFS 文件 =="
_big=$(
  git ls-files -s -z |
    awk -v RS='\0' '{ sub(/^[0-9]+ /, ""); sub(/ [0-9]+\t/, "\t"); print }' |
    git cat-file --batch-check='%(objectsize)\t%(rest)' 2>/dev/null |
    awk -F'\t' -v lim="$MAX_FILE_MB" '
      ($1 + 0) > lim * 1048576 {
        printf "  %8.2f MiB  %s\n", $1 / 1048576, $2
        n++
      }
      END { exit !(n > 0) }
    '
)
if [ -n "$_big" ]; then
  echo "$_big"
  echo "  ↑ 上面每个文件都成了仓库历史里不可回收的 blob。"
  echo "    处理方式：按 .gitattributes 的 Git LFS 小节选定扩展名，执行"
  echo "      git lfs track \"<扩展名>\" && git add --renormalize . && git commit"
  echo "    若该文件本就不该入库，直接 git rm --cached 并补进 .gitignore。"
  _fail=1
else
  echo "  ✅ 没有超限文件"
fi

# -----------------------------------------------------------------------------
# 检查 2：声明 filter=lfs 但未以指针形式入库的文件
# 通过 git check-attr 取每个已跟踪文件的有效 filter 属性，再比对首行是否为
# LFS 指针标识。用 -z 分隔，避免路径带空格时解析错位。
# -----------------------------------------------------------------------------
echo
echo "== 检查 2/2：声明 LFS 但未以指针形式入库的文件 =="
_not_lfs=0
_bad_list=""
while IFS= read -r -d '' _path &&
  IFS= read -r -d '' _attr &&
  IFS= read -r -d '' _value; do
  [ "$_value" = "lfs" ] || continue
  # 读 index 里的内容，即“将要提交的版本”。
  # 先 head -c 截断再用 tr 去掉 NUL：二进制文件的首个换行前可能夹着 NUL 字节，
  # 直接捕获进变量会让 bash 告警并丢字符。
  _head=$(git cat-file blob ":${_path}" 2>/dev/null | head -c 80 | tr -d '\0' | head -n 1)
  case "$_head" in
    "$_lfs_magic"*) ;;
    *)
      _not_lfs=$((_not_lfs + 1))
      _bad_list="${_bad_list}  ${_path}"$'\n'
      ;;
  esac
done < <(git ls-files -z | git check-attr --stdin -z filter)

if [ -n "$_bad_list" ]; then
  printf '%s' "$_bad_list"
  echo "  ↑ 这些文件的扩展名已被 .gitattributes 声明为 LFS，但入库内容仍是真实数据。"
  echo "    它们会一直显示为 modified，且继续占用普通 git 存储。修正："
  echo "      git add --renormalize . && git commit -m \"转为 Git LFS 存储\""
  _fail=1
else
  echo "  ✅ 所有声明 LFS 的文件均为合法指针"
fi

echo
if [ "$_fail" -ne 0 ]; then
  echo "仓库体积卫生检查：未通过"
  exit 1
fi
echo "仓库体积卫生检查：通过"
exit 0
