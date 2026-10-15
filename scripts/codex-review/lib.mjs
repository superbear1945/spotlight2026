/**
 * lib.mjs —— AI 审查流程的共享纯函数库（不调用 git、不读写文件、不访问网络）。
 *
 * 【作用】
 *   把「文件分类、git 输出解析、报告校验、结论判定、汇总渲染」这些逻辑集中在一处，
 *   供 collect-context.mjs / validate-report.mjs / summarize.mjs 三个入口与测试共同复用。
 *
 * 【输入 / 输出】
 *   全部是纯函数：入参为普通数据（字符串、数组、对象），返回新对象，
 *   不修改入参、不产生副作用，便于在测试里直接断言。
 *
 * 【为什么单独抽出这个文件】
 *   结论判定（通过 / 审查不通过 / 审查未完成）是整个 CI 门禁的核心。它必须能离线、
 *   可重复地验证；若写在 workflow 的 shell 里，就无法在 PR 上证明「报告缺失时必须判未完成」
 *   这类规则真的生效。抽成纯函数后可以直接被 node:test 覆盖。
 */

/** 三个审查角色的 id，顺序固定：顺序决定汇总报告里的展示顺序。 */
export const REVIEW_ROLES = ['code', 'docs', 'comments'];

/** 角色 id → 中文显示名；仅用于汇总与日志，不参与判定。 */
export const ROLE_TITLES = {
  code: '代码与规范审查',
  docs: '文档一致性审查',
  comments: '注释一致性审查',
};

/** 报告允许的完成状态。completed=完成审查；insufficient=未能完整审查；not_applicable=本次改动与该角色无关。 */
export const REPORT_STATUSES = ['completed', 'insufficient', 'not_applicable'];

/** 问题严重程度。blocking 会阻止合并；advisory 只展示建议。 */
export const SEVERITIES = ['blocking', 'advisory'];

/**
 * Codex CLI 支持的推理强度取值（对应 `--config model_reasoning_effort`）。
 * 空字符串表示「不传该参数」，由 CLI 用自己的默认值（官方文档为 medium）。
 * 取值来源：Codex CLI 的 config 文档（model_reasoning_effort 小节）。
 */
export const REASONING_EFFORTS = ['minimal', 'low', 'medium', 'high'];

/**
 * isValidEffort —— 校验推理强度取值。
 *
 * 输入：任意字符串（允许空串）。
 * 输出：布尔值；空串视为「未配置」也算合法。
 * 为什么需要：取值写错时 Codex CLI 不会报错，而是把无效值原样发给供应商，
 * 表现为「改了配置但行为完全没变」——这类问题从日志里看不出来，必须在入口拦住。
 */
export function isValidEffort(value) {
  const effort = String(value ?? '').trim();
  return effort === '' || REASONING_EFFORTS.includes(effort);
}

/** 报告对象必须存在的字段，与 .github/codex-review/report.schema.json 的 required 保持一致。 */
export const REPORT_REQUIRED_FIELDS = [
  'role',
  'headSha',
  'status',
  'statusReason',
  'summary',
  'reviewedFiles',
  'findings',
];

/** findings 数组里每个问题必须存在的字段，与同一份 schema 的 items.required 保持一致。 */
export const FINDING_REQUIRED_FIELDS = [
  'id',
  'severity',
  'category',
  'file',
  'line',
  'rule',
  'evidence',
  'impact',
  'suggestion',
];

/**
 * 审查基础设施路径前缀：改动这些文件等于改动「审查本身」。
 * 这类改动不会被 AI 视作违规，但必须在汇总里显著提示，便于人工确认。
 */
export const HARNESS_PREFIXES = [
  '.github/codex-review/',
  'scripts/codex-review/',
  '.github/workflows/codex-review.yml',
];

/** 按扩展名判定为二进制（无法做文本语义审查）的后缀。 */
const BINARY_EXTENSIONS = [
  '.png', '.jpg', '.jpeg', '.gif', '.webp', '.bmp', '.tga',
  '.ttf', '.otf', '.woff', '.woff2',
  '.xlsx', '.xls', '.pdf', '.zip', '.7z', '.rar',
  '.mp3', '.mp4', '.wav', '.ogg', '.mov',
  '.unitypackage', '.dll', '.so', '.pdb',
];

