/**
 * codex-review.test.mjs —— AI 审查流程的离线测试（不调用任何付费 API、不依赖 Unity）。
 *
 * 【作用】
 *   验证三件事：
 *   1. 纯函数层（lib.mjs）：文件分类、git 输出解析、报告契约校验、结论判定；
 *   2. 三个 CLI 入口在真实 git 仓库上的端到端行为（含中文路径、重命名、超限失败）；
 *   3. 仓库内静态约束：报告契约与校验代码的必填字段一致、workflow 保持并行与门禁语义。
 *
 * 【输入 / 输出】
 *   无入参。用 `node --test scripts/codex-review/tests/codex-review.test.mjs` 运行；
 *   全部通过退出 0，任一断言失败退出非 0。
 *
 * 【为什么必须离线可跑】
 *   门禁的核心规则是「报告缺失必须判未完成」「有 blocking 问题必须判不通过」这类语义。
 *   如果只有靠真实 PR + 真实 API 才能验证，它们就会在每次改动后失去保护。
 *   这里用夹具构造这几种情况，让规则随时可复现。
 */

import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import {
  FINDING_REQUIRED_FIELDS,
  REASONING_EFFORTS,
  REPORT_REQUIRED_FIELDS,
  buildChangeList,
  classifyFile,
  decideVerdict,
  isHarnessPath,
  isSafeRelativePath,
  isReviewableClass,
  isValidEffort,
  parseGitNameStatus,
  parseGitNumstat,
  parseReportText,
  renderSummaryMarkdown,
  renderTaskHeader,
  reviewablePathspec,
  splitReviewScope,
  stripCodeFence,
  validateReport,
} from '../lib.mjs';

/** 仓库根目录（本文件位于 scripts/codex-review/tests/，向上三级）。 */
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const scriptsDir = path.join(repoRoot, 'scripts', 'codex-review');

/**
 * makeTempRepo —— 建一个临时 git 仓库夹具。
 *
 * 输入：无。
 * 输出：仓库绝对路径。
 * 为什么用真实 git 仓库：审查范围的计算（merge-base、重命名检测、路径转义）只能用真 git 验证，
 * 手写假数据会把最容易出错的解析部分绕过去。
 */
function makeTempRepo() {
  const dir = mkdtempSync(path.join(os.tmpdir(), 'spotlight-codex-review-'));
  const git = (...args) =>
    execFileSync('git', ['-c', 'core.quotePath=true', '-c', 'core.autocrlf=false', ...args], {
      cwd: dir,
      encoding: 'utf8',
    });
  git('init', '-b', 'main');
  git('config', 'user.email', 'ci@example.com');
  git('config', 'user.name', 'CI');
  return dir;
}

/**
 * writeAndCommit —— 写入若干文件并提交。
 *
 * 输入：仓库路径、提交信息、{ 相对路径: 内容 } 映射。
 * 输出：本次提交的 SHA。
 * 为什么返回 SHA：审查范围就是拿两个提交的 SHA 表达的，测试必须能引用它们。
 */
function writeAndCommit(dir, message, files) {
  for (const [relative, content] of Object.entries(files)) {
    const absolute = path.join(dir, relative);
    mkdirSync(path.dirname(absolute), { recursive: true });
    writeFileSync(absolute, content, 'utf8');
  }
  const git = (...args) => execFileSync('git', args, { cwd: dir, encoding: 'utf8' });
  git('add', '-A');
  git('commit', '--quiet', '-m', message);
  return git('rev-parse', 'HEAD').trim();
}

/**
 * runCli —— 运行本目录下的 CLI，并捕获退出码与输出。
 *
 * 输入：脚本文件名、参数数组、工作目录。
 * 输出：{ status, stdout, stderr }（status 为 0 时表示正常退出）。
 * 为什么不用 execFileSync 的返回值：它遇到非零退出会抛异常，而这里恰恰要断言非零退出码。
 */
