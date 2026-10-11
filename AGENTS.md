# 项目规范

## 0. 项目概述

Spotlight 是一款基于卡牌的战棋对局游戏（首版为 Windows 桌面可玩验证版）。

- 棋盘默认 5 行 × 10 列，双方各有“家”，摧毁敌方家即获胜。
- 卡牌分资源卡 / 攻击卡 / 特殊卡，可部署、移动、攻击、互换、覆盖升级；星门提供四邻部署扩展。
- Boss 默认由 AI 控制，也可切换为玩家手动模式。
- 所有数值来自 Excel 编译生成的**只读配置资产**，规则代码不按具体卡名分支。
- 详细规则见 `docs/玩法.md`，实现设计见 `docs/最初版本实现方案.md`，运行步骤见 `docs/使用说明.md`。
- 机制与身份的统一命名见 `docs/游戏内名词.md`：抽牌堆 / 手牌 / 墓地 / 卡牌记录 / 战斗单位等概念一律以该文档为准，新增文档与代码不得另造同义名称。

## 1. 技术栈

| 项目 | 版本 / 选型 | 说明 |
|---|---|---|
| Unity | 2022.3.62f3c1（见 `ProjectSettings/ProjectVersion.txt`） | 固定版本，不得擅自升级 |
| 语言 | C#（Unity 2022.3 对应语言版本） | 不使用 Core 之外的引擎依赖完成规则逻辑 |
| 渲染 | URP 2D（`com.unity.render-pipelines.universal` 14.0.12） | 工程已配置 URP 全局设置 |
| UI | UI Toolkit / UXML + USS 1.0.0（静态面板）；UGUI 1.0.0 + TextMeshPro 3.0.7（棋盘与手牌） | 两套 UI 分工不可混用 |
| 测试 | Unity Test Framework 1.1.33（NUnit） | EditMode + PlayMode |
| 动画反馈 | DOTween Free 1.2.765（`Assets/Plugins/Demigiant/DOTween/`） | 仅做表现，不参与结算 |
| Excel 读取 | ExcelDataReader 3.9.0（`Assets/Plugins/ExcelDataReader/Editor/`） | **仅编辑器**，正式运行包不包含 |
| 目标平台 | Windows | 首版只交付 Windows 桌面包 |

约束：

- 以上第三方依赖均以本地副本随工程提交，不通过包管理器安装；升级版本需同步更新本文件与 `docs/使用说明.md`。
- 规则内核（`Spotlight.Core`）必须 `noEngineReferences: true`，不得引用 `UnityEngine`。
- 汉字显示依赖生成的字体资产与 PanelSettings，新增界面文本前先确认字体资产已生成。

## 2. 命名空间与程序集

| 程序集 | 目录 | 命名空间 | 引用 |
|---|---|---|---|
| `Spotlight.Core` | `Assets/Spotlight/Core/` | `Spotlight` | 不引用引擎（`noEngineReferences`） |
| `Spotlight.Runtime` | `Assets/Spotlight/Runtime/` | `Spotlight` | `Spotlight.Core`、TMP、uGUI |
| `Spotlight.Editor` | `Assets/Spotlight/Editor/` | `Spotlight.EditorTools` | `Spotlight.Core`、`Spotlight.Runtime`、TMP（仅编辑器） |
| `Spotlight.Tests.EditMode` | `Assets/Spotlight/Tests/EditMode/` | `Spotlight.Tests` | `Spotlight.Core`、`Spotlight.Runtime`、`Spotlight.Editor` |
| `Spotlight.Tests.PlayMode` | `Assets/Spotlight/Tests/PlayMode/` | `Spotlight.Tests` | `Spotlight.Core`、`Spotlight.Runtime` |

规则：