/**
 * normalizePath —— 统一成仓库内的相对路径写法（正斜杠、去掉 "./" 前缀）。
 *
 * 输入：任意路径字符串（可能来自 git、JSON 报告或 Windows 风格）。
 * 输出：正斜杠分隔、无 "./" 前缀的字符串。
 * 为什么需要：报告的 file 字段由模型生成，可能写成 `./docs/玩法.md` 或 `docs\\玩法.md`；
 * 统一后才能与改动清单、harness 前缀做可靠比较。
 */
export function normalizePath(value) {
  let path = String(value ?? '').trim().replaceAll('\\', '/');
  while (path.startsWith('./')) path = path.slice(2);
  return path;
}

/**
 * classifyFile —— 把改动文件归类，决定谁审、怎么审。
 *
 * 输入：仓库相对路径。
 * 输出：'binary' | 'generated' | 'code' | 'doc' | 'ci' | 'other' 之一。
 * 为什么需要：
 *   - 生成资产与二进制不能被要求「补注释/补文档」，也不能仅凭文本 diff 判定内容正确；
 *   - 代码/文档/CI 的分类决定各角色是否「本次改动与自己无关」。
 */
export function classifyFile(filePath) {
  const path = normalizePath(filePath);
  const lower = path.toLowerCase();

  if (BINARY_EXTENSIONS.some((ext) => lower.endsWith(ext))) return 'binary';
  if (path.startsWith('Assets/Spotlight/Generated/')) return 'generated';
  if (path.endsWith('.cs') || path.endsWith('.asmdef') || path.endsWith('.asmref')) return 'code';
  if (path === 'AGENTS.md' || path.startsWith('docs/') || lower.endsWith('.md')) return 'doc';
  if (path.startsWith('.github/') || path.startsWith('scripts/')) return 'ci';
  return 'other';
}

/**
 * isReviewableClass —— 该类别是否参与「文本语义审查」。
 *
 * 输入：classifyFile 的输出。
 * 输出：true 表示应把它的 diff 交给审查角色；false 表示只登记、不做语义判定。
 * 为什么需要：生成资产与二进制由生成流程、配置校验、LFS 检查负责，AI 判不了也不该判。
 */
export function isReviewableClass(fileClass) {
  return fileClass !== 'binary' && fileClass !== 'generated';
}

/**
 * isHarnessPath —— 判断某路径是否属于「审查基础设施」。
 *
 * 输入：仓库相对路径。
 * 输出：布尔值。
 * 为什么需要：PR 修改审查提示词或校验脚本时，其审查结论需要人工留意，
 * 汇总报告必须把这件事显式说出来，而不是悄悄放行。
 */
export function isHarnessPath(filePath) {
  const path = normalizePath(filePath);
  return HARNESS_PREFIXES.some((prefix) =>
    prefix.endsWith('/') ? path.startsWith(prefix) : path === prefix,
  );
}

/**
 * parseGitNameStatus —— 解析 `git diff --name-status -M` 的输出。
 *
 * 输入：逐行的 git 输出文本（字段以制表符分隔，路径中不出现制表符/换行）。
 * 输出：{ status, path, previousPath } 数组。
 *   status 取 git 的字母（A/M/D/R/C/T/U/X），R/C 时 previousPath 为原名。
 * 为什么需要：审查范围必须包含「重命名」与「删除」，只看新增行会漏掉被删掉的防护逻辑。
 */
export function parseGitNameStatus(text) {
  const entries = [];
  for (const rawLine of String(text ?? '').split('\n')) {
    const line = rawLine.replace(/\r$/, '');
    if (line.trim() === '') continue;
    const parts = line.split('\t');
    if (parts.length < 2) continue;
    const status = parts[0].trim().charAt(0).toUpperCase();
    if (status === 'R' || status === 'C') {
      if (parts.length < 3) continue;
      entries.push({
        status,
        previousPath: normalizePath(parts[1]),
        path: normalizePath(parts[2]),
      });
    } else {
      entries.push({ status, path: normalizePath(parts[1]) });
    }
  }
  return entries;
}

