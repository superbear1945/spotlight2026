#!/usr/bin/env bash
# check-repo-hygiene.sh 的隔离回归测试；输入可选检查器绝对路径。
# 输出每个场景的退出码/关键内容断言结果，0 全通过、1 断言失败、2 夹具异常。
# 用 Git 对象/index plumbing 创建夹具，不调用真实 LFS filter、不访问网络或真实仓库 index。
set -Eeuo pipefail
export LC_ALL=C
_checker="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/check-repo-hygiene.sh}"
_tmp=$(mktemp -d)
_count=0

# Cleanup：无输入；删除全部临时夹具，确保测试不污染仓库或后续 CI 步骤。
Cleanup() { rm -rf -- "$_tmp"; }
trap Cleanup EXIT
trap 'printf "自检夹具异常：第 %s 行\n" "$LINENO" >&2; exit 2' ERR
# 子进程只写夹具摘要；所有潜在 ::error 均捕获到文件，失败诊断也加前缀。
unset GITHUB_STEP_SUMMARY GITHUB_ACTIONS GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE GIT_COMMON_DIR
unset GIT_OBJECT_DIRECTORY GIT_ALTERNATE_OBJECT_DIRECTORIES GIT_CONFIG_COUNT GIT_CONFIG_PARAMETERS
export GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_GLOBAL=/dev/null

# NewFixture：输入场景名；初始化独立仓库并设置本场景日志/摘要路径。
# 不复用 index，避免一个测试留下的属性或大对象影响另一场景。
NewFixture() {
  _case="$1"
  _repo="$_tmp/$_case"
  mkdir -p "$_repo"
  git init -q "$_repo"
  git -C "$_repo" config core.attributesFile /dev/null
  _log="$_repo/output.log"
  _summary="$_repo/summary.md"
}

# StageBlob：输入路径和内容文件；写入 blob 并暂存，返回 OID 到 _oid。
# NUL index-info 可在 Windows 上测试含 CR/LF/Tab 的 Git 路径，而不创建非法宿主文件名。
StageBlob() {
  _oid=$(git -C "$_repo" hash-object -w --stdin <"$2")
  printf '100644 %s\t%s\0' "$_oid" "$1" |
    git -C "$_repo" -c core.protectNTFS=false update-index -z --index-info
}

# StageText：输入路径与文本；转交 StageBlob，便于构造属性与短指针内容。
StageText() {
  printf '%s' "$2" >"$_tmp/content"
  StageBlob "$1" "$_tmp/content"
}

# RunCheck：输入期望退出码及可选环境变量；捕获检查器输出到夹具，断言精确退出码。
# 每次覆盖摘要文件，避免之前的报告让当前的断言假通过。
RunCheck() {
  local expected="$1" actual=0
  shift
  : >"$_summary"
  (cd "$_repo" && env MAX_FILE_MB=5 GITHUB_ACTIONS=false GITHUB_STEP_SUMMARY="$_summary" \
    "$@" bash "$_checker") >"$_log" 2>&1 || actual=$?
  if [ "$actual" -ne "$expected" ]; then
    Fail "期望退出码 $expected，实际 $actual"
  fi
}

# Fail：输入断言失败说明；打印带前缀的捕获日志并退出 1，避免发出真实 CI 注解。
Fail() {
  printf '  ❌ %s：%s\n' "$_case" "$1" >&2
  sed 's/^/    | /' "$_log" >&2
  exit 1
}

# AssertContains：输入文件和字面子串；断言存在，验证路径/大小而不是只判真假。
AssertContains() { grep -Fq -- "$2" "$1" || Fail "缺少关键输出：$2"; }

# AssertAbsent：输入文件和字面子串；断言不存在，验证边界文件不误报/异常不假绿。
AssertAbsent() { if grep -Fq -- "$2" "$1"; then Fail "不应出现：$2"; fi; }

# Pass：无输入；累计并打印场景成功，数量由实际执行计算，不手写容易漂移的用例数。
Pass() { _count=$((_count + 1)); printf '  ✅ %s\n' "$_case"; }

