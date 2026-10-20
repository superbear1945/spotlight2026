# 项目规范

## 0. 项目概述

Spotlight 是一款基于卡牌的战棋对局游戏（首版为 Windows 桌面可玩验证版，同时产出 Android 安装包）。

- 棋盘默认 5 行 × 10 列，双方各有“家”，摧毁敌方家即获胜。
- 卡牌分资源卡 / 攻击卡 / 特殊卡，可部署、移动、攻击、互换、覆盖升级；星门提供四邻部署扩展。
- 抽牌堆由 `Rules` 的抽牌堆张数 + 三阶级比例生成，携带哪些卡种由玩家每次启动时的“携带选择（选卡）”决定；工作簿不再有 `Deck` 表。
- Boss 默认由 AI 控制，也可切换为玩家手动模式。
- 所有数值来自 Excel 编译生成的**只读配置资产**，规则代码不按具体卡名分支。
- 详细规则见 `docs/玩法.md`，实现设计见 `docs/最初版本实现方案.md`，运行步骤见 `docs/使用说明.md`。
- 机制与身份的统一命名见 `docs/游戏内名词.md`：抽牌堆 / 手牌 / 墓地 / 卡牌记录 / 战斗单位等概念一律以该文档为准，新增文档与代码不得另造同义名称。

## 1. 技术栈

| 项目 | 版本 / 选型 | 说明 |
|---|---|---|
| Unity | 2022.3.62f3（见 `ProjectSettings/ProjectVersion.txt`） | 固定版本，不得擅自升级 |
| 语言 | C#（Unity 2022.3 对应语言版本） | 不使用 Core 之外的引擎依赖完成规则逻辑 |
| 渲染 | URP 2D（`com.unity.render-pipelines.universal` 14.0.12） | 工程已配置 URP 全局设置 |
| UI | UI Toolkit / UXML + USS 1.0.0（静态面板）；UGUI 1.0.0 + TextMeshPro 3.0.7（棋盘与手牌） | 两套 UI 分工不可混用 |
| 测试 | Unity Test Framework 1.1.33（NUnit） | EditMode + PlayMode |
| 动画反馈 | DOTween Free 1.2.765（`Assets/Plugins/Demigiant/DOTween/`） | 仅做表现，不参与结算 |
| Excel 读取 | ExcelDataReader 3.9.0（`Assets/Plugins/ExcelDataReader/Editor/`） | **仅编辑器**，正式运行包不包含 |
| 目标平台 | Windows（桌面）+ Android（手机，APK） | `Build/windows/` 与 `Build/android/` 两个产物目录；Android 固定 IL2CPP、APK 侧载 |

约束：

- 以上第三方依赖均以本地副本随工程提交，不通过包管理器安装；升级版本需同步更新本文件与 `docs/使用说明.md`。
- 规则内核（`Spotlight.Core`）必须 `noEngineReferences: true`，不得引用 `UnityEngine`。
- 汉字显示依赖生成的字体资产与 PanelSettings，新增界面文本前先确认字体资产已生成。
- 工作簿固定 5 张表（`Cards` / `Rules` / `Homes` / `BossSlots` / `Presentation`）。`Deck` 工作表已废除：抽牌堆改由 `Rules` 的 `drawPileSize` + 三阶级比例生成，携带卡种由玩家开局时选择；工作簿里残留 `Deck` 表会被读取阶段直接报错并给出删除提示。

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

- `Generated/Cards/<id>.asset`：卡种定义 SO，能力组件配置作为子资产保存（`<id>_Combat` / `_Resource` / `_Teleport` / `_DeploymentPenalty` / `_MarkOnEnter` / `_DeathGrowth` / `_Instant` / `_Spawn` / `_AdjacentClearance` / `_KillAction` / `_DamageMark`，未启用的能力不生成子资产）。
- `Generated/Cards/<id>.prefab`：卡种 Prefab，只保存组件组合与 SO 引用，不重复存储数值。
- `Generated/Resources/SpotlightConfig.asset`：主配置资产，运行时通过 `Resources/SpotlightConfig` 加载。**不存源工作簿路径/摘要/编译时间**（它会被打进正式包），卡种资产同理。
- `Generated/ConfigManifest.asset`：编译清单，记录源工作簿路径、SHA256 摘要、编译时间与全部产出路径；不在 `Resources/` 下、运行时不被引用，因此只存在于编辑器侧。

约束：

- 外观模板（`Runtime/View/CardTemplate.prefab`）与生成资产分离，可手工替换外观而不影响重新编译。
- `docs/diagrams/` 下 **JSON 是图源**，SVG/PNG/HTML/drawio 是导出结果；改图先改 `.json` 再重新渲染，禁止手改导出文件。

## 4. 核心类型与接口

### 4.1 规则内核（`Spotlight.Core`）

配置契约（`Core/Configuration.cs`）：

