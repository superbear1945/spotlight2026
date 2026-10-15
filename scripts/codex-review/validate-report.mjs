#!/usr/bin/env node
/**
 * validate-report.mjs —— 单个审查角色结束后的「快速自检」入口。
 *
 * 【作用】
 *   把 codex 写出的报告按统一契约校验一遍，并把规范化结果渲染成 Markdown，
 *   让「本次审查到底看到了什么、有没有发现问题」当场出现在 job 摘要里。
 *   校验不通过即以非零退出码让本 job 失败，从而让汇总把该角色判为「审查未完成」。
 *
 * 【输入】命令行参数
 *   --role <code|docs|comments>  本 job 的角色（报告中的 role 必须与之一致）
 *   --report <file>              模型输出的报告文件（codex exec --output-file 的产物）
 *   --context <dir>              准备阶段产出的上下文目录（用于取 headSha）
 *   --head-sha <sha>             可选：上下文缺失时的兜底 head SHA
 *
 * 【输出】
 *   标准输出：该角色的 Markdown 小结（workflow 会追加进 $GITHUB_STEP_SUMMARY）；
 *   同时把同一份小结写到与报告同目录的 <role>.md，作为 artifact 留档。
 *   退出码：0 报告合法；1 报告非法或缺失；2 参数/IO 错误。
 *
 * 【为什么要在每个角色 job 里先校验一次】
 *   汇总 job 之后还会独立复校验（它才是权威判定），但那时三个 job 都已结束。
 *   在角色 job 内先失败，可以让红灯直接落在对应角色上，作者一眼就知道是哪个角色、
 *   出了什么问题（比如模型输出被截断），而不是等汇总阶段才看到一个笼统的失败。
 */

import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';

import {
  ROLE_TITLES,
  REVIEW_ROLES,
  parseReportText,
  renderFinding,
  validateReport,
} from './lib.mjs';

/**
 * parseArgs —— 解析命令行参数。
 * 输入：process.argv.slice(2)；输出：{ role, report, context, headSha }。
 * 为什么手写：与 collect-context.mjs 同理，本目录不依赖 npm 包。
 */
function parseArgs(argv) {
  const options = { role: '', report: '', context: '', headSha: '' };
  const known = new Set(Object.keys(options));
  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (!token.startsWith('--')) throw new Error(`无法识别的参数：${token}`);
    // 命令行用 kebab-case（--head-sha），对象字段用 camelCase（headSha）。
    // 这里漏掉转换曾导致 CI 上三个角色全部以「无法识别的参数：--head-sha」失败，
    // 而本地用例从不传该参数所以没发现；对应的回归用例见 tests/codex-review.test.mjs
    // 里的「workflow 传给三个 CLI 的参数」两条。
    const key = token
      .slice(2)
      .replace(/-([a-z])/g, (_, char) => char.toUpperCase());
    if (!known.has(key)) throw new Error(`无法识别的参数：${token}`);
    const value = argv[index + 1];
    if (value === undefined || value.startsWith('--')) throw new Error(`参数 ${token} 缺少取值`);
    index += 1;
    options[key] = value;
  }
  if (!REVIEW_ROLES.includes(options.role)) {
    throw new Error(`--role 必须是 ${REVIEW_ROLES.join(' / ')} 之一`);
  }
  if (!options.report) throw new Error('必须提供 --report');
  return options;
}

/**
 * readExpectedHeadSha —— 取本次被审查的 head SHA。
 *
 * 输入：上下文目录、兜底 SHA。
 * 输出：SHA 字符串（取不到时返回空串）。
 * 为什么优先用上下文：上下文里的 headSha 是准备阶段从 git 解析出来的确定值，
 * 兜底参数只在准备阶段产物流失（例如产物上传失败）时使用。
 */
function readExpectedHeadSha(contextDir, fallback) {
  if (contextDir) {
    const runInfoPath = path.join(contextDir, 'run-info.json');
    if (existsSync(runInfoPath)) {
      try {
        const runInfo = JSON.parse(readFileSync(runInfoPath, 'utf8'));
        if (typeof runInfo.headSha === 'string' && runInfo.headSha !== '') return runInfo.headSha;
      } catch (error) {
        console.error(`[validate-report] 读取 run-info.json 失败：${error.message}`);
      }
    }
  }
  return fallback || '';
}

