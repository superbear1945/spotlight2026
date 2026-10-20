# CI 测试分层计划（按变更范围决定跑哪套测试）

> 状态：**已实施**（2026-10-20）。本文保留方案、决策与实施结果，实现以下面第 9 节为准。
> 目标：只改文档的 PR 只跑大文件/LFS 审查；只改一层的改动只跑覆盖那层的那套 Unity 测试
> （逻辑改动仍跑两套，见 Q1=D）；把当前"每个 PR 恒定 8 分钟 + 两次 Unity 授权席位"降下来。
>
> 已对齐的决策：
> - **Q1 = D**：文档层不跑 Unity；规则层 EditMode + PlayMode；表现层只 PlayMode；交叉层两套。
> - **Q2 = 口径一**：注释改动不特殊处理，按所在路径当作正常改动（`Core/**` 里改注释照样跑测试）。
> - **Q5 = 要显示"Skipped"**：因此采用 **job 级 `if` 真跳过**（不是"job 跑但步骤跳过"），
>   矩阵必须拆成两个字面名 job（3.2 节）。这也决定了跳过说明只能写在 `classify` job 的摘要里。
> - **Q3 = B**：`classify` job 额外在 PR 下发/更新一条 sticky 评论，把"本次跑了什么、跳过了什么、为什么"写在对话里。
> - **Q4 = 暂不加 `ci-full` 标签**：手动 `Run workflow` 已能强制两套都跑（无 PR 上下文 → 分类器默认两套），
>   区别见第 6 节（手动 run 不会把 PR 上那两个 Skipped 变成"已执行"，但也不需要）。

## 0. 先对齐一件事：PlayMode 不是"自动跑游玩流程验逻辑"

这是本方案最关键的认知差异，它决定了映射表怎么定。

| 套件 | 位置 | 实际测的东西 |
|---|---|---|
| EditMode（约 30 个文件） | `Assets/Spotlight/Tests/EditMode/` | **规则逻辑的唯一主力**：`GameSessionTests`、`BossPhaseTests`、`CardKillActionTests`、`CardDamageMarkTests`、`DeckSelectionTests`、`ConfigCompilerTests`、`DamageResolutionTests` …；另有配置往返一致性、视图契约（`BoardViewTests`、`UiPresentationTests` 直接加载 UXML/USS/prefab 查结构）与构建门禁 |
| PlayMode（10 个文件） | `Assets/Spotlight/Tests/PlayMode/` | **真实场景 + 帧循环下的表现与交互**：点击穿透（`CardsDoNotBlockClicks`）、拖放部署（`DirectDragHighlightsAndDeploysExactlyOnce`）、DOTween 期间的状态（`RestartDuringExplosionCancelsFutureStepsAndMarkers`）、HUD/UXML 显示（`MenuButtonSwapsBattleAndLegacyHud`）、棋盘位置跟随快照、携带选择面板、配置剪贴板 |

PlayMode 用例确实通过 `GameBootstrap.Submit(GameCommand)` 驱动真实对局（有集成价值），但它断言的对象是视图与布局：
`Assert.That(view.transform.parent, Is.EqualTo(_board.CellRect(unit.Position)))`、`Graphic.raycastTarget == false`、
`Label.text` 含某段文字、`resolvedStyle.display`。它是 **"界面有没有跟着规则动" 的回归套件**，
不是"规则算得对不对"的套件——后者在 EditMode。

所以最终映射按"哪层改了就跑覆盖那层的那套"来定；逻辑层额外保留 PlayMode（Q1=D），
用来兜住"规则改了、视图契约塌了"这类跨层回归。

## 1. 三个硬约束（决定了实现方式，不能绕过）

1. **不能用 workflow 级 `paths:` 过滤。** 必需检查是 `main` ruleset 里按字符串精确匹配的；workflow 因
   paths 过滤未触发时，对应检查会永久停在 **Pending**，PR 被锁死。官方文档原话：
   *"If a workflow is skipped due to path filtering, branch filtering or a commit message, then checks
   associated with that workflow will remain in a Pending state."*
   `diagrams-check.yml` 能用 workflow 级 paths 过滤，正是因为它**不是**必需检查。
2. **job 级跳过是安全的，而且会显示成 "Skipped"。** 官方文档：*"A job that is skipped will report its
   status as 'Success'. It will not prevent a pull request from merging, even if it is a required check."*
   在 PR 的 Checks 列表里该 job 显示为灰色 **Skipped**（社区还在提 issue 想把它藏起来，说明它确实是可见的），
   对 ruleset 而言等同通过。