- `Side`：`Player` / `Boss`；`CardCategory`：`Resource` / `Attack` / `Special`。
- `CardTier`：独立卡牌阶级 `Basic`（初级）/ `Intermediate`（中级）/ `Advanced`（高级）；`None` 为 Boss 专用不适用状态，`Invalid` 仅用于报告缺失或非法配置。`Cards.tier` 必填，通过 `CardTiers.Parse` 严格转换；`none` 卡种不出现在携带选择里，因此也不能进入抽牌堆。阶级按张数统计（3 张同种初级卡计作 3 张初级卡）；每局能携带几个**卡种**由 `Rules` 的 `maxTypes*` 控制，不引入卡组包分类，也不按阶级限制部署回合。
- 只读配置接口：`ICombatConfig` / `IResourceConfig` / `ITeleportConfig` / `IDeploymentPenaltyConfig` / `IMarkOnEnterConfig` / `IDeathGrowthConfig` / `IInstantEffectConfig` / `ISpawnConfig` / `IAdjacentClearanceConfig` / `IKillActionConfig` / `IDamageMarkConfig` —— **规则内核依赖接口，不依赖 ScriptableObject**。后三者分别是“四邻空格限制”“击杀恢复行动”（空标记接口，非空即启用）与“伤害标记”。
- 表行类型：`CardRow` / `RulesRow` / `HomeRow` / `BossSlotRow` / `PresentationRow`（`DeckRow` 与 `Deck` 工作表已废除）。
- `RulesRow` 包含抽牌堆规则的 7 列：`drawPileSize`（抽牌堆总张数，默认 24，≥ 3）、`tierRatioBasic` / `tierRatioIntermediate` / `tierRatioAdvanced`（三阶级比例，默认 70 / 20 / 10，各自 ≥ 1 且合计必须为 100，不支持 0%）、`maxTypesBasic` / `maxTypesIntermediate` / `maxTypesAdvanced`（每局最多携带的三阶级**卡种数量**，默认 5 / 3 / 2，各自 ≥ 1）。
- 配置聚合：`ConfigDocument`（Excel 中间态，`schemaVersion = 2`）→ `GameConfiguration`（运行态，含 `MatchConfig` / `DeckConfig` / `PresentationConfig` / `CardArchetype` / `CardOrder`（按 `Cards` 表顺序的卡种清单）/ `Deck`（由选卡结果生成的最终抽牌堆清单，元素为 `DeckEntry`）/ `SpawnSlot`）。
- `CardArchetype` 新增只读属性 `AdjacentClearance` / `KillAction` / `DamageMark`（未启用为 null）；Excel、SO 与调试 JSON 三条路径都必须往返一致。
- 抽牌堆构成（`Core/DeckSelection.cs`，纯 C#，可 EditMode 直测）：
  - `DeckConfig`：`Rules` 抽牌堆 7 列的张数 / 三比例 / 三上限，并给出 `EffectiveMaxTypes(tier, poolTypeCount) = min(配置上限, 该阶级卡种数)`。
  - `DeckComposition`：构图**唯一实现** —— ①每阶级 `floor(张数 × 比例 / 100)`；②每阶级至少 1 张；③合计不足时缺额全部给初级，超出（只可能由第 ② 步抬升造成）时从张数最多的阶级扣、平手按高级→中级→初级、每阶级保底 1 张；④把每阶级张数均分给该阶级已选卡种，余数按 `Cards` 表顺序发给靠前卡种，因此每个被选卡种至少 1 张。配置校验、选卡预览与正式开局共用它，界面数字不会与真实抽牌堆分叉。
  - `DeckSelectionState`：开局前的携带选择状态（已选卡种、每阶级计数与有效上限、实时张数预览、默认选卡回退），只存在于本次运行会话，不做持久化。
- 可携带卡种池不做静态配置：池子 = `Cards` 表里 `tier != none` 的全部卡种；每阶级至少选 1 种、最多选到有效上限。
- `ConfigValidation.Validate(ConfigDocument)`：唯一业务校验入口，返回全部错误列表，不抛异常。新能力同样在此校验：空格阈值必须在 0～4（关闭时为 0、启用时为 1～4），击杀恢复与伤害标记只允许攻击类卡启用，伤害标记关闭时每层数值必须为 0、启用时为正整数；`GameConfiguration.ValidateComposition` 对 SO 组装路径执行同等检查。抽牌堆规则由 `ValidateDeckRules` 共用校验：三比例各自 ≥ 1 且合计 100、每个阶级至少有一个可携带卡种，且按比例算出的每阶级张数不少于该阶级有效携带上限（保证玩家勾满时每个被选卡种都能抽到）。

对局状态与命令（`Core/GameState.cs`）：

