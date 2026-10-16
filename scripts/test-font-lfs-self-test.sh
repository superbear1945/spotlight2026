#!/usr/bin/env bash
# test-font-lfs.sh 的离线自检：输入可选检查器路径，输出独立场景结果。
# Git plumbing 只构造临时 index，无需下载 LFS 对象或安装 git-lfs。
# 验证指针存储契约和扫描失败，防止测试本身误放行或阻止字体正常缩小。
set -Eeuo pipefail
export LC_ALL=C
_checker="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/test-font-lfs.sh}"
_tmp=$(mktemp -d)
_count=0
readonly fontPath='Assets/Spotlight/Fonts/SimHei TextCore.asset'

# Cleanup：无输入；退出时删除所有临时夹具，真实工作区和 index 不受影响。
Cleanup() { rm -rf -- "$_tmp"; }
trap Cleanup EXIT
trap 'printf "字体测试夹具异常：第 %s 行\n" "$LINENO" >&2; exit 2' ERR
unset GITHUB_STEP_SUMMARY GITHUB_ACTIONS GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE GIT_COMMON_DIR
unset GIT_OBJECT_DIRECTORY GIT_ALTERNATE_OBJECT_DIRECTORIES GIT_CONFIG_COUNT GIT_CONFIG_PARAMETERS
export GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_GLOBAL=/dev/null

# StageText：输入 Git 路径、文本；直接构造 index blob，不运行过滤器或创建实体字体。
StageText() {
  local oid
  oid=$(printf '%s' "$2" | git -C "$_repo" hash-object -w --stdin)
  printf '100644 %s\t%s\0' "$oid" "$1" | git -C "$_repo" update-index -z --index-info
}

# StagePointer：输入实体长度；构造标准三行指针，hash 仅是独立测试夹具。
StagePointer() {
  local pointer
  printf -v pointer 'version https://git-lfs.github.com/spec/v1\noid sha256:%064d\nsize %s\n' 1 "$1"
  StageText "$fontPath" "$pointer"
}

# NewFixture：输入场景名；创建干净临时仓库及精确路径属性，隔离各个场景。
NewFixture() {
  _case="$1"
  _repo="$_tmp/$_case"
  mkdir -p "$_repo"
  git init -q "$_repo"
  git -C "$_repo" config core.attributesFile /dev/null
  StageText '.gitattributes' $'*.asset text diff=unity\n"Assets/Spotlight/Fonts/SimHei TextCore.asset" filter=lfs diff=lfs merge=lfs -text\n'
  StageText 'ordinary.asset' 'ordinary Unity YAML'
  StagePointer 6379140
  _log="$_repo/check.log"
}

# RunCheck：输入预期退出码、关键诊断和可选环境；断言两者以避免只检验失败真假。
# 捕获日志并加前缀展示，fixture 的错误不会成为真实 GitHub 注解。
RunCheck() {
  local expected="$1" diagnostic="$2" actual=0
  shift 2
  (cd "$_repo" && env "$@" bash "$_checker") >"$_log" 2>&1 || actual=$?
  if [ "$actual" -ne "$expected" ] || ! grep -Fq -- "$diagnostic" "$_log"; then
    printf 'FAIL %s: expected exit %s, got %s; diagnostic: %s\n' "$_case" "$expected" "$actual" "$diagnostic" >&2
    awk '{print "  | " $0}' "$_log" >&2
    exit 1
  fi
  _count=$((_count + 1))
  printf 'PASS %s\n' "$_case"
}

# MakeGitFailure：输入 Git 子命令；wrapper 在输出部分合法结果后失败。
# 针对曾被进程替换吞掉的失败，不修改真实 Git 配置或访问网络。
MakeGitFailure() {
  _wrapper="$_repo/bin"
  mkdir "$_wrapper"
  {
    printf '#!/usr/bin/env bash\n'
    printf 'if [ "${1:-}" = %q ] && { [ "$1" = ls-files ] || [ "${3:-}" = --stdin ]; }; then\n' "$1"
    printf '  %q "$@"\n' "$(command -v git)"
    printf '  echo "fixture partial scan failure" >&2; exit 42\nfi\n'
    printf 'exec %q "$@"\n' "$(command -v git)"
  } >"$_wrapper/git"
  chmod +x "$_wrapper/git"
}

NewFixture 'pointer-only-checkout'
RunCheck 0 'Git blob 132 bytes'
[ ! -e "$_repo/$fontPath" ] || exit 1

NewFixture 'regenerated-smaller-font'
StagePointer 1024
RunCheck 0 'size 1024;'

NewFixture 'original-non-lfs-font'
StageText '.gitattributes' $'*.asset text diff=unity\n'
StageText "$fontPath" 'original YAML'
RunCheck 1 'filter: unspecified (expected lfs)'

NewFixture 'raw-content-under-lfs-rule'
StageText "$fontPath" 'original YAML'
RunCheck 1 'expected a complete three-line v1 SHA256 LFS pointer'

NewFixture 'malformed-pointer'
StageText "$fontPath" $'version https://git-lfs.github.com/spec/v1\noid sha256:invalid\nsize 100\n'
RunCheck 1 'expected a complete three-line v1 SHA256 LFS pointer'

NewFixture 'noncanonical-extra-newline'
printf -v _pointer 'version https://git-lfs.github.com/spec/v1\noid sha256:%064d\nsize 100\n\n' 1
StageText "$fontPath" "$_pointer"
RunCheck 1 'pointer is not canonical LF-terminated text'

NewFixture 'overbroad-asset-rule'
StageText '.gitattributes' $'*.asset filter=lfs diff=lfs merge=lfs -text\n'
RunCheck 1 'ordinary.asset: non-target .asset unexpectedly uses LFS'

for _command in ls-files check-attr; do
  NewFixture "partial-scan-failure-$_command"
  MakeGitFailure "$_command"
  RunCheck 42 'fixture partial scan failure' PATH="$_wrapper:$PATH"
  if grep -Fq 'PASS test-font-lfs' "$_log"; then exit 1; fi
done

printf 'Font LFS self-test: %s scenarios passed.\n' "$_count"