/**
 * parseGitNumstat —— 解析 `git diff --numstat -z` 的输出为「新路径 → 增删行数」。
 *
 * 输入：以 NUL 分隔的 numstat 字段文本；二进制文件的行数是 `-`，按 null 处理。
 * 输出：Map<路径, { additions: number|null, deletions: number|null }>。
 * 为什么必须配 `-z`：不加 `-z` 时重命名会被压成 `Core/{A.cs => B.cs}` 这种单字段写法
 * （路径含特殊字符时还会变成带引号的 `"a" => "b"`），而 buildChangeList 是按**新路径**
 * 查表的，查不到就把文本文件误标成「二进制」并丢掉增删行数。
 * `-z` 把头部统计、旧路径、新路径拆成独立字段，结果与 name-status 的新路径天然对齐。
 *
 * 格式（实测）：
 *   普通：`<add>\t<del>\t<path>\0`
 *   重命名/复制：`<add>\t<del>\t\0<oldPath>\0<newPath>\0`
 */
export function parseGitNumstat(text) {
  const stats = new Map();
  const fields = String(text ?? '').split('\0');
  const toNumber = (value) => (/^\d+$/.test(value) ? Number(value) : null);

  for (let index = 0; index < fields.length; index += 1) {
    const header = fields[index];
    if (header === '') continue;
    const parts = header.split('\t');
    if (parts.length < 3) continue;

    const additions = toNumber(parts[0]);
    const deletions = toNumber(parts[1]);
    const inlinePath = parts.slice(2).join('\t');

    if (inlinePath !== '') {
      stats.set(normalizePath(inlinePath), { additions, deletions });
      continue;
    }

    // 重命名/复制：第三字段为空，紧随其后的两个字段分别是旧路径与新路径。
    // 统计值记在新路径上（与 name-status 的 entry.path 一致），旧路径由 previousPath 表达。
    const newPath = fields[index + 2];
    index += 2;
    if (newPath !== undefined && newPath !== '') {
      stats.set(normalizePath(newPath), { additions, deletions });
    }
  }
  return stats;
}

/**
 * buildChangeList —— 合并 name-status 与 numstat，得到带分类的改动清单。
 *
 * 输入：parseGitNameStatus 的结果、parseGitNumstat 的结果。
 * 输出：{ path, previousPath, status, additions, deletions, class } 数组（顺序与入参一致）。
 * 为什么需要：后续的 diff 生成、角色范围判断、汇总展示都以这份清单为唯一事实来源，
 * 避免三处各自解析 git 输出而产生不一致。
 */
export function buildChangeList(nameStatusEntries, numstatMap) {
  return (nameStatusEntries ?? []).map((entry) => {
    const stats = numstatMap?.get?.(entry.path) ?? null;
    return {
      path: entry.path,
      previousPath: entry.previousPath ?? null,
      status: entry.status,
      additions: stats ? stats.additions : null,
      deletions: stats ? stats.deletions : null,
      class: classifyFile(entry.path),
    };
  });
}

/**
 * splitReviewScope —— 把改动清单拆成「参与语义审查」与「排除在外」两组。
 *
 * 输入：buildChangeList 的结果。
 * 输出：{ reviewable: 文件数组, excluded: [{ file, reason }] }。
 * 为什么需要：diff 只对可审查文件生成，避免生成资产/二进制把 diff 撑爆，
 * 同时让每次运行都留一份「哪些东西没有被语义审查」的显式记录。
 */
export function splitReviewScope(files) {
  const reviewable = [];
  const excluded = [];
  for (const file of files ?? []) {
    if (!isReviewableClass(file.class)) {
      excluded.push({
        file,
        reason:
          file.class === 'binary'
            ? '二进制内容，无法做文本语义审查（由生成流程、配置校验与 LFS 检查负责）'
            : '生成资产（只读产物，禁止手工修改，由重新编译与配置校验负责）',
      });
      continue;
    }
    reviewable.push(file);
  }
  return { reviewable, excluded };
}

/**
 * reviewablePathspec —— 生成 `git diff` 用的路径参数。
 *
 * 输入：可审查文件数组。
 * 输出：去重后的路径字符串数组（重命名会同时包含旧路径与新路径）。
 * 为什么同时给旧路径：只给新路径时 git 可能把重命名退化成「新增文件」，
 * 让审查角色看不到被改写的原始内容。
 */
export function reviewablePathspec(reviewableFiles) {
  const paths = new Set();
  for (const file of reviewableFiles ?? []) {
    paths.add(file.path);
    if (file.previousPath) paths.add(file.previousPath);
  }
  return [...paths];
}