- `Cell`：整数坐标值类型（第 0 行在底部），仅做曼哈顿距离；不用 `Vector2Int`。
- 三套身份：`CardRecord`（抽牌堆/手牌/墓地中的卡牌记录）、`UnitState`（在场的可变单位）、`HomeState`（独立的家）。**三者身份不可混用**；家 ID 为负数（玩家 -1、Boss -2），单位 ID 为正数。
- `UnitState` 只保存来源卡牌的值副本（`SourceCardId` / `TypeId` / `Owner`），**不引用 `CardRecord`**；永久伤害标记保存在 `DamageMarks`（每层是施加时的数值副本，`Copy()` 必须深拷贝），对外只暴露层数与下次受伤追加总量。玩家卡在成功部署时进入墓地，单位阵亡或被覆盖升级不再重复回收，因此“抽牌堆 + 手牌 + 墓地”的卡牌总数恒定，同一张卡可以反复部署出多个互相独立的单位。
- 场上战斗属性（当前生命 `Hp`、生命上限 `MaxHp`、当前攻击 `Attack`、射程 `Range`、移动距离 `MoveDistance` 与一次性部署减攻状态 `DeploymentPenaltyPending` / `DeploymentAttackReduction`）只存在于 `UnitState`：部署时从只读卡种配置初始化，之后由规则代码修改；伤害、合法动作、AI 与刷怪条件一律读当前值，不得回读卡种配置。
- 名词与机制的完整定义见 `docs/游戏内名词.md`。
- `GameCommand`：不可变操作请求，含 `CommandKind`（Deploy / BossDeploy / Move / MoveHome / Attack / Swap / Withdraw / Discard / EndTurn）、`Actor`、`SubjectId`、`Target`、`TargetId`、`SkipDeployment`。**Target 视为不可信输入，执行时必须复验**。
- `Selection` / `SelectionKind`：界面选择上下文，显式区分手牌与单位身份。
- `LegalActions`：规则层算出的合法目标（`Deploy` / `Move` / `Attack` / `Swap` / `CanWithdraw`）。**UI 不得自行推导合法性，高亮仅提示**。
- `GameEvent`：已发生的领域事件（`Kind` / `Message` / `SubjectId`，以及可选的炸弹信息、爆炸步骤与结构化 `DamageResult`），仅用于日志与表现，**动画不得回写结算结果**。
- 统一伤害结算：`Core/DamageResolution.cs` 定义 `DamageKind`（`DirectAttack` / `Bomb` / `MarkBonus`）、`DamageContext`（来源阵营/单位、目标、本笔与根类型、炸弹信息）、`DamageMarkLayer` 与 `DamageResult`（基础/追加实际扣血、是否死亡、受伤前层副本）。`GameSession.ResolveDirectDamage` 是普通攻击与炸弹共用的唯一扣血入口：先扣基础、死亡即只做一次 `Remove(Death)`，仍存活再按受伤前层依次追加；炸弹每轮 1 点基础扣血消耗 1 点预算，追加伤害不耗预算。
- `CommandResult`：成功状态、失败原因与有序事件；失败时状态未被修改。
- `GameSnapshot`：独立只读快照（回合、行动方、阶段、资源、手牌/牌堆/墓地、单位与家副本、兵位状态、日志），持有旧快照不会受后续命令影响。
- `IRandomSource` / `SeededRandom`：洗牌随机源，固定种子保证测试可复现，避免依赖 `UnityEngine.Random` 全局状态。

对局内核（`Core/GameSession.cs`）—— 玩家与 AI 共用同一入口：

- `Execute(GameCommand)`：部署、移动、攻击、互换、离场、弃牌、结束回合。
- `GetLegalActions(Selection)`：提供合法目标与高亮依据。
- `GetSnapshot()`：提供 UI、日志与调试快照。
- `SetInputBlocked(bool)`：规则层输入闸门（配置面板打开时启用）。

### 4.2 运行层（`Spotlight.Runtime`）

- 卡牌视图：`Card : MonoBehaviour`，配合 `CombatComponent` / `ResourceComponent` / `TeleportComponent` / `DeploymentPenaltyComponent` / `SpawnComponent` 五个能力组件；**同一个 `Card` 承载所有卡种，不为卡种建子类**。四邻空格限制、击杀恢复行动与伤害标记是纯规则能力，只有 SO 子资产、没有 MonoBehaviour，视图读取快照与卡种定义显示。
- 配置资产（运行层）：`CardDefinitionSO` / `CombatConfigSO` / `ResourceConfigSO` / `TeleportConfigSO` / `SpawnConfigSO` / `AdjacentClearanceConfigSO` / `KillActionConfigSO` / `DamageMarkConfigSO` / `SpotlightConfigAsset`，均实现只读接口；`GeneratedConfigAttribute` 是**无参标记**（特性参数会被编译进正式包程序集，不得携带工作簿路径）。**`ConfigManifest` 不在运行层**：它是纯编辑器侧的校验依据，类型放在 `Editor/ConfigManifest.cs`（`Spotlight.EditorTools`），否则它的 `_sourceWorkbookPath` / `_sourceHash` 字段名会跟着进 `Spotlight.Runtime.dll`。
- 加载入口：`ConfigLoader`（`DefaultResourcePath = "SpotlightConfig"`，`TryCreateConfiguration(...)` 失败返回错误文本，不抛异常）。
- 视图：`BoardView` / `BoardCellView`（UGUI 棋盘）、`HandView`（UGUI 手牌）、`HudView`（UI Toolkit / UXML）、`DeckSelectionPanel`（UI Toolkit 携带选择面板，每行显示属性行 + 特性短标，右侧详情区由悬停/点击触发）、`CardFactory` / `CardText`。
- 引导与支持：`GameBootstrap`（加载配置 → 创建对局 → 绑定视图）、`DebugSupport`（`IsDebugAvailable` 判定 + `PanelInstaller` 编辑器侧回调点，面板类型不在运行层）、`CardFeedback`（DOTween 表现）。
- 调试面板（**只在编辑器内存在**）：`ConfigDebugPanel` / `ConfigFieldBuilder` / `DebugConfigDraft`（含 `LegacyJsonMigration` / `JsonShapeValidator`）与安装器 `ConfigPanelInstaller` 全部放在 `Editor/Debug/`（编进 `Spotlight.Editor`，仅编辑器平台）；面板是**普通类不是 MonoBehaviour**（编辑器程序集的脚本禁止 `AddComponent`），由 `ConfigPanelInstaller`（`[InitializeOnLoad]` 注册到 `DebugSupport.PanelInstaller`）在进入 Play 时 `new` 出来并绑定 `HudView.Root`；面板骨架由 `EnsurePanelSkeleton` **用代码搭建**（不得做成 UXML/任何资产），两个入口按钮由 `HudView` 在 `#if UNITY_EDITOR` 里用代码插入。运行层只保留一个 `_debugPanelOpen` 状态位与编辑器侧回调点。**为什么必须搬到 Editor 下**：`#if` 能把类型从 DLL 去掉，但 Unity 仍会按编辑器编译结果在 `globalgamemanagers.assets` 写出顶层类名；放进仅编辑器程序集后连类名都不进包。**调试改动只存在于本次运行会话**。面板里不再有“逐卡种卡组数量”：抽牌堆一侧改为抽牌堆规则 7 项数值 + “调试用强制牌堆”（任一卡种填 > 0 即整体覆盖抽牌堆，全 0 表示按选卡结果生成）。