3. **Unity 个人版授权是账号级席位。** 现在靠 `max-parallel: 1` + 顶层 `concurrency: unity-seat` 保证同一时刻
   只有一个 Unity 进程。改造后矩阵被拆开，串行必须换一种方式实现（3.2 节用 `needs` 链），否则会出现
   "0 free entitlements" 假失败（历史上发生过两次）。

补充：`jobs.<job_id>.if` **读不到 `matrix` 上下文**（只支持 `github` / `needs` / `vars` / `inputs`），
所以"保留矩阵 + job 级按 testMode 跳过"行不通——这正是必须拆矩阵的技术原因，不是为了好看。

## 2. 变更分类矩阵（最终版）

按 PR 的 diff 路径分四层；未识别的路径一律落到"两套都跑"（fail-safe）。

| 层 | 判定路径（相对仓库根） | EditMode | PlayMode | LFS |
|---|---|---|---|---|
| **文档层** | `docs/**`（含 `docs/diagrams/**`）、任意 `*.md`、`AGENTS.md`、`.gitattributes`、`.github/ISSUE_TEMPLATE/**`、`LICENSE` | 跳过 | 跳过 | ✅ |
| **规则层** | `Assets/Spotlight/Core/**`、`Assets/Spotlight/Generated/**`、`Assets/Spotlight/Config/**`（xlsx）、`Assets/Spotlight/Editor/**`、`Tests/EditMode/**` | ✅ | ✅（Q1=D） | ✅ |
| **表现层** | `Assets/Spotlight/Runtime/UI/**`、`Runtime/View/**`、`Runtime/Feedback/**`、`Runtime/Debug/**`、`Assets/Spotlight/UI/**`（uxml/uss/prefab）、`Assets/Spotlight/Scenes/**`、`Tests/PlayMode/**` | 跳过 | ✅ | ✅ |
| **交叉层** | `Runtime/Cards/**`、`Runtime/Config/**`、`Runtime/Game/**`、`Assets/Spotlight/Fonts/**`、任意 `*.meta`、任意 `*.asmdef`、`ProjectSettings/**`、`Packages/**`、`Assets/Plugins/**`、`.github/workflows/**`、`scripts/**`、**其余全部未识别路径** | ✅ | ✅ | ✅ |

分层理由（要写进脚本注释，避免以后有人"顺手把 docs 加进两边"）：

- `Runtime/Cards`、`Runtime/Config`、`Runtime/Game` 同时被规则快照与视图消费（`Card` 是视图，
  但 `ConfigLoader` / `GameBootstrap` 决定对局怎么起），一侧坏了另一侧测不出来，故两套都跑。
- `Fonts/**` 两边都有覆盖：EditMode 的 `CjkFontSetupTests` 查资产接线，PlayMode 查真实文本渲染。
- `*.meta`（改 GUID）能让 prefab/场景引用静默失效，必须两套都跑。
- 文档层的 `docs/diagrams/**` 只影响 `diagrams-check`（它自己有 paths 过滤），与 Unity 无关。
- **注释改动不特殊处理**（Q2 口径一）：`Core/GameSession.cs` 里只加一行 `//` 也算规则层，
  照样跑两套。理由见 4.3。

效果（按 workflow 现有注释"两套串行约 8 分钟"估算，单套约 4 分钟）：

| PR 类型 | 现在 | 改造后 |
|---|---|---|
| 只改文档 / AGENTS.md | 约 8 分钟 + 2 个席位 | **约 1 分钟 + 0 个席位**（只剩 classify + LFS 检查） |
| 只改 UI / UXML / 场景 | 约 8 分钟 + 2 个席位 | 约 5 分钟 + 1 个席位 |
| 只改规则 / 配置 / 编译器 | 约 8 分钟 + 2 个席位 | 约 8.5 分钟 + 2 个席位（多一个冒烟用例，见 5.1） |
| 规则 + UI 混合 | 约 8 分钟 | 约 8.5 分钟（不变，应该的） |

> 说明：Q1=D 的代价是"逻辑层 PR 不会变快，只多跑一个端到端冒烟用例"。省下来的主要是文档层与表现层 PR，
> 而这两类在提交节奏里频次不低（文档、UI 微调）。
> 如果以后想把逻辑层也压到一条，把 `playmode-tests` 的 `-testFilter` 改成只跑冒烟用例即可（一行改动，4.x）。