- 命名空间根固定为 `Spotlight`；编辑器工具统一放在 `Spotlight.EditorTools`；测试统一放在 `Spotlight.Tests`。
- 依赖方向单向：`Tests → Editor → Runtime → Core`。运行层不得引用编辑器或测试程序集；`Core` 不得引用任何上层。
- 新增代码必须放进对应程序集目录，并在需要时更新 `.asmdef` 引用；不得用 `#if UNITY_EDITOR` 绕过程序集边界来共享规则逻辑。
- 自定义继承链不超过三层（不计 Unity 框架继承）；当前只有单一 `Card : MonoBehaviour`，**不为具体卡种建立子类**。

## 3. 目录与产物约定

```
Assets/Spotlight/
├── Core/      规则内核：配置契约与校验、对局状态、GameSession、Boss AI
├── Runtime/   运行层：Card 与组件、配置 SO 与加载、UI 视图、动效、调试面板
├── Editor/    编辑器工具：Excel 读取/校验/编译、资产发布、外观模板、字体、菜单、构建门禁
├── Tests/     测试：EditMode / PlayMode
└── Generated/ 编译产物（只读，禁止手工修改）
```

生成产物（只读，禁止手工编辑；需要修改请改 Excel 后重新编译）：

- `Generated/Cards/<id>.asset`：卡种定义 SO，三个组件配置作为子资产保存（`<id>_Combat` / `_Resource` / `_Teleport`）。
- `Generated/Cards/<id>.prefab`：卡种 Prefab，只保存组件组合与 SO 引用，不重复存储数值。
- `Generated/Resources/SpotlightConfig.asset`：主配置资产，运行时通过 `Resources/SpotlightConfig` 加载。
- `Generated/ConfigManifest.asset`：编译清单，记录源工作簿路径、SHA256 摘要、编译时间与全部产出路径。

约束：

- 外观模板（`Runtime/View/CardTemplate.prefab`）与生成资产分离，可手工替换外观而不影响重新编译。
- `docs/diagrams/` 下 **JSON 是图源**，SVG/PNG/HTML/drawio 是导出结果；改图先改 `.json` 再重新渲染，禁止手改导出文件。

## 4. 核心类型与接口

### 4.1 规则内核（`Spotlight.Core`）

配置契约（`Core/Configuration.cs`）：

- `Side`：`Player` / `Boss`；`CardCategory`：`Resource` / `Attack` / `Special`。
- 只读配置接口：`ICombatConfig` / `IResourceConfig` / `ITeleportConfig` —— **规则内核依赖接口，不依赖 ScriptableObject**。
- 表行类型：`CardRow` / `DeckRow` / `RulesRow` / `HomeRow` / `BossSlotRow` / `PresentationRow`。
- 配置聚合：`ConfigDocument`（Excel 中间态）→ `GameConfiguration`（运行态，含 `MatchConfig` / `PresentationConfig` / `CardArchetype` / `DeckEntry` / `SpawnSlot`）。
- `ConfigValidation.Validate(ConfigDocument)`：唯一业务校验入口，返回全部错误列表，不抛异常。

对局状态与命令（`Core/GameState.cs`）：

