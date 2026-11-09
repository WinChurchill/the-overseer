import fs from 'node:fs';
import { randomBytes } from 'node:crypto';

const STALE_LOCK_MS = 5000;

export class Deadline {
  constructor(ms) {
    this.end = Date.now() + Math.max(0, ms);
  }
  remaining() {
    return this.end - Date.now();
  }
  expired() {
    return Date.now() >= this.end;
  }
  /** Sub-deadline clamped to what is left of this one. */
  child(ms) {
    const child = new Deadline(0);
    child.end = Math.min(this.end, Date.now() + Math.max(0, ms));
    return child;
  }
}

export function sleepSync(ms) {
  if (!(ms > 0)) return;
  try {
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
  } catch {
    /* ignore */
  }
}

export function utcStamp(date = new Date()) {
  return date.toISOString().replace(/[-:]/g, '').replace(/\.\d{3}Z$/, 'Z');
}

export function readJson(file) {
  try {
    return JSON.parse(readSpoolFile(file, 64 * 1024));
  } catch {
    return null;
  }
}

export function ensureRegularDirectory(directory) {
  let info = fs.lstatSync(directory, { throwIfNoEntry: false });
  if (!info) {
    fs.mkdirSync(directory, { recursive: true });
    info = fs.lstatSync(directory);
  }
  if (!info.isDirectory()) throw unsafePath();
}

function unsafePath() {
  return Object.assign(new Error('unsafe-spool-path'), { code: 'UNSAFE_SPOOL_PATH' });
}

export function regularFile(file, singleLink = false) {
  const info = fs.lstatSync(file, { throwIfNoEntry: false });
  if (info && (!info.isFile() || (singleLink && info.nlink !== 1))) throw unsafePath();
  return info;
}

export function openRegularFile(file, flags, { singleLink = false, create } = {}) {
  const before = regularFile(file, singleLink);
  const fd = fs.openSync(file, !before && create ? create : flags);
  try {
    const after = fs.fstatSync(fd);
    if (!after.isFile() || (singleLink && after.nlink !== 1) || (before && !sameFile(before, after))) throw unsafePath();
    return fd;
  } catch (error) {
    try { fs.closeSync(fd); } catch { /* preserve the original error */ }
    throw error;
  }
}

export function readSpoolFile(file, maximumBytes) {
  const fd = openRegularFile(file, 'r');
  try {
    const buffer = Buffer.alloc(maximumBytes + 1);
    let length = 0;
    while (length < buffer.length) {
      const read = fs.readSync(fd, buffer, length, buffer.length - length, length);
      if (!read) break;
      length += read;
    }
    if (length > maximumBytes) throw Object.assign(new Error('oversized-spool-file'), { code: 'SPOOL_FILE_TOO_LARGE' });
    return buffer.toString('utf8', 0, length);
  } finally {
    fs.closeSync(fd);
  }
}

function sameFile(left, right) {
  return left?.dev === right?.dev && left?.ino === right?.ino;
}

export function writeJsonAtomic(file, value) {
  writeAtomic(file, JSON.stringify(value, null, 2) + '\n');
}

export function writeAtomic(file, contents) {
  const tmp = `${file}.tmp-${process.pid}-${randomBytes(4).toString('hex')}`;
  try {
    fs.writeFileSync(tmp, contents, { flag: 'wx' });
    fs.renameSync(tmp, file);
  } catch (err) {
    try { fs.rmSync(tmp, { force: true }); } catch { /* ignore */ }
    throw err;
  }
}

function tryAcquireLock(lockPath) {
  try {
    const fd = fs.openSync(lockPath, 'wx');
    try {
      fs.writeSync(fd, `${process.pid} ${Date.now()}\n`);
      return fs.fstatSync(fd);
    } finally {
      fs.closeSync(fd);
    }
  } catch (err) {
    if (err && err.code === 'EEXIST') return false;
    throw err;
  }
}

export function processIsAlive(pid) {
  if (!Number.isSafeInteger(pid) || pid < 1 || pid > 0x7fffffff) return false;
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return error?.code !== 'ESRCH';
  }
}

function removeOwnLock(lockPath, expected) {
  const actual = regularFile(lockPath);
  if (actual && sameFile(actual, expected)) fs.unlinkSync(lockPath);
}

/**
 * Exclusive-create lock with a stale-lock takeover and a hard deadline.
 * Returns { acquired, value } - never blocks past the deadline.
 */
export function withLock(lockPath, deadline, fn, wait = true) {
  let acquired;
  while (!deadline.expired()) {
    acquired = tryAcquireLock(lockPath);
    if (acquired) break;
    try {
      const info = regularFile(lockPath);
      if (!info) continue;
      if (Date.now() - info.mtimeMs > STALE_LOCK_MS) {
        const pid = Number(readSpoolFile(lockPath, 64).split(' ', 1)[0]);
        if (!processIsAlive(pid)) {
          removeOwnLock(lockPath, info);
          continue;
        }
      }
    } catch (error) {
      if (error?.code === 'ENOENT') continue;
      if (error?.code === 'UNSAFE_SPOOL_PATH') throw error;
    }
    if (!wait) break;
    sleepSync(Math.min(4, Math.max(1, deadline.remaining())));
  }
  if (!acquired) return { acquired: false, value: null };
  try {
    return { acquired: true, value: fn() };
  } finally {
    try { removeOwnLock(lockPath, acquired); } catch { /* ignore */ }
  }
}