/**
 * stripCodeFence —— 去掉模型可能包在 JSON 外面的 Markdown 代码块围栏。
 *
 * 输入：模型最终消息原文。
 * 输出：去围栏并 trim 后的文本。
 * 为什么需要：即使要求「只输出 JSON」，模型仍常加 ```json 围栏；
 * 这属于格式噪声而非内容错误，剥掉比直接判失败更合理。
 */
export function stripCodeFence(text) {
  let value = String(text ?? '').trim();
  const fenced = /^```(?:json|JSON)?\s*\n([\s\S]*?)\n?```$/.exec(value);
  if (fenced) value = fenced[1].trim();
  return value;
}

/**
 * parseReportText —— 把模型输出解析成报告对象。
 *
 * 输入：模型输出文本。
 * 输出：解析后的对象。
 * 抛出：文本不是合法 JSON 对象时抛 Error（调用方负责转成「审查未完成」）。
 */
export function parseReportText(text) {
  const parsed = JSON.parse(stripCodeFence(text));
  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
    throw new Error('报告顶层必须是 JSON 对象');
  }
  return parsed;
}

/**
 * isSafeRelativePath —— 判断字符串是否是安全的仓库相对路径。
 *
 * 输入：任意字符串。
 * 输出：布尔值。
 * 为什么需要：报告里的路径会进日志与汇总，必须排除绝对路径、盘符与 `..`，
 * 避免出现指向仓库之外的位置。
 */
export function isSafeRelativePath(value) {
  const path = normalizePath(value);
  if (path === '') return false;
  if (path.startsWith('/')) return false;
  if (/^[A-Za-z]:/.test(path)) return false;
  if (path.split('/').includes('..')) return false;
  return true;
}

/**
 * validateReport —— 校验单个角色报告是否符合契约。
 *
 * 输入：
 *   report —— 模型输出的报告对象；
 *   options.expectedRole —— 本次 job 声明的角色（报告必须与之一致，防止角色串台）；
 *   options.expectedHeadSha —— 本次被审查的 head SHA（报告必须逐字一致，防止审查了旧提交）。
 * 输出：{ ok, errors, warnings, blocking, advisory, report }。
 *   errors 非空即视为「报告非法 → 审查未完成」；
 *   blocking 为 blocking 级问题，advisory 为非阻断建议。
 * 为什么必须校验 headSha：报告文件是模型写的，若不比对范围，
 * 一次错位的会话就可能让「审查了旧版本」被当成「本次通过」。
 */
