#!/usr/bin/env node
/**
 * OVERSEER Claude Code hook bridge - opt-in installer.
 *
 * Preview by default. Nothing is written without --apply. Existing settings are
 * read first and refused when malformed, backed up before any write, and every
 * non-OVERSEER entry is preserved semantically.
 *
 * Usage:
 *   node install.mjs [settingsPath] [--apply] [--events=A,B] [--bridge=path] [--json]
 *   (default settings path: ~/.claude/settings.json)
 */

import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

import { DEFAULT_INSTALL_EVENTS, SUPPORTED_EVENTS } from './lib/mapping.mjs';
import {
  DEFAULT_SETTINGS_PATH,
  MARKER,
  backupSettings,
  diffSummary,
  expandHome,
  loadSettings,
  parseArgs,
  planInstall,
  serializeSettings,
  writeSettings,
} from './lib/settings.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const DEFAULT_BRIDGE_PATH = path.join(HERE, 'bridge.mjs');

function fail(message, code = 2) {
  process.stderr.write(`install: ${message}\n`);
  process.exit(code);
}

function main() {
  const options = parseArgs(process.argv.slice(2));
  if (options.unknown.length > 0) {
    fail(`unrecognized argument(s): ${options.unknown.join(' ')}\nusage: node install.mjs [settingsPath] [--apply] [--events=A,B] [--bridge=path] [--json]`, 1);
  }

  const settingsPath = path.resolve(expandHome(options.settingsPath ?? DEFAULT_SETTINGS_PATH));
  const bridgePath = path.resolve(expandHome(options.bridgePath ?? DEFAULT_BRIDGE_PATH));
  const events = options.events ?? [...DEFAULT_INSTALL_EVENTS];

  const unknownEvents = events.filter((event) => !SUPPORTED_EVENTS.includes(event));
  if (unknownEvents.length > 0) {
    fail(`unsupported event name(s): ${unknownEvents.join(', ')}\nsupported: ${SUPPORTED_EVENTS.join(', ')}`, 1);
  }

  const loaded = loadSettings(settingsPath);
  if (loaded.error) {
    fail(`refusing to touch ${loaded.path}: ${loaded.error.message}`, 2);
  }

  const plan = planInstall(loaded, { bridgePath, events });
  if (plan.error) {
    fail(`refusing to touch ${loaded.path}: ${plan.error.message}`, 2);
  }

  const before = loaded.data;
  const after = plan.data;
  const diff = diffSummary(before, after);
  const bridgeExists = fs.existsSync(bridgePath);
  const changed = diff.length > 0;

  if (options.json) {
    process.stdout.write(JSON.stringify({
      action: 'install',
      applied: options.apply && changed,
      settingsPath: loaded.path,
      settingsExisted: loaded.existed,
      bridgePath,
      bridgeExists,
      events,
      marker: MARKER,
      changes: plan.changes,
      diff,
      untouchedOwnedEvents: plan.untouchedOwnedEvents,
    }, null, 2) + '\n');
    if (!options.apply || !changed) return 0;
  } else {
    process.stdout.write(`OVERSEER Claude Code hook bridge - install (${options.apply ? 'apply' : 'dry run'})\n`);
    process.stdout.write(`  settings : ${loaded.path}${loaded.existed ? '' : ' (will be created)'}\n`);
    process.stdout.write(`  bridge   : ${bridgePath}${bridgeExists ? '' : ' (MISSING - install the integration files first)'}\n`);
    process.stdout.write(`  marker   : ${MARKER}\n`);
    process.stdout.write(`  events   : ${events.length} (${events.join(', ')})\n`);
    if (plan.untouchedOwnedEvents.length > 0) {
      process.stdout.write(`  note     : OVERSEER entries exist for events outside this install: ${plan.untouchedOwnedEvents.join(', ')}\n`);
      process.stdout.write('             run uninstall.mjs first if you want a clean re-install.\n');
    }
    if (!changed) {
      process.stdout.write('  diff     : none\n');
      process.stdout.write('Already installed; no files written.\n');
      return 0;
    }
    process.stdout.write(`  diff     : ${diff.length} change(s)\n`);
    for (const line of diff) process.stdout.write(`    ${line}\n`);
    if (!options.apply) {
      process.stdout.write('Preview only: no files written. Re-run with --apply to write.\n');
      return 0;
    }
  }

  let backupPath = null;
  if (loaded.existed) {
    backupPath = backupSettings(loaded.path, loaded.raw);
  }
  writeSettings(loaded.path, serializeSettings(after, loaded.style));

  if (!options.json) {
    if (backupPath) process.stdout.write(`  backup   : ${backupPath}\n`);
    process.stdout.write(`  wrote    : ${loaded.path}\n`);
    process.stdout.write(`Rollback: node uninstall.mjs "${loaded.path}" --apply${backupPath ? `, or restore ${backupPath}` : ''}\n`);
  }
  return 0;
}

process.exit(main());
