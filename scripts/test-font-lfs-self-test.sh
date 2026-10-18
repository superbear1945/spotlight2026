#!/usr/bin/env bash
# test-font-lfs.sh 的离线自检：输入可选检查器路径，输出独立场景结果。
# Git plumbing 只构造临时 index，无需下载 LFS 对象或安装 git-lfs。
# 验证指针存储契约和扫描失败，防止测试本身误放行或阻止字体正常缩小。
set -Eeuo pipefail
export LC_ALL=C
_checker="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/test-font-lfs.sh}"
_tmp=$(mktemp -d)
_count=0
readonly fontPaths=(
  'Assets/Spotlight/Fonts/SimHei SDF.asset'
  'Assets/Spotlight/Fonts/SimHei TextCore.asset'
)

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

# StagePointer：输入字体路径与实体长度；构造标准三行指针，hash 仅是独立测试夹具。
StagePointer() {
  local pointer
  printf -v pointer 'version https://git-lfs.github.com/spec/v1\noid sha256:%064d\nsize %s\n' 1 "$2"
  StageText "$1" "$pointer"
}

# NewFixture：输入场景名；创建干净临时仓库及精确路径属性，隔离各个场景。
NewFixture() {
  _case="$1"
  _repo="$_tmp/$_case"
  mkdir -p "$_repo"
  git init -q "$_repo"
  git -C "$_repo" config core.attributesFile /dev/null
  local fontPath attributes=$'*.asset text diff=unity\n' rule
  for fontPath in "${fontPaths[@]}"; do
    printf -v rule '"%s" filter=lfs diff=lfs merge=lfs -text\n' "$fontPath"
    attributes+="$rule"
    StagePointer "$fontPath" 6379140
  done
  StageText '.gitattributes' "$attributes"
  StageText 'ordinary.asset' 'ordinary Unity YAML'
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
for fontPath in "${fontPaths[@]}"; do
  [ ! -e "$_repo/$fontPath" ] || exit 1
done

# 每个字体分别破坏，另一个保持合法，证明两条路径均被完整验证而非简单跳过。
for fontPath in "${fontPaths[@]}"; do
  NewFixture "regenerated-smaller-${fontPath##*/}"
  StagePointer "$fontPath" 1024
  RunCheck 0 'size 1024;'

  NewFixture "missing-rule-${fontPath##*/}"
  _attributes=$'*.asset text diff=unity\n'
  for otherFont in "${fontPaths[@]}"; do
    if [[ "$otherFont" != "$fontPath" ]]; then
      printf -v _rule '"%s" filter=lfs diff=lfs merge=lfs -text\n' "$otherFont"
      _attributes+="$_rule"
    fi
  done
  StageText '.gitattributes' "$_attributes"
  RunCheck 1 "$fontPath: filter: unspecified (expected lfs)"

  NewFixture "raw-content-${fontPath##*/}"
  StageText "$fontPath" 'original YAML'
  RunCheck 1 "$fontPath: expected a complete three-line v1 SHA256 LFS pointer"

  NewFixture "malformed-pointer-${fontPath##*/}"
  StageText "$fontPath" $'version https://git-lfs.github.com/spec/v1\noid sha256:invalid\nsize 100\n'
  RunCheck 1 "$fontPath: expected a complete three-line v1 SHA256 LFS pointer"

  NewFixture "noncanonical-extra-newline-${fontPath##*/}"
  printf -v _pointer 'version https://git-lfs.github.com/spec/v1\noid sha256:%064d\nsize 100\n\n' 1
  StageText "$fontPath" "$_pointer"
  RunCheck 1 "$fontPath: pointer is not canonical LF-terminated text"
done

NewFixture 'overbroad-asset-rule'
StageText '.gitattributes' $'*.asset filter=lfs diff=lfs merge=lfs -text\n'
RunCheck 1 'ordinary.asset: non-target .asset unexpectedly uses LFS'

NewFixture 'similar-font-path-is-not-allowlisted'
StageText '.gitattributes' $'*.asset text diff=unity\n"Assets/Spotlight/Fonts/SimHei*.asset" filter=lfs diff=lfs merge=lfs -text\n'
StagePointer 'Assets/Spotlight/Fonts/SimHei SDF Copy.asset' 1024
RunCheck 1 'SimHei SDF Copy.asset: non-target .asset unexpectedly uses LFS'

for _command in ls-files check-attr; do
  NewFixture "partial-scan-failure-$_command"
  MakeGitFailure "$_command"
  RunCheck 42 'fixture partial scan failure' PATH="$_wrapper:$PATH"
  if grep -Fq 'PASS test-font-lfs' "$_log"; then exit 1; fi
done

printf 'Font LFS self-test: %s scenarios passed.\n' "$_count"