export function validateReport(report, options = {}) {
  const errors = [];
  const warnings = [];
  const { expectedRole, expectedHeadSha } = options;

  if (report === null || typeof report !== 'object' || Array.isArray(report)) {
    return { ok: false, errors: ['报告顶层必须是 JSON 对象'], warnings, blocking: [], advisory: [], report: null };
  }

  const expectString = (field, value, { allowEmpty = false } = {}) => {
    if (typeof value !== 'string') {
      errors.push(`字段 ${field} 必须是字符串`);
      return '';
    }
    if (!allowEmpty && value.trim() === '') {
      errors.push(`字段 ${field} 不能为空`);
    }
    return value;
  };

  for (const field of REPORT_REQUIRED_FIELDS) {
    if (!(field in report)) errors.push(`缺少必填字段 ${field}`);
  }

  const role = expectString('role', report.role);
  if (role !== '' && !REVIEW_ROLES.includes(role)) {
    errors.push(`role 取值非法：${role}（允许：${REVIEW_ROLES.join(' / ')}）`);
  }
  if (expectedRole && role !== expectedRole) {
    errors.push(`role 与本次执行的角色不一致：报告为 ${role || '(空)'}，期望 ${expectedRole}`);
  }

  const headSha = expectString('headSha', report.headSha);
  if (expectedHeadSha && headSha !== expectedHeadSha) {
    errors.push(
      `headSha 与被审查范围不一致：报告为 ${headSha || '(空)'}，期望 ${expectedHeadSha}`,
    );
  }

  const status = expectString('status', report.status);
  if (status !== '' && !REPORT_STATUSES.includes(status)) {
    errors.push(`status 取值非法：${status}（允许：${REPORT_STATUSES.join(' / ')}）`);
  }

  expectString('summary', report.summary);

  const statusReason = report.statusReason;
  if (status === 'completed') {
    if (statusReason !== null) {
      errors.push('status 为 completed 时 statusReason 必须是 null');
    }
  } else if (status !== '') {
    if (typeof statusReason !== 'string' || statusReason.trim() === '') {
      errors.push(`status 为 ${status} 时 statusReason 必须是非空字符串`);
    }
  }

  if (!Array.isArray(report.reviewedFiles)) {
    errors.push('reviewedFiles 必须是数组');
  } else {
    report.reviewedFiles.forEach((file, index) => {
      const field = `reviewedFiles[${index}]`;
      if (typeof file !== 'string') {
        errors.push(`${field} 必须是字符串`);
        return;
      }
      if (!isSafeRelativePath(file)) errors.push(`${field} 不是安全的仓库相对路径：${file}`);
    });
    if (status === 'completed' && report.reviewedFiles.length === 0) {
      errors.push('status 为 completed 时 reviewedFiles 不能为空（必须说明实际看了哪些文件）');
    }
  }

  const blocking = [];
  const advisory = [];
  if (!Array.isArray(report.findings)) {
    errors.push('findings 必须是数组');
  } else {
    report.findings.forEach((finding, index) => {
      const field = `findings[${index}]`;
      if (finding === null || typeof finding !== 'object' || Array.isArray(finding)) {
        errors.push(`${field} 必须是对象`);
        return;
      }
      for (const key of FINDING_REQUIRED_FIELDS) {
        if (!(key in finding)) errors.push(`${field} 缺少必填字段 ${key}`);
      }
      const requireFindingString = (key) => {
        const value = finding[key];
        if (typeof value !== 'string' || value.trim() === '') {
          errors.push(`${field}.${key} 必须是非空字符串`);
          return '';
        }
        return value;
      };

      const id = requireFindingString('id');
      const severity = requireFindingString('severity');
      const file = requireFindingString('file');
      requireFindingString('category');
      requireFindingString('rule');
      requireFindingString('evidence');
      requireFindingString('impact');
      requireFindingString('suggestion');

      if (severity !== '' && !SEVERITIES.includes(severity)) {
        errors.push(`${field}.severity 取值非法：${severity}（允许：${SEVERITIES.join(' / ')}）`);
      }
      if (file !== '' && !isSafeRelativePath(file)) {
        errors.push(`${field}.file 不是安全的仓库相对路径：${file}`);
      }
      if (!('line' in finding) || finding.line === null || finding.line === undefined) {
        // line 允许为 null（问题不属于某一行时），但字段本身必须存在。
      } else if (!Number.isInteger(finding.line) || finding.line < 1) {
        errors.push(`${field}.line 必须是 >=1 的整数或 null`);
      }

      const normalized = {
        id,
        severity,
        category: String(finding.category ?? ''),
        file,
        line: Number.isInteger(finding.line) ? finding.line : null,
        rule: String(finding.rule ?? ''),
        evidence: String(finding.evidence ?? ''),
        impact: String(finding.impact ?? ''),
        suggestion: String(finding.suggestion ?? ''),
        role: role || null,
      };
      if (severity === 'blocking') blocking.push(normalized);
      else if (severity === 'advisory') advisory.push(normalized);
    });
  }

  if (status === 'not_applicable' && Array.isArray(report.findings) && report.findings.length > 0) {
    errors.push('status 为 not_applicable 时 findings 必须为空');
  }

  const known = new Set(REPORT_REQUIRED_FIELDS);
  const extra = Object.keys(report).filter((key) => !known.has(key));
  if (extra.length > 0) {
    warnings.push(`报告包含契约之外的字段（已忽略）：${extra.join(', ')}`);
  }

  return { ok: errors.length === 0, errors, warnings, blocking, advisory, report };
}

/**
 * decideVerdict —— 由三个角色的结果得出最终门禁结论。
 *
 * 输入：角色结果数组，每项形如 { role, ok, errors, status, blocking }。
 * 输出：{ status: 'pass' | 'blocked' | 'incomplete', reasons: string[] }。
 * 判定顺序（先看完整性，再看违规）：
 *   1) 任一角色报告缺失或非法 → incomplete（绝不当成通过）；
 *   2) 任一角色 status=insufficient → incomplete；
 *   3) 任一角色存在 blocking 级问题 → blocked；
 *   4) 其余 → pass（advisory 只展示，不阻断）。
 * 为什么 incomplete 优先于 blocked：报告不可信时，连「有没有问题」都无从谈起，
 * 必须把「审查没做完」和「审查发现违规」区分开，前者不能靠修代码解决。
 */
