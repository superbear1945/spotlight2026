#!/usr/bin/env node
/**
 * summarize.mjs —— 汇总入口：读取三个角色的报告，给出唯一的门禁结论。
 *
 * 【作用】
 *   汇总 job 是「唯一决定 CI 状态」的地方。它不信任单个角色 job 的结论，而是重新
 *   读取三份原始报告、重新套用同一套契约校验，再按 decideVerdict 得出：
 *     pass       三个角色都完成且没有必须修复的问题
 *     blocked    有角色报出必须修复的问题（可修代码解决）
 *     incomplete 报告缺失/非法/未能完整审查（不是靠改代码能解决的，需要重跑或修环境）
 *
 * 【输入】命令行参数
 *   --reports <dir>          三个角色报告的目录（artifact 解压后合并到同一目录）
 *   --context <dir>          准备阶段的上下文目录（取 run-info.json；可以不存在）
 *   --enabled <true|false>   AI 审查总开关（对应仓库变量 CODEX_REVIEW_ENABLED）
 *   --head-sha-fallback <s>  上下文缺失时的兜底 head SHA
 *   --out <file>             汇总 Markdown 的写出路径
 *   --json-out <file>        汇总 JSON 的写出路径（可省略）
 *
 * 【输出】退出码：0 通过；1 审查不通过；2 审查未完成（两者都会让 workflow 红灯）。
 *
 * 【为什么「未启用」要单独一条分支并且退出 0】
 *   本工作流首次引入时尚未配置密钥与地址，若直接红灯会让所有无关 PR 都亮红。
 *   因此未启用时明确写出「本次未做任何 AI 审查」并放行；
 *   docs/AI审查说明.md 已写明：只有在变量置为 true 并跑通一次之后，
 *   才允许把 `AI 审查门禁` 加入分支规则集的必需检查。
 */

import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';

import {
  REVIEW_ROLES,
  ROLE_TITLES,
  decideVerdict,
  parseReportText,
  renderSummaryMarkdown,
  validateReport,
} from './lib.mjs';

/**
 * parseArgs —— 解析命令行参数。
 * 输入：process.argv.slice(2)；输出：解析后的选项对象。
 * 为什么手写：本目录不依赖 npm 包，参数数量固定且很少。
 */
function parseArgs(argv) {
  const options = {
    reports: 'codex-review-reports',
    context: '',
    enabled: 'false',
    headShaFallback: '',
    out: 'codex-review-summary.md',
    jsonOut: '',
  };
  const known = new Set(Object.keys(options));
  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (!token.startsWith('--')) throw new Error(`无法识别的参数：${token}`);
    // `--head-sha-fallback` → `headShaFallback`
    const key = token
      .slice(2)
      .replace(/-([a-z])/g, (_, char) => char.toUpperCase());
    if (!known.has(key)) throw new Error(`无法识别的参数：${token}`);
    const value = argv[index + 1];
    if (value === undefined || value.startsWith('--')) throw new Error(`参数 ${token} 缺少取值`);
    index += 1;
    options[key] = value;
  }
  return options;
}

/**
 * readRunInfo —— 读取准备阶段的 run-info.json。
 *
 * 输入：上下文目录（可为空串）。
 * 输出：解析后的对象，或 null。
 * 为什么允许为 null：准备阶段可能直接失败（git 范围解析不了、diff 超限），
 * 此时仍要出一份「审查未完成」的汇总，而不是让汇总 job 自己崩掉。
 */
function readRunInfo(contextDir) {
  if (!contextDir) return null;
  const runInfoPath = path.join(contextDir, 'run-info.json');
  if (!existsSync(runInfoPath)) return null;
  try {
    return JSON.parse(readFileSync(runInfoPath, 'utf8'));
  } catch (error) {
    console.error(`[summarize] run-info.json 解析失败：${error.message}`);
    return null;
  }
}

/**
 * collectRoleResults —— 读取并校验三个角色的报告。
 *
 * 输入：报告目录、期望的 head SHA、角色列表。
 * 输出：角色结果数组（每项含 ok/errors/status/blocking/advisory 等）。
 * 为什么在这里再校验一次：角色 job 里的校验只是「快速自检」；若角色 job 崩在
 * 校验之后（或产物上传失败），汇总必须自己得出「这份报告不可用」，不能依赖上游结论。
 */
function collectRoleResults(reportsDir, expectedHeadSha, roles) {
  return roles.map((role) => {
    const reportPath = path.join(reportsDir, `${role}.json`);
    const base = { role, reviewFiles: [], blocking: [], advisory: [], warnings: [] };

    if (!existsSync(reportPath)) {
      return {
        ...base,
        ok: false,
        errors: [`未找到 ${role} 角色的报告（${reportPath}）：可能被取消、超时或运行失败`],
        status: null,
        summary: '',
      };
    }

    let parsed;
    try {
      parsed = parseReportText(readFileSync(reportPath, 'utf8'));
    } catch (error) {
      return {
        ...base,
        ok: false,
        errors: [`${role} 角色的报告不是合法 JSON：${error.message}`],
        status: null,
        summary: '',
      };
    }

    const result = validateReport(parsed, {
      expectedRole: role,
      expectedHeadSha: expectedHeadSha || undefined,
    });
    return {
      ...base,
      ok: result.ok,
      errors: result.errors,
      warnings: result.warnings,
      blocking: result.blocking,
      advisory: result.advisory,
      status: result.report?.status ?? null,
      statusReason: result.report?.statusReason ?? null,
      summary: result.report?.summary ?? '',
      reviewedFiles: Array.isArray(result.report?.reviewedFiles) ? result.report.reviewedFiles : [],
    };
  });
}