## 3. workflow 改造

### 3.1 新增分类 job（唯一的"变更判定"入口，永远运行）

```yaml
jobs:
  classify:
    # 本 job 永远运行，且是唯一能写"为什么跳过"的地方——被跳过的 job 没有步骤可执行，写不了摘要。
    name: 变更分类（决定跑哪些测试）
    runs-on: ubuntu-latest
    outputs:
      edit: ${{ steps.classify.outputs.edit }}
      play: ${{ steps.classify.outputs.play }}
      reason: ${{ steps.classify.outputs.reason }}
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0, lfs: false }   # 需要 base..head 全历史做 diff；不拉 LFS 实体
      - id: classify
        env:
          BASE: ${{ github.event.pull_request.base.sha }}
          HEAD: ${{ github.event.pull_request.head.sha }}
        run: bash scripts/classify-change-set.sh >> "$GITHUB_OUTPUT"
      - name: 写入运行摘要
        run: bash scripts/classify-change-set.sh --summary >> "$GITHUB_STEP_SUMMARY"
      - name: 在 PR 对话里贴/更新分类结论
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: bash scripts/classify-change-set.sh --comment || true   # 发不出评论（如 fork PR 只读令牌）也不许让检查变红
```

两处说明的区别：

- `$GITHUB_STEP_SUMMARY` = Actions 运行摘要（run 页面 → 该 job → Summary），只有点进 Actions 才看得到；
- `--comment` = 在 PR 对话里贴一条带隐藏标记 `<!-- spotlight-ci-classify -->` 的评论，
  下次运行按标记找到它并**原地更新**（不刷屏）。fork PR 的 `GITHUB_TOKEN` 是只读的，发帖会失败，
  因此这一步 `|| true`：评论是辅助信息，不能让它把 CI 弄红。

### 3.2 测试 job：拆成两个字面名 job（因为 Q5 要"真跳过"）

```yaml
  editmode-tests:
    # ⚠️ 本 job 名被 ruleset 的必需检查按字符串精确引用，改名必须同步改 ruleset，否则所有 PR 永久 pending。
    name: EditMode 测试（Unity 2022.3.62f3）
    needs: classify
    # 分类 job 失败/无输出时默认照跑（宁可多花 4 分钟，也不能"分类器坏了还显示通过"）
    if: always() && (needs.classify.result != 'success' || needs.classify.outputs.edit == 'true')
    ...

  playmode-tests:
    name: PlayMode 测试（Unity 2022.3.62f3）
    # needs 链同时承担两件事：等待分类结果 + 保证两套 Unity 不会同时启动抢授权席位
    needs: [classify, editmode-tests]
    if: always() && (needs.classify.result != 'success' || needs.classify.outputs.play == 'true')
    ...
```

要点：

- **串行靠 `needs` 链**，不再靠 `max-parallel: 1`（矩阵没了）。`if: always()` 保证 EditMode 失败或被跳过时
  PlayMode 仍会按条件执行（等价于原来的 `fail-fast: false`：两套都跑、都汇报）。
- 顶层 `concurrency: unity-seat` 保留（跨 PR 串行，占 1 个席位）；不再需要 job 级 concurrency。
- 每个 job 的步骤（检出 / 缓存 / `unity-test-runner` / 上传产物）都是原样搬过来，只把
  `matrix.unityVersion` → 字面量 `'2022.3.62f3'`、`matrix.testMode` → `EditMode` / `PlayMode`。
- **检查名完全不变**，ruleset 一行都不用改（这是本方案最重要的"零风险"点）。

### 3.3 不改的东西

- `permissions`、`cliVersion: v0.1.72`、`customParameters: -testFilter Spotlight.Tests`、
  `artifactsPath` / `checkName`、失败排查注释全部保留。
- `repo-hygiene.yml`（LFS 检查）始终跑，约 30 秒，不依赖 Unity。
- `diagrams-check.yml` 不动（它继续用 workflow 级 paths 过滤，因为它不是必需检查）。

## 4. 分类器脚本：`scripts/classify-change-set.sh`

### 4.1 接口

```
scripts/classify-change-set.sh                    # 用 GITHUB 事件里的 base/head，输出 GITHUB_OUTPUT 行
scripts/classify-change-set.sh --summary          # 输出人读的 Markdown 表格（喂 $GITHUB_STEP_SUMMARY）
scripts/classify-change-set.sh --base X --head Y  # 本地指定比较范围
scripts/classify-change-set.sh --files <文件>     # 直接喂文件列表，不碰 git（自检/本地调试用）
scripts/classify-change-set.sh --self-test        # 独立夹具断言，不读仓库
```