export function decideVerdict(roleResults) {
  const reasons = [];
  const results = roleResults ?? [];

  const missingOrInvalid = results.filter((item) => !item.ok);
  for (const item of missingOrInvalid) {
    reasons.push(`${ROLE_TITLES[item.role] ?? item.role}：报告缺失或格式非法（${item.errors.join('；')}）`);
  }

  const insufficient = results.filter((item) => item.ok && item.status === 'insufficient');
  for (const item of insufficient) {
    reasons.push(`${ROLE_TITLES[item.role] ?? item.role}：未能完整审查（${item.statusReason ?? '未给出原因'}）`);
  }

  if (missingOrInvalid.length > 0 || insufficient.length > 0) {
    return { status: 'incomplete', reasons };
  }

  const blocked = results.filter((item) => (item.blocking ?? []).length > 0);
  for (const item of blocked) {
    reasons.push(`${ROLE_TITLES[item.role] ?? item.role}：存在 ${item.blocking.length} 条必须修复的问题`);
  }
  if (blocked.length > 0) return { status: 'blocked', reasons };

  return { status: 'pass', reasons: [] };
}

/**
 * formatBytes —— 把字节数格式化成便于阅读的字符串。
 *
 * 输入：字节数（number）。
 * 输出：如 "12.3 KiB"。
 * 为什么需要：摘要里要说明 diff 体积与上限，纯数字不直观。
 */
export function formatBytes(value) {
  const bytes = Number(value) || 0;
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KiB`;
  return `${(bytes / (1024 * 1024)).toFixed(2)} MiB`;
}

/**
 * renderTaskHeader —— 生成喂给审查 Agent 的「任务头」（与角色无关的公共部分）。
 *
 * 输入：run-info 对象（见 collect-context.mjs 的产出）与输出目录名。
 * 输出：Markdown 文本。
 * 为什么需要：范围（base/head/merge-base）与硬性输出要求必须对三个角色完全一致，
 * 由脚本生成可以保证它们不会被手写提示词写错，也保证 headSha 一定是本次运行的真实值。
 */
export function renderTaskHeader(runInfo, contextDir = 'codex-review-context') {
  const files = runInfo.files ?? [];
  const excluded = runInfo.excludedPaths ?? [];
  const lines = [
    '# Spotlight AI 审查任务',
    '',
    '你是一名评审者，正在审查一个 Unity 卡牌战棋项目的 Pull Request。',
    '',
    '## 本次审查范围（不可更改）',
    '',
    `- 仓库：${runInfo.repository || '(未知)'}`,
    `- PR 编号：${runInfo.prNumber ?? '(手动触发)'}`,
    `- base SHA：\`${runInfo.baseRev || '(未知)'}\``,
    `- merge-base SHA：\`${runInfo.mergeBaseSha || '(未知)'}\``,
    `- **head SHA（本次唯一被审查的版本）：\`${runInfo.headSha}\`**`,
    `- 审查规则版本：${runInfo.ruleVersion || '(未标注)'}`,
    `- 改动文件数：${files.length}（其中可做语义审查 ${runInfo.reviewablePaths?.length ?? 0} 个）`,
    '',
    '## 硬性要求',
    '',
    `1. 只审查 \`${runInfo.mergeBaseSha || runInfo.baseRev}..${runInfo.headSha}\` 之间的改动，不要审查仓库里的历史遗留问题。`,
    `2. 最终回答必须是一个 JSON 对象，字段与取值严格遵守 \`.github/codex-review/report.schema.json\`，不要在 JSON 之外输出解释性文字。`,
    `3. 报告里的 \`headSha\` 必须逐字等于 \`${runInfo.headSha}\`，否则整份报告会被判为无效。`,
    '4. 只读作业：不得修改、创建或删除仓库中的任何文件，也不得切换分支或提交。',
    '5. 允许用只读方式检索上下文（读文件、搜索关键字、`git diff` / `git log` / `git show`）来确认调用方与影响面。',
    '   注意：运行环境的沙箱是只读的，某些 git 子命令可能因无法写入 `.git` 而失败；',
    '   出现这种情况不要报错，改用上面「上下文文件」里已经准备好的 diff 与清单即可。',
    '6. 每条问题都要能被复核：写清文件、规则依据、具体证据（代码或行为）、影响与修改建议。',
    '7. 没有证据的猜测请降级为 `advisory`，不要用臆测的问题阻断合并。',
    '',
    '## 上下文文件',
    '',
    `- 改动清单：\`${contextDir}/change-list.md\``,
    `- 可审查改动的 diff：\`${contextDir}/diff.patch\``,
    `- 机器可读清单：\`${contextDir}/run-info.json\``,
    '',
  ];

  if (excluded.length > 0) {
    lines.push(
      '## 已排除在语义审查之外的改动',
      '',
      '以下文件本次改动过，但由生成流程 / 配置校验 / LFS 检查负责，不要针对它们提「补注释」「补文档」之类的问题：',
      '',
      ...excluded.map((item) => `- \`${item.path}\`（${item.reason}）`),
      '',
    );
  }

  return lines.join('\n');
}

