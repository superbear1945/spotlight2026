# 架构图与流程图（模块 4 交付）

本目录的图由 `fireworks-tech-graph` 技能（本地技能目录中的 `SKILL.md`）生成：**JSON 是源，SVG 是规范产物，PNG / HTML / drawio 是导出结果**。
修改内容时请改 `.json` 后重新渲染，不要直接手改 `.svg`，否则 `check` 的下一次结果会与源不一致。

## 1. 目录结构

**每张图一个子目录**，目录内是同名的各类产物，避免四张图的同名文件混在一个列表里难以区分：

```
docs/diagrams/
├── README.md              本说明
├── spec-to-drawio.py      图纸 JSON → draw.io 原生 .drawio 的转换与校验脚本
├── 01-modules/
│   ├── 01-modules.json            图纸定义（唯一来源，改这个）
│   ├── 01-modules.layout.json     渲染时的布局检查报告（交付证据，不手工维护）
│   ├── 01-modules.svg             规范矢量产物
│   ├── 01-modules.png             用于文档预览的位图（SVG 的 2 倍尺寸）
│   ├── 01-modules.html            离线可缩放查看器
│   └── 01-modules.drawio          draw.io 原生文件（用于后续手工调整）
├── 02-cards/     （同上 6 个文件，前缀为 02-cards）
├── 03-dataflow/  （同上 6 个文件，前缀为 03-dataflow）
└── 04-turnflow/  （同上 6 个文件，前缀为 04-turnflow）
```

文件名保留完整前缀（`02-cards/02-cards.svg`）而不是统一叫 `diagram.svg`，是为了在任何地方看到单个文件时都能立刻知道它属于哪张图，也便于全文检索与 CI 产物归类。

## 2. 四张图分别是什么

| 目录 | 主题 | 渲染模式 |
|---|---|---|
| [`01-modules`](01-modules/) | 模块架构与程序集依赖（Core → Runtime → Editor，Tests 依赖三者，Plugins 编辑器/运行时分离） | `architecture` |
| [`02-cards`](02-cards/) | `Card`、三个能力组件、三个组件 SO、`CardDefinitionSO` 与 `UnitState` / `CardRecord` / `HomeState` / `GameSession` 的关系 | `er-diagram` |
| [`03-dataflow`](03-dataflow/) | Excel 编译 → 生成资产（SO + Prefab）→ 配置加载 → GameSession → 快照 → UI，含调试草稿 / JSON 导入导出 / “应用并重开”分支 | `data-flow` |
| [`04-turnflow`](04-turnflow/) | 玩家回合（恢复行动 → 资源结算 → 维持费 → 抽牌 → 操作）→ 超限弃牌 → Boss 回合（恢复行动 → 补兵 → 按部署顺序行动 → 自动结束）→ 胜负检查 | `flowchart` |

## 3. 生成与检查命令

```bash
# 技能目录（按本机安装位置替换）
SKILL="{SKILL_ROOT}"

# 1) 渲染 SVG，并输出布局报告
python "$SKILL/scripts/fireworks.py" render architecture docs/diagrams/01-modules/01-modules.json  docs/diagrams/01-modules/01-modules.svg  --report docs/diagrams/01-modules/01-modules.layout.json
python "$SKILL/scripts/fireworks.py" render er-diagram  docs/diagrams/02-cards/02-cards.json      docs/diagrams/02-cards/02-cards.svg      --report docs/diagrams/02-cards/02-cards.layout.json
python "$SKILL/scripts/fireworks.py" render data-flow   docs/diagrams/03-dataflow/03-dataflow.json  docs/diagrams/03-dataflow/03-dataflow.svg  --report docs/diagrams/03-dataflow/03-dataflow.layout.json
python "$SKILL/scripts/fireworks.py" render flowchart   docs/diagrams/04-turnflow/04-turnflow.json  docs/diagrams/04-turnflow/04-turnflow.svg  --report docs/diagrams/04-turnflow/04-turnflow.layout.json

# 2) 结构、几何与构图检查（5 项全部为 true 才算通过）
python "$SKILL/scripts/fireworks.py" check docs/diagrams/01-modules/01-modules.svg

# 3) 离线可缩放 HTML
python "$SKILL/scripts/fireworks.py" export-html docs/diagrams/01-modules/01-modules.svg docs/diagrams/01-modules/01-modules.html --title "Spotlight 模块架构与程序集依赖" --slug spotlight-modules

# 4) draw.io 原生文件（见第 5 节）
python docs/diagrams/spec-to-drawio.py docs/diagrams
```