# MakeGitFailure：输入要失败的 Git 子命令；输出夹具 wrapper 路径到 _wrapper。
# 仅通过单次 PATH 注入模拟扫描失败，不修改任何真实 Git 配置。
MakeGitFailure() {
  _wrapper="$_repo/bin"
  mkdir "$_wrapper"
  {
    printf '#!/usr/bin/env bash\n'
    printf 'if [ "${1:-}" = %q ]; then echo "fixture Git scan failure" >&2; exit 42; fi\n' "$1"
    printf 'exec %q "$@"\n' "$(command -v git)"
  } >"$_wrapper/git"
  chmod +x "$_wrapper/git"
}

printf '== check-repo-hygiene.sh 隔离自检 ==\n'
NewFixture '声明LFS但内容是真实数据'
StageText '.gitattributes' $'*.bin filter=lfs diff=lfs merge=lfs -text\n'
StageText 'a.bin' $'REAL-BINARY-DATA\n'
RunCheck 1
AssertContains "$_log" '路径：a.bin'
AssertContains "$_log" '大小 17 bytes（0.00 MiB）'
AssertContains "$_log" '声明 filter=lfs，但 index 内容缺少 LFS 指针标识'
AssertContains "$_summary" '| <code>a.bin</code> | 17 | 0.00 | 5242880 bytes（5 MiB） |'
AssertAbsent "$_log" '::error'
Pass

NewFixture '合法LFS指针放行'
StageText '.gitattributes' $'*.bin filter=lfs diff=lfs merge=lfs -text\n'
printf 'version https://git-lfs.github.com/spec/v1\noid sha256:%064d\nsize 16\n' 0 >"$_tmp/pointer"
StageBlob 'a.bin' "$_tmp/pointer"
RunCheck 0
AssertContains "$_log" '仓库体积卫生检查：通过（0 条违规记录）'
AssertContains "$_summary" '## 仓库体积卫生检查：通过'
AssertAbsent "$_log" '路径：'
Pass

NewFixture '普通超限文件的完整路径回归'
head -c 20000 /dev/zero >"$_tmp/big"
StageBlob 'big.bin' "$_tmp/big"
RunCheck 1 MAX_FILE_MB=0.001
AssertContains "$_log" '路径：big.bin'
AssertContains "$_log" '大小 20000 bytes（0.02 MiB）'
AssertContains "$_log" '阈值 1048.576 bytes（0.001 MiB）'
AssertContains "$_log" '普通 Git blob 超过大小阈值'
Pass

NewFixture '中文空格路径及多个超限文件'
StageBlob 'Assets/字体/中文 字体.asset' "$_tmp/big"
StageBlob 'another big.bin' "$_tmp/big"
RunCheck 1 MAX_FILE_MB=0.001 GITHUB_ACTIONS=true
AssertContains "$_log" '路径：Assets/字体/中文 字体.asset'
AssertContains "$_log" '路径：another big.bin'
AssertContains "$_log" '::error file=Assets/字体/中文 字体.asset::普通 Git blob 超过大小阈值'
AssertContains "$_summary" '| <code>Assets/字体/中文 字体.asset</code> | 20000 | 0.02 |'
AssertContains "$_summary" '| <code>another big.bin</code> | 20000 | 0.02 |'
AssertContains "$_log" '未通过（2 条违规记录）'
Pass

NewFixture '恰好阈值放行且超一字节拦截'
head -c 1048576 /dev/zero >"$_tmp/boundary"
StageBlob 'exact.bin' "$_tmp/boundary"
RunCheck 0 MAX_FILE_MB=1
AssertContains "$_log" '通过（0 条违规记录）'
printf x >>"$_tmp/boundary"
StageBlob 'one-over.bin' "$_tmp/boundary"
RunCheck 1 MAX_FILE_MB=1
AssertContains "$_log" '路径：one-over.bin'
AssertContains "$_log" '大小 1048577 bytes（1.00 MiB）；阈值 1048576 bytes（1 MiB）'
AssertAbsent "$_log" '路径：exact.bin'
AssertContains "$_summary" '未通过'
Pass

