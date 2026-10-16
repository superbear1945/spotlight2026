#!/usr/bin/env bash
# 仓库体积卫生检查：扫描 index（CI checkout 后等价于检出的提交）。
# 输入：MAX_FILE_MB 为 MiB 阈值，默认 5，允许小数；--self-test 运行隔离用例。
# 输出：完整路径、字节/MiB 大小、阈值和原因；Actions 注解及可选 step summary。
# 退出码：0 合规，1 违规，2 用法/扫描/报告写入异常；不能把扫描失败当作空结果。
# 为什么需要：普通大 blob 入库后，删除文件也不能回收历史体积；LFS 指针约 130 字节。
set -Eeuo pipefail
export LC_ALL=C

_self_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
if [ "$#" -eq 1 ] && [ "$1" = "--self-test" ]; then
  exec bash "$_self_dir/test-check-repo-hygiene.sh" "$_self_dir/check-repo-hygiene.sh"
fi

readonly _lfs_magic="version https://git-lfs.github.com/spec/v1"
_tmp=""
_summary_rows=""
_execution_error=""
_operation="准备检查"
_violations=0
MAX_FILE_MB="${MAX_FILE_MB:-5}"
_limit_bytes="未计算"

# Cleanup：无输入；删除本次扫描临时文件。退出时运行，避免在仓库中留下产物。
Cleanup() {
  if [ -n "$_tmp" ]; then rm -rf -- "$_tmp"; fi
}
trap Cleanup EXIT

# DisplayPath：输入原始 Git 路径；输出单行可读路径（反斜杠及 CR/LF/Tab 可见转义）。
# 扫描始终保留 NUL 分隔；只在展示时转义，防止换行伪造日志或拆散摘要表。
DisplayPath() {
  local text="$1"
  text="${text//\\/\\\\}"
  text="${text//$'\r'/\\r}"
  text="${text//$'\n'/\\n}"
  text="${text//$'\t'/\\t}"
  printf '%s' "$text"
}

# EscapeCommand：输入文本及可选 property 模式；输出 GitHub workflow command 编码。
# 数据转义 %/CR/LF；file 属性另转义冒号与逗号，避免路径被解释为新属性/命令。
EscapeCommand() {
  local text="$1"
  text="${text//'%'/'%25'}"
  text="${text//$'\r'/'%0D'}"
  text="${text//$'\n'/'%0A'}"
  if [ "${2:-}" = "property" ]; then
    text="${text//':'/'%3A'}"
    text="${text//','/'%2C'}"
  fi
  printf '%s' "$text"
}

# EscapeSummary：输入展示文本；输出适合 Markdown 表格内 HTML code 的文本。
# 转义 HTML、竖线及反引号，防止文件名破坏表格或被渲染成标签/链接。
EscapeSummary() {
  local text="$1"
  text="${text//'&'/'&amp;'}"
  text="${text//'<'/'&lt;'}"
  text="${text//'>'/'&gt;'}"
  text="${text//'"'/'&quot;'}"
  text="${text//\'/'&#39;'}"
  text="${text//'|'/'&#124;'}"
  text="${text//'`'/'&#96;'}"
  text="${text//\\/'&#92;'}"
  printf '%s' "$text"
}

# WriteSummary：输入最终状态；有 GITHUB_STEP_SUMMARY 时追加表格，否则不写。
# 返回写入结果，失败和扫描异常也保留摘要；调用方必须把写入失败升级为退出码 2。
WriteSummary() {
  [ -n "${GITHUB_STEP_SUMMARY:-}" ] || return 0
  {
    printf '## 仓库体积卫生检查：%s\n\n阈值：%s bytes（%s MiB）；违规记录：%s。\n\n' \
      "$1" "$_limit_bytes" "$MAX_FILE_MB" "$_violations" &&
    printf '| 路径 | 大小（bytes） | 大小（MiB） | 阈值 | 违规原因 |\n| --- | ---: | ---: | --- | --- |\n' &&
    { [ -z "$_summary_rows" ] || cat "$_summary_rows"; } &&
    printf '\n%s\n' "$_execution_error"
  } >>"$GITHUB_STEP_SUMMARY"
}

# ExecutionError：输入执行失败上下文；打印错误、尝试写异常摘要并退出 2。
# ERR trap 覆盖 Git/awk/临时文件错误，不再以“输出为空”推断扫描成功。
ExecutionError() {
  trap - ERR
  _execution_error="执行异常：$1"
  printf '%s\n' "$_execution_error" >&2
  if ! WriteSummary '执行异常（扫描未完成）'; then
    printf '执行异常：无法写入 GITHUB_STEP_SUMMARY\n' >&2
  fi
  exit 2
}
trap 'ExecutionError "第 $LINENO 行（$_operation）"' ERR

# ReportViolation：输入原始路径、字节数、MiB 大小和原因；输出日志/注解/摘要行。
# 共享一处报告逻辑，保证两类违规都有路径、大小和阈值；记录数按违规而非文件计。
ReportViolation() {
  local path="$1" bytes="$2" mib="$3" reason="$4" display message
  display=$(DisplayPath "$path")
  message="$reason；大小 $bytes bytes（$mib MiB）；阈值 $_limit_bytes bytes（$MAX_FILE_MB MiB）"
  printf '  路径：%s\n  %s\n' "$display" "$message"
  if [ "${GITHUB_ACTIONS:-}" = "true" ]; then
    printf '::error file=%s::%s\n' "$(EscapeCommand "$path" property)" "$(EscapeCommand "$message")"
  fi
  printf '| <code>%s</code> | %s | %s | %s bytes（%s MiB） | %s |\n' \
    "$(EscapeSummary "$display")" "$bytes" "$mib" "$_limit_bytes" "$MAX_FILE_MB" "$reason" >>"$_summary_rows"
  _violations=$((_violations + 1))
}