function runCli(script, args, cwd) {
  try {
    const stdout = execFileSync(process.execPath, [path.join(scriptsDir, script), ...args], {
      cwd,
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    return { status: 0, stdout, stderr: '' };
  } catch (error) {
    return {
      status: typeof error.status === 'number' ? error.status : 1,
      stdout: error.stdout?.toString() ?? '',
      stderr: error.stderr?.toString() ?? '',
    };
  }
}

/**
 * validReport —— 构造一份合法报告的三要素（角色 / head / 状态）。
 *
 * 输入：字段覆盖对象。
 * 输出：可直接喂给 validateReport 的报告对象。
 * 为什么需要：绝大多数用例只关心某一个字段的差异，其余字段必须是合法值，
 * 否则断言会淹没在无关错误里。
 */
function validReport(overrides = {}) {
  return {
    role: 'code',
    headSha: 'a'.repeat(40),
    status: 'completed',
    statusReason: null,
    summary: '本次改动没有发现问题。',
    reviewedFiles: ['Assets/Spotlight/Core/A.cs'],
    findings: [],
    ...overrides,
  };
}

/** validFinding —— 构造一条合法问题。 */
function validFinding(overrides = {}) {
  return {
    id: 'BUG-1',
    severity: 'blocking',
    category: '逻辑错误',
    file: 'Assets/Spotlight/Core/A.cs',
    line: 12,
    rule: 'AGENTS.md 第 4.1 节：命令失败时状态不得被修改',
    evidence: '失败分支已经写入了状态后才返回错误。',
    impact: '失败的命令会留下半更新状态。',
    suggestion: '把状态写入移到所有校验通过之后。',
    ...overrides,
  };
}

// ---------------------------------------------------------------------------
// 一、纯函数：文件分类与 git 输出解析
// ---------------------------------------------------------------------------

test('classifyFile：按扩展名与目录区分生成资产、二进制、代码、文档与 CI', () => {
  assert.equal(classifyFile('Assets/Spotlight/Generated/Cards/x.asset'), 'generated');
  assert.equal(classifyFile('Assets/Spotlight/Generated/Cards/x.asset.meta'), 'generated');
  assert.equal(classifyFile('docs/界面预览.png'), 'binary');
  assert.equal(classifyFile('Assets/Spotlight/Core/GameState.cs'), 'code');
  assert.equal(classifyFile('Assets/Spotlight/Core/Spotlight.Core.asmdef'), 'code');
  assert.equal(classifyFile('docs/玩法.md'), 'doc');
  assert.equal(classifyFile('AGENTS.md'), 'doc');
  assert.equal(classifyFile('.github/workflows/unity-tests.yml'), 'ci');
  assert.equal(classifyFile('scripts/check-ci-config.sh'), 'ci');
  assert.equal(classifyFile('ProjectSettings/ProjectVersion.txt'), 'other');
});

test('isReviewableClass：生成资产与二进制不参与文本语义审查', () => {
  assert.equal(isReviewableClass('code'), true);
  assert.equal(isReviewableClass('doc'), true);
  assert.equal(isReviewableClass('ci'), true);
  assert.equal(isReviewableClass('other'), true);
  assert.equal(isReviewableClass('generated'), false);
  assert.equal(isReviewableClass('binary'), false);
});

test('parseGitNameStatus：覆盖新增/修改/删除与重命名（含旧路径）', () => {
  const text = [
    'A\tAssets/Spotlight/Core/New.cs',
    'M\tdocs/玩法.md',
    'D\tAssets/Spotlight/Core/Old.cs',
    'R100\tAssets/Spotlight/Core/A.cs\tAssets/Spotlight/Core/B.cs',
    '',
  ].join('\n');
  assert.deepEqual(parseGitNameStatus(text), [
    { status: 'A', path: 'Assets/Spotlight/Core/New.cs' },
    { status: 'M', path: 'docs/玩法.md' },
    { status: 'D', path: 'Assets/Spotlight/Core/Old.cs' },
    {
      status: 'R',
      previousPath: 'Assets/Spotlight/Core/A.cs',
      path: 'Assets/Spotlight/Core/B.cs',
    },
  ]);
});

test('parseGitNumstat：解析 -z 输出，重命名按新路径关联、二进制记作 null', () => {
  // 为什么用 NUL 分隔的夹具：collect-context 必须传 `-z`，否则重命名会被压成
  // `Core/{A.cs => B.cs}` 这种单字段写法，按新路径查表就查不到（见 lib.mjs 注释）。
  const text = [
    '3\t1\tAssets/Spotlight/Core/A.cs\0',
    '-\t-\tdocs/界面预览.png\0',
    '4\t2\t\0Assets/Spotlight/Core/Old.cs\0Assets/Spotlight/Core/New.cs\0',
  ].join('');
  const stats = parseGitNumstat(text);

  assert.deepEqual(stats.get('Assets/Spotlight/Core/A.cs'), { additions: 3, deletions: 1 });
  assert.deepEqual(stats.get('docs/界面预览.png'), { additions: null, deletions: null });
  assert.deepEqual(stats.get('Assets/Spotlight/Core/New.cs'), { additions: 4, deletions: 2 });
  assert.equal(stats.has('Assets/Spotlight/Core/Old.cs'), false, '旧路径不应成为统计键');
});

test('buildChangeList + splitReviewScope：生成资产与二进制被排除且带原因', () => {
  const entries = parseGitNameStatus(
    ['M\tAssets/Spotlight/Core/A.cs', 'M\tdocs/玩法.md', 'A\tAssets/Spotlight/Generated/Cards/x.asset', 'M\tdocs/界面预览.png', ''].join('\n'),
  );
  const list = buildChangeList(entries, parseGitNumstat('1\t1\tAssets/Spotlight/Core/A.cs\0'));
  const { reviewable, excluded } = splitReviewScope(list);

  assert.deepEqual(reviewable.map((file) => file.path), ['Assets/Spotlight/Core/A.cs', 'docs/玩法.md']);
  assert.deepEqual(excluded.map((item) => item.file.path), [
    'Assets/Spotlight/Generated/Cards/x.asset',
    'docs/界面预览.png',
  ]);
  assert.match(excluded[0].reason, /生成资产/);
  assert.match(excluded[1].reason, /二进制/);
  assert.deepEqual(list[0].additions, 1);
  assert.equal(list[0].class, 'code');
});

test('reviewablePathspec：重命名时同时给出新旧路径', () => {
  const list = buildChangeList(
    parseGitNameStatus('R100\told/A.cs\tnew/B.cs\n'),
    new Map(),
  );
  const { reviewable } = splitReviewScope(list);
  assert.deepEqual(reviewablePathspec(reviewable).sort(), ['new/B.cs', 'old/A.cs']);
});

test('isHarnessPath：能识别提示词、报告契约、校验脚本与工作流自身', () => {
  assert.equal(isHarnessPath('.github/codex-review/prompts/code.md'), true);
  assert.equal(isHarnessPath('scripts/codex-review/lib.mjs'), true);
  assert.equal(isHarnessPath('.github/workflows/codex-review.yml'), true);
  assert.equal(isHarnessPath('Assets/Spotlight/Core/GameState.cs'), false);
  assert.equal(isHarnessPath('.github/workflows/unity-tests.yml'), false);
});

// ---------------------------------------------------------------------------
// 二、纯函数：报告解析与契约校验
// ---------------------------------------------------------------------------

test('stripCodeFence / parseReportText：容忍被 ```json 围栏包裹的 JSON', () => {
  const raw = '```json\n{"role":"code"}\n```';
  assert.equal(stripCodeFence(raw), '{"role":"code"}');
  assert.deepEqual(parseReportText(raw), { role: 'code' });
  assert.deepEqual(parseReportText('  {"role":"code"}  '), { role: 'code' });
});

test('parseReportText：非 JSON 与顶层非对象都要抛错', () => {
  assert.throws(() => parseReportText('这不是 JSON'));
  assert.throws(() => parseReportText('[1,2,3]'));
});

test('isSafeRelativePath：拒绝绝对路径、盘符与目录穿越', () => {
  assert.equal(isSafeRelativePath('docs/玩法.md'), true);
  assert.equal(isSafeRelativePath('./docs/玩法.md'), true);
  assert.equal(isSafeRelativePath('/etc/passwd'), false);
  assert.equal(isSafeRelativePath('C:/Windows/system32'), false);
  assert.equal(isSafeRelativePath('../outside.md'), false);
  assert.equal(isSafeRelativePath(''), false);
});

test('validateReport：合法报告通过，且 blocking/advisory 正确分流', () => {
  const result = validateReport(
    validReport({
      findings: [
        validFinding({ id: 'BUG-1', severity: 'blocking' }),
        validFinding({ id: 'SUG-1', severity: 'advisory', line: null }),
      ],
    }),
    { expectedRole: 'code', expectedHeadSha: 'a'.repeat(40) },
  );
  assert.equal(result.ok, true);
  assert.equal(result.errors.length, 0);
  assert.equal(result.blocking.length, 1);
  assert.equal(result.advisory.length, 1);
});

test('validateReport：角色串台与 headSha 错位都必须判非法', () => {
  const wrongRole = validateReport(validReport({ role: 'docs' }), {
    expectedRole: 'code',
    expectedHeadSha: 'a'.repeat(40),
  });
  assert.equal(wrongRole.ok, false);
  assert.match(wrongRole.errors.join('；'), /role 与本次执行的角色不一致/);

  const wrongHead = validateReport(validReport({ headSha: 'b'.repeat(40) }), {
    expectedRole: 'code',
    expectedHeadSha: 'a'.repeat(40),
  });
  assert.equal(wrongHead.ok, false);
  assert.match(wrongHead.errors.join('；'), /headSha 与被审查范围不一致/);
});

test('validateReport：completed 必须说明看过哪些文件', () => {
  const result = validateReport(validReport({ reviewedFiles: [] }), {});
  assert.equal(result.ok, false);
  assert.match(result.errors.join('；'), /reviewedFiles 不能为空/);
});

test('validateReport：statusReason 与 status 必须匹配', () => {
  const completedWithReason = validateReport(validReport({ statusReason: '随便写点' }), {});
  assert.equal(completedWithReason.ok, false);
  assert.match(completedWithReason.errors.join('；'), /completed 时 statusReason 必须是 null/);

  const insufficientWithoutReason = validateReport(
    validReport({ status: 'insufficient', statusReason: null }),
    {},
  );
  assert.equal(insufficientWithoutReason.ok, false);
  assert.match(insufficientWithoutReason.errors.join('；'), /statusReason 必须是非空字符串/);

  const insufficientOk = validateReport(
    validReport({ status: 'insufficient', statusReason: 'diff 过大，未能读完', reviewedFiles: [] }),
    {},
  );
  assert.equal(insufficientOk.ok, true);
});

test('validateReport：not_applicable 必须给原因且不能带问题', () => {
  const noReason = validateReport(
    validReport({ status: 'not_applicable', statusReason: null, reviewedFiles: [] }),
    {},
  );
  assert.equal(noReason.ok, false);
  assert.match(noReason.errors.join('；'), /statusReason 必须是非空字符串/);

  const withFindings = validateReport(
    validReport({ status: 'not_applicable', statusReason: '只改了图片', reviewedFiles: [], findings: [validFinding()] }),
    {},
  );
  assert.equal(withFindings.ok, false);
  assert.match(withFindings.errors.join('；'), /findings 必须为空/);

  const ok = validateReport(
    validReport({ status: 'not_applicable', statusReason: '只改了图片', reviewedFiles: [] }),
    {},
  );
  assert.equal(ok.ok, true);
});

test('validateReport：非法 severity、缺失字段与不安全路径都要报错', () => {
  const badSeverity = validateReport(validReport({ findings: [validFinding({ severity: 'major' })] }), {});
  assert.equal(badSeverity.ok, false);
  assert.match(badSeverity.errors.join('；'), /severity 取值非法/);

  const missingField = validReport({ findings: [validFinding()] });
  delete missingField.findings[0].evidence;
  const missing = validateReport(missingField, {});
  assert.equal(missing.ok, false);
  assert.match(missing.errors.join('；'), /缺少必填字段 evidence/);

  const unsafe = validateReport(validReport({ findings: [validFinding({ file: '/etc/passwd' })] }), {});
  assert.equal(unsafe.ok, false);
  assert.match(unsafe.errors.join('；'), /不是安全的仓库相对路径/);

  const badLine = validateReport(validReport({ findings: [validFinding({ line: 0 })] }), {});
  assert.equal(badLine.ok, false);
  assert.match(badLine.errors.join('；'), /line 必须是 >=1 的整数或 null/);
});

test('validateReport：契约之外的字段只提示，不算失败', () => {
  const result = validateReport(validReport({ note: '模型自由发挥' }), {});
  assert.equal(result.ok, true);
  assert.match(result.warnings.join('；'), /契约之外的字段/);
});

// ---------------------------------------------------------------------------
// 三、纯函数：门禁结论
// ---------------------------------------------------------------------------

test('decideVerdict：三份报告齐全且无 blocking 时通过', () => {
  const verdict = decideVerdict([
    { role: 'code', ok: true, status: 'completed', blocking: [] },
    { role: 'docs', ok: true, status: 'completed', blocking: [] },
    { role: 'comments', ok: true, status: 'not_applicable', blocking: [] },
  ]);
  assert.equal(verdict.status, 'pass');
});

test('decideVerdict：有 blocking 问题即判「审查不通过」', () => {
  const verdict = decideVerdict([
    { role: 'code', ok: true, status: 'completed', blocking: [validFinding()] },
    { role: 'docs', ok: true, status: 'completed', blocking: [] },
    { role: 'comments', ok: true, status: 'completed', blocking: [] },
  ]);
  assert.equal(verdict.status, 'blocked');
  assert.match(verdict.reasons.join('；'), /代码与规范审查/);
});

test('decideVerdict：报告缺失或非法时判「审查未完成」而不是通过', () => {
  const missing = decideVerdict([
    { role: 'code', ok: false, errors: ['未找到报告'], blocking: [] },
    { role: 'docs', ok: true, status: 'completed', blocking: [] },
    { role: 'comments', ok: true, status: 'completed', blocking: [] },
  ]);
  assert.equal(missing.status, 'incomplete');

  const insufficient = decideVerdict([
    { role: 'code', ok: true, status: 'insufficient', statusReason: '上下文超限', blocking: [] },
    { role: 'docs', ok: true, status: 'completed', blocking: [] },
    { role: 'comments', ok: true, status: 'completed', blocking: [] },
  ]);
  assert.equal(insufficient.status, 'incomplete');
  assert.match(insufficient.reasons.join('；'), /未能完整审查/);
});

test('decideVerdict：完整性优先于违规（报告不可信时先修流程）', () => {
  const verdict = decideVerdict([
    { role: 'code', ok: false, errors: ['JSON 非法'], blocking: [] },
    { role: 'docs', ok: true, status: 'completed', blocking: [validFinding()] },
    { role: 'comments', ok: true, status: 'completed', blocking: [] },
  ]);
  assert.equal(verdict.status, 'incomplete');
});

test('renderSummaryMarkdown：区分三种结论，并显著提示审查基础设施被改动', () => {
  const roleResults = [
    { role: 'code', ok: true, status: 'completed', summary: '没问题', reviewedFiles: ['a.cs'], blocking: [], advisory: [], warnings: [] },
    { role: 'docs', ok: false, errors: ['报告缺失'], blocking: [], advisory: [] },
    { role: 'comments', ok: true, status: 'not_applicable', statusReason: '只改了图片', summary: '不适用', reviewedFiles: [], blocking: [], advisory: [], warnings: [] },
  ];
  const markdown = renderSummaryMarkdown(
    { status: 'incomplete', reasons: ['文档一致性审查：报告缺失或格式非法（报告缺失）'] },
    roleResults,
    { prNumber: 42, baseRev: 'b', mergeBaseSha: 'm', headSha: 'h', files: [], reviewablePaths: [], excludedPaths: [], ruleVersion: 'v1' },
    { harnessChanged: true, harnessPaths: ['.github/codex-review/prompts/code.md'] },
  );

  assert.match(markdown, /⚠️ 审查未完成/);
  assert.match(markdown, /报告缺失/);
  assert.match(markdown, /修改了 AI 审查基础设施/);
  assert.match(markdown, /codex-review\/prompts\/code\.md/);
  assert.match(markdown, /➖ 不适用/);
});

test('renderTaskHeader：把范围与 headSha 写死进任务头', () => {
  const header = renderTaskHeader(
    {
      repository: 'o/r',
      prNumber: 7,
      baseRev: 'base',
      mergeBaseSha: 'merge',
      headSha: 'head1234567890abcdef',
      ruleVersion: 'v1',
      files: [{ path: 'a.asset' }],
      reviewablePaths: ['a.cs'],
      excludedPaths: [{ path: 'a.asset', class: 'generated', reason: '生成资产' }],
    },
    'codex-review-context',
  );
  assert.match(header, /head SHA（本次唯一被审查的版本）：`head1234567890abcdef`/);
  assert.match(header, /只读作业/);
  assert.match(header, /codex-review-context\/diff\.patch/);
  assert.match(header, /已排除在语义审查之外的改动/);
});

// ---------------------------------------------------------------------------
// 四、静态约束：契约与 workflow 结构
// ---------------------------------------------------------------------------

test('报告契约与校验代码的必填字段保持一致', () => {
  const schema = JSON.parse(
    readFileSync(path.join(repoRoot, '.github', 'codex-review', 'report.schema.json'), 'utf8'),
  );
  assert.deepEqual([...schema.required].sort(), [...REPORT_REQUIRED_FIELDS].sort());
  assert.deepEqual(
    [...schema.properties.findings.items.required].sort(),
    [...FINDING_REQUIRED_FIELDS].sort(),
  );
  // OpenAI 严格结构化输出要求 properties 的每个键都出现在 required 里，
  // 因此这里显式断言，避免以后加字段时漏掉。
  assert.deepEqual(
    Object.keys(schema.properties).sort(),
    [...REPORT_REQUIRED_FIELDS].sort(),
  );
  assert.deepEqual(
    Object.keys(schema.properties.findings.items.properties).sort(),
    [...FINDING_REQUIRED_FIELDS].sort(),
  );
});

test('isValidEffort：只接受 Codex CLI 的四种推理强度，空值表示交给 CLI 默认', () => {
  for (const effort of REASONING_EFFORTS) assert.equal(isValidEffort(effort), true, effort);
  assert.equal(isValidEffort(''), true);
  assert.equal(isValidEffort('   '), true);
  assert.equal(isValidEffort('HIGH'), false);
  assert.equal(isValidEffort('xhigh'), false);
  assert.equal(isValidEffort('max'), false);
  assert.equal(isValidEffort('none'), false);
});

test('workflow：暴露推理强度配置，且校验取值与 lib 保持一致', () => {
  const workflow = readFileSync(
    path.join(repoRoot, '.github', 'workflows', 'codex-review.yml'),
    'utf8',
  );

  // 必须真的把 effort 传给 action，否则变量配了也不生效。
  assert.match(workflow, /^\s+effort: \$\{\{ steps\.model\.outputs\.effort \}\}$/m);

  // 每个允许的取值都要出现在 workflow 的校验分支里：任何一边加值、另一边忘了加，
  // 都会让「配了却不生效」重现（CLI 不会因为取错值而报错）。
  const caseBlock = /case "\$effort" in([\s\S]*?)\n\s*esac/.exec(workflow);
  assert.ok(caseBlock, 'workflow 里应存在对 $effort 的取值校验');
  for (const effort of REASONING_EFFORTS) {
    assert.ok(caseBlock[1].includes(effort), `$effort 校验缺少取值 ${effort}`);
  }

  // 默认配置必须是从「不传」开始的：显式传 medium 会在供应商不支持 reasoning 字段时把请求弄坏。
  assert.match(workflow, /留空 = 不传该参数/);
});

test('workflow：三个角色并行、fail-fast: false、门禁 job 固定名且 always 执行', () => {
  const workflow = readFileSync(
    path.join(repoRoot, '.github', 'workflows', 'codex-review.yml'),
    'utf8',
  );

  assert.match(workflow, /max-parallel: 3/);
  assert.match(workflow, /fail-fast: false/);
  assert.match(workflow, /name: AI 审查门禁/);
  assert.match(workflow, /if: always\(\)/);
  // 必须显式传入第三方 Responses 地址，否则会静默回落到官方默认地址。
  assert.match(workflow, /responses-api-endpoint:/);
  assert.match(workflow, /output-schema-file:/);
  // 三个角色的规则文件必须都存在。
  for (const role of ['code', 'docs', 'comments']) {
    assert.ok(
      existsSync(path.join(repoRoot, '.github', 'codex-review', 'prompts', `${role}.md`)),
      `缺少角色规则文件 prompts/${role}.md`,
    );
  }
});

/**
 * extractCliFlags —— 从 workflow 文本里抽出「三个 CLI 各自实际用到的 --参数」。
 *
 * 输入：workflow 文件内容。
 * 输出：{ '<脚本名>': ['--flag', ...] }。
 * 为什么从 workflow 里抽而不是手抄一份清单：参数名写错（例如漏掉 kebab-case → camelCase
 * 转换）只在 CI 真实调用时才暴露。直接从 workflow 取，可以保证用例测的就是 CI 会跑的那条命令。
 */
function extractCliFlags(workflowText) {
  const lines = workflowText.split('\n');
  const result = {};
  for (let index = 0; index < lines.length; index += 1) {
    const match = /node scripts\/codex-review\/([A-Za-z0-9._-]+)/.exec(lines[index]);
    if (!match) continue;
    const flags = [];
    for (let next = index + 1; next < lines.length; next += 1) {
      const flagMatch = /^\s+(--[a-z][a-z0-9-]*)/.exec(lines[next]);
      if (!flagMatch) break;
      if (!flags.includes(flagMatch[1])) flags.push(flagMatch[1]);
    }
    result[match[1]] = flags;
  }
  return result;
}

/** 三个 CLI 在 workflow 里用到的参数快照：新增或改名参数时这里必须同步跟改。 */
const EXPECTED_CLI_FLAGS = {
  'collect-context.mjs': [
    '--repo-root', '--out', '--base', '--base-ref', '--head', '--pr-number', '--repository', '--rule-version', '--max-diff-bytes',
  ],
  'validate-report.mjs': ['--role', '--report', '--context', '--head-sha'],
  'summarize.mjs': ['--reports', '--context', '--enabled', '--head-sha-fallback', '--out', '--json-out'],
};

test('workflow 传给三个 CLI 的参数集合与清单一致（参数增删必须同步更新用例）', () => {
  const workflow = readFileSync(
    path.join(repoRoot, '.github', 'workflows', 'codex-review.yml'),
    'utf8',
  );
  assert.deepEqual(extractCliFlags(workflow), EXPECTED_CLI_FLAGS);
});

test('按 workflow 的原样参数调用三个 CLI 都能被正确解析（守住 kebab-case 转换）', (t) => {
  // 这条用例来自一次真实 CI 失败：validate-report.mjs 漏了 kebab-case 转换，
  // 收到 --head-sha 就报「无法识别的参数」并以退出码 2 结束，三个角色全红；
  // 而当时的用例从不传 --head-sha，所以本地全绿。
  const dir = mkdtempSync(path.join(os.tmpdir(), 'spotlight-codex-cli-'));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  const head = 'a1'.repeat(20);
  setupGateFixture(dir, head, {
    code: validReport({ role: 'code', headSha: head }),
    docs: validReport({ role: 'docs', headSha: head }),
    comments: validReport({
      role: 'comments',
      headSha: head,
      status: 'not_applicable',
      statusReason: '本次没有改动 .cs',
      reviewedFiles: [],
    }),
  });

  const validate = runCli(
    'validate-report.mjs',
    ['--role', 'code', '--report', 'reports/code.json', '--context', 'ctx', '--head-sha', head],
    dir,
  );
  assert.equal(validate.status, 0, validate.stderr);

  const summarize = runCli(
    'summarize.mjs',
    [
      '--reports', 'reports', '--context', 'ctx', '--enabled', 'true',
      '--head-sha-fallback', head, '--out', 'summary.md', '--json-out', 'summary.json',
    ],
    dir,
  );
  assert.equal(summarize.status, 0, summarize.stderr);
  assert.equal(JSON.parse(readFileSync(path.join(dir, 'summary.json'), 'utf8')).status, 'pass');

  // collect-context：刻意用空串表示 --pr-number（手动触发时 workflow 就是这么传的），
  // 并同时带上 --base 与 --base-ref——两者只有一个非空是契约。
  const repo = makeTempRepo();
  t.after(() => rmSync(repo, { recursive: true, force: true }));
  const repoHead = writeAndCommit(repo, 'first', { 'docs/玩法.md': 'v2\n' });
  const context = runCli(
    'collect-context.mjs',
    [
      '--repo-root', '.', '--out', 'codex-review-context', '--base', '', '--base-ref', 'main',
      '--head', repoHead, '--pr-number', '', '--repository', 'o/r', '--rule-version', 'v1',
      '--max-diff-bytes', '1048576',
    ],
    repo,
  );
  assert.equal(context.status, 0, context.stderr);
  const runInfo = JSON.parse(
    readFileSync(path.join(repo, 'codex-review-context', 'run-info.json'), 'utf8'),
  );
  assert.equal(runInfo.prNumber, null, '空的 --pr-number 应落成 null 而不是 NaN/0');
  assert.equal(runInfo.headSha, repoHead);
});

test('collect-context --base-ref：detached HEAD 且没有本地 main 时也能解析（CI 上真实翻过车）', (t) => {
  // 这条用例复现的是报告里的阻断问题 BUG-1：
  // 手动触发时把分支名当成 revision 传给 --base，而 actions/checkout 是按 SHA 检出的
  // detached HEAD，本地没有 main 分支（只有 refs/remotes/origin/main），于是准备阶段直接失败。
  const upstream = makeTempRepo();
  t.after(() => rmSync(upstream, { recursive: true, force: true }));
  writeAndCommit(upstream, 'first', { 'docs/玩法.md': 'v1\n' });
  const headCommit = writeAndCommit(upstream, 'second', { 'docs/玩法.md': 'v2\n' });

  const workdir = mkdtempSync(path.join(os.tmpdir(), 'spotlight-codex-detached-'));
  t.after(() => rmSync(workdir, { recursive: true, force: true }));
  const cloneDir = path.join(workdir, 'repo');
  execFileSync('git', ['clone', '--quiet', '--no-hardlinks', upstream, cloneDir], { encoding: 'utf8' });
  const git = (...args) => execFileSync('git', args, { cwd: cloneDir, encoding: 'utf8' });
  git('checkout', '--quiet', '--detach', headCommit);
  git('branch', '--delete', '--force', 'main');

  // 前置条件自检：本地没有 main，但 origin/main 存在
  assert.throws(() => git('rev-parse', '--verify', 'main^{commit}'));
  assert.equal(git('rev-parse', '--verify', 'origin/main^{commit}').trim(), headCommit);

  // 旧写法（把分支名当 revision）必须失败——这正是线上发生过的故障
  const asRevision = runCli(
    'collect-context.mjs',
    ['--repo-root', '.', '--out', 'ctx-a', '--base', 'main', '--head', headCommit],
    cloneDir,
  );
  assert.equal(asRevision.status, 2);
  assert.match(asRevision.stderr, /无法确定审查范围/);

  // 正确写法：--base-ref 由脚本自己回退到 origin/main
  const asRef = runCli(
    'collect-context.mjs',
    ['--repo-root', '.', '--out', 'ctx-b', '--base-ref', 'main', '--head', headCommit],
    cloneDir,
  );
  assert.equal(asRef.status, 0, asRef.stderr);
  const runInfo = JSON.parse(readFileSync(path.join(cloneDir, 'ctx-b', 'run-info.json'), 'utf8'));
  assert.equal(runInfo.baseRev, headCommit);
  assert.equal(runInfo.baseRef, 'origin/main', '报告的 baseRef 应记录实际命中的写法，便于回溯');
  assert.equal(runInfo.mergeBaseSha, headCommit);
});

// ---------------------------------------------------------------------------
// 五、端到端：prepare / validate / summarize 三个 CLI
// ---------------------------------------------------------------------------

test('collect-context：中文路径、重命名、生成资产与二进制的处理', (t) => {
  const dir = makeTempRepo();
  t.after(() => rmSync(dir, { recursive: true, force: true }));

  const base = writeAndCommit(dir, 'first', {
    'docs/玩法.md': '# 玩法 v1\n',
    'Assets/Spotlight/Core/A.cs': [
      'class A',
      '{',
      '    void M1() {}',
      '    void M2() {}',
      '    void M3() {}',
      '    void M4() {}',
      '    void M5() {}',
      '}',
      '',
    ].join('\n'),
    'Assets/Spotlight/Generated/Cards/x.asset': 'asset v1\n',
  });
  // 重命名 + 小改动（保持足够相似度，让 git 仍判定为重命名）+ 新增生成资产 + 新增二进制图片
  const git = (...args) => execFileSync('git', args, { cwd: dir, encoding: 'utf8' });
  git('mv', 'Assets/Spotlight/Core/A.cs', 'Assets/Spotlight/Core/B.cs');
  writeFileSync(
    path.join(dir, 'Assets/Spotlight/Core/B.cs'),
    [
      'class A',
      '{',
      '    void M1() {}',
      '    void M2() {}',
      '    void M3() { /* 改了这一行 */ }',
      '    void M4() {}',
      '    void M5() {}',
      '}',
      '',
    ].join('\n'),
    'utf8',
  );
  writeFileSync(path.join(dir, 'docs/玩法.md'), '# 玩法 v2\n', 'utf8');
  const head = writeAndCommit(dir, 'second', {
    'Assets/Spotlight/Generated/Cards/y.asset': 'asset v2\n',
    'docs/界面预览.png': 'not really a png but treated as binary by extension\n',
  });

  const result = runCli(
    'collect-context.mjs',
    ['--repo-root', '.', '--out', 'codex-review-context', '--base', base, '--head', head, '--pr-number', '12', '--repository', 'o/r', '--rule-version', 'v1'],
    dir,
  );
  assert.equal(result.status, 0, result.stderr);

  const outDir = path.join(dir, 'codex-review-context');
  const runInfo = JSON.parse(readFileSync(path.join(outDir, 'run-info.json'), 'utf8'));

  assert.equal(runInfo.headSha, head);
  assert.equal(runInfo.mergeBaseSha, base);
  assert.equal(runInfo.prNumber, 12);
  assert.equal(runInfo.diffTruncated, false);

  const renamed = runInfo.files.find((file) => file.path === 'Assets/Spotlight/Core/B.cs');
  assert.equal(renamed.status, 'R');
  assert.equal(renamed.previousPath, 'Assets/Spotlight/Core/A.cs');
  assert.equal(renamed.class, 'code');
  // 重命名必须保留真实增删行数：numstat 未配 -z 时曾把文本文件误标成「二进制」
  assert.equal(renamed.additions, 1);
  assert.equal(renamed.deletions, 1);

  assert.deepEqual(
    [...runInfo.reviewablePaths].sort(),
    ['Assets/Spotlight/Core/B.cs', 'docs/玩法.md'],
  );
  assert.deepEqual(
    [...runInfo.excludedPaths].map((item) => item.path).sort(),
    ['Assets/Spotlight/Generated/Cards/y.asset', 'docs/界面预览.png'],
  );

  const diff = readFileSync(path.join(outDir, 'diff.patch'), 'utf8');
  assert.match(diff, /docs\/玩法\.md/);
  assert.ok(!diff.includes('Generated/Cards/y.asset'), 'diff 不应包含生成资产');

  const changeList = readFileSync(path.join(outDir, 'change-list.md'), 'utf8');
  assert.match(changeList, /玩法\.md/);
  // 重命名那一行不应被写成「二进制」（这是改动清单误导审查者的根源）
  const renamedRow = changeList.split('\n').find((line) => line.includes('Core/B.cs'));
  assert.ok(renamedRow, '改动清单应包含重命名后的路径');
  assert.ok(!renamedRow.includes('二进制'), `重命名行不应标成二进制：${renamedRow}`);
  assert.match(renamedRow, /\+1\/-1/);

  const header = readFileSync(path.join(outDir, 'task-header.md'), 'utf8');
  assert.match(header, new RegExp(head));

  // 本次没有改到审查基础设施，不应误报
  assert.equal(runInfo.harnessChanged, false);
});

test('collect-context：改动审查基础设施时会被标记', (t) => {
  const dir = makeTempRepo();
  t.after(() => rmSync(dir, { recursive: true, force: true }));

  const base = writeAndCommit(dir, 'first', { 'README.md': 'hi\n' });
  const head = writeAndCommit(dir, 'second', {
    'scripts/codex-review/lib.mjs': '// tweak\n',
  });

  const result = runCli(
    'collect-context.mjs',
    ['--repo-root', '.', '--out', 'ctx', '--base', base, '--head', head],
    dir,
  );
  assert.equal(result.status, 0, result.stderr);

  const runInfo = JSON.parse(readFileSync(path.join(dir, 'ctx', 'run-info.json'), 'utf8'));
  assert.equal(runInfo.harnessChanged, true);
  assert.deepEqual(runInfo.harnessPaths, ['scripts/codex-review/lib.mjs']);
  assert.match(result.stdout, /改动了审查基础设施/);
});

test('collect-context：diff 超限时以退出码 3 失败并标记截断（绝不静默截断）', (t) => {
  const dir = makeTempRepo();
  t.after(() => rmSync(dir, { recursive: true, force: true }));

  const base = writeAndCommit(dir, 'first', { 'docs/玩法.md': 'v1\n' });
  const head = writeAndCommit(dir, 'second', {
    'docs/玩法.md': `${'新内容\n'.repeat(200)}`,
  });

  const result = runCli(
    'collect-context.mjs',
    ['--repo-root', '.', '--out', 'ctx', '--base', base, '--head', head, '--max-diff-bytes', '64'],
    dir,
  );
  assert.equal(result.status, 3);
  assert.match(result.stderr, /超过上限/);

  const runInfo = JSON.parse(readFileSync(path.join(dir, 'ctx', 'run-info.json'), 'utf8'));
  assert.equal(runInfo.diffTruncated, true);
  assert.ok(!existsSync(path.join(dir, 'ctx', 'diff.patch')), '超限时不应产出会被误信的 diff');
});

test('collect-context：base 解析不了时以退出码 2 失败', (t) => {
  const dir = makeTempRepo();
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  const head = writeAndCommit(dir, 'first', { 'a.txt': 'a\n' });

  const result = runCli(
    'collect-context.mjs',
    ['--repo-root', '.', '--out', 'ctx', '--base', 'no-such-rev', '--head', head],
    dir,
  );
  assert.equal(result.status, 2);
  assert.match(result.stderr, /无法确定审查范围/);
});

test('validate-report：合法报告通过，headSha 错位则失败', (t) => {
  const dir = mkdtempSync(path.join(os.tmpdir(), 'spotlight-codex-report-'));
  t.after(() => rmSync(dir, { recursive: true, force: true }));

  const head = 'c'.repeat(40);
  mkdirSync(path.join(dir, 'ctx'), { recursive: true });
  writeFileSync(
    path.join(dir, 'ctx', 'run-info.json'),
    JSON.stringify({ headSha: head }),
    'utf8',
  );
  mkdirSync(path.join(dir, 'reports'), { recursive: true });
  writeFileSync(
    path.join(dir, 'reports', 'code.json'),
    JSON.stringify(validReport({ headSha: head })),
    'utf8',
  );

  const ok = runCli(
    'validate-report.mjs',
    ['--role', 'code', '--report', 'reports/code.json', '--context', 'ctx'],
    dir,
  );
  assert.equal(ok.status, 0, ok.stderr);
  assert.match(ok.stdout, /结论：✅ 完成/);
  assert.ok(existsSync(path.join(dir, 'reports', 'code.md')), '应写出角色小结');

  writeFileSync(
    path.join(dir, 'reports', 'code.json'),
    JSON.stringify(validReport({ headSha: 'd'.repeat(40) })),
    'utf8',
  );
  const mismatch = runCli(
    'validate-report.mjs',
    ['--role', 'code', '--report', 'reports/code.json', '--context', 'ctx'],
    dir,
  );
  assert.equal(mismatch.status, 1);
  assert.match(mismatch.stderr, /headSha 与被审查范围不一致/);

  const missing = runCli(
    'validate-report.mjs',
    ['--role', 'docs', '--report', 'reports/docs.json', '--context', 'ctx'],
    dir,
  );
  assert.equal(missing.status, 1);
  assert.match(missing.stderr, /报告文件不存在/);
});

/**
 * setupGateFixture —— 为 summarize 的用例铺一份「上下文 + 三份报告」。
 *
 * 输入：目录、head SHA、每个角色要写入的报告（null 表示故意不写，模拟运行失败）。
 * 输出：无。
 * 为什么需要：summarize 的输入完全来自文件系统，夹具必须把「报告缺失」这种真实故障
 * 也表达出来，否则测不到最重要的那条判定分支。
 */
function setupGateFixture(dir, head, reports) {
  mkdirSync(path.join(dir, 'ctx'), { recursive: true });
  writeFileSync(
    path.join(dir, 'ctx', 'run-info.json'),
    JSON.stringify({
      headSha: head,
      baseRev: 'b'.repeat(40),
      mergeBaseSha: 'b'.repeat(40),
      prNumber: 1,
      ruleVersion: 'v1',
      files: [],
      reviewablePaths: [],
      excludedPaths: [],
      harnessChanged: false,
      harnessPaths: [],
    }),
    'utf8',
  );
  mkdirSync(path.join(dir, 'reports'), { recursive: true });
  for (const [role, report] of Object.entries(reports)) {
    if (report === null) continue;
    writeFileSync(path.join(dir, 'reports', `${role}.json`), JSON.stringify(report), 'utf8');
  }
}

test('summarize：未启用时明确写出「未做任何 AI 审查」并以 0 退出', (t) => {
  const dir = mkdtempSync(path.join(os.tmpdir(), 'spotlight-codex-gate-'));
  t.after(() => rmSync(dir, { recursive: true, force: true }));

  const result = runCli(
    'summarize.mjs',
    ['--reports', 'reports', '--context', 'ctx', '--enabled', 'false', '--out', 'summary.md', '--json-out', 'summary.json'],
    dir,
  );
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /未启用（本次未做任何 AI 审查）/);
  assert.equal(JSON.parse(readFileSync(path.join(dir, 'summary.json'), 'utf8')).status, 'disabled');
});

test('summarize：三个角色都完成且无 blocking → 通过', (t) => {
  const dir = mkdtempSync(path.join(os.tmpdir(), 'spotlight-codex-gate-'));
  t.after(() => rmSync(dir, { recursive: true, force: true }));

  const head = 'e'.repeat(40);
  setupGateFixture(dir, head, {
    code: validReport({ role: 'code', headSha: head }),
    docs: validReport({ role: 'docs', headSha: head }),
    comments: validReport({
      role: 'comments',
      headSha: head,
      status: 'not_applicable',
      statusReason: '本次没有改动 .cs',
      reviewedFiles: [],
    }),
  });

  const result = runCli(
    'summarize.mjs',
    ['--reports', 'reports', '--context', 'ctx', '--enabled', 'true', '--out', 'summary.md', '--json-out', 'summary.json'],
    dir,
  );
  assert.equal(result.status, 0, result.stderr);
  assert.equal(JSON.parse(readFileSync(path.join(dir, 'summary.json'), 'utf8')).status, 'pass');
});

test('summarize：有 blocking 问题 → 退出码 1（审查不通过）', (t) => {
  const dir = mkdtempSync(path.join(os.tmpdir(), 'spotlight-codex-gate-'));
  t.after(() => rmSync(dir, { recursive: true, force: true }));

  const head = 'f'.repeat(40);
  setupGateFixture(dir, head, {
    code: validReport({ role: 'code', headSha: head, findings: [validFinding()] }),
    docs: validReport({ role: 'docs', headSha: head }),
    comments: validReport({ role: 'comments', headSha: head }),
  });

  const result = runCli(
    'summarize.mjs',
    ['--reports', 'reports', '--context', 'ctx', '--enabled', 'true', '--out', 'summary.md', '--json-out', 'summary.json'],
    dir,
  );
  assert.equal(result.status, 1);
  assert.match(result.stdout, /必须修复/);
  assert.equal(JSON.parse(readFileSync(path.join(dir, 'summary.json'), 'utf8')).status, 'blocked');
});

test('summarize：报告缺失 → 退出码 2（审查未完成，绝不能当通过）', (t) => {
  const dir = mkdtempSync(path.join(os.tmpdir(), 'spotlight-codex-gate-'));
  t.after(() => rmSync(dir, { recursive: true, force: true }));

  const head = '1'.repeat(40);
  setupGateFixture(dir, head, {
    code: validReport({ role: 'code', headSha: head }),
    docs: null,
    comments: validReport({ role: 'comments', headSha: head }),
  });

  const result = runCli(
    'summarize.mjs',
    ['--reports', 'reports', '--context', 'ctx', '--enabled', 'true', '--out', 'summary.md', '--json-out', 'summary.json'],
    dir,
  );
  assert.equal(result.status, 2);
  assert.match(result.stdout, /审查未完成/);
  assert.equal(JSON.parse(readFileSync(path.join(dir, 'summary.json'), 'utf8')).status, 'incomplete');
});

test('summarize：上下文整体缺失时仍给出「审查未完成」而不是崩溃', (t) => {
  const dir = mkdtempSync(path.join(os.tmpdir(), 'spotlight-codex-gate-'));
  t.after(() => rmSync(dir, { recursive: true, force: true }));

  const result = runCli(
    'summarize.mjs',
    ['--reports', 'reports', '--context', 'ctx', '--enabled', 'true', '--out', 'summary.md', '--json-out', 'summary.json'],
    dir,
  );
  assert.equal(result.status, 2);
  assert.match(result.stdout, /无法确定审查范围|未找到/);
  assert.equal(JSON.parse(readFileSync(path.join(dir, 'summary.json'), 'utf8')).status, 'incomplete');
});

test('summarize：报告齐全但拿不到可信 head SHA 时必须拒绝判通过（fail-closed）', (t) => {
  // 复现的是一轮真实报告里的阻断问题（BUG-1）：
  // 上下文缺失 + HEAD_SHA_FALLBACK 为空（手动触发时 event.pull_request 不存在）
  // → expectedHeadSha 成为空串 → validateReport 只能跳过 SHA 比对，
  // 于是三份**来自不同提交**的报告也能凑成一个 pass。
  const dir = mkdtempSync(path.join(os.tmpdir(), 'spotlight-codex-gate-'));
  t.after(() => rmSync(dir, { recursive: true, force: true }));

  mkdirSync(path.join(dir, 'reports'), { recursive: true });
  for (const [role, sha] of [['code', '1'], ['docs', '2'], ['comments', '3']]) {
    writeFileSync(
      path.join(dir, 'reports', `${role}.json`),
      JSON.stringify(validReport({ role, headSha: sha.repeat(40) })),
      'utf8',
    );
  }

  const result = runCli(
    'summarize.mjs',
    [
      '--reports', 'reports', '--context', 'ctx', '--enabled', 'true',
      '--head-sha-fallback', '', '--out', 'summary.md', '--json-out', 'summary.json',
    ],
    dir,
  );
  assert.equal(result.status, 2, '没有可信范围时必须判「审查未完成」');
  const summary = JSON.parse(readFileSync(path.join(dir, 'summary.json'), 'utf8'));
  assert.equal(summary.status, 'incomplete');
  assert.match(summary.reasons.join('；'), /无法确定本次被审查的 head SHA/);
});

test('summarize：上下文缺失但有兜底 SHA 时仍可正常判定（避免修得过头）', (t) => {
  const dir = mkdtempSync(path.join(os.tmpdir(), 'spotlight-codex-gate-'));
  t.after(() => rmSync(dir, { recursive: true, force: true }));

  const head = 'e'.repeat(40);
  mkdirSync(path.join(dir, 'reports'), { recursive: true });
  for (const role of ['code', 'docs', 'comments']) {
    writeFileSync(
      path.join(dir, 'reports', `${role}.json`),
      JSON.stringify(validReport({ role, headSha: head })),
      'utf8',
    );
  }

  const result = runCli(
    'summarize.mjs',
    [
      '--reports', 'reports', '--context', 'ctx', '--enabled', 'true',
      '--head-sha-fallback', head, '--out', 'summary.md', '--json-out', 'summary.json',
    ],
    dir,
  );
  assert.equal(result.status, 0, result.stderr);
  assert.equal(JSON.parse(readFileSync(path.join(dir, 'summary.json'), 'utf8')).status, 'pass');
});
