/**
 * Test-only preload (`node --import`) that replaces wall-clock time with a
 * logical clock for one bridge process. Not imported by bridge.mjs, install.mjs
 * or uninstall.mjs.
 *
 * Bridge deadlines are measured with Date.now(). On a loaded runner, several
 * hook processes starting together can be descheduled for longer than a whole
 * 60 ms accounting slice, so a test asserting exact counts would depend on CPU
 * scheduling rather than on the bridge's logic. Here time advances only as the
 * process reads the clock: one millisecond per READS_PER_MS reads. An ordinary
 * hook reads it about 165 times, well inside its 250 ms budget, while a wait on a
 * held lock still exhausts its budget deterministically. Tests that assert real
 * elapsed time opt out with `clock: 'wall'`.
 */
const READS_PER_MS = 4;
const start = Date.now();
let reads = 0;
Date.now = () => start + Math.floor(reads++ / READS_PER_MS);

/** Charge simulated slow work to the logical clock instead of sleeping. */
export function advanceLogicalClock(ms) {
  reads += Math.max(0, ms) * READS_PER_MS;
}
