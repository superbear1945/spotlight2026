# AI 审查说明（Codex + 第三方 Responses API）

本文说明 `.github/workflows/codex-review.yml` 的**作用、接入方式、判定规则与故障处理**。
它由第三方 Agent（OpenAI Codex CLI，经 OpenAI 官方 `openai/codex-action` 安装并无头执行）驱动，
但**模型与 API 地址由本仓库指定**，走的是你自己的第三方供应商，不是 OpenAI 官方端点。

- 现有三条必需检查与合并流程见 [使用说明.md](使用说明.md) 第 8 节。
- 玩法与机制名词以 [玩法.md](玩法.md)、[游戏内名词.md](游戏内名词.md) 为准。

---

## 1. 三个审查角色

| 角色 id | job 名 | 负责什么 | 规则文件 |
|---|---|---|---|
| `code` | 代码与规范审查 | 逻辑/边界错误、程序集依赖边界、规则内核不变量、表现层约定、生成资产被手工改动、行为变化缺少测试 | `.github/codex-review/prompts/code.md` |
| `docs` | 文档一致性审查 | 现有文档是否被改坏、新对外行为是否有说明、`AGENTS.md` 是否同步、名词是否与 `游戏内名词.md` 冲突 | `.github/codex-review/prompts/docs.md` |
| `comments` | 注释一致性审查 | 新增类/函数是否说明「作用 / 输入输出 / 为什么需要」、改动后既有注释是否过期 | `.github/codex-review/prompts/comments.md` |

三个角色**并行**执行（`strategy.max-parallel: 3`，`fail-fast: false`）。
某个角色失败不会取消另外两个，因此能拿到完整结论。
最终由单独的 job **`AI 审查门禁`** 给出唯一结论。

> 三个角色都不执行项目脚本、不启动 Unity、不跑测试。
> 可执行验证仍由 `unity-tests.yml` 负责，本工作流只补「语义审查」这一层。

---

## 2. 接入配置（一次性）

### 2.1 GitHub Secret

| 名称 | 内容 |
|---|---|
| `CODEX_REVIEW_API_KEY` | 第三方供应商的 API Key |

### 2.2 GitHub Variables（仓库 → Settings → Secrets and variables → Actions → Variables）

| 名称 | 必填 | 说明 |
|---|---|---|
| `CODEX_REVIEW_ENABLED` | 是 | 总开关。**只有 `true` 才会执行审查**；否则门禁明确写出「本次未做任何 AI 审查」并放行 |
| `CODEX_REVIEW_RESPONSES_ENDPOINT` | 是 | **完整的 Responses 接口地址**，如 `https://供应商域名/v1/responses` |
| `CODEX_REVIEW_MODEL` | 是 | 三个角色共用的默认模型 id |
| `CODEX_REVIEW_CODE_MODEL` | 否 | 代码角色专用模型（覆盖默认；建议用推理能力更强的模型） |
| `CODEX_REVIEW_DOCS_MODEL` | 否 | 文档角色专用模型 |
| `CODEX_REVIEW_COMMENTS_MODEL` | 否 | 注释角色专用模型 |
| `CODEX_REVIEW_EFFORT` | 否 | 三个角色共用的推理强度，见下节；**留空 = 不传该参数**（由 CLI 用默认值） |
| `CODEX_REVIEW_CODE_EFFORT` | 否 | 代码角色的推理强度 |
| `CODEX_REVIEW_DOCS_EFFORT` | 否 | 文档角色的推理强度 |
| `CODEX_REVIEW_COMMENTS_EFFORT` | 否 | 注释角色的推理强度 |
| `CODEX_REVIEW_CLI_VERSION` | 否 | 覆盖 Codex CLI 版本，默认 `0.162.1` |
| `CODEX_REVIEW_MAX_DIFF_BYTES` | 否 | 可审查 diff 的体积上限，默认 `1048576`（1 MiB） |

### 2.3 模型与推理强度怎么选

#### 推理强度（`model_reasoning_effort`）

- **允许取值只有四个：`minimal` / `low` / `medium` / `high`**（来自 Codex CLI 的 config 文档）。
  写其他值（如 `xhigh`、`max`、`none`）会被 CLI **静默忽略**，表现为「改了配置但行为完全没变」；
  因此本工作流在调用前就拦住非法值。
- **留空与写 `medium` 不等价。** 留空是「根本不传这个字段」，由 CLI 决定是否启用推理；
  显式写 `medium` 会把 `reasoning` 参数写进请求。若供应商不接受该字段，显式传值反而会把请求弄坏。
  **所以默认请留空**，确认供应商支持后再按需提升到 `high`。
