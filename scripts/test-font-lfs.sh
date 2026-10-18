#!/usr/bin/env bash
# test-font-lfs.sh —— SimHei TMP / TextCore 字体的离线存储回归测试。
# 输入：当前仓库的 index（提交前或 CI checkout 后均可）；--self-test 运行隔离用例。
# 输出：通过摘要并返回 0；存储契约违规返回 1，Git/环境错误返回非零。
# 只读 Git blob 与暂存的属性，不读字体工作区内容、不下载 LFS 对象、无需 Unity
# 或 git-lfs。校验指针结构而非固定 hash，允许正常重新生成字体后更新 oid/size。
set -euo pipefail
export LC_ALL=C

_self_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
if [ "$#" -eq 1 ] && [ "$1" = "--self-test" ]; then
  exec bash "$_self_dir/test-font-lfs-self-test.sh" "$_self_dir/test-font-lfs.sh"
fi

readonly fontPaths=(
  'Assets/Spotlight/Fonts/SimHei SDF.asset'
  'Assets/Spotlight/Fonts/SimHei TextCore.asset'
)
readonly maxBlobBytes=1024
_reports=()

# Fail：输入一条失败原因，输出带测试名的诊断并以 1 退出。
# 统一断言错误的格式，让本地与 CI 能直接定位字体存储回归。
Fail() {
  printf 'FAIL test-font-lfs: %s\n' "$1" >&2
  exit 1
}

# CheckAttribute：输入字体路径、属性名与期望值，读取 index 属性；不符则失败。
# 必须使用 --cached，确保检查将要提交的规则，而非尚未暂存的工作区修改。
CheckAttribute() {
  local fontPath="$1" attribute="$2" expected="$3" actual
  actual=$(git check-attr --cached "$attribute" -- "$fontPath")
  [[ "$actual" == "$fontPath: $attribute: $expected" ]] ||
    Fail "$actual (expected $expected)"
}

# CheckFont：输入一个允许使用 LFS 的精确字体路径；校验全部属性和规范指针，保存结果摘要。
# 两个字体逐一验证，不能只放宽排除列表而漏检新增字体的真实 Git blob。
CheckFont() {
  local fontPath="$1" blobBytes pointer pointerPattern oid size
  CheckAttribute "$fontPath" filter lfs
  CheckAttribute "$fontPath" diff lfs
  CheckAttribute "$fontPath" merge lfs
  CheckAttribute "$fontPath" text unset
  blobBytes=$(git cat-file -s ":$fontPath")
  (( blobBytes > 0 && blobBytes <= maxBlobBytes )) ||
    Fail "$fontPath: Git blob is $blobBytes bytes, expected a pointer <= $maxBlobBytes bytes"
  pointer=$(git cat-file blob ":$fontPath")
  pointerPattern='^version https://git-lfs.github.com/spec/v1'$'\n''oid sha256:([0-9a-f]{64})'$'\n''size ([1-9][0-9]*)$'
  [[ "$pointer" =~ $pointerPattern ]] ||
    Fail "$fontPath: expected a complete three-line v1 SHA256 LFS pointer"
  oid="${BASH_REMATCH[1]}"
  size="${BASH_REMATCH[2]}"
  # 命令替换会去掉尾部换行；按规范重新构造并逐字节比较，拒绝多余空行或 NUL。
  git cat-file blob ":$fontPath" |
    cmp -s - <(printf 'version https://git-lfs.github.com/spec/v1\noid sha256:%s\nsize %s\n' "$oid" "$size") ||
    Fail "$fontPath: pointer is not canonical LF-terminated text"
  _reports+=("$fontPath: oid sha256:$oid; size $size; Git blob $blobBytes bytes")
}

# IsTargetFont：输入已跟踪路径；仅精确命中上述两个字体时返回 0，否则返回 1。
# 与指针校验共用同一清单，既允许两个超限字体，也防止普通资产被批量迁入 LFS。
IsTargetFont() {
  local fontPath
  for fontPath in "${fontPaths[@]}"; do
    [[ "$1" != "$fontPath" ]] || return 0
  done
  return 1
}

[ "$#" -eq 0 ] || Fail 'usage: test-font-lfs.sh [--self-test]'
cd "$(git rev-parse --show-toplevel)"
for fontPath in "${fontPaths[@]}"; do CheckFont "$fontPath"; done
# 仅允许两个精确字体路径；检查其余已跟踪 .asset，防止误加 *.asset 的宽泛 LFS 规则。
# 先保存扫描结果并检查退出码，不能用进程替换隐藏 Git 失败或接受部分结果。
_tmp=$(mktemp -d)
# Cleanup：无输入；退出时删除本次只读属性扫描的临时文件，避免污染工作区。
Cleanup() { rm -rf -- "$_tmp"; }
trap Cleanup EXIT
git ls-files -z -- '*.asset' >"$_tmp/paths"
git check-attr --cached --stdin -z filter <"$_tmp/paths" >"$_tmp/attributes"
ordinaryAssets=0
while IFS= read -r -d '' path || [ -n "$path" ]; do
  IFS= read -r -d '' attribute <&0
  IFS= read -r -d '' value <&0
  [[ "$attribute" == filter ]] || Fail "$path: unexpected attribute record"
  if IsTargetFont "$path"; then continue; fi
  [[ "$value" != lfs ]] || Fail "$path: non-target .asset unexpectedly uses LFS"
  ordinaryAssets=$((ordinaryAssets + 1))
done <"$_tmp/attributes"
(( ordinaryAssets > 0 )) || Fail 'no ordinary .asset files checked'

printf 'PASS test-font-lfs: %s fonts\n' "${#fontPaths[@]}"
printf '  %s\n' "${_reports[@]}"
printf '  %s non-target .asset files remain outside LFS\n' "$ordinaryAssets"