- `Cell`：整数坐标值类型（第 0 行在底部），仅做曼哈顿距离；不用 `Vector2Int`。
- 三套身份：`CardRecord`（抽牌堆/手牌/墓地中的卡牌记录）、`UnitState`（在场的可变单位）、`HomeState`（独立的家）。**三者身份不可混用**；家 ID 为负数（玩家 -1、Boss -2），单位 ID 为正数。
- `UnitState` 只保存来源卡牌的值副本（`SourceCardId` / `TypeId` / `Owner`），**不引用 `CardRecord`**。玩家卡在成功部署时进入墓地，单位阵亡或被覆盖升级不再重复回收，因此“抽牌堆 + 手牌 + 墓地”的卡牌总数恒定，同一张卡可以反复部署出多个互相独立的单位。
- 名词与机制的完整定义见 `docs/游戏内名词.md`。
- `GameCommand`：不可变操作请求，含 `CommandKind`（Deploy / BossDeploy / Move / MoveHome / Attack / Swap / Discard / EndTurn）、`Actor`、`SubjectId`、`Target`、`TargetId`、`SkipDeployment`。**Target 视为不可信输入，执行时必须复验**。
- `Selection` / `SelectionKind`：界面选择上下文，显式区分手牌与单位身份。
- `LegalActions`：规则层算出的合法目标（`Deploy` / `Move` / `Attack` / `Swap`）。**UI 不得自行推导合法性，高亮仅提示**。
- `GameEvent`：已发生的领域事件（`Kind` / `Message` / `SubjectId`），仅用于日志与表现，**动画不得回写结算结果**。
- `CommandResult`：成功状态、失败原因与有序事件；失败时状态未被修改。
- `GameSnapshot`：独立只读快照（回合、行动方、阶段、资源、手牌/牌堆/墓地、单位与家副本、兵位状态、日志），持有旧快照不会受后续命令影响。
- `IRandomSource` / `SeededRandom`：洗牌随机源，固定种子保证测试可复现，避免依赖 `UnityEngine.Random` 全局状态。

对局内核（`Core/GameSession.cs`）—— 玩家与 AI 共用同一入口：

- `Execute(GameCommand)`：部署、移动、攻击、互换、弃牌、结束回合。
- `GetLegalActions(Selection)`：提供合法目标与高亮依据。
- `GetSnapshot()`：提供 UI、日志与调试快照。
- `SetInputBlocked(bool)`：规则层输入闸门（配置面板打开时启用）。

### 4.2 运行层（`Spotlight.Runtime`）

- 卡牌视图：`Card : MonoBehaviour`，配合 `CombatComponent` / `ResourceComponent` / `TeleportComponent` 三个能力组件；**同一个 `Card` 承载所有卡种，不为卡种建子类**。
- 配置资产：`CardDefinitionSO` / `CombatConfigSO` / `ResourceConfigSO` / `TeleportConfigSO` / `SpotlightConfigAsset` / `ConfigManifest`，均实现只读接口；`GeneratedConfigAttribute` 标记生成产物。
- 加载入口：`ConfigLoader`（`DefaultResourcePath = "SpotlightConfig"`，`TryCreateConfiguration(...)` 失败返回错误文本，不抛异常）。
- 视图：`BoardView` / `BoardCellView`（UGUI 棋盘）、`HandView`（UGUI 手牌）、`HudView`（UI Toolkit / UXML）、`CardFactory` / `CardText`。
- 引导与支持：`GameBootstrap`（加载配置 → 创建对局 → 绑定视图）、`DebugSupport.IsDebugAvailable`（调试能力唯一判定点）、`CardFeedback`（DOTween 表现）。
- 调试面板（仅编辑器与 Development Build）：`ConfigDebugPanel` / `ConfigFieldBuilder` / `DebugConfigDraft`（含 `LegacyJsonMigration` / `JsonShapeValidator`），**调试改动只存在于本次运行会话**。

表现层硬性约定：

- 棋盘上的卡牌一律 `SetRaycastTarget(false)`，点击必须穿透到格子；手牌保持可点。
- 规则快照变化后，界面位置必须跟随，不得把棋子留在旧格（DOTween 需 `SetTarget` + `OnComplete/OnKill`，`Kill` 用 `DOKill(true)`）。
- `Card.OnDestroy` 只停止自身补间（不补完），避免访问已销毁的 `RectTransform`。

### 4.3 编辑器工具（`Spotlight.EditorTools`）