/**
 * renderDisabledNotice —— 生成「AI 审查未启用」的说明。
 *
 * 输入：无。
 * 输出：Markdown 文本。
 * 为什么措辞要这么重：这条路径会让检查直接变绿，如果不把「本次没有做任何 AI 审查」
 * 写得足够显眼，很容易被误读成「AI 审查通过了」。
 */
function renderDisabledNotice() {
  return [
    '# AI 审查门禁',
    '',
    '**结论：⏭️ 未启用（本次未做任何 AI 审查）**',
    '',
    '仓库变量 `CODEX_REVIEW_ENABLED` 不是 `true`，因此三个审查角色都没有执行。',
    '',
    '> 本次检查的「通过」**不代表**做过任何 AI 审查。',
    '> 在把 `AI 审查门禁` 加入分支规则集之前，必须先完成：',
    '> 1) 配置 `CODEX_REVIEW_API_KEY`、`CODEX_REVIEW_RESPONSES_ENDPOINT`、`CODEX_REVIEW_MODEL`；',
    '> 2) 把 `CODEX_REVIEW_ENABLED` 置为 `true`；',
    '> 3) 在真实 PR 上跑通一次并确认三个角色都完成。',
    '',
  ].join('\n');
}

/** main —— 入口：读开关 → 收集角色结果 → 判定 → 落盘 → 退出码。 */
function main() {
  let options;
  try {
    options = parseArgs(process.argv.slice(2));
  } catch (error) {
    console.error(`[summarize] 参数错误：${error.message}`);
    process.exit(2);
  }

  if (String(options.enabled).toLowerCase() !== 'true') {
    const markdown = renderDisabledNotice();
    writeFileSync(path.resolve(options.out), `${markdown}\n`, 'utf8');
    if (options.jsonOut) writeFileSync(path.resolve(options.jsonOut), `${JSON.stringify({ status: 'disabled' }, null, 2)}\n`, 'utf8');
    console.log(markdown);
    console.log('[summarize] AI 审查未启用，本次不参与判定。');
    return;
  }

  const reportsDir = path.resolve(options.reports);
  const runInfo = readRunInfo(options.context ? path.resolve(options.context) : '');
  const expectedHeadSha = runInfo?.headSha || options.headShaFallback || '';

  const roleResults = collectRoleResults(reportsDir, expectedHeadSha, REVIEW_ROLES);
  let verdict = decideVerdict(roleResults);

  // fail-closed：拿不到可信的 head SHA 时不能判通过。
  // 为什么必须单独兜住：validateReport 在没有「期望值」时只能放弃 SHA 比对（这是它在
  // 单角色 job 里的正常语义），于是三份可能来自不同提交的报告会全部校验通过，
  // 最终给出一个看似完好的 pass。而把报告与提交绑定正是本门禁最核心的保证，
  // 所以这里宁愿报「审查未完成」也不能在没有范围约束的情况下放行。
  // 触发场景：手动触发下 HEAD_SHA_FALLBACK 为空，且上下文产物下载失败。
  if (!expectedHeadSha && verdict.status !== 'incomplete') {
    verdict = {
      status: 'incomplete',
      reasons: [
        '无法确定本次被审查的 head SHA（上下文产物与兜底值都缺失）：报告无法与提交绑定，拒绝在没有范围约束的情况下判定通过',
      ],
    };
  }
  const markdown = renderSummaryMarkdown(verdict, roleResults, runInfo, {
    harnessChanged: Boolean(runInfo?.harnessChanged),
    harnessPaths: runInfo?.harnessPaths ?? [],
  });

  mkdirSync(path.dirname(path.resolve(options.out)), { recursive: true });
  writeFileSync(path.resolve(options.out), `${markdown}\n`, 'utf8');
  if (options.jsonOut) {
    writeFileSync(
      path.resolve(options.jsonOut),
      `${JSON.stringify(
        {
          status: verdict.status,
          reasons: verdict.reasons,
          headSha: expectedHeadSha || null,
          roles: roleResults.map((item) => ({
            role: item.role,
            title: ROLE_TITLES[item.role] ?? item.role,
            ok: item.ok,
            status: item.status,
            errors: item.errors,
            blocking: (item.blocking ?? []).length,
            advisory: (item.advisory ?? []).length,
          })),
        },
        null,
        2,
      )}\n`,
      'utf8',
    );
  }

  console.log(markdown);
  console.error(`[summarize] 结论：${verdict.status}`);

  if (verdict.status === 'blocked') process.exit(1);
  if (verdict.status === 'incomplete') process.exit(2);
}

main();
