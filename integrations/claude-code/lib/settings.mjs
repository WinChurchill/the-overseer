/**
 * OVERSEER Claude Code hook bridge - settings.json editing.
 *
 * Owns exactly one shape in a Claude Code settings file: command hook handlers
 * that carry the OVERSEER marker. Everything else in the file is preserved
 * semantically; install and uninstall are preview-by-default.
 *
 * Upstream documentation (retrieved 2026-09-24):
 *   https://code.claude.com/docs/en/hooks#configuration
 *   https://code.claude.com/docs/en/hooks#hook-handler-fields
 */

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

import { utcStamp } from './spool.mjs';

/** Ownership marker: present in the handler args of every OVERSEER entry. */
export const MARKER = 'overseer-claude-bridge';

export const DEFAULT_SETTINGS_PATH = '~/.claude/settings.json';
export const HOOK_TIMEOUT_SECONDS = 10;

export function isPlainObject(value) {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

export function expandHome(target) {
  if (typeof target !== 'string' || target.length === 0) return target;
  if (target === '~') return os.homedir();
  if (target.startsWith('~/') || target.startsWith('~\\')) {
    return path.join(os.homedir(), target.slice(2));
  }
  return target;
}

export function isOwnedHandler(handler) {
  if (!isPlainObject(handler)) return false;
  const command = typeof handler.command === 'string' ? handler.command : '';
  if (command.includes(MARKER)) return true;
  if (Array.isArray(handler.args)) {
    return handler.args.some((arg) => typeof arg === 'string' && arg.includes(MARKER));
  }
  return false;
}

/** The single handler shape this integration owns. */
export function buildOwnedHandler(bridgePath) {
  return {
    type: 'command',
    command: 'node',
    args: [bridgePath, MARKER],
    timeout: HOOK_TIMEOUT_SECONDS,
  };
}

function detectStyle(raw) {
  const match = /\n([ \t]+)"/.exec(raw);
  let indent = 2;
  if (match) {
    indent = match[1].includes('\t') ? '\t' : match[1].length;
  }
  return { indent, trailingNewline: /\n\s*$/.test(raw) };
}

export function serializeSettings(data, style) {
  const text = JSON.stringify(data, null, style.indent);
  return style.trailingNewline ? text + '\n' : text;
}

/**
 * Read and parse a settings file.
 * Returns { path, existed, raw, data, style } or { error } for refusals.
 */
export function loadSettings(filePath) {
  const absolute = path.resolve(expandHome(filePath));
  let raw = null;
  try {
    raw = fs.readFileSync(absolute, 'utf8');
  } catch (err) {
    if (err && err.code === 'ENOENT') {
      return {
        path: absolute,
        existed: false,
        raw: null,
        data: {},
        style: { indent: 2, trailingNewline: true },
      };
    }
    return { path: absolute, error: { code: 'unreadable', message: String(err && err.message ? err.message : err) } };
  }

  let data;
  try {
    data = JSON.parse(raw);
  } catch (err) {
    return {
      path: absolute,
      existed: true,
      raw,
      error: { code: 'malformed', message: 'settings file is not valid JSON: ' + String(err && err.message ? err.message : err) },
    };
  }
  if (!isPlainObject(data)) {
    return {
      path: absolute,
      existed: true,
      raw,
      error: { code: 'malformed', message: 'settings file must contain a JSON object at the top level' },
    };
  }
  return { path: absolute, existed: true, raw, data, style: detectStyle(raw) };
}

function hooksSection(data) {
  if (data.hooks === undefined) return { ok: true, hooks: undefined };
  if (!isPlainObject(data.hooks)) {
    return { ok: false, message: 'existing "hooks" value is not a JSON object; refusing to edit it' };
  }
  return { ok: true, hooks: data.hooks };
}

function handlersOfGroup(group) {
  if (!isPlainObject(group)) return null;
  if (Array.isArray(group.hooks)) return group.hooks;
  return null;
}

function handlerEquals(a, b) {
  return JSON.stringify(a) === JSON.stringify(b);
}

/**
 * Plan an install. Returns { data, changes, untouchedOwnedEvents } or { error }.
 */
export function planInstall(settings, { bridgePath, events }) {
  const section = hooksSection(settings.data);
  if (!section.ok) return { error: { code: 'hooks-not-an-object', message: section.message } };

  const next = JSON.parse(JSON.stringify(settings.data));
  if (!isPlainObject(next.hooks)) next.hooks = {};
  const hooks = next.hooks;
  const desired = buildOwnedHandler(bridgePath);
  const changes = [];
  const untouchedOwnedEvents = [];

  for (const event of events) {
    const existing = hooks[event];
    if (existing !== undefined && !Array.isArray(existing)) {
      return {
        error: {
          code: 'event-not-an-array',
          message: `hooks.${event} exists but is not an array; refusing to edit it`,
        },
      };
    }
    const groups = Array.isArray(existing) ? existing : [];
    const located = [];
    for (let g = 0; g < groups.length; g++) {
      const handlers = handlersOfGroup(groups[g]);
      if (!handlers) continue;
      for (let h = 0; h < handlers.length; h++) {
        if (isOwnedHandler(handlers[h])) located.push({ group: g, handler: h });
      }
    }

    if (located.length === 0) {
      const group = { hooks: [desired] };
      if (Array.isArray(existing)) existing.push(group);
      else hooks[event] = [group];
      changes.push(`+ hooks.${event}: added OVERSEER handler`);
      continue;
    }

    const first = located[0];
    const current = groups[first.group].hooks[first.handler];
    if (!handlerEquals(current, desired)) {
      groups[first.group].hooks[first.handler] = desired;
      changes.push(`~ hooks.${event}: updated OVERSEER handler to this bridge path`);
    }
    for (let i = located.length - 1; i >= 1; i--) {
      groups[located[i].group].hooks.splice(located[i].handler, 1);
      changes.push(`- hooks.${event}: removed duplicate OVERSEER handler`);
    }
    for (let g = groups.length - 1; g >= 0; g--) {
      const handlers = handlersOfGroup(groups[g]);
      if (handlers && handlers.length === 0) groups.splice(g, 1);
    }
  }

  for (const event of Object.keys(hooks)) {
    if (events.includes(event)) continue;
    const groups = Array.isArray(hooks[event]) ? hooks[event] : [];
    const owned = groups.some((group) => {
      const handlers = handlersOfGroup(group);
      return handlers ? handlers.some(isOwnedHandler) : false;
    });
    if (owned) untouchedOwnedEvents.push(event);
  }

  return { data: next, changes, untouchedOwnedEvents };
}

/**
 * Plan an uninstall. Removes only handlers carrying the OVERSEER marker.
 * Returns { data, removed, unremovable }.
 */
export function planUninstall(settings) {
  const section = hooksSection(settings.data);
  if (!section.ok) return { error: { code: 'hooks-not-an-object', message: section.message } };

  const next = JSON.parse(JSON.stringify(settings.data));
  const removed = [];
  const unremovable = [];
  const hooks = next.hooks;
  if (!isPlainObject(hooks)) return { data: next, removed, unremovable };

  for (const event of Object.keys(hooks)) {
    const value = hooks[event];
    if (!Array.isArray(value)) {
      if (JSON.stringify(value).includes(MARKER)) {
        unremovable.push(`hooks.${event}: not an array, left untouched`);
      }
      continue;
    }
    const keptGroups = [];
    for (let g = 0; g < value.length; g++) {
      const group = value[g];
      const handlers = handlersOfGroup(group);
      if (!handlers) {
        // A bare handler written directly into the event array.
        if (isOwnedHandler(group)) {
          removed.push(`hooks.${event}[${g}]: OVERSEER handler`);
          continue;
        }
        if (JSON.stringify(group).includes(MARKER)) {
          unremovable.push(`hooks.${event}[${g}]: unrecognized OVERSEER entry, left untouched`);
        }
        keptGroups.push(group);
        continue;
      }
      const keptHandlers = handlers.filter((handler) => !isOwnedHandler(handler));
      const removedHere = handlers.length - keptHandlers.length;
      for (let i = 0; i < removedHere; i++) removed.push(`hooks.${event}[${g}].hooks: OVERSEER handler`);
      if (keptHandlers.length === 0) continue; // drop the now-empty group
      if (removedHere > 0) group.hooks = keptHandlers;
      keptGroups.push(group);
    }
    if (keptGroups.length === 0) delete hooks[event];
    else hooks[event] = keptGroups;
  }

  return { data: next, removed, unremovable };
}

/** Compact diff-style summary of two JSON trees. */
export function diffSummary(before, after, limit = 60) {
  const lines = [];
  const walk = (a, b, prefix) => {
    if (lines.length >= limit) return;
    if (isPlainObject(a) && isPlainObject(b)) {
      const keys = [...new Set([...Object.keys(a), ...Object.keys(b)])];
      for (const key of keys) {
        walk(a[key], b[key], prefix ? `${prefix}.${key}` : key);
        if (lines.length >= limit) return;
      }
      return;
    }
    if (Array.isArray(a) && Array.isArray(b)) {
      const max = Math.max(a.length, b.length);
      for (let i = 0; i < max; i++) {
        walk(a[i], b[i], `${prefix}[${i}]`);
        if (lines.length >= limit) return;
      }
      return;
    }
    if (JSON.stringify(a) === JSON.stringify(b)) return;
    if (a === undefined) lines.push(`+ ${prefix} = ${short(b)}`);
    else if (b === undefined) lines.push(`- ${prefix} = ${short(a)}`);
    else lines.push(`~ ${prefix}: ${short(a)} -> ${short(b)}`);
  };
  const short = (value) => {
    if (Array.isArray(value)) {
      const primitive = value.every((item) => item === null || typeof item !== 'object');
      if (!primitive) return `[${value.length} item(s)]`;
    } else if (isPlainObject(value)) {
      return `{${Object.keys(value).join(', ')}}`;
    }
    const text = JSON.stringify(value);
    return text === undefined ? 'undefined' : text.length > 140 ? text.slice(0, 137) + '...' : text;
  };
  walk(before, after, '');
  if (lines.length >= limit) lines.push(`... diff truncated at ${limit} entries`);
  return lines;
}

/** Copy the current file aside before any write. Returns the backup path. */
export function backupSettings(filePath, raw) {
  let candidate = `${filePath}.overseer-backup-${utcStamp()}`;
  let suffix = 1;
  while (fs.existsSync(candidate)) {
    candidate = `${filePath}.overseer-backup-${utcStamp()}-${suffix++}`;
  }
  fs.writeFileSync(candidate, raw, 'utf8');
  return candidate;
}

/** Atomic settings write (temp file + rename in the same directory). */
export function writeSettings(filePath, text) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  const tmp = `${filePath}.overseer-tmp-${process.pid}`;
  fs.writeFileSync(tmp, text, 'utf8');
  try {
    fs.renameSync(tmp, filePath);
  } catch (err) {
    try { fs.rmSync(tmp, { force: true }); } catch { /* ignore */ }
    throw err;
  }
}

export function parseArgs(argv) {
  const options = { settingsPath: null, apply: false, json: false, events: null, bridgePath: null, prune: false, unknown: [] };
  for (const arg of argv) {
    if (arg === '--apply') options.apply = true;
    else if (arg === '--json') options.json = true;
    else if (arg === '--dry-run') options.apply = false;
    else if (arg.startsWith('--events=')) options.events = arg.slice('--events='.length).split(',').map((s) => s.trim()).filter(Boolean);
    else if (arg.startsWith('--bridge=')) options.bridgePath = arg.slice('--bridge='.length);
    else if (arg.startsWith('--settings=')) options.settingsPath = arg.slice('--settings='.length);
    else if (arg.startsWith('--')) options.unknown.push(arg);
    else if (!options.settingsPath) options.settingsPath = arg;
    else options.unknown.push(arg);
  }
  return options;
}
