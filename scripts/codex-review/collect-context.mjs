#!/usr/bin/env node
/**
 * collect-context.mjs —— 准备阶段入口：把「本次 PR 到底改了什么」固化成上下文文件。
 *
 * 【作用】
 *   1. 校验 base / head 两个 revision 确实存在，并算出 merge-base；
 *   2. 解析改动清单（含新增/修改/删除/重命名）；
 *   3. 只为「可做文本语义审查」的文件生成 diff（生成资产与二进制排除在外）；
 *   4. 写出 run-info.json / change-list.md / diff.patch / task-header.md 四份上下文。
 *
 * 【输入】命令行参数
 *   --repo-root <dir>      仓库根目录（默认当前目录）
 *   --out <dir>            输出目录（默认 codex-review-context）
 *   --base <rev>           基线 revision（PR 场景为 base.sha）
 *   --base-ref <name>      基线的分支名（手动触发场景，由本脚本从 origin 解析）
 *   --head <rev>           被审查 revision（PR 场景为 head.sha）
 *   --pr-number <n>        PR 编号，可省略
 *   --repository <o/r>     仓库全名，可省略
 *   --rule-version <v>     审查规则版本标记，可省略
 *   --max-diff-bytes <n>   可审查 diff 的体积上限（默认 1 MiB）
 *
 * 【为什么同时存在 --base 与 --base-ref】
 *   PR 场景能直接拿到 base.sha，用 --base 最精确。
 *   手动触发时用户给的是分支名，而 actions/checkout 是按 SHA 检出的 detached HEAD，
 *   本地不会存在 main 分支（只有 refs/remotes/origin/main）——这一点在 CI 上真实翻过车：
 *   把 "main" 当 revision 直接传进去会 rev-parse 失败，导致准备阶段失败、三个角色全跑不了。
 *   把解析放在这里而不是 workflow 的 shell 里，是为了它能被 node 用例直接覆盖。
 *
 * 【输出】写入 --out 目录的四个文件；退出码：
 *   0  成功
 *   2  参数或 git 命令失败（无法确定审查范围）
 *   3  可审查 diff 超出体积上限（刻意失败，绝不静默截断后放行）
 *
 * 【为什么要有「超限即失败」】
 *   报告里若少了被截断的文件，读者无法分辨「审查过且没问题」与「根本没看到」。
 *   宁可让准备阶段红灯（汇总会报「审查未完成」），也不要产出一份看似完整的假报告。
 */

import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';

import {
  buildChangeList,
  formatBytes,
  isHarnessPath,
  parseGitNameStatus,
  parseGitNumstat,
  renderChangeListMarkdown,
  renderTaskHeader,
  reviewablePathspec,
  splitReviewScope,
} from './lib.mjs';

const DEFAULT_MAX_DIFF_BYTES = 1024 * 1024;

/**
 * parseArgs —— 解析命令行参数。
 *
 * 输入：process.argv.slice(2)。
 * 输出：{ repoRoot, out, base, head, prNumber, repository, ruleVersion, maxDiffBytes }。
 * 为什么手写而不是引第三方库：本目录不参与 npm 依赖管理（仓库没有 package.json），
 * 参数只有寥寥几个，手写可以避免给 CI 增加安装步骤。
 */
function parseArgs(argv) {
  const options = {
    repoRoot: '.',
    out: 'codex-review-context',
    base: '',
    baseRef: '',
    head: '',
    prNumber: '',
    repository: '',
    ruleVersion: '',
    maxDiffBytes: DEFAULT_MAX_DIFF_BYTES,
  };
  const known = new Set(Object.keys(options));

  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (!token.startsWith('--')) throw new Error(`无法识别的参数：${token}`);
    // 命令行用 kebab-case（--repo-root），对象字段用 camelCase（repoRoot）。
    const key = token
      .slice(2)
      .replace(/-([a-z])/g, (_, char) => char.toUpperCase());
    if (!known.has(key)) throw new Error(`无法识别的参数：${token}`);
    const value = argv[index + 1];
    if (value === undefined || value.startsWith('--')) throw new Error(`参数 ${token} 缺少取值`);
    index += 1;
    options[key] = key === 'maxDiffBytes' ? Number(value) : value;
  }

  if (!options.base && !options.baseRef) throw new Error('必须提供 --base 或 --base-ref');
  if (!options.head) throw new Error('必须提供 --head');
  if (!Number.isFinite(options.maxDiffBytes) || options.maxDiffBytes <= 0) {
    throw new Error('--max-diff-bytes 必须是正数');
  }
  return options;
}