`GITHUB_OUTPUT` 输出：

```
edit=true|false
play=true|false
reason=仅文档改动（docs/**、AGENTS.md）
```

`--summary` 输出示例：

```markdown
### 本次变更分类：仅文档改动
| 套件 | 是否运行 | 原因 |
|---|---|---|
| EditMode | ⏭ 跳过 | 变更集全部落在文档层 |
| PlayMode | ⏭ 跳过 | 变更集全部落在文档层 |
| 大文件 / LFS | ✅ 运行 | 与本分类无关，始终执行 |
```

### 4.2 关键实现要求

- **diff 命令**：`git -c core.quotepath=false diff --name-only --no-renames "$BASE...$HEAD"`
  （`core.quotepath=false` 防止中文路径被转义；`--no-renames` 把重命名拆成删+增，两个路径都被分类）。
- **fail-safe**：任何异常（base sha 不存在、git 报错、空 diff、未知路径）→ `edit=true` `play=true`。
  脚本永远 `exit 0`，绝不因为分类器自己出错而让流水线"跳过测试还显示绿色"。
- **规则外部化**：路径规则集中在脚本顶部的一张表里，新增目录只改一处。
- **自检用例至少覆盖**：`docs/玩法.md` → 都不跑；`AGENTS.md` → 都不跑；`docs/diagrams/*.json` → 都不跑；
  `Core/GameSession.cs` → 两套；`Runtime/UI/HandView.cs` → 只 PlayMode；`Assets/Spotlight/UI/Spotlight.uxml`
  → 只 PlayMode；`Runtime/Cards/Card.cs`（交叉层）→ 两套；`Runtime/Game/GameBootstrap.cs` → 两套；
  `ProjectSettings/ProjectVersion.txt` → 两套；`Assets/Spotlight/UI/BoardCell.prefab.meta` → 两套；
  未知新目录 `Assets/Spotlight/NewThing/x.cs` → 两套；空列表 → 两套；
  "文档 + Core 混合" → 两套（任一文件命中就跑）。

### 4.3 注释改动为什么不做特殊判定（Q2 口径一）

分类器只拿到"改了哪些文件"，判不出"只改了注释"。要判就得做 C# 词法扫描（`/* */` 跨行、
`@"..."` 里的 `//` 会被误判成注释、`#if` 分支里的注释），**误判成"纯注释"= 静默漏测**，
而误判的代价完全不可见；相比之下多跑一次已缓存的 Unity 约 4 分钟。所以按你的选择：
注释改动 = 正常改动，按路径所在层跑对应套件。

## 5. 代码与守卫改动清单

### 5.1 新增 PlayMode 端到端冒烟用例（Q1=D 的交付物）

新增 `Assets/Spotlight/Tests/PlayMode/PlayFlowSmokePlayModeTests.cs`，一个 `[UnityTest]` 走完整一轮：

1. 加载真实场景 + 夹具配置（沿用现有 PlayMode 夹具写法：`GameBootstrap.RestartWith`）；
2. 部署 1 张资源卡 + 1 张战斗卡（含 `WaitForPresentation`）；
3. 移动 → 攻击一次 → 互换一次 → 撤走一次（`Withdraw`）；
4. 结束回合，等 Boss AI 回合跑完（含炸弹/刷怪触发路径不崩溃）；
5. 断言：快照关键不变量（卡牌总数守恒：抽牌堆 + 手牌 + 墓地恒定）、棋盘视图数量 == 快照单位数、
   输入闸门已解除、`LogAssert.NoUnexpectedReceived()`。

约束：**不重复 EditMode 的逻辑断言**（那是 `GameSessionTests` 的活），只验"连续流程能走完、视图不掉队"。

### 5.2 守卫：`scripts/check-ci-config.sh`

| 现有检查 | 改造 |
|---|---|
| `CheckUnityParallelism`（强制 `strategy.max-parallel: 1`） | **重写为 `CheckUnitySerialization`**：`playmode-tests` 的 `needs` 必须包含 `editmode-tests`（矩阵拆开后串行改由 needs 链保证）。自检用例 E–H 同步改写（缺 needs → 拦截；两个 Unity job 并列无依赖 → 拦截；有链 → 放行） |
| `CheckUnityConcurrency` | 保持不变（顶层 `unity-seat` + 禁止 `cancel-in-progress: true`） |
| 触发策略检查（必须有 PR/手动、不得有 push） | 保持不变 |
| — | **新增 `CheckNoPathFilter`**：`unity-tests.yml` 的 `on:` 块不得出现 `paths` / `paths-ignore`（否则必需检查永久 Pending）。自检补对应用例 |
| — | **新增分类器接线检查**：必须存在 `classify` job，且两个 Unity job 的 `needs` 都引用它；`if:` 必须包含 `always()` 兜底（防"分类 job 失败 → 静默绿"） |