### 本机 PNG 渲染方式（重要）

本机 CairoSVG 缺少原生 `libcairo`（`import cairosvg` 可用但渲染时报 `no library called "cairo-2"`），
因此 `fireworks.py export-png` 不可用，改用技能自带的 Chrome/Puppeteer 通道（输出为 SVG 画布的 2 倍尺寸）：

```bash
npm install --prefix /tmp/fw-puppeteer puppeteer-core     # 需要网络，只装到临时目录
NODE_PATH=/tmp/fw-puppeteer/node_modules \
FIREWORKS_PYTHON=python \
FIREWORKS_PUPPETEER_PATH=/tmp/fw-puppeteer/node_modules/puppeteer-core \
FIREWORKS_CHROME_PATH="C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe" \
node "$SKILL/scripts/svg2png.js" docs/diagrams
```

说明：`svg2png.js` 会递归该目录下每个子目录中的 `.svg` 并生成同名 `.png`（2 倍尺寸），因此一次命令即可刷新全部 PNG。
Windows 上 `python3` 是应用商店占位程序，必须用 `python`，所以设置了 `FIREWORKS_PYTHON`。

## 4. 图纸约定

- 风格：Style 1 Flat Icon（默认；白底、圆角卡片、语义箭头与图例）。
- 构图档位：`quality_profile: "standard"`（本套图含容器与跨层连线，未使用更严的 `showcase`；所有违规项为 0，见布局报告的 `composition.metrics`）。
- 文字：`text_policy: "strict"`，任何标签被截断都会让渲染直接失败，因此图纸中的中文标签都是完整渲染的。
- 文字内容为中文，风格与配色统一；图例、页脚与画布尺寸在各 JSON 中固定，便于对比与重生成。

## 5. draw.io 原生文件（用于后续手工调整）

每张图的目录里都有一份 **draw.io 原生文件** `<name>.drawio`，
可以直接用 draw.io 打开（无需“导入”），也可以从 `File → Open` 或拖进 app.diagrams.net。

**为什么不直接用 SVG 导入 draw.io：**
draw.io 导入 SVG 时会把每个 `<path>` 当成普通图形，箭头会变成与方框没有关系的静态折线，
移动方框后连线不跟随、连接关系也就丢了。
`.drawio` 是 mxGraph 原生模型：方框是**顶点**、箭头是带 `source` / `target` 的**边**，
拖动方框时箭头会自动重连，可以长期维护。

| 转换结果 | 说明 |
|---|---|
| 分区容器 | 虚线圆角框 + 左上角标题，作为背景元素，可单独选中/移动 |
| 普通节点 | 圆角矩形；`double_rect` 使用 draw.io 原生 `double=1` 画出双线边框 |
| 圆柱 / 六边形 | `cylinder3` / `hexagon`，可在 draw.io 里继续换形状 |
| 箭头 | 真正的边，带出入口端口（左/右/上/下），颜色沿用 SVG 的语义流向配色 |
| 图例 | 带箭头的短线段 + 文字，可整体拖动 |
| 标题 / 副标题 / 页脚 | 无边框文本框（draw.io 的 `text` 样式），位置与画布一致 |

标签使用 draw.io 原生的 HTML 标签格式（`html=1` + `&lt;b&gt;标题&lt;/b&gt;&lt;br&gt;&lt;font ...&gt;副标题&lt;/font&gt;`），
因此标题加粗、副标题灰色，与 PNG 观感一致；双击任意标签即可直接改文字或改样式。

### 重新生成 / 校验

```bash
python docs/diagrams/spec-to-drawio.py docs/diagrams           # 由各子目录的 *.json 重新生成对应 .drawio
python docs/diagrams/spec-to-drawio.py docs/diagrams --check   # 只校验：XML 合法 + 每条边的端点都能找到顶点
```

脚本会自动扫描 `docs/diagrams/<name>/<name>.json`（并兼容早期平铺在根目录的 JSON）。
修改图纸时请改 `*.json`（唯一来源），然后重新生成本节列出的全部产物；
也可以直接在 draw.io 里改 `.drawio`，但要注意此时 JSON 与 draw.io 会各有一份，需自行保持同步。