/**
 * git —— 在当前仓库里执行一条只读 git 命令。
 *
 * 输入：仓库根目录、git 子命令参数数组。
 * 输出：标准输出文本。
 * 为什么统一加 `-c core.quotePath=false`：默认配置会把非 ASCII 路径写成 \346\226\207… 八进制转义，
 * 本仓库大量使用中文文件名（如 docs/玩法.md），不关掉转义会让清单和报告里的路径对不上。
 */
function git(repoRoot, args) {
  return execFileSync('git', ['-c', 'core.quotePath=false', ...args], {
    cwd: repoRoot,
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024,
  });
}

/**
 * resolveCommit —— 把任意 revision 解析成完整 commit SHA。
 *
 * 输入：仓库根目录、revision 字符串。
 * 输出：40 位 SHA。
 * 抛出：revision 不存在时抛错（调用方转成退出码 2）。
 * 为什么必须解析：base 可能是 `origin/main` 这类会移动的引用，
 * 必须在写上下文前固化成 SHA，否则三份产物描述的可能不是同一个范围。
 */
function resolveCommit(repoRoot, rev) {
  return git(repoRoot, ['rev-parse', '--verify', `${rev}^{commit}`]).trim();
}

/**
 * tryResolve —— 尝试把某个写法解析成 commit SHA，解析不了返回 null。
 *
 * 输入：仓库根目录、候选 revision 字符串。
 * 输出：40 位 SHA 或 null。
 * 为什么返回 null 而不是抛错：调用方要依次尝试多种写法（原名 / origin/原名），
 * 中间失败是预期路径而不是异常。
 */
function tryResolve(repoRoot, rev) {
  try {
    return git(repoRoot, ['rev-parse', '--verify', '--quiet', `${rev}^{commit}`]).trim() || null;
  } catch {
    return null;
  }
}

/**
 * resolveBaseRef —— 把「用户给的分支名」解析成基线 commit SHA。
 *
 * 输入：仓库根目录、分支名（也可能是 `origin/xxx` 或一个 SHA）。
 * 输出：{ sha, rev }。rev 是实际命中的写法，写进 runInfo 方便回溯。
 * 抛出：两种写法都解析不了时抛错（调用方转成退出码 2）。
 * 为什么先试原名再试 origin/原名：
 *   - 用户可能直接给了 SHA 或 `origin/main`，此时不需要网络也不需要回退；
 *   - 给的是纯分支名 `main` 时，CI 的 detached HEAD 里没有本地 main，
 *     必须回退到 `origin/main`——这正是曾经把 base 写成字面量 `main` 而失败的地方。
 * 只有在两种写法都解析不了时才去 fetch：避免为「本来就能解析」的情况白跑一次网络请求。
 */
function resolveBaseRef(repoRoot, refName) {
  const candidates = [refName, `origin/${refName}`];
  for (const candidate of candidates) {
    const sha = tryResolve(repoRoot, candidate);
    if (sha) return { sha, rev: candidate };
  }

  try {
    git(repoRoot, ['fetch', '--no-tags', '--quiet', 'origin', refName]);
  } catch {
    // 忽略：取不到远端不是致命错误，下面的回退与最终报错才是权威判定。
  }
  for (const candidate of candidates) {
    const sha = tryResolve(repoRoot, candidate);
    if (sha) return { sha, rev: candidate };
  }

  throw new Error(
    `找不到基线引用 \`${refName}\`（已尝试 ${candidates.join(' 与 ')}，并尝试过 git fetch origin ${refName}）`,
  );
}