### 5.3 其它文件

| 文件 | 改动 |
|---|---|
| `scripts/classify-change-set.sh` | 新增（含 `--self-test`、`--summary`、`--files`） |
| `.github/workflows/unity-tests.yml` | 3.1 + 3.2 的结构改造；顶部注释补"为什么必须 job 级跳过、为什么不能 paths 过滤、为什么矩阵要拆" |
| `.github/workflows/repo-hygiene.yml` | 增加一步 `bash scripts/classify-change-set.sh --self-test` |
| `AGENTS.md` §7 | 把"每次 PR 全量跑两套"改写成按层分工 + 跳过=Success 的机制 + fail-safe 默认 + 逃生口 |
| `docs/使用说明.md` | 若有 CI 说明，同步一句分层口径与强制全量的方式 |

## 6. 已确认的两个小选项（原 Q3/Q4）

### Q3 = B：分类结论同时进 PR 对话

`classify` 在 `$GITHUB_STEP_SUMMARY` 之外，再用带隐藏标记的 sticky 评论把结论贴进 PR 对话
（见 3.1）。实现要点：`pull-requests: write` 已有；评论失败一律 `|| true`。

### Q4 = 暂不加 `ci-full` 标签：手动 `workflow_dispatch` 已够

先说手动 run 会不会被分类跳过：**不会**。`workflow_dispatch` 没有 PR 的 base/head，
分类器按 fail-safe 规则默认 `edit=true` `play=true`，所以手动 run **永远是两套全量**。

那它和 `ci-full` 标签的差别是什么：

| | 手动 `Run workflow` | `ci-full` 标签 |
|---|---|---|
| 是否真的跑两套 | ✅ 是 | ✅ 是 |
| 检查挂在哪个 commit | 你选的分支的 **head** commit | PR 事件 → 挂在 **merge commit**（`refs/pull/N/merge`） |
| 会不会把 PR 上原本 `Skipped` 的那两个 job 变成"已执行" | ❌ 不会（那是另一次 run，不改写上次 run 的结论） | ✅ 会（重新触发 `pull_request`，两个 job 由 `if` 判定为执行） |
| 是否必要才能合并 | ❌ 不需要：`Skipped` 对 ruleset 就是通过 | ❌ 同上 |
| 额外成本 | 0 行 workflow 改动 | `on.pull_request.types` 要加 `labeled`，且**任何**标签都会起一次 run（里面 job 全 Skipped） |

结论：跳过本身就是"通过"，所以强制全量唯一的价值是"我想亲眼看到两套真跑一遍"，
而这用手动 run 就能拿到（只是证据挂在分支 head 上，而不是 PR 的 merge 提交）。
先按"不加标签"落地；真觉得不方便，再加那一行 `types` + `contains(labels.*.name, 'ci-full')` 即可，不牵动其它结构。

## 7. 实施顺序

> 下列 1–6 步已按序完成，实际落地的细节见第 9 节。

1. `scripts/classify-change-set.sh` + `--self-test`（先把判定逻辑和用例做完，本地可跑）。
2. `unity-tests.yml` 拆 job 接入 classify（含 `if: always()` 兜底）。
3. 新增 `PlayFlowSmokePlayModeTests.cs`。
4. `check-ci-config.sh` 重写串行守卫 + 新增 paths / 接线守卫，自检同步；`repo-hygiene.yml` 加分类器自检步骤。
5. 用一个 PR 验证四种典型：只改文档（两套 Skipped、0 席位）、只改 UI（只 PlayMode）、只改 Core（两套）、
   混合改动（两套）。
6. 更新 `AGENTS.md` §7 与 `docs/使用说明.md`，把本文状态改为"已实施"。

## 8. 风险清单