表现层硬性约定：

- 棋盘上的卡牌一律 `SetRaycastTarget(false)`，点击必须穿透到格子；手牌保持可点。
- 规则快照变化后，界面位置必须跟随，不得把棋子留在旧格（DOTween 需 `SetTarget` + `OnComplete/OnKill`，`Kill` 用 `DOKill(true)`）。
- `Card.OnDestroy` 只停止自身补间（不补完），避免访问已销毁的 `RectTransform`。
- HUD 由同一个 `Spotlight.uxml` 承载两套视图：**战斗界面**（左右详情区、墓地/抽牌堆计数、顶部工具栏、结束回合）默认显示；**菜单视图**（对局日志）移入菜单，进入时切换并暂停（复用 `SetModalOpen` 的“界面遮罩 + 规则层输入闸门”）。右下角旧的 `side-panel`（“Boss 状态 / 选中详情”两块文本侧栏）已删除：Boss 阶段与战舰状态由顶部常驻 `boss-phase-label` 承载，选中详情由左右卡面详情区承载，阶段提示只走 `error-label` 提示条与对局日志。新增界面元素要明确属于哪一套，避免菜单里点不到按钮或被遮罩压住。
- 开局前必经携带选择：`GameBootstrap` 加载配置后先显示 `DeckSelectionPanel`，确认后才创建对局；开局入口没有“取消”按钮。齿轮菜单里的「选卡」复用同一面板，进入前先弹二次确认（确认后当前对局重开）。选卡界面必须让玩家看清每张卡再决定：行内属性行与特性短标**必须**取自 `CardText.StatLine(CardArchetype)` / `CardText.TraitTags`（与对局卡面同源，不得在面板里另拼数值），右侧详情正文取自 `CardText.DetailBody`；“携带/取消”按钮与查看详情必须是两个独立点击区域。触屏没有 hover，因此点击卡种信息块也要能固定详情。
- 牌堆与墓地共用同一套计数面板（`.pile-panel`：底图 + 正中数字 + 上方标题 + 底部阶级小字；底图到位前用 USS 占位圆章，换图只改 `.pile-panel` 的一条 `background-image`）：**牌堆**正中显示剩余总张数（取 `snapshot.DrawPile.Count`，不以三阶级相加代替），底部一行小字给阶级构成「高 x · 中 y · 初 z」（按快照逐张查卡种阶级统计，抽牌递减、墓地洗回后回涨）；**墓地**只显示总数。标题与数字都绝对定位（`.pile-title` / `.pile-value`），因此牌堆多一行小字也不会把数字顶偏，两块面板的数字始终在同一高度。
- 卡面由两层组成：底色 Image（`Card._body`，仍是分类色 × 阵营色）与卡图 Image（`Card._art`）。卡图按**使用场景**分两列：`Cards.cardArt`（手牌）→ `CardDefinitionSO.Art`，`Cards.cardArtUnit`（棋盘上的战斗单位，默认目录 `Assets/Imported/SpotlightArt/Units/`）→ `CardDefinitionSO.UnitArt`；发布时解析成 Sprite，运行时 `Card.ApplyArt(definition, asUnit)` 按场景取图（`BindHand` 取 `Art`，`BindUnit` 取 `BoardArt = UnitArt ?? Art`）。卡图与阵营无关：Boss 专用卡只在场上出现，只填 `cardArtUnit`。**禁止把卡图写进 `Generated/Cards/*.prefab`**（每次「编译全部配置」都会覆盖）。没有卡图时 `_art` 整个节点禁用，回退纯色矩形；绑定为“家”或 `Unbind` 必须清掉卡图。`_art` 固定是外观模板的第一个子节点（渲染顺序：底色 → 卡图 → 高亮框 → 文本）且开启 `preserveAspect`，因此高亮仍由 `_highlight` 覆盖层表达。

### 4.3 编辑器工具（`Spotlight.EditorTools`）