- 强度越高，耗时与 token 消耗越大。三个角色各自独立调用，且 job 超时是 20 分钟；
  代码角色用 `high` 时请留意大 PR 是否会逼近超时（超时会直接判「审查未完成」）。

#### 模型分配

三个角色可以分别指定模型。**我无法从可验证的来源判断 `gpt-6-astra` 与 `gpt-6.1-sol` 在本项目上的实际审查质量差异**，
因此不给出「哪个更好」的结论，而是给一个可执行的选择方法：

1. **先用同一个模型跑通全流程**（只设 `CODEX_REVIEW_MODEL`），确认三个角色都能产出合法报告。
2. **再做对比实验**：找 2～3 个已合并的历史 PR（其中至少一个含真实 bug、一个含文档不一致），
   把两个模型分别指给 `code` 角色跑一遍，只看两件事：
   - 真实缺陷是否被发现（漏报）；
   - 是否报出大量站不住脚的 `blocking`（误报，这种最贵：会拦住无关的合并）。
3. **按角色分配**：把误报少、能给出可复查证据的那个模型给 `code`；
   `docs` 与 `comments` 的判断依据更偏向文本一致性、对推理深度的要求低，可以用更便宜的模型，
   必要时再把 `code` 单独提到 `high` 强度。

一个基于职责分工的**初始假设**（需用上面的实验验证，不要当成结论）：

| 角色 | 建议模型 | 建议强度 | 理由 |
|---|---|---|---|
| `code` | 推理能力更强、工具调用更稳的那个 | 留空；不足再升 `high` | 要跨文件追踪调用方与不变量，最难 |
| `docs` | 成本更低/更快的那个 | 留空 | 主要是「文档某句话是否已不成立」的比对 |
| `comments` | 成本更低/更快的那个 | 留空 | 参数/返回值/副作用是否与注释一致 |

> 无论选哪个模型，报告都必须通过 `.github/codex-review/report.schema.json` 校验；
> 不支持结构化输出的模型会直接表现为「报告非法 → 审查未完成」，不会被当成通过。

### 2.4 供应商必须兼容 Responses API（重要）

- 本工作流通过 Codex Action 的 `responses-api-endpoint` 直连 **Responses API**。
  供应商只兼容 `/v1/chat/completions` 时**不能**用。
- 「OpenAI 兼容」通常只指 Chat Completions，**不等于**支持 Responses。
  接入前请向供应商确认：`/v1/responses`、流式响应、工具调用（读取文件 / 搜索 / 执行只读命令）、
  结构化输出（`json_schema`）。
- 地址里不要写 Key，也不要用 `http://`：工作流只接受 `https://`，并会拒绝含 `@` 的地址。

### 2.5 启用顺序（不要跳步）

```text
1. 先合入本工作流（此时 CODEX_REVIEW_ENABLED 未设置，门禁以「未启用」通过，不影响任何 PR）
2. 配置 Secret 与 Variables：CODEX_REVIEW_API_KEY / CODEX_REVIEW_RESPONSES_ENDPOINT / CODEX_REVIEW_MODEL
3. 把 CODEX_REVIEW_ENABLED 置为 true（此时门禁仍未加入必需检查，即使红灯也拦不住别人）
4. 跑一次真实审查，确认三个角色都产出报告、门禁给出明确结论（触发方式见下表）
5. 最后才在分支规则集「main」中把 `AI 审查门禁` 加进必需检查
```

> ⚠️ 不能把第 5 步提前：若先把 `AI 审查门禁` 设为必需检查而变量仍不是 `true`，
> 检查会以「未启用」通过——**看起来绿，其实什么都没审**。
>
> ⚠️ 也不能把第 3 步放到第 4 步之后：`CODEX_REVIEW_ENABLED` 不是 `true` 时，
> **任何**触发方式（包括手动 `Run workflow`）都只会得到「未启用」的结论，等于白跑一趟。

第 4 步的三种触发方式：

| 触发方式 | 怎么做 | 适用场景 |
|---|---|---|
| `pull_request`（自动） | 开一个 PR，或往已存在的 PR 推一次提交 | **最接近真实使用流程**，推荐先走这条 |
| `workflow_dispatch`（手动） | 仓库 → **Actions** → 左侧选 `Codex Review` → 右上 **Run workflow** → Branch 下拉选**有改动的分支** → Run | 想在不改动 PR 的情况下重跑；`base` 默认取默认分支，可用输入 `base_ref` 指定 |
| `gh` CLI | `gh workflow run codex-review.yml --ref <分支>` | 习惯命令行时 |

