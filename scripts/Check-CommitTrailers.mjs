#!/usr/bin/env node
// Fail when a commit lists an AI tool as a co-author, so contributions stay credited
// to the people who made them (see CONTRIBUTING.md, "AI-assisted changes").
// Usage: node scripts/Check-CommitTrailers.mjs <base-sha|empty> <head-sha>
//   With a base, checks commits reachable from head but not from base.
//   Without a usable base (new branch, force push, manual run), checks head only.
import { execFileSync } from 'node:child_process';

const git = (...args) => execFileSync('git', args, { encoding: 'utf8' });
const exists = sha => {
  try { execFileSync('git', ['cat-file', '-e', `${sha}^{commit}`], { stdio: 'ignore' }); return true; } catch { return false; }
};
const short = ref => (/^[0-9a-f]{40}$/i.test(ref) ? ref.slice(0, 7) : ref);

const [base = '', head = 'HEAD'] = process.argv.slice(2);
const usableBase = base && !/^0+$/.test(base) && exists(base);
const range = usableBase ? [head, '--not', base] : ['-1', head];

// Names and addresses of AI tools that add co-author lines by default.
const aiTool = /\b(claude|anthropic|copilot|chatgpt|openai|codex|deepseek|gemini|cursor|devin|aider|codeium|windsurf|tabnine|amazon q)\b|noreply@anthropic\.com/i;

const log = git('log', '--format=%H%x00%B%x1e', ...range);
const problems = [];
let commits = 0;
for (const record of log.split('\x1e')) {
  const [sha, body] = record.replace(/^\s+/, '').split('\x00');
  if (!sha) continue;
  commits++;
  for (const line of (body ?? '').split('\n')) {
    const match = line.match(/^\s*co-authored-by:\s*(.+)$/i);
    if (match && aiTool.test(match[1])) problems.push(`${sha.slice(0, 7)}: ${line.trim()}`);
  }
}

console.log(`Checked ${commits} commit(s) ${usableBase ? `in ${short(base)}..${short(head)}` : `at ${short(head)}`}`);
if (problems.length) {
  console.log('AI tools must not be listed as commit co-authors. Remove these lines:');
  for (const p of problems) console.log(`  ${p}`);
  console.log('See CONTRIBUTING.md, "AI-assisted changes", for how to reword the commits.');
  process.exitCode = 1;
}
