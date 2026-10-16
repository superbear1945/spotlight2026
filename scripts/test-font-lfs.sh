#!/usr/bin/env bash
# test-font-lfs.sh —— SimHei TextCore 的离线存储回归测试。
# 输入：当前仓库的 index（提交前或 CI checkout 后均可）；无参数。
# 输出：通过摘要并返回 0；存储契约违规返回 1，Git/环境错误返回非零。
# 只读 Git blob 与暂存的属性，不读字体工作区内容、不下载 LFS 对象、无需 Unity
# 或 git-lfs。校验指针结构而非固定 hash，允许正常重新生成字体后更新 oid/size。
set -euo pipefail

readonly fontPath='Assets/Spotlight/Fonts/SimHei TextCore.asset'
readonly maxBlobBytes=1024
readonly thresholdBytes=$((5 * 1024 * 1024))

# Fail：输入一条失败原因，输出带测试名的诊断并以 1 退出。
# 统一断言错误的格式，让本地与 CI 能直接定位字体存储回归。
Fail() {
  printf 'FAIL test-font-lfs: %s\n' "$1" >&2
  exit 1
}

# CheckAttribute：输入属性名与期望值，读取目标字体的 index 属性；不符则失败。
# 必须使用 --cached，确保检查将要提交的规则，而非尚未暂存的工作区修改。
CheckAttribute() {
  local attribute="$1" expected="$2" actual
  actual=$(git check-attr --cached "$attribute" -- "$fontPath")
  [[ "$actual" == "$fontPath: $attribute: $expected" ]] ||
    Fail "$actual (expected $expected)"
}

cd "$(git rev-parse --show-toplevel)"
CheckAttribute filter lfs
CheckAttribute diff lfs
CheckAttribute merge lfs
CheckAttribute text unset

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
awk -v size="$size" -v threshold="$thresholdBytes" 'BEGIN { exit !(size > threshold) }' ||
  Fail "$fontPath: declared size $size must exceed the original 5 MiB threshold"

# 本次只迁移这个字体；检查其余已跟踪 .asset，防止误加 *.asset 的宽泛 LFS 规则。
ordinaryAssets=0
while IFS= read -r -d '' path &&
  IFS= read -r -d '' attribute &&
  IFS= read -r -d '' value; do
  [[ "$path" != "$fontPath" ]] || continue
  [[ "$value" != lfs ]] || Fail "$path: non-target .asset unexpectedly uses LFS"
  ordinaryAssets=$((ordinaryAssets + 1))
done < <(git ls-files -z -- '*.asset' | git check-attr --cached --stdin -z filter)
(( ordinaryAssets > 0 )) || Fail 'no ordinary .asset files checked'

printf 'PASS test-font-lfs: %s\n' "$fontPath"
printf '  oid sha256:%s; size %s; Git blob %s bytes\n' "$oid" "$size" "$blobBytes"
printf '  %s non-target .asset files remain outside LFS\n' "$ordinaryAssets"