- Excel 管线：`WorkbookReader` / `XlsxInspector`（读取与原始标记扫描）、`TableSchema` / `TableMapper`（表结构与行映射）、`ConfigIssue` / `CompileReport`（问题与报告）、`ConfigCompiler`（校验 + 编译）、`ConfigAssetPublisher`（发布 + 回滚）、`DefaultWorkbookWriter`（默认工作簿）。
- 资产与模板：`ViewTemplateBuilder`（外观模板与 Prefab）、`CjkFontSetup`（中文字体资产）。
- 入口与门禁：`SpotlightMenu`（`Tools/Spotlight/*` 菜单）、`ConfigManifest`（编译清单 SO，记录源工作簿路径/摘要/编译时间与产出清单，只供编辑器侧的 `ConfigBuildGate` 与测试使用）、`ConfigBuildGate` + `ConfigBuildProcessor`（`IPreprocessBuildWithReport` + Play 前检查）。
- 构建产物：`BuildArtifactCleanup`（`IPostprocessBuildWithReport`）删除输出目录下的 `*_BurstDebugInformation_DoNotShip` 与残留的 `lib_burst_generated.pdb`；必须挂接口而不能只改 `BuildEntry`，否则 CI 的构建脚本不会执行清理。打完包用 `scripts/check-build-hygiene.sh` 复核（详见第 7 节）。
- 打包入口：`BuildEntry` 一个类同时负责 Windows 与 Android（`BuildWindows` / `BuildAndroid` / `BuildAll`，菜单与命令行共用）。产物按平台分子目录：`Build/windows/`（exe + 数据目录）与 `Build/android/*.apk`；每次打包只清空**自己平台**的子目录。Android 在构建期临时覆盖三项设置并在构建后还原：出 **APK**（非 AAB）、**ARMv7 + ARM64** 双 ABI、**仅横屏**（棋盘 10 列宽、CanvasScaler 按 1920×1080 设计，竖屏装不下）。`ProjectSettings` 里 Android 脚本后端**固定 IL2CPP**：Mono 不支持 ARM64，改回 Mono 会让纯 64 位机型装不上，且会被静默降级成纯 armeabi-v7a。
- 版本覆盖：CI 发版通过 `-buildVersion <x.y.z>` / `-androidVersionCode <正整数>` 把本次版本写进包体，由 `BuildVersionOverride` 解析并临时改写 `PlayerSettings`（构建后还原，工程设置不留 diff）；版本号本身只能来自 `scripts/next-release-version.sh`。参数缺失（本地菜单打包）时完全不动 `PlayerSettings`，参数非法则打包直接失败。
- **发布包体约束（已实测）**：Unity 会在 `globalgamemanagers.assets` 里给每个“引用引擎的程序集”写一份 MonoScript 类名目录，即使类型被 `#if` 挡在正式包程序集之外，**顶层类名仍会入表**（嵌套类型不会、`noEngineReferences` 的 Core 不在表内）。结论：调试/开发专用代码**不能只靠条件编译**，必须放进仅编辑器编译的程序集（`Editor/` 或 `includePlatforms: ["Editor"]` 的 asmdef）；另外“场景上挂过组件”“持有 UXML 等资产引用”都会把内容拉进包。`scripts/check-build-hygiene.sh` 对上述泄露是硬失败。

## 5. 关键名词表

> 完整机制名词（抽牌堆、携带选择、手牌、墓地、卡牌记录、战斗单位等）以 `docs/游戏内名词.md` 为唯一来源；下表只列代码类型与高频术语。

| 名词 | 含义 |
|---|---|
| 抽牌堆 `GameSnapshot.DrawPile` | 本次对局的卡牌来源，由携带选择 + 抽牌堆规则生成；HUD 显示剩余**总张数**并为底部一行阶级小字。**不得称为“牌库”** |
| 携带选择（选卡）`DeckSelectionState` | 开局前勾选本次携带的卡种（每阶级至少 1 种、最多到有效上限），确认后生成抽牌堆开局；菜单「选卡」会先二次确认再重开对局；结果不持久化。界面每行显示属性行与特性短标，悬停/点击行内信息块在右侧展开完整规则与消耗 |
| 抽牌堆规则 `DeckConfig` / `DeckComposition` | `Rules` 的张数 + 三阶级比例 + 三阶级携带上限 7 列，以及把比例换算成张数、再分给已选卡种的唯一算法 |
| 有效携带上限 `DeckConfig.EffectiveMaxTypes` | `min(配置上限, 该阶级卡种数)`；该阶级可选卡种池不足时，界面按它判断能否继续勾选 |
| 强制牌堆 | 调试面板的“调试用强制牌堆”：任一卡种填 > 0 即整体覆盖抽牌堆（与选卡结果无关），仅本次运行会话有效，不写入任何资产 |
| 卡组 | 已废除的旧配置概念（`Deck` 工作表 / `DeckRow`）。现行口径是“携带选择 → 抽牌堆”，运行时来源一律叫抽牌堆 |
| 卡牌记录 `CardRecord` | 仅含实例 ID / 卡种 ID / 阵营，在抽牌堆、手牌、墓地间流转，是唯一会被洗回和再次抽到的身份 |
| 战斗单位 `UnitState` | 一次部署产生的可变实体，只保存来源卡的值副本（`SourceCardId` / `TypeId` / `Owner`），不引用 `CardRecord`；同一张卡可反复部署出多个互相独立的单位，伤势不回流卡牌 |
| 家 `HomeState` | 独立实体，不进入抽牌堆、不参与互换、无攻击力，可被攻击与移动 |
| 离场 `Withdraw` | 玩家主动把自己在场的单位移出棋盘；不消耗该单位本回合行动、不回收来源卡（部署时已进墓地），Boss 单位与家不可离场 |
| 维持费 upkeep | 单位每回合的持续消耗；不足时本回合不能行动 |
| 产出 produce | 家或资源卡每回合提供的资源 |
| 召唤失调 | “刚上场不能行动”，由规则开关控制 |
| 覆盖升级 | `requiresUpgrade` + `upgradeFrom`，资源卡只能覆盖指定卡种，按卡种 ID 关联，改名不失效 |
| 星门 | 特殊卡，`deployHomeOnly` + `avoidAdjacentSameType`，为相邻格提供战斗卡部署扩展 |
| 兵位 / 补兵 | `BossSlotRow` / `BossReplacement`，Boss 三个固定兵位独立计时，阵亡后按 `bossRespawnDelay` 补回 |
| 弃牌阶段 | `MatchPhase.Discard`，手牌超过上限时必须弃至上限才能进入对方回合 |
| 一次性部署减攻 | `deploymentPenaltyComponent` + `deploymentAttackReduction`；来源单位在场期间一次，使随后同阵营攻击类单位攻击力降低配置值（下限 0），多来源按部署顺序依次触发 |
| 四邻空格限制 | `adjacentClearanceComponent` + `adjacentClearanceMinEmpty`（1～4）：部署只查目标格，移动查起点与落点最终布局，互换查前后布局且双方能力单位都检查；斜角不计、棋盘外算空、炸弹不占格；被围仍可攻击 |
| 击杀恢复行动 | `killActionComponent`（仅攻击类，无数值参数）：本次直接攻击（含标记追加）击杀战斗单位后恢复攻击者一次普通行动，可连续触发，但不重开回合、不跨回合保存；炸弹与其它单位击杀不触发 |
| 伤害标记 | `damageMarkComponent` + `damageMarkDamagePerStack`（仅攻击类）：直接攻击存活战斗单位后加 1 层，永久可叠加、来源离场不失效；本次受伤按受伤前层数追加，追加不递归、不挂层、不耗炸弹预算，家不挂层 |
| 正式高级卡 | 星门、后羿-II综合舰（`houyi2`，四邻 2 空）、德克萨斯游骑兵（`texasRanger`）、维京长船-2056（`viking2056`，击杀恢复）、SNP-EU（`snpEu`，伤害标记 1 点）、拿破仑-1769（`napoleon1769`）；全部为攻击类/特殊卡，免费部署、有资源配置支付维持费 |
| 快照 `GameSnapshot` | 规则状态的只读副本，UI/日志/调试统一数据源 |
| 生成资产 | `Generated/` 下的只读 SO 与 Prefab，禁止手工修改 |