/** main —— 入口：串起解析、计算、落盘三步。 */
function main() {
  let options;
  try {
    options = parseArgs(process.argv.slice(2));
  } catch (error) {
    console.error(`[collect-context] 参数错误：${error.message}`);
    process.exit(2);
  }

  const repoRoot = path.resolve(options.repoRoot);
  const outDir = path.resolve(repoRoot, options.out);

  let baseSha;
  let headSha;
  let mergeBaseSha;
  let baseRev;
  try {
    headSha = resolveCommit(repoRoot, options.head);
    if (options.base) {
      // PR 场景：base.sha 直接就是一个能解析的 revision。
      baseSha = resolveCommit(repoRoot, options.base);
      baseRev = options.base;
    } else {
      const resolved = resolveBaseRef(repoRoot, options.baseRef);
      baseSha = resolved.sha;
      baseRev = resolved.rev;
    }
    mergeBaseSha = git(repoRoot, ['merge-base', baseSha, headSha]).trim();
  } catch (error) {
    console.error(`[collect-context] 无法确定审查范围：${error.message}`);
    console.error('  base/head 必须能被本仓库解析；浅克隆请先补齐历史。');
    process.exit(2);
  }

  const fileList = buildChangeList(
    parseGitNameStatus(git(repoRoot, ['diff', '--name-status', '-M', '--find-renames=50%', mergeBaseSha, headSha])),
    // 必须配 -z：否则重命名会被压成 `{old => new}` 单字段，按新路径查表就查不到（见 lib.mjs 注释）。
    parseGitNumstat(git(repoRoot, ['diff', '--numstat', '-z', mergeBaseSha, headSha])),
  );

  const { reviewable, excluded } = splitReviewScope(fileList);
  const pathspec = reviewablePathspec(reviewable);

  // 只对可审查文件取 diff：先给 `--` 再跟路径，避免把路径误解成 revision。
  const diffText = pathspec.length === 0
    ? ''
    : git(repoRoot, ['diff', '--no-color', '-M', '--unified=3', mergeBaseSha, headSha, '--', ...pathspec]);

  const diffBytes = Buffer.byteLength(diffText, 'utf8');
  const harnessPaths = fileList.filter((file) => isHarnessPath(file.path)).map((file) => file.path);

  const runInfo = {
    schemaVersion: 1,
    repository: options.repository || null,
    prNumber: options.prNumber ? Number(options.prNumber) : null,
    baseRev: baseSha,
    baseRef: baseRev,
    headSha,
    mergeBaseSha,
    ruleVersion: options.ruleVersion || null,
    generatedAt: new Date().toISOString(),
    maxDiffBytes: options.maxDiffBytes,
    diffBytes,
    diffTruncated: false,
    files: fileList,
    reviewablePaths: reviewable.map((file) => file.path),
    excludedPaths: excluded.map(({ file, reason }) => ({ path: file.path, class: file.class, reason })),
    harnessChanged: harnessPaths.length > 0,
    harnessPaths,
  };

  mkdirSync(outDir, { recursive: true });

  if (diffBytes > options.maxDiffBytes) {
    // 超限时仍然落盘，便于人工查看「到底是哪些文件把 diff 撑大了」，
    // 但明确标记截断并让进程失败，配合汇总给出「审查未完成」。
    runInfo.diffTruncated = true;
    writeFileSync(path.join(outDir, 'run-info.json'), `${JSON.stringify(runInfo, null, 2)}\n`, 'utf8');
    writeFileSync(path.join(outDir, 'change-list.md'), `${renderChangeListMarkdown(runInfo)}\n`, 'utf8');
    console.error(
      `[collect-context] 可审查 diff 为 ${formatBytes(diffBytes)}，超过上限 ${formatBytes(options.maxDiffBytes)}；` +
        '按约定判为「审查未完成」，不生成截断后的上下文。',
    );
    process.exit(3);
  }

  writeFileSync(path.join(outDir, 'run-info.json'), `${JSON.stringify(runInfo, null, 2)}\n`, 'utf8');
  writeFileSync(path.join(outDir, 'change-list.md'), `${renderChangeListMarkdown(runInfo)}\n`, 'utf8');
  writeFileSync(path.join(outDir, 'diff.patch'), diffText, 'utf8');
  writeFileSync(
    path.join(outDir, 'task-header.md'),
    `${renderTaskHeader(runInfo, path.basename(outDir))}\n`,
    'utf8',
  );

  console.log(
    `[collect-context] 已写出上下文：改动 ${fileList.length} 个文件` +
      `（可审查 ${reviewable.length}，排除 ${excluded.length}），diff ${formatBytes(diffBytes)}`,
  );
  console.log(`[collect-context] head=${headSha} merge-base=${mergeBaseSha}`);
  if (runInfo.harnessChanged) {
    console.log(`[collect-context] 注意：本次改动了审查基础设施：${harnessPaths.join('、')}`);
  }
}

main();
