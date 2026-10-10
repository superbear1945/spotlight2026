# 仓库体积与 Git LFS 约定

## 为什么有这份文档

git 的 blob 一旦进入历史，之后**删掉文件也回收不了体积**（只能重写历史），而 GitHub 还有
**单文件 100 MB 的硬上限**，超了直接推不上去。一次误提交的大贴图、大视频或大模型，
就足以把仓库永久撑大，而且对后续所有克隆者都是长期负担。

因此本仓库采用两条防线：

1. **`.gitattributes` 里的 Git LFS 规则** —— 按扩展名把内容资产交给 LFS 托管；
2. **`scripts/check-repo-hygiene.sh` 体积守卫** —— 在 PR 阶段拦住漏网的大文件。

## 新克隆仓库后必须做的事

LFS 指针在没有安装 `git-lfs` 的机器上只是一个 130 字节的文本文件，Unity 会把它当成
损坏的贴图/字体。所以**克隆后第一件事**：

```bash
git lfs install        # 只需一次，写入本机全局 hook 与 filter
git lfs pull           # 拉取 LFS 实体内容
```

CI 侧已在 `actions/checkout` 上使用 `lfs: true`；如果新增工作流而它需要读取
`Assets/` 下的图片、字体或 `Assets/Spotlight/Config/Spotlight.xlsx`，
**必须同样设置 `lfs: true`，否则读到的是指针文本而不是真实文件**。

## 哪些文件走 LFS

完整规则见仓库根目录 `.gitattributes` 末尾的 "Git LFS" 小节，覆盖：

| 类别 | 扩展名 |
| --- | --- |
| 字体 | `ttf` `otf` `woff` `woff2` |
| 位图 / 贴图 | `psd` `psb` `tga` `exr` `hdr` `tif` `tiff` `png` `jpg` `jpeg` `gif` `bmp` |
| 音频 | `wav` `mp3` `ogg` `aiff` `aif` `flac` |
| 视频 | `mp4` `mov` `webm` `avi` |
| 3D / DCC | `fbx` `obj` `blend` `dae` `3ds` `max` `ma` `mb` |
| 离线包 / 文档 / 归档 | `unitypackage` `pdf` `xlsx` `xls` `docx` `pptx` `zip` `7z` `rar` `gz` `tar` |
| 构建产物兜底 | `apk` `aab` |

**有意不走 LFS 的**：`dll` `so` `jar` `aar` 等代码依赖。它们通常只有几十到几百 KB，
保留为普通 blob 便于在网页上直接查看比对；真有超大原生库时，体积守卫会报错并提示改用 LFS。

## 日常用法

```bash
# 提交前自查（本地就能跑，不需要等 CI）
bash scripts/check-repo-hygiene.sh

# 校验守卫脚本本身（3 个用例）
bash scripts/check-repo-hygiene.sh --self-test

# 调整大小阈值（默认 5 MiB）
MAX_FILE_MB=10 bash scripts/check-repo-hygiene.sh
```

守卫有两条检查：

- **检查 1**：暂存区里任何超过阈值的 blob 都算违规（LFS 指针只有约 130 字节，不会误伤）；
- **检查 2**：`.gitattributes` 里声明了 `filter=lfs` 的文件，必须真的以 LFS 指针入库。
  否则它既撑大仓库，又会因为 clean filter 结果与 index 不一致而**永远显示为 modified**。

同目录下的 `scripts/check-ci-config.sh` 是它的兄弟守卫，负责保证各 workflow 不会把
已删除的 `push` 触发加回来，见 [使用说明.md](使用说明.md) 第 8 节。

## 新增了一类需要 LFS 的扩展名

```bash
git lfs track "*.psb"                  # 会自动往 .gitattributes 追加 filter=lfs 规则
git add .gitattributes "<你的文件>"
git commit -m "新增 psb 走 Git LFS"
```

注意：`.gitattributes` 里的 LFS 小节**必须排在所有 `binary` 宏之后**。
`binary` 展开为 `-text -diff -merge`，会覆盖掉 `diff=lfs`，而 gitattributes 是
**后出现的规则优先**。

## 如果某个文件已经被当成普通 blob 提交了

LFS 声明是在提交那一刻生效的，所以改完 `.gitattributes` 后，**已经提交过的文件不会自动转换**：

```bash
git add --renormalize -- "<路径>"       # 重新跑一次 clean filter，转成 LFS 指针
git commit -m "转为 Git LFS 存储"
```

这样只影响新提交，历史里的旧 blob **依然存在**，仓库总体积不会下降。若确实要回收历史体积，
需要 `git lfs migrate import --everything` 重写全部历史并 force-push，属于破坏性操作，
必须和相关同学协调后单独进行 —— 本仓库目前**没有**做这件事。

## 已知现状

- `Assets/Spotlight/Fonts/SimHei.ttf`（9.3 MB）仍是历史中最大的 blob，受限于"不重写历史"的决定，
  它会继续留在 `.git` 里；但它在**当前分支**上已经是 LFS 指针，后续修改都会走 LFS。
- 开启 LFS 后本地 `.git` 体积会短暂变大（旧 blob 与 LFS 对象并存），属于预期现象。
- `SimHei.ttf` 的**再分发授权**需要单独确认；字体入库的授权风险通常比体积更麻烦。