- Excel 管线：`WorkbookReader` / `XlsxInspector`（读取与原始标记扫描）、`TableSchema` / `TableMapper`（表结构与行映射）、`ConfigIssue` / `CompileReport`（问题与报告）、`ConfigCompiler`（校验 + 编译）、`ConfigAssetPublisher`（发布 + 回滚）、`DefaultWorkbookWriter`（默认工作簿）。
- 资产与模板：`ViewTemplateBuilder`（外观模板与 Prefab）、`CjkFontSetup`（中文字体资产）。
- 入口与门禁：`SpotlightMenu`（`Tools/Spotlight/*` 菜单）、`ConfigBuildGate` + `ConfigBuildProcessor`（`IPreprocessBuildWithReport` + Play 前检查）。

## 5. 关键名词表

> 完整机制名词（卡组、抽牌堆、手牌、墓地、卡牌记录、战斗单位等）以 `docs/游戏内名词.md` 为唯一来源；下表只列代码类型与高频术语。

| 名词 | 含义 |
|---|---|
| 卡牌记录 `CardRecord` | 仅含实例 ID / 卡种 ID / 阵营，在抽牌堆、手牌、墓地间流转，是唯一会被洗回和再次抽到的身份 |
| 战斗单位 `UnitState` | 一次部署产生的可变实体，只保存来源卡的值副本（`SourceCardId` / `TypeId` / `Owner`），不引用 `CardRecord`；同一张卡可反复部署出多个互相独立的单位，伤势不回流卡牌 |
| 家 `HomeState` | 独立实体，不进入抽牌堆、不参与互换、无攻击力，可被攻击与移动 |
| 维持费 upkeep | 单位每回合的持续消耗；不足时本回合不能行动 |
| 产出 produce | 家或资源卡每回合提供的资源 |
| 召唤失调 | “刚上场不能行动”，由规则开关控制 |
| 覆盖升级 | `requiresUpgrade` + `upgradeFrom`，资源卡只能覆盖指定卡种，按卡种 ID 关联，改名不失效 |
| 星门 | 特殊卡，`deployHomeOnly` + `avoidAdjacentSameType`，为相邻格提供战斗卡部署扩展 |
| 兵位 / 补兵 | `BossSlotRow` / `BossReplacement`，Boss 三个固定兵位独立计时，阵亡后按 `bossRespawnDelay` 补回 |
| 弃牌阶段 | `MatchPhase.Discard`，手牌超过上限时必须弃至上限才能进入对方回合 |
| 快照 `GameSnapshot` | 规则状态的只读副本，UI/日志/调试统一数据源 |
| 生成资产 | `Generated/` 下的只读 SO 与 Prefab，禁止手工修改 |

## 6. 关键操作与工作流

Excel 配置编译（菜单 `Tools/Spotlight/*`，按顺序）：

1. `生成默认工作簿` → 2. `验证配置`（只校验不写资产，失败一次性列出全部问题）→ 3. `编译全部配置`（校验通过才发布，失败保留上一套产物并回滚）→ 4. `生成中文字体资产` → 5. `生成演示场景`。

- `编译全部配置` 采用稳定 ID 更新已有资产并保留 GUID；改名不破坏关联。
- 运行与构建门禁由 `ConfigBuildGate` 拦截：配置缺失或非法时禁止进入 Play / 构建，并给出可读错误。
- 调试配置面板仅通过“关闭”或成功“应用并重开”退出；“应用并重开”失败时保留原对局与面板。
- 正式数据单元格禁止公式；编译器检查未定义列、重复表头、缺失列、公式、错误单元格、重复 ID、空/重名、非法分类、非法引用等。
- 架构图（`docs/diagrams/01-modules` / `02-cards` / `03-dataflow` / `04-turnflow` / `05-card-lifecycle`）修改后需按 `docs/diagrams/README.md` 重新渲染 SVG 及其导出产物（PNG/HTML/drawio），并由 CI 校验。

## 7. 测试与 CI 规范