> ⚠️ **`Run workflow` 按钮只有在工作流文件已经存在于默认分支（main）上时才会出现**
> （官方要求：要能触发 `workflow_dispatch`，工作流必须位于默认分支）。
> 因此第 1 步必须先合入；在它合入之前，只能用 `pull_request` 触发来验证工作流本身能不能跑起来。

---

## 3. 判定规则与红灯含义

| 门禁结论 | 触发条件 | 怎么处理 |
|---|---|---|
| ✅ 通过 | 三个角色都完成，且没有任何 `blocking` 问题 | 无需操作；`advisory` 建议看一眼 |
| ❌ 审查不通过 | 任一角色报出 `blocking` 问题 | 按 Actions 摘要与产物里的「必须修复」逐条改代码后重新提交 |
| ⚠️ 审查未完成 | 报告缺失、JSON 非法、模型声明 `insufficient`、准备阶段失败 | **不是靠改代码解决**：看对应角色 job 的日志（接入配置、超时、上下文超限）后重跑 |
| ⏭️ 未启用 | `CODEX_REVIEW_ENABLED != true` | 本次**没有做任何 AI 审查**，不代表通过 |

- 问题级别里只有 `blocking` 会让门禁判「审查不通过」，`advisory` 仅展示建议。
  但**「审查不通过」与「审查未完成」都会让门禁失败并返回非零退出码**；
  前者靠改代码解决，后者靠重跑或修接入配置。
  （在 `AI 审查门禁` 被加入分支规则集之前，这两种失败都不会阻止合并；加入后两者都会。）
- **「审查未完成」绝不当作通过。** 报告不可信时连「有没有问题」都无法判断。
- 报告里的 `headSha` 必须等于本次被审查的提交，否则整份报告判为无效（防止审查了旧版本却算作本次通过）。
  若汇总阶段连可信的 head SHA 都拿不到，会直接判「审查未完成」而不是放行。

看报告的位置：

1. 该次运行的 **Actions 摘要**（每个角色一段，加门禁总表）；
2. 产物 `codex-review-summary`（Markdown + JSON）；
3. 产物 `codex-review-report-<role>`（原始 JSON 与角色小结）。

---

## 4. 涉及的文件

| 路径 | 作用 |
|---|---|
| `.github/workflows/codex-review.yml` | prepare / review（三角色矩阵）/ gate 三个 job |
| `.github/codex-review/report.schema.json` | 三个角色统一的输出契约（结构化输出） |
| `.github/codex-review/prompts/*.md` | 三个角色的审查规则（要改审查标准就改这里） |
| `scripts/codex-review/collect-context.mjs` | 固化审查范围，生成上下文与改动清单 |
| `scripts/codex-review/validate-report.mjs` | 单角色报告快速自检 |
| `scripts/codex-review/summarize.mjs` | 汇总与最终判定 |
| `scripts/codex-review/lib.mjs` | 纯函数层（分类、解析、校验、判定、渲染） |
| `scripts/codex-review/tests/codex-review.test.mjs` | 离线测试（不调用付费 API；具体用例数以 `node --test` 输出为准） |

### 审查范围的产物理清

- `codex-review-context/run-info.json`：base / merge-base / head、改动清单、分类、是否改动了审查基础设施。
- `codex-review-context/change-list.md`：人可读的改动清单，分「参与语义审查」与「排除在外」。
- `codex-review-context/diff.patch`：**仅可审查改动**的 diff（不含生成资产与二进制）。
- `codex-review-context/task-header.md`：与角色无关的任务头（含本次真实 head SHA）。

排除规则：

| 类别 | 判定依据 | 为什么排除 |
|---|---|---|
| `generated` | `Assets/Spotlight/Generated/**` | 只读编译产物，由重新编译与配置校验负责 |
| `binary` | 常见二进制扩展名（图片/字体/工作簿/压缩包等） | 文本语义审查对内容无判断能力，由生成流程与 LFS 检查负责 |

---

## 5. 本地验证与修改指南

```bash
# 只跑离线测试（秒级，不需要 Unity，不调用任何 API）
node --test scripts/codex-review/tests/codex-review.test.mjs

# 在本地对一个范围生成上下文（检查解析结果是否符合预期）
node scripts/codex-review/collect-context.mjs \
  --repo-root . --out /tmp/ctx --base origin/main --head HEAD
```

修改规则时：

