#!/usr/bin/env node
/**
 * OVERSEER Claude Code hook bridge - opt-in uninstaller.
 *
 * Preview by default. Removes only hook handlers carrying the OVERSEER marker
 * and reports anything it could not remove. Non-OVERSEER entries are preserved
 * semantically.
 *
 * Usage:
 *   node uninstall.mjs [settingsPath] [--bridge=path] [--apply] [--json]
 */

import path from 'node:path';
import process from 'node:process';

import {
  DEFAULT_SETTINGS_PATH,
  MARKER,
  backupSettings,
  diffSummary,
  expandHome,
  loadSettings,
  parseArgs,
  planUninstall,
  serializeSettings,
  writeSettings,
} from './lib/settings.mjs';

function fail(message, code = 2) {
  process.stderr.write(`uninstall: ${message}\n`);
  process.exit(code);
}

function main() {
  const options = parseArgs(process.argv.slice(2));
  if (options.unknown.length > 0) {
    fail(`unrecognized argument(s): ${options.unknown.join(' ')}\nusage: node uninstall.mjs [settingsPath] [--bridge=path] [--apply] [--json]`, 1);
  }
  if (options.bridgePath !== null && !options.bridgePath.trim()) fail('--bridge requires a non-empty path', 1);
  const bridgePath = options.bridgePath === null ? null : path.resolve(expandHome(options.bridgePath));

  const settingsPath = path.resolve(expandHome(options.settingsPath ?? DEFAULT_SETTINGS_PATH));
  const loaded = loadSettings(settingsPath);
  if (loaded.error) {
    fail(`refusing to touch ${loaded.path}: ${loaded.error.message}`, 2);
  }
  if (!loaded.existed) {
    if (options.json) {
      process.stdout.write(JSON.stringify({ action: 'uninstall', applied: false, settingsPath: loaded.path, settingsExisted: false, removed: [], unremovable: [], diff: [] }, null, 2) + '\n');
    } else {
      process.stdout.write(`OVERSEER Claude Code hook bridge - uninstall (${options.apply ? 'apply' : 'dry run'})\n`);
      process.stdout.write(`  settings : ${loaded.path} (does not exist)\nNothing to remove.\n`);
    }
    return 0;
  }

  const plan = planUninstall(loaded, { bridgePath });
  if (plan.error) {
    fail(`refusing to touch ${loaded.path}: ${plan.error.message}`, 2);
  }

  const diff = diffSummary(loaded.data, plan.data);
  const changed = diff.length > 0;

  if (options.json) {
    process.stdout.write(JSON.stringify({
      action: 'uninstall',
      applied: options.apply && changed,
      settingsPath: loaded.path,
      settingsExisted: true,
      marker: MARKER,
      bridgePath,
      removed: plan.removed,
      unremovable: plan.unremovable,
      diff,
    }, null, 2) + '\n');
  } else {
    process.stdout.write(`OVERSEER Claude Code hook bridge - uninstall (${options.apply ? 'apply' : 'dry run'})\n`);
    process.stdout.write(`  settings : ${loaded.path}\n`);
    process.stdout.write(`  marker   : ${MARKER}\n`);
    if (!changed) {
      process.stdout.write('  found    : no OVERSEER entries\nNothing to remove.\n');
    } else {
      process.stdout.write(`  removing : ${plan.removed.length} OVERSEER handler(s)\n`);
      for (const line of plan.removed) process.stdout.write(`    - ${line}\n`);
      process.stdout.write(`  diff     : ${diff.length} change(s)\n`);
      for (const line of diff) process.stdout.write(`    ${line}\n`);
    }
    if (plan.unremovable.length > 0) {
      process.stdout.write(`  WARNING  : ${plan.unremovable.length} OVERSEER entr(y/ies) could not be removed\n`);
      for (const line of plan.unremovable) process.stdout.write(`    ! ${line}\n`);
    }
  }

  if (options.apply && changed) {
    const backupPath = backupSettings(loaded.path, loaded.raw);
    writeSettings(loaded.path, serializeSettings(plan.data, loaded.style));
    if (!options.json) {
      process.stdout.write(`  backup   : ${backupPath}\n`);
      process.stdout.write(`  wrote    : ${loaded.path}\n`);
    }
  } else if (!options.apply && changed && !options.json) {
    process.stdout.write('Preview only: no files written. Re-run with --apply to write.\n');
  }

  // Partial removal is reported through the exit code so scripts can react.
  return plan.unremovable.length > 0 ? 3 : 0;
}

process.exit(main());
