# CI 手动打包发版（Workflow：`release-build.yml`）

状态：接线与守卫已完成；**首轮需在分支上 `dry_run` 手动 dispatch 验证能出包后，才把发版当作可用流程**。

## 1. 目标

在 GitHub Actions 页面点一次 `Run workflow`，就能：

1. 打包 Windows 桌面包与 Android APK；
2. 版本号 = **上一个 Release 的版本 +0.0.1**；
3. 把两个包发布成一个新的 GitHub Release。

不追求“每次合并自动发版”：发版不可回滚（tag + Release + 资产），且一次要跑两轮 Unity。

## 2. 为什么是现在这套接法

| 决策 | 原因 |
|---|---|
| `game-ci/unity-builder@v6.0.0` | v4/v5 的前置校验只认 `UNITY_LICENSE`(.ulf) 或 `UNITY_SERIAL`，个人版只能邮箱+密码认领席位，表现为 0.3 秒报 `Missing Unity License File and no Serial was found`（PR #29 曾因此整体移除夜间发版）。v6 起是 `game-ci/cli` 的薄封装，授权解析与 `unity-test-runner@v4.4.0` 同源，邮箱+密码可用。 |
| `cliVersion: v0.1.72` | 与测试流水线固定同一个 CLI 版本。`latest` 要先请求 GitHub Releases API 解析 tag，共享 runner IP 的匿名额度会被打成 403。 |
| `buildMethod: BuildEntry.BuildWindows / BuildAndroid` | 用工程自己的构建入口，“CI 出的包”与“本地菜单出的包”走同一条代码路径：按平台分目录、Android 强制 APK + ARMv7&ARM64 + 仅横屏并还原、配置门禁照跑。builder 的内置构建脚本做不到 Android ABI 覆盖，会把 ARM64 静默丢掉。 |
| 成功日志必须含 `Build succeeded!` | `buildMethod` 绕过了 game-ci 的默认构建脚本，而真正打印成功标记的就是那个脚本。CLI 在 Unity 以 0 退出后还会扫一遍 stdout（`src/model/unity/build-validation/unity-build-validation.ts`）：只认 `Build succeeded!` 或 `# Build results #` 段里的 `Errors: 0`，两者都没有就统一报 `There was an error building the project.`。所以 `BuildEntry.FormatCiSuccessLine` 在成功路径上必须带这个标记，并有 EditMode 用例钉住（否则只改一行日志文案就会重现首轮那种假失败）。 |
| 产物落在 `Build/windows` / `Build/android` | 这是 `BuildEntry` 的既定目录，workflow 自己归档（zip / cp / sha256），不依赖 builder 的默认输出目录。 |
| `versioning: Custom` + `version` / `androidVersionCode` | 这两个输入会变成 Unity 命令行的 `-buildVersion` / `-androidVersionCode`；`BuildVersionOverride` 读它们临时写进 `PlayerSettings`（Windows 侧 exe 版本、Android 侧 versionName/versionCode），构建后还原，工程设置不留 diff。 |
| 版本运算只在 `scripts/next-release-version.sh` | tag、包内 `bundleVersion`、Android `versionCode`（`X*10000 + Y*100 + Z`）三处必须同源；散落在 workflow 里的 `sed/awk` 一定会分叉。 |
| `concurrency.group: unity-seat` | 个人版授权是账号级席位，发版与测试必须排队；不设 `cancel-in-progress`（中途杀 Unity 会泄漏席位，且会留下“tag 建了资产没传完”的半成品）。 |
| 只允许 `workflow_dispatch` | 一次提交就发一个 Release 是不可接受的副作用。守卫在 `scripts/check-ci-config.sh`（`release-*.yml` 走与测试 workflow **相反**的规则）。 |

## 3. 守卫（`scripts/check-ci-config.sh`）

`release-*.yml` 必须满足，否则 PR 上的 `大文件与 LFS 一致性检查` 直接红：

1. 只有 `workflow_dispatch`，不得出现 `pull_request` / `push`；
2. 必须声明 `permissions.contents: write`（缺了会在最后一步 403，白烧半小时）；
3. 必须调用 `scripts/next-release-version.sh`；
4. `concurrency.group` 必须是仓库级固定 `unity-seat`，且不得 `cancel-in-progress: true`；
5. 有打包/归档动作时必须保留删除 `*_BurstDebugInformation_DoNotShip` 的兜底行。

对应自检用例：`check-ci-config.sh --self-test` 的 Y–AE；版本递推用例：`next-release-version.sh --self-test`（在 `repo-hygiene.yml` 里每次 PR 都跑）。

## 4. 版本规则

| 项 | 计算 |
|---|---|
| 基线 | 全部已发布 Release 里版本号最大的那个（按版本排序，不按创建时间，避免补发旧版本把版本倒回去）；没有 Release 时为 `0.0.0` |
| 新版本 | 默认 `patch + 1`，即 +0.0.1（`v0.2.0` → `v0.2.1`）；`minor` / `major` 仅在下拉里显式选择时使用 |
| tag | `v<版本>` |
| 包内版本 | `PlayerSettings.bundleVersion = <版本>` |
| Android versionCode | `major*10000 + minor*100 + patch`；`minor`/`patch` 超过 99 会直接报错（会与相邻版本撞号，撞号后无法覆盖安装） |

## 5. 首轮验证清单

1. 在分支上 `Run workflow`，`dry_run = true`：确认两个平台的包都能出、`check-build-hygiene.sh` 通过、产物可从运行页下载。
2. 在真机上确认包内版本与计算出的版本一致（Windows：文件属性/`Application.version`；Android：`aapt dump badging` 或安装页）。
3. 再跑一次 `dry_run = false`，确认 Release 资产与 tag 正确；随后本地 `gh release view <tag>` 复核。
4. 若要撤销：`gh release delete <tag> --cleanup-tag --yes`。

## 6. 已知风险

- **game-ci 的 stdout 契约**：除 `Build succeeded!` 以外的任何“成功”字样它都不认；改 `BuildEntry` 的成功日志时必须保留英文标记（`BuildEntryTests.FormatCiSuccessLine_CarriesGameCiSuccessMarker` 会挡）。
- **首轮实跑记录（2026-10-20）**：Windows 包实际已产出（`Build/windows/Spotlight.exe`，572.8 MB，97.5 秒），但 CLI 因缺成功标记把整步判成失败 → 已修为打印 `Build succeeded!`；Android 那一步还没真正跑到过。
- **Unity 席位排队**：发版与 PR 测试共用 `unity-seat`，发版期间 PR 测试会排队（反之亦然）。
- **耗时**：Android IL2CPP 首次构建可能 20 分钟以上；`Library` 缓存键与测试流水线共用，能省掉导入时间但不能省 IL2CPP 链接时间。
- **Android 签名**：仍用 Unity 调试签名，只能侧载试玩，不能上架；要上架需要接 `androidKeystoreBase64` 等输入。
- **包体体积**：Release 单资产上限 2 GB；当前体量远低于该限制，若将来超限需改分卷或改用 artifact + Release 说明。
