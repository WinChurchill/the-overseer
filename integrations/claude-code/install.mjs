#!/usr/bin/env node
/**
 * OVERSEER Claude Code hook bridge - opt-in installer.
 *
 * Preview by default. Nothing is written without --apply. Existing settings are
 * read first and refused when malformed, backed up before any write, and every
 * non-OVERSEER entry is preserved semantically.
 *
 * Before `--apply` writes anything it executes the hook command it is about to
 * record against a throwaway spool, so a settings file can never be pointed at a
 * missing or non-runnable bridge (see lib/verify.mjs).
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
  isOwnedHandler,
  loadSettings,
  parseArgs,
  planInstall,
  serializeSettings,
  writeSettings,
} from './lib/settings.mjs';
import { HOOK_COMMAND, verifyBridgeCommand } from './lib/verify.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const DEFAULT_BRIDGE_PATH = path.join(HERE, 'bridge.mjs');
const BRIDGE_ASSET = 'bridge.mjs';

/** Advice appended whenever the recorded bridge cannot be executed. */
const STABLE_LOCATION_HINT = [
  'the recorded path must stay valid for as long as the hook is installed.',
  'A release archive that is extracted, run and later deleted makes the recorded',
  'path dangling. Copy this integration to a directory you keep (for example',
  'under your user profile), then install from there:',
  `  node install.mjs "<settings.json>" --bridge="<kept directory>/${BRIDGE_ASSET}" --apply`,
].join('\n  ');

function fail(message, code = 2) {
  process.stderr.write(`install: ${message}\n`);
  process.exit(code);
}

function relativeIfUnderHERE(target) {
  const relative = path.relative(HERE, target);
  return relative && !relative.startsWith('..') && !path.isAbsolute(relative) ? relative : null;
}

/** Windows paths are case-insensitive; compare resolved paths accordingly. */
function samePath(a, b) {
  const left = path.resolve(a);
  const right = path.resolve(b);
  return process.platform === 'win32'
    ? left.toLowerCase() === right.toLowerCase()
    : left === right;
}

/**
 * The bridge path already recorded in the settings file, or null when no owned
 * handler exists yet. Used to decide whether this run is recording a NEW path
 * (which must be executed before it is trusted) or re-confirming the one already
 * in place (which is idempotent and needs no probe).
 */
function recordedBridgePath(data) {
  const hooks = data && typeof data.hooks === 'object' && data.hooks !== null ? data.hooks : {};
  for (const groups of Object.values(hooks)) {
    if (!Array.isArray(groups)) continue;
    for (const group of groups) {
      const handlers = group && Array.isArray(group.hooks) ? group.hooks : null;
      if (!handlers) continue;
      for (const handler of handlers) {
        if (!isOwnedHandler(handler)) continue;
        const args = Array.isArray(handler.args) ? handler.args : [];
        if (typeof args[0] === 'string' && args[0].length > 0) return args[0];
      }
    }
  }
  return null;
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
  const alreadyRecorded = recordedBridgePath(before);

  // When this run is about to record a hook path the settings file does not already
  // point at, prove the move is safe first: a path that has never been executed must
  // not be written into settings. Re-running against the already-installed path
  // changes nothing, so it stays cheap and does not need the probe.
  const recordPathIsNew = alreadyRecorded === null || !samePath(alreadyRecorded, bridgePath);
  let verification = null;
  if (options.apply && changed && !options.noVerify && recordPathIsNew) {
    verification = verifyBridgeCommand({ bridgePath, command: HOOK_COMMAND });
    if (!verification.ok) {
      const detail = `refusing to write ${loaded.path}: ${verification.message} [${verification.code}]\n  ${STABLE_LOCATION_HINT}`;
      if (options.json) {
        process.stdout.write(JSON.stringify({
          action: 'install',
          applied: false,
          refused: true,
          settingsPath: loaded.path,
          settingsExisted: loaded.existed,
          bridgePath,
          bridgeExists,
          bridgeVerified: false,
          verification,
          events,
          marker: MARKER,
          changes: plan.changes,
          diff,
          untouchedOwnedEvents: plan.untouchedOwnedEvents,
        }, null, 2) + '\n');
      }
      fail(detail, 2);
    }
  }

  if (options.json) {
    process.stdout.write(JSON.stringify({
      action: 'install',
      applied: options.apply && changed,
      settingsPath: loaded.path,
      settingsExisted: loaded.existed,
      bridgePath,
      bridgeExists,
      bridgeVerified: verification ? verification.ok === true : null,
      verification,
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
    if (verification && verification.ok) {
      process.stdout.write(`  verified : ran the hook command once; it wrote a ${verification.recordKind} record\n`);
    }
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
      const underHere = relativeIfUnderHERE(bridgePath);
      if (!bridgeExists) {
        process.stdout.write(`  warning  : the recorded bridge does not exist yet; --apply would refuse to write.\n`);
      } else if (underHere) {
        process.stdout.write(`  warning  : the recorded bridge is inside this integration directory (${underHere}).\n`);
        process.stdout.write('             Deleting or moving that directory later leaves a dangling hook path.\n');
        process.stdout.write(`             For a durable install, copy the integration elsewhere and pass --bridge=<kept path>/${BRIDGE_ASSET}.\n`);
      }
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