NewFixture '注解属性及摘要表格转义'
_special_path=$'目录/空 格%,:|<&`"\t\r\ntail.bin'
StageBlob "$_special_path" "$_tmp/big"
RunCheck 1 MAX_FILE_MB=0.001 GITHUB_ACTIONS=true
AssertContains "$_log" '路径：目录/空 格%,:|<&`"\t\r\ntail.bin'
AssertContains "$_log" $'::error file=目录/空 格%25%2C%3A|<&`"\t%0D%0Atail.bin::普通 Git blob 超过大小阈值'
AssertContains "$_summary" '| <code>目录/空 格%,:&#124;&lt;&amp;&#96;&quot;&#92;t&#92;r&#92;ntail.bin</code> | 20000 | 0.02 |'
[ "$(grep -c '^::error ' "$_log")" -eq 1 ] || Fail '换行路径拆散了注解'
Pass

NewFixture '暂存属性不被工作区属性覆盖'
StageText '.gitattributes' $'*.bin filter=lfs -text\n'
StageText 'a.bin' 'real data'
printf '' >"$_repo/.gitattributes"
RunCheck 1
AssertContains "$_log" '声明 filter=lfs，但 index 内容缺少 LFS 指针标识'
Pass

# 三条扫描命令的失败都必须退出 2，空输出不能变成“没有超限文件”。
for _command in ls-files cat-file check-attr; do
  NewFixture "扫描异常-$_command"
  StageText 'small.txt' 'small'
  MakeGitFailure "$_command"
  RunCheck 2 PATH="$_wrapper:$PATH"
  AssertContains "$_log" 'fixture Git scan failure'
  AssertContains "$_log" '执行异常'
  AssertContains "$_summary" '执行异常（扫描未完成）'
  AssertAbsent "$_log" '仓库体积卫生检查：通过'
  Pass
done

NewFixture 'LFS内容读取异常不能当作违规或成功'
StageText '.gitattributes' $'*.bin filter=lfs -text\n'
StageText 'a.bin' 'real data'
MakeGitFailure 'unused'
{
  printf '#!/usr/bin/env bash\n'
  printf 'if [ "${1:-}" = cat-file ] && [ "${2:-}" = blob ]; then echo "fixture blob read failure" >&2; exit 42; fi\n'
  printf 'exec %q "$@"\n' "$(command -v git)"
} >"$_wrapper/git"
RunCheck 2 PATH="$_wrapper:$PATH"
AssertContains "$_log" 'fixture blob read failure'
AssertContains "$_summary" '执行异常（扫描未完成）'
AssertAbsent "$_log" '仓库体积卫生检查：通过'
Pass

NewFixture '缺失blob即使batch返回零也不能假绿'
StageText 'lost.bin' 'lost'
rm "$_repo/.git/objects/${_oid:0:2}/${_oid:2}"
RunCheck 2
AssertContains "$_log" 'git cat-file 未返回预期 blob'
AssertAbsent "$_log" '仓库体积卫生检查：通过'
Pass

NewFixture '摘要写入失败升级为执行异常'
StageBlob 'big.bin' "$_tmp/big"
RunCheck 2 MAX_FILE_MB=0.001 GITHUB_STEP_SUMMARY="$_repo"
AssertContains "$_log" '路径：big.bin'
AssertContains "$_log" '无法写入 GITHUB_STEP_SUMMARY'
AssertAbsent "$_log" '仓库体积卫生检查：通过'
Pass

NewFixture '成功扫描的摘要写入失败也退出2'
StageText 'small.txt' 'small'
RunCheck 2 GITHUB_STEP_SUMMARY="$_repo"
AssertContains "$_log" '无法写入 GITHUB_STEP_SUMMARY'
AssertAbsent "$_log" '仓库体积卫生检查：通过'
Pass

NewFixture '没有摘要环境变量也可成功'
StageText 'small.txt' 'small'
RunCheck 0 GITHUB_STEP_SUMMARY=''
AssertContains "$_log" '通过（0 条违规记录）'
[ ! -s "$_summary" ] || Fail '未配置摘要时不应写报告'
Pass

NewFixture '子模块gitlink不作为缺失blob'
printf '160000 %040d\tsubmodule\0' 1 | git -C "$_repo" update-index -z --index-info
RunCheck 0
AssertContains "$_log" '通过（0 条违规记录）'
Pass

printf '自检结果：%s 个场景全部通过（精确退出码与关键输出断言）\n' "$_count"