/**
 * renderRoleMarkdown —— 渲染单角色小结。
 *
 * 输入：角色 id、校验结果（validateReport 的返回值）。
 * 输出：Markdown 文本。
 * 为什么单独渲染：角色 job 的摘要是作者最先看到的内容，必须直接给出「状态 / 摘要 / 问题」，
 * 不需要再去翻原始 JSON。
 */
function renderRoleMarkdown(role, result) {
  const title = ROLE_TITLES[role] ?? role;
  const lines = [`# ${title}`, ''];

  if (!result.ok) {
    lines.push('**结论：报告非法或缺失 → 审查未完成**', '');
    lines.push(...result.errors.map((error) => `- ${error}`), '');
    lines.push(
      '> 这份报告不能被采信，因此本次 PR 的 AI 审查整体判为「审查未完成」。',
      '> 请检查模型是否网络中断、上下文是否超限，或输出是否符合 `.github/codex-review/report.schema.json`。',
      '',
    );
    return lines.join('\n');
  }

  const statusText = {
    completed: '✅ 完成',
    insufficient: '⚠️ 未能完整审查',
    not_applicable: '➖ 本次改动与该角色无关',
  }[result.report.status] ?? result.report.status;

  lines.push(`**结论：${statusText}**`, '');
  lines.push(`- 摘要：${result.report.summary}`);
  if (result.report.statusReason) lines.push(`- 原因：${result.report.statusReason}`);
  lines.push(`- head：\`${result.report.headSha}\``);
  lines.push(`- 已审查文件：${result.report.reviewedFiles.length} 个`);
  if (result.report.reviewedFiles.length > 0) {
    lines.push(`  ${result.report.reviewedFiles.map((file) => `\`${file}\``).join('、')}`);
  }
  lines.push('');

  if (result.blocking.length > 0) {
    lines.push('## 必须修复（阻断合并）', '');
    for (const finding of result.blocking) lines.push(...renderFinding(finding));
  }
  if (result.advisory.length > 0) {
    lines.push('## 建议（不阻断合并）', '');
    for (const finding of result.advisory) lines.push(...renderFinding(finding));
  }
  if (result.blocking.length === 0 && result.advisory.length === 0) {
    lines.push('未发现问题。', '');
  }
  return lines.join('\n');
}

/** main —— 入口：读报告 → 校验 → 渲染并落盘 → 退出码。 */
function main() {
  let options;
  try {
    options = parseArgs(process.argv.slice(2));
  } catch (error) {
    console.error(`[validate-report] 参数错误：${error.message}`);
    process.exit(2);
  }

  const reportPath = path.resolve(options.report);
  const expectedHeadSha = readExpectedHeadSha(
    options.context ? path.resolve(options.context) : '',
    options.headSha,
  );

  let result;
  if (!existsSync(reportPath)) {
    result = {
      ok: false,
      errors: [`报告文件不存在：${options.report}（模型可能未产出结果或运行失败）`],
      warnings: [],
      blocking: [],
      advisory: [],
      report: null,
    };
  } else {
    const raw = readFileSync(reportPath, 'utf8');
    try {
      result = validateReport(parseReportText(raw), {
        expectedRole: options.role,
        expectedHeadSha: expectedHeadSha || undefined,
      });
    } catch (error) {
      result = {
        ok: false,
        errors: [`报告不是合法 JSON：${error.message}`],
        warnings: [],
        blocking: [],
        advisory: [],
        report: null,
      };
    }
  }

  const markdown = renderRoleMarkdown(options.role, result);
  const markdownPath = path.join(path.dirname(reportPath), `${options.role}.md`);
  mkdirSync(path.dirname(markdownPath), { recursive: true });
  writeFileSync(markdownPath, `${markdown}\n`, 'utf8');

  console.log(markdown);
  if (result.warnings.length > 0) {
    for (const warning of result.warnings) console.log(`> 提示：${warning}`);
  }
  console.log(`[validate-report] 已写出角色小结：${markdownPath}`);

  if (!result.ok) {
    console.error('[validate-report] 报告校验未通过：');
    for (const error of result.errors) console.error(`  - ${error}`);
    process.exit(1);
  }
  console.log(
    `[validate-report] 报告合法：role=${options.role} status=${result.report.status} ` +
      `blocking=${result.blocking.length} advisory=${result.advisory.length}`,
  );
}

main();