## 6. 关键操作与工作流

Excel 配置编译（菜单 `Tools/Spotlight/*`，按顺序）：

1. `生成默认工作簿` → 2. `验证配置`（只校验不写资产，失败一次性列出全部问题）→ 3. `编译全部配置`（校验通过才发布，失败保留上一套产物并回滚）→ 4. `生成中文字体资产` → 5. `生成演示场景`。

- 工作簿固定 5 张表（`Cards` / `Rules` / `Homes` / `BossSlots` / `Presentation`）；`Deck` 表已废除，残留会被读取阶段报错，删除该表即可继续。
- 携带哪些卡种**不是配置项**：可携带卡种池 = `Cards` 里 `tier != none` 的全部卡种，由玩家每次开局时选卡决定；策划只填抽牌堆张数、三阶级比例与三阶级携带上限。
- 新增卡种不需要登记任何“卡组”表，卡种会自动进入对应阶级的可选池；若某阶级首次出现，注意该阶级的携带上限与比例是否仍满足“分到的张数 ≥ 有效上限”。
- `编译全部配置` 采用稳定 ID 更新已有资产并保留 GUID；改名不破坏关联。
- 运行与构建门禁由 `ConfigBuildGate` 拦截：配置缺失或非法时禁止进入 Play / 构建，并给出可读错误。
- 调试配置面板仅通过“关闭”或成功“应用并重开”退出；“应用并重开”失败时保留原对局与面板。调试面板的“调试用强制牌堆”只覆盖本次运行会话的抽牌堆；“应用并重开”导致当前选卡变非法时静默回退默认选卡（每阶级按有效上限取满、`Cards` 表顺序）。
- 正式数据单元格禁止公式；编译器检查未定义列、重复表头、缺失列、公式、错误单元格、重复 ID、空/重名、非法分类、非法引用等。
- 架构图（`docs/diagrams/01-modules` / `02-cards` / `03-dataflow` / `04-turnflow` / `05-card-lifecycle`）修改后需按 `docs/diagrams/README.md` 重新渲染 SVG 及其导出产物（PNG/HTML/drawio），并由 CI 校验。

## 7. 测试与 CI 规范