/**
 * renderChangeListMarkdown —— 生成人可读的改动清单。
 *
 * 输入：run-info 对象。
 * 输出：Markdown 文本（写进 codex-review-context/change-list.md，也会作为产物留档）。
 * 为什么需要：审查角色与人都需要一份「本次到底动了哪些文件」的确定列表，
 * 用来交叉核对 reviewedFiles，避免漏审。
 */
export function renderChangeListMarkdown(runInfo) {
  const { reviewable, excluded } = splitReviewScope(runInfo.files ?? []);
  const statusText = {
    A: '新增', M: '修改', D: '删除', R: '重命名', C: '复制', T: '类型变化',
  };
  const formatStat = (file) => {
    if (file.additions === null && file.deletions === null) return '二进制';
    return `+${file.additions ?? 0}/-${file.deletions ?? 0}`;
  };
  const formatRow = (file) => {
    const status = statusText[file.status] ?? file.status;
    const rename = file.previousPath ? `（原 \`${file.previousPath}\`）` : '';
    return `| ${status} | ${file.class} | ${formatStat(file)} | \`${file.path}\`${rename} |`;
  };

  const lines = [
    '# 改动清单',
    '',
    `- 仓库：${runInfo.repository || '(未知)'}`,
    `- PR：${runInfo.prNumber ?? '(手动触发)'}`,
    `- base：\`${runInfo.baseRev || '(未知)'}\``,
    `- merge-base：\`${runInfo.mergeBaseSha || '(未知)'}\``,
    `- head：\`${runInfo.headSha}\``,
    `- diff 体积：${formatBytes(runInfo.diffBytes)}（上限 ${formatBytes(runInfo.maxDiffBytes)}）`,
    '',
    `## 参与语义审查（${reviewable.length}）`,
    '',
  ];

  if (reviewable.length === 0) {
    lines.push('（无）', '');
  } else {
    lines.push('| 状态 | 类别 | 增/删 | 路径 |', '|---|---|---|---|', ...reviewable.map(formatRow), '');
  }

  lines.push(`## 排除在语义审查之外（${excluded.length}）`, '');
  if (excluded.length === 0) {
    lines.push('（无）', '');
  } else {
    lines.push('| 状态 | 类别 | 增/删 | 路径 |', '|---|---|---|---|', ...excluded.map(formatRow), '');
  }

  return lines.join('\n');
}

/**
 * renderSummaryMarkdown —— 生成最终的门禁汇总 Markdown。
 *
 * 输入：
 *   verdict —— decideVerdict 的输出；
 *   roleResults —— 角色结果数组（含 findings 与 reviewedFiles）；
 *   runInfo —— collect-context 的产出（可能为 null，表示准备阶段就失败了）；
 *   options.harnessChanged —— 本次 PR 是否改动了审查基础设施。
 * 输出：Markdown 文本。
 * 为什么需要：Actions 摘要与产物要能独立看懂「谁审了、结果如何、为什么红灯」，
 * 不能只留一句 exit code。
 */