- **调整审查标准** → 只改 `.github/codex-review/prompts/*.md`。
- **增删报告字段** → 必须同时改 `.github/codex-review/report.schema.json` 与 `lib.mjs` 里的
  `REPORT_REQUIRED_FIELDS` / `FINDING_REQUIRED_FIELDS`；离线测试会断言两者一致。
- **改工作流结构** → 必须保留 `max-parallel: 3`、`fail-fast: false`、门禁 job 名 `AI 审查门禁`
  与 `if: always()`；离线测试会断言这些约束。
- 改完都要跑一次上面的离线测试。

---

## 6. 已知限制（务必知悉）

1. **同仓库写权限即是信任边界。**
   PR 作者能修改本工作流的定义、校验脚本与规则本身。工作流无法在自己内部实现完全防篡改。
   作为补偿，汇总会在头部显著提示「本次 PR 修改了 AI 审查基础设施」，
   但这不是强制阻断——否则就永远无法升级审查规则。
   需要更强隔离时的后续方向：把本工作流改为受保护的外部工作流（或由独立的
   受保护分支提供规则），再由本仓库调用。首版不引入。
2. **只支持同仓库分支的 PR。**
   Codex Action 内置「触发者需有仓库写权限」的检查；Fork PR 取不到 Secret，
   会失败并判为「审查未完成」，**不会**被当作通过。本轮不使用 `pull_request_target`
   去执行 PR 代码（那会把 Secret 暴露给不可信代码）。
3. **大 PR 会被拦下而不是被截断。**
   可审查 diff 超过 `CODEX_REVIEW_MAX_DIFF_BYTES` 时准备阶段直接失败，
   判为「审查未完成」。这是刻意的：截断后的报告无法分辨「审过没问题」与「根本没看到」。
4. **费用与限流不由本工作流控制。**
   硬上限请在供应商侧设置额度。同一 PR 有新提交时会取消旧运行（`cancel-in-progress`），
   避免重复消耗；但三个角色本身就是三次独立调用。
5. **版本固定不等于验证过。**
   Action 固定在 `openai/codex-action` 的 commit `bdf19a4`（v1.13），
   Codex CLI 默认固定在 `0.162.1`。两者与你的第三方 endpoint 的组合**必须在真实 PR 上联调确认**。
   若出现 CLI/Action 不兼容，用仓库变量 `CODEX_REVIEW_CLI_VERSION` 覆盖到已验证的版本。

---

## 7. 故障排查

| 现象 | 原因 | 处理 |
|---|---|---|
| 三个角色都在「校验接入配置」失败 | Secret 或 Variables 缺失/写错 | 按 `::error::` 提示补齐；地址必须是 `https://` 且以 `/responses` 结尾 |
| 报 `No valid ...` / 授权或写权限错误 | 触发者无仓库写权限（如 Fork PR），或 Action 的写权限检查未通过 | 由有写权限的贡献者在同仓库分支上开 PR |
| 报告文件不存在 | 模型调用失败、超时（默认 20 分钟）或被限流 | 看该角色 job 日志；必要时提高 `timeout-minutes` 或调大供应商额度后重跑 |
| 报告「不是合法 JSON」 | 供应商不支持结构化输出，或响应被截断 | 与供应商确认 `json_schema` 支持；确认 `CODEX_REVIEW_MODEL` 是支持工具调用的模型 |
| 准备阶段报「无法确定审查范围」 | base/head 解析不了（浅克隆、rebase 后 base 变动） | 重新触发（`synchronize` 会带来新的 base/head） |
| 准备阶段报「超过上限」 | 可审查 diff 过大 | 拆分 PR；或确认是否误把生成资产改成了非生成路径，必要时调大 `CODEX_REVIEW_MAX_DIFF_BYTES` |
| 门禁一直是「未启用」 | `CODEX_REVIEW_ENABLED` 不是 `true` | 按第 2.5 节顺序启用 |
| 改了推理强度/模型但行为没变 | 强度取值写错（只允许 minimal/low/medium/high），或该供应商不识别 `reasoning` 字段 | 看该角色 job 的「解析审查模型与推理强度」日志确认实际取值；再向供应商确认是否支持推理参数 |
| 三个角色都报大量同一条问题 | 规则写得过宽，或模型不擅长该判断 | 收窄对应 `prompts/*.md` 的措辞；先以 `advisory` 观察几次再收紧 |
| `AI 审查门禁` 在 PR 上永久 pending | job 名被改动但规则集未同步 | 见 [使用说明.md](使用说明.md) 第 8 节的字符串精确匹配注意事项；本地跑 `bash scripts/check-ci-config.sh --check-ruleset` |