- 每个功能与修复必须**同时提交对应测试用例**，否则不予合并。
- EditMode 测试放 `Assets/Spotlight/Tests/EditMode/`，用于规则内核、配置编译器、视图契约；PlayMode 测试放 `Assets/Spotlight/Tests/PlayMode/`，用于真实帧循环下的渲染、点击命中与 DOTween 动画。
- 测试通过公开命令（`Execute` / `GetLegalActions` / `GetSnapshot`）推进对局，**不得直接篡改 `GameSession` 私有状态**；测试内数值属于独立夹具，不是正式默认值。
- 洗牌类逻辑使用 `SeededRandom` 固定种子，保证测试可复现。
- 测试与调试面板只修改独立配置文档，不得修改加载中的正式 SO 或它暴露的表行引用。`SpotlightConfigAsset.ToDocument()` 必须深拷贝全部可变表行；使用正式资产的 PlayMode 夹具需核对测试前后配置一致，避免测试参数泄漏到后续对局。
- GitHub Actions（**PR 测试只在 PR 上跑**，不在 main 的 `push` 上重复跑，理由见下一条）：
  - `.github/workflows/unity-tests.yml`：`game-ci/unity-test-runner`，按**变更分层**决定跑哪套（详见下一条“测试分层”与 `docs/tasks/completed/CI测试分层计划.md`）。
  - **测试分层：** 先由 `classify` job 调用 `scripts/classify-change-set.sh` 给 PR 的变更文件分层，再决定两个测试 job 跑不跑：
    - **文档层**（`docs/**`、任意 `*.md`、`AGENTS.md`、`.gitattributes`、issue 模板）→ 两套都跳过，只留大文件/LFS 检查（约 1 分钟、零 Unity 席位）。
    - **表现层**（`Runtime/UI|View|Feedback|Debug/**`、`UI/**` 的 uxml/uss/prefab、`Scenes/**`、`Tests/PlayMode/**`）→ 只跑 PlayMode。
    - **规则层**（`Core/**`、`Generated/**`、`Config/*.xlsx`、`Editor/**`、`Tests/EditMode/**`）与**交叉层**（`Runtime/Cards|Config|Game/**`、`Fonts/**`、任意 `*.meta`/`*.asmdef`、`ProjectSettings/**`、`Packages/**`、`.github/workflows/**`、`scripts/**`、**一切未识别路径**）→ 两套都跑。
    - 注释改动不特殊处理：`Core/**` 里只改一行 `//` 也算规则层（脚本拿不到“只改了注释”这个信息，误判=静默漏测）。
    - 判不准时一律两套都跑（fail-safe）：读不到 diff、空变更集、分类脚本报错都走这条；需要强制全量时用 `workflow_dispatch` 手动触发（无 PR 上下文 → 必然两套）。
    - 每 PR 的分类结论会写进运行摘要并粘成 PR 上的 sticky 评论；跳过的检查在 PR 上显示为灰色 **Skipped**，对 ruleset 等同通过。
  - **不能用 workflow 级 `paths:` 过滤：** 三条必需检查按 job 名精确匹配，workflow 没触发时检查会永久停在 Pending、PR 被锁死。“少跑”只能是 job 级跳过，由 `scripts/check-ci-config.sh` 强制拦截 `paths`/`paths-ignore`。
  - **Unity 席位互斥：** `unity-tests.yml` 的 `concurrency.group` 固定为仓库级 `unity-seat`（不按 ref 分组），因此两个 PR 的测试不会同时启动去抢同一个个人版授权席位；也不得设 `cancel-in-progress: true`（中途杀 Unity 会泄漏席位）。代价是仓库内同一时刻只留 1 个 running + 1 个 pending 运行，排队中的旧运行会被新的顶掉（PR 检查可能显示 cancelled，重跑即可）。矩阵拆成两个字面名 job 后，**单次运行内的两套串行由 `playmode-tests` 的 `needs` 链保证**（不能再靠 `max-parallel: 1`），跨 run 的互斥靠 `concurrency`；两条都由 `scripts/check-ci-config.sh` 校验。彻底不排队需浮动授权（`unityLicensingServer`）或 self-hosted runner。
  - **CI 发版（`.github/workflows/release-build.yml`）只有手动入口，永不自动触发。** 在 Actions 页面 `Run workflow` 选版本递增方式（默认 `patch` = 上一个 Release 版本 +0.0.1）：流水线用 `game-ci/unity-builder@v6.0.0`（自 v6 起是 `game-ci/cli` 的薄封装，个人版只需 `UNITY_EMAIL`/`UNITY_PASSWORD`，`cliVersion` 固定 `v0.1.72`）依次调用 `BuildEntry.BuildWindows` / `BuildEntry.BuildAndroid`，产物落在 `Build/windows|android`，随后校验产物存在、跑 `scripts/check-build-hygiene.sh` 门禁、压 zip，最后 `gh release create` 发布。**版本运算只有一处**：`scripts/next-release-version.sh`（tag / `bundleVersion` / Android `versionCode` 三处同源，`--self-test` 在 `repo-hygiene.yml` 里跑）；包内版本由 `BuildVersionOverride` 读 `-buildVersion` / `-androidVersionCode` 临时写入 `PlayerSettings` 并在构建后还原。发版必须在 PR 合并后用 `workflow_dispatch` 手动跑：它占 Unity 席位且创建不可回滚的 tag/Release。
  - **发版 workflow 的守卫与豁免：** `scripts/check-ci-config.sh` 对普通 workflow 要求必须有 `pull_request`，但 `release-*.yml` 走相反规则（只允许 `workflow_dispatch`；禁止 `pull_request` / `push`；必须有 `permissions.contents: write`；必须调用 `scripts/next-release-version.sh`；`concurrency.group` 同样是仓库级 `unity-seat`），四条都有自检用例。**不要为了让发版 workflow 过守卫而给它加 `pull_request` 触发**——那等于让每个 PR 各发一个 Release。
  - `.github/workflows/repo-hygiene.yml`：大文件与 Git-LFS 一致性检查（`scripts/check-repo-hygiene.sh`）、CI 触发/串行/分层接线守卫（`scripts/check-ci-config.sh`）、变更分类器自检（`scripts/classify-change-set.sh --self-test`）与发布包体去调试化检查（`scripts/check-build-hygiene.sh`，无 `Build/` 时自动跳过但自检步骤始终跑），大二进制必须以 LFS 指针入库。
  - `.github/workflows/diagrams-check.yml`：校验 `docs/diagrams` 图源 JSON 仍能生成结构完整的 drawio。