export function renderSummaryMarkdown(verdict, roleResults, runInfo, options = {}) {
  const statusBadge = {
    pass: '✅ 通过',
    blocked: '❌ 审查不通过',
    incomplete: '⚠️ 审查未完成',
  };
  const lines = [
    '# AI 审查门禁',
    '',
    `**结论：${statusBadge[verdict.status] ?? verdict.status}**`,
    '',
  ];

  if (verdict.reasons.length > 0) {
    lines.push('## 未通过的原因', '', ...verdict.reasons.map((reason) => `- ${reason}`), '');
  }

  if (runInfo) {
    lines.push(
      '## 审查范围',
      '',
      `- PR：${runInfo.prNumber ?? '(手动触发)'}`,
      `- base：\`${runInfo.baseRev}\``,
      `- merge-base：\`${runInfo.mergeBaseSha}\``,
      `- head：\`${runInfo.headSha}\``,
      `- 改动文件：${(runInfo.files ?? []).length} 个（可语义审查 ${(runInfo.reviewablePaths ?? []).length} 个，排除 ${(runInfo.excludedPaths ?? []).length} 个）`,
      `- 审查规则版本：${runInfo.ruleVersion || '(未标注)'}`,
      '',
    );
  } else {
    lines.push('## 审查范围', '', '- 上下文未生成（准备阶段失败），无法确定审查范围。', '');
  }

  if (options.harnessChanged) {
    lines.push(
      '> ⚠️ **本次 PR 修改了 AI 审查基础设施**（提示词、报告契约、校验脚本或本工作流）。',
      '> 这类改动会让「审查规则」本身发生变化，请人工确认其合理性后再合并。',
      `> 涉及：${(options.harnessPaths ?? []).join('、')}`,
      '',
    );
  }

  lines.push('## 三个角色的结果', '', '| 角色 | 状态 | 已审查文件 | 阻断问题 | 建议 |', '|---|---|---|---|---|');
  for (const item of roleResults) {
    const title = ROLE_TITLES[item.role] ?? item.role;
    if (!item.ok) {
      lines.push(`| ${title} | ❌ 报告非法/缺失 | - | - | - |`);
      continue;
    }
    const statusText = {
      completed: '✅ 完成',
      insufficient: '⚠️ 未完成',
      not_applicable: '➖ 不适用',
    }[item.status] ?? item.status;
    lines.push(
      `| ${title} | ${statusText} | ${(item.reviewedFiles ?? []).length} | ${(item.blocking ?? []).length} | ${(item.advisory ?? []).length} |`,
    );
  }
  lines.push('');

  for (const item of roleResults) {
    const title = ROLE_TITLES[item.role] ?? item.role;
    lines.push(`## ${title}`, '');
    if (!item.ok) {
      lines.push('报告缺失或不符合契约，视为「审查未完成」：', '');
      lines.push(...item.errors.map((error) => `- ${error}`), '');
      continue;
    }
    lines.push(`- 状态：${item.status}${item.statusReason ? `（${item.statusReason}）` : ''}`);
    lines.push(`- 摘要：${item.summary}`);
    if ((item.reviewedFiles ?? []).length > 0) {
      lines.push(`- 已审查：${item.reviewedFiles.map((file) => `\`${file}\``).join('、')}`);
    }
    if ((item.warnings ?? []).length > 0) {
      lines.push(`- 提示：${item.warnings.join('；')}`);
    }
    lines.push('');

    const blocking = item.blocking ?? [];
    const advisory = item.advisory ?? [];
    if (blocking.length > 0) {
      lines.push('### 必须修复', '');
      for (const finding of blocking) {
        lines.push(...renderFinding(finding));
      }
    }
    if (advisory.length > 0) {
      lines.push('### 建议（不阻断合并）', '');
      for (const finding of advisory) {
        lines.push(...renderFinding(finding));
      }
    }
    if (blocking.length === 0 && advisory.length === 0 && item.status === 'completed') {
      lines.push('未发现问题。', '');
    }
  }

  return lines.join('\n');
}

/**
 * renderFinding —— 把一条问题渲染成 Markdown 片段。
 *
 * 输入：规范化后的问题对象（见 validateReport）。
 * 输出：Markdown 行数组，便于调用方一次性 push 进汇总。
 * 为什么需要：汇总里每条问题必须自带「位置 + 依据 + 证据 + 影响 + 修复建议」，
 * 否则作者无法据此判断该不该改。
 */
export function renderFinding(finding) {
  const location = finding.line ? `\`${finding.file}:${finding.line}\`` : `\`${finding.file}\``;
  return [
    `- **[${finding.id}]** ${location}（${finding.category}）`,
    `  - 依据：${finding.rule}`,
    `  - 证据：${finding.evidence}`,
    `  - 影响：${finding.impact}`,
    `  - 建议：${finding.suggestion}`,
    '',
  ];
}