| 风险 | 兜底 |
|---|---|
| 分类器把"该跑的"判成"不用跑" | 未识别路径一律两套都跑；脚本异常也两套都跑；需要时手动 `workflow_dispatch` 强制两套全量 |
| fork PR 发不出分类评论 | 评论步骤 `|| true`；结论同时在 `$GITHUB_STEP_SUMMARY` 与 job 的 Skipped 状态里 |
| 分类 job 自己失败导致测试静默跳过 | 两个测试 job 的 `if` 都带 `always()` + `needs.classify.result != 'success'` 兜底 |
| 有人把 workflow 级 `paths:` 加回来 | `check-ci-config.sh` 新增守卫拦截（否则必需检查永久 Pending） |
| 拆矩阵后两套 Unity 并行抢席位 | `playmode-tests` 的 `needs` 必须含 `editmode-tests`，由守卫强制 |
| 拆矩阵时把 job 名写错（版本号从矩阵变量改成字面量） | 名字必须与 ruleset 完全一致；推送前本地跑 `bash scripts/check-ci-config.sh --check-ruleset`（需 gh 登录） |
| "Skipped 的检查是灰色"被误解为失败 | AGENTS.md 写明：灰色 Skipped = 本次不需要跑，等同通过；`classify` 摘要写明原因 |

## 9. 实施结果（2026-10-20）

### 9.1 落地文件

| 文件 | 改动 |
|---|---|
| `scripts/classify-change-set.sh` | 新增。四层分层 + `--files` / `--summary` / `--comment` / `--self-test`；23 条单文件用例 + 9 条聚合用例 + 原因文案用例；任何异常都返回 `edit=true play=true` |
| `.github/workflows/unity-tests.yml` | 矩阵拆成 `classify` / `editmode-tests` / `playmode-tests` 三个 job；两个测试 job 用 job 级 `if` 真跳过；串行改由 `needs: [classify, editmode-tests]` 保证 |
| `.github/workflows/repo-hygiene.yml` | 新增一步 `bash scripts/classify-change-set.sh --self-test`（与其它守卫脚本自检同驻） |
| `scripts/check-ci-config.sh` | `CheckUnityParallelism`（max-parallel）→ `CheckUnitySerialization`（needs 链）+ 新增 `CheckNoPathFilter`、`CheckClassificationWiring`；自检用例 E–H 重写，新增 W（未调用分类脚本）、X（出现 paths 过滤） |
| `Assets/Spotlight/Tests/PlayMode/PlayFlowSmokePlayModeTests.cs` | 新增端到端冒烟：拖拽部署 → 互换 → 移动 → 攻击 Boss 家 → 离场 → 结束回合（Boss AI 回合）→ 回到玩家，每步核对“棋盘卡牌 ⇄ 快照单位”与卡牌总数守恒 |
| `AGENTS.md` §7 / `docs/使用说明.md` §8 | 改成“按变更分层”的口径，并写明 Skipped 语义、为何不能用 workflow 级 `paths`、为何矩阵要拆、守卫新增了什么 |

### 9.2 与计划的差异

- job 键名从原矩阵的 `unity-tests` 改为 `editmode-tests` / `playmode-tests`；**job 名（name）保持与 ruleset 必需检查完全一致**，ruleset 未改动。
  本地已跑 `bash scripts/check-ci-config.sh --check-ruleset`，确认两条必需检查与 job 名仍精确匹配。
- `BASE`/`HEAD` 放在 `classify` 的 job 级 `env`（分类、摘要、评论三步共用同一份 diff，避免各算一份）。
- 分类器的读取端用 `while IFS= read -r p || [ -n "$p" ]`：`--files`/自检喂进来的清单若没有结尾换行，普通 `while read` 会静默丢掉最后一行。

### 9.3 验证程度

- 本地已跑：`classify-change-set.sh --self-test`（全部通过）、`check-ci-config.sh --self-test`（全部通过）、
  `check-ci-config.sh`（真实仓库 3 个 workflow 均合规）、`check-ci-config.sh --check-ruleset`（与远端规则集匹配）。
- 两个 workflow 文件已用 YAML 解析器验证可解析。
- **未本地跑 Unity**：EditMode/PlayMode 与新增冒烟用例的编译与执行由 PR 上的 GitHub Actions 验证
  （AGENTS.md §7：测试留在云端跑）。

### 9.4 后续可选

- 若以后想把“规则层”也压到只跑一套，把 `playmode-tests` 的 `-testFilter` 改成只跑冒烟用例即可（一行改动）。
- 若发现手动 `workflow_dispatch` 不方便，再按第 6 节 Q4 的方式加 `ci-full` 标签（需把 `on.pull_request.types` 扩成含 `labeled`）。