- `main` 由分支规则集「main」保护，**只能通过 PR 新增提交**：`pull_request` 规则要求所有变更都关联一个 PR，直推（含普通快进推送）会被拒；`non_fast_forward` 另行禁掉 force push；`deletion` 禁删分支；`bypass_actors` 为空且 `current_user_can_bypass` 为 `never`，连仓库所有者也不能绕过。
- **不要求任何人工审阅**：`required_approving_review_count` 为 0、`require_code_owner_review` 关闭、`required_reviewers` 为空，仓库也不使用 CODEOWNERS。合并条件只有两条——全部必需检查通过、分支已与最新 main 同步（`strict_required_status_checks_policy` 开启）；两条同时满足后 PR 作者即可自行手动合并（不启用 auto-merge）。
- 必需检查共三条：`EditMode 测试（Unity 2022.3.62f3）`、`PlayMode 测试（Unity 2022.3.62f3）`、`大文件与 LFS 一致性检查`，且 `strict_required_status_checks_policy` 开启（PR 分支必须先与 main 同步）。因此 PR 阶段被测的结果就是合并后落到 main 的结果，`push` 触发只会重复跑（既费时间又额外占用 Unity 授权席位）；需要临时在 main 上验证时用 `workflow_dispatch`。两条 Unity 检查在不需要跑时显示为灰色 **Skipped**，对 ruleset 等同通过。
- 不需要在本地跑任何测试，测试留在推送后的pr中，github action云端跑，必要时用gh监视测试
- ⚠️ ruleset 里的必需检查名与 workflow 的 **job 名**是字符串精确匹配（如 `EditMode 测试（Unity 2022.3.62f3）`）。矩阵已拆成两个字面名 job，**版本号直接写在 job 名里**：改 `unityVersion` 或改 job 名时必须同步修改 ruleset，否则所有 PR 会**永久 pending 且没有任何报错提示**。推送前先在本地跑 `bash scripts/check-ci-config.sh --check-ruleset` 比对一次。

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

## 10. Boss 阶段与通用刷怪定位

- `Core/BossPhaseStateMachine.cs`：`BossPhase` 与 `MatchPhase` 独立；`IBossPhaseState` 提供 Enter/Exit、双方回合开始、结算后检查。仅允许一 → 二 → 三，不持有第二套单位/生命数据。三阶段在 `BossPhaseThreeState.Enter` 一次性生成双列护卫舰。
- `Core/GameSession.BossPhases.cs`：两艘指定战舰记录、三等分坐标、占位回退与目标过滤；`GameSession` 始终是唯一状态所有者。默认 Boss 30 血，阈值 20；跨阈值伤害截断，阶段切换先于胜负判定。
- 二阶段锁血且不可被攻击，战舰自动部署（包括手动模式），不扣普通放牌次数；满盘等待，下次 Boss 回合重试。只统计两个指定单位 ID 的阵亡，不补回。两舰阵亡进入三阶段并解除锁血。
- 二、三阶段停止新炸弹、新普通刷怪笼及固定兵位补兵；已有炸弹、已有单位及刷怪能力继续结算。手动普通放牌继续使用原卡种和次数。
- `Core/SpawnResolver.cs` 选格，`GameSession.ProduceSpawnUnits` 结算；`SpawnComponent` 只绑定配置/快照。按所属阵营回合序号 `DeployedOwnerTurn` 判断资格，按部署顺序生成，行动恢复之后执行，保留新单位召唤失调。
- `IsBossSpawner` 仅标记一阶段技能部署身份及唯一性限制，不代表刷怪能力。能力由 `CardArchetype.Spawn` 决定；不可按卡名或卡种 ID 判断行动权限。
- Cards：`spawnComponent` / `spawnCardType`；战斗开关 `canAttackResources` / `canSwap`。Rules：`bossPhaseTwoHp` / `bossWarshipColumn` / `bossWarshipCardType`。旧全局 `bossSpawnerSpawnCardType` 必须迁移，详见 `docs/使用说明.md`。
- 快照提供阶段、锁血和两条战舰记录的独立副本。新增回归入口：`BossPhaseTests`、`BossPhaseConfigTests`、`BossPhasePlayModeTests`；保持既有 PR CI 门禁。
- 与其他能力组合时，敌方攻击统一经过 `CanTarget`，友方治疗保留独立分支；战舰阵亡与标记奖励、死亡加攻共用 `Remove` 结算，不可互相覆盖。阶段战舰不能配置成即时卡，否则自动退场会使二阶段无法完成。默认共 25 个卡种，五个 Boss 专用卡的阶级为 `none`。

- 三阶段护卫舰：Rules 的 `bossPhaseThreeFrontCardType` / `bossPhaseThreeRearCardType` 引用 Cards 的 Boss 专用攻击卡，默认一式护卫舰 7/1/2/1、二式护卫舰 3/3/2/1（生命/攻击/射程/移动）。以进入时 Boss 家当前列及朝玩家方向前列生成一次，保护双方的家，先全部死亡清场再部署，不补回且遵守召唤失调。
- 三阶段顶替使用 `Remove(Death)`，触发死亡加攻与入场标记奖励；旧通用刷怪顶替仍为 `ReplacedBySpawn`。不重复回收玩家来源卡，不触发直接攻击击杀恢复或伤害标记追加。
- `UnitState.AiTargetPolicy` 由部署来源赋值；三阶段生成使用 `BossGuard`，不按卡名判断。索敌攻击类 → 家 → 特殊 → 资源；攻击类按距 Boss 家当前坐标升序、当前攻击降序。先攻击射程内目标，无目标才按同一排序追击可达目标；手动模式仍由玩家操作。