- 每个功能与修复必须**同时提交对应测试用例**，否则不予合并。
- EditMode 测试放 `Assets/Spotlight/Tests/EditMode/`，用于规则内核、配置编译器、视图契约；PlayMode 测试放 `Assets/Spotlight/Tests/PlayMode/`，用于真实帧循环下的渲染、点击命中与 DOTween 动画。
- 测试通过公开命令（`Execute` / `GetLegalActions` / `GetSnapshot`）推进对局，**不得直接篡改 `GameSession` 私有状态**；测试内数值属于独立夹具，不是正式默认值。
- 洗牌类逻辑使用 `SeededRandom` 固定种子，保证测试可复现。
- GitHub Actions（**只在 PR 上跑**，不在 main 的 `push` 上重复跑，理由见下一条）：
  - `.github/workflows/unity-tests.yml`：`game-ci/unity-test-runner`，EditMode + PlayMode 矩阵。
  - `.github/workflows/repo-hygiene.yml`：大文件与 Git-LFS 一致性检查（`scripts/check-repo-hygiene.sh`）与 CI 触发配置守卫（`scripts/check-ci-config.sh`），大二进制必须以 LFS 指针入库。
  - `.github/workflows/diagrams-check.yml`：校验 `docs/diagrams` 图源 JSON 仍能生成结构完整的 drawio。
- `main` 由分支规则集「main」保护：必须走 PR、禁 force push、禁删除、`bypass_actors` 为空，且 `strict_required_status_checks_policy` 开启。因此 PR 阶段被测的结果就是合并后落到 main 的结果，`push` 触发只会重复跑（既费时间又额外占用 Unity 授权席位）；需要临时在 main 上验证时用 `workflow_dispatch`。
- ⚠️ ruleset 里的必需检查名与 workflow 的 **job 名 / 矩阵变量** 是字符串精确匹配（如 `EditMode 测试（Unity 2022.3.62f1）`）。改动 job 名或矩阵里的 `unityVersion` 时必须同步修改 ruleset，否则所有 PR 会**永久 pending 且没有任何报错提示**。

## 8. 编码规范

### 1. 每次生成新的函数和类时，需要详细注释，说明这个类/函数的作用，输入输出是什么，为什么需要这个类/函数。
### 2. 私有字段一律使用"\_var"的命名方式，添加 "\_" 前缀
### 3. 普通变量才用小驼峰，函数名和类名使用大驼峰命名方式
### 4. 开发后需要一并留下对应的测试用例,使用的测试平台为github action来做pr测试

## 9. 网络与环境问题处理

**遇到网络不通（连接超时、DNS 解析失败、证书错误、代理拒绝等），必须立刻停下来告诉用户，不得自行绕行。**

为什么禁止绕行：绕行会把“环境坏了”这个事实藏起来，把一次可以当场解决的网络问题变成别人以后要排查的怪现象；而且绕行往往要改本地配置或替换操作通道，用户不知道改过什么、也不知道怎么撤销。判断“该开代理还是该换网络”是用户的决定，不是执行者可以替他做的。

明确禁止的做法（下面每一条都是在本项目真实发生过或差点发生的）：

- **关掉校验让流程过关**：如 `git config lfs.<url>.locksverify false`、`filter.lfs.required false`、注释掉失败的网络相关步骤或校验脚本。
- **偷偷改网络路径**：为某条命令临时加 `-c http.proxy=...`、改 `http.proxy`、换镜像源或第三方中转。
- **换一个通道替原操作**：例如用 REST API 提交代替 `git push`，绕过真正失败的那一步。
- **靠反复重试蒙过去**，或换个写法反复试到通过为止。

正确做法：

1. 立即停手，不要反复重试或换法子试。
2. 一次把三件事情说清楚：**跑了什么命令**、**报什么错**（原文，带域名/端口/退出码）、**涉及哪个服务**（如 `github.com:443`、`hub.docker.com`）。
3. 由用户决定下一步。若用户明确要求走代理，只在**当次命令**里带临时参数，不写进任何持久配置。
4. 如果确实动了本地配置（无论多小），当次就说清楚“改了什么、怎么撤销”，并在用户确认后撤销。