[ "$#" -eq 0 ] || ExecutionError '用法：check-repo-hygiene.sh [--self-test]'
if ! [[ "$MAX_FILE_MB" =~ ^[0-9]+(\.[0-9]+)?$ ]]; then
  MAX_FILE_MB="无效"
  ExecutionError 'MAX_FILE_MB 必须是非负数字'
fi
_limit_bytes=$(awk -v mb="$MAX_FILE_MB" 'BEGIN { printf "%.10g", mb * 1048576 }')
_operation="git rev-parse --show-toplevel"
_root=$(git rev-parse --show-toplevel)
cd "$_root"
_tmp=$(mktemp -d)
_summary_rows="$_tmp/rows"
: >"$_summary_rows"

# 路径从 ls-files 到 check-attr 始终 NUL 分隔，绝不转换为换行。
# 只有十六进制 OID 写入 batch-check 的逐行协议，因此空格/中文/Tab/换行不影响配对。
_operation="git ls-files --stage -z"
git ls-files --stage -z >"$_tmp/index"
_paths=() _oids=()
: >"$_tmp/oids"
: >"$_tmp/paths"
while IFS= read -r -d '' _entry; do
  _metadata="${_entry%%$'\t'*}"
  _path="${_entry#*$'\t'}"
  if ! [[ "$_metadata" =~ ^([0-9]{6})\ ([0-9a-f]+)\ 0$ ]] || [ "$_path" = "$_entry" ]; then
    ExecutionError 'index 记录异常或存在未解决的合并冲突'
  fi
  _mode="${BASH_REMATCH[1]}"
  _oid="${BASH_REMATCH[2]}"
  # gitlink 记录是子模块提交，不是本仓库 blob；其对象不一定在本仓库中。
  [ "$_mode" != "160000" ] || continue
  _paths+=("$_path") _oids+=("$_oid")
  printf '%s\n' "$_oid" >>"$_tmp/oids"
  printf '%s\0' "$_path" >>"$_tmp/paths"
done <"$_tmp/index"

_operation="git cat-file --batch-check"
git cat-file --batch-check='%(objectname) %(objecttype) %(objectsize)' <"$_tmp/oids" >"$_tmp/sizes"
_operation="计算 blob 大小"
awk -v lim="$MAX_FILE_MB" '{ printf "%s %s %s %.2f %d\n", $1, $2, $3, $3 / 1048576, ($3 > lim * 1048576) }' \
  "$_tmp/sizes" >"$_tmp/metrics"
# --cached 使属性与被扫描的 index 一致，不受未暂存的 .gitattributes 改动影响。
_operation="git check-attr --cached --stdin -z filter"
git check-attr --cached --stdin -z filter <"$_tmp/paths" >"$_tmp/attrs"

printf '== 仓库体积与 LFS 一致性检查（index）==\n阈值：%s bytes（%s MiB）\n' "$_limit_bytes" "$MAX_FILE_MB"
exec 3<"$_tmp/metrics" 4<"$_tmp/attrs"
for ((_i=0; _i<${#_paths[@]}; _i++)); do
  _path="${_paths[_i]}"
  _operation="解析 Git 扫描结果"
  IFS=' ' read -r _oid _type _bytes _mib _big <&3
  if [ "$_oid" != "${_oids[_i]}" ] || [ "$_type" != "blob" ] || ! [[ "$_bytes" =~ ^[0-9]+$ ]]; then
    ExecutionError 'git cat-file 未返回预期 blob（对象可能缺失）'
  fi
  IFS= read -r -d '' _attr_path <&4
  IFS= read -r -d '' _attr <&4
  IFS= read -r -d '' _value <&4
  if [ "$_attr_path" != "$_path" ] || [ "$_attr" != "filter" ]; then
    ExecutionError 'git check-attr 返回记录不匹配'
  fi
  if [ "$_big" = "1" ]; then
    ReportViolation "$_path" "$_bytes" "$_mib" '普通 Git blob 超过大小阈值'
  fi
  if [ "$_value" = "lfs" ]; then
    _operation="git cat-file blob（检查 LFS 首行）"
    # 完整消费管道，避免 head 提前退出造成 SIGPIPE，也保留 cat-file 的失败状态。
    # 只保留固定长度前缀且先剔除 NUL，二进制内容不会进入 Bash 变量。
    _head=$(git cat-file blob "$_oid" | { head -c "${#_lfs_magic}"; cat >/dev/null; } | tr -d '\0')
    if [ "$_head" != "$_lfs_magic" ]; then
      ReportViolation "$_path" "$_bytes" "$_mib" '声明 filter=lfs，但 index 内容缺少 LFS 指针标识'
    fi
  fi
done
# 有额外记录也不能假定扫描成功（截断记录由上面的 read 非零状态拦截）。
if IFS= read -r _extra <&3 || IFS= read -r -d '' _extra <&4; then
  ExecutionError 'Git 扫描结果存在多余记录'
fi
exec 3<&- 4<&-

if [ "$_violations" -gt 0 ]; then
  printf '\n处理：可按具体资产路径执行 git lfs track "<路径>"，再 git add --renormalize -- "<路径>"。\n'
  printf '不必将所有 *.asset 纳入 LFS；不应入库的文件请 git rm --cached 并补充 .gitignore。\n'
  _status='未通过'
  _exit_code=1
else
  _status='通过'
  _exit_code=0
fi
if ! WriteSummary "$_status"; then
  printf '执行异常：无法写入 GITHUB_STEP_SUMMARY\n' >&2
  exit 2
fi
printf '\n仓库体积卫生检查：%s（%s 条违规记录）\n' "$_status" "$_violations"
exit "$_exit_code"
