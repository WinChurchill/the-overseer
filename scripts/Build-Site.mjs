#!/usr/bin/env node
// Build a dependency-free static site. Default output is a local, noindex draft.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const args = Object.fromEntries(process.argv.slice(2).map(arg => {
  const i = arg.indexOf('=');
  if (i < 0 || !arg.startsWith('--')) throw new Error('Use --name=value');
  return [arg.slice(2, i), arg.slice(i + 1)];
}));
for (const key of Object.keys(args)) if (!['output', 'base-url', 'repository-url', 'download-url', 'version'].includes(key)) throw new Error(`Unknown option ${key}`);
const out = path.resolve(root, args.output ?? 'artifacts/site');
const artifacts = path.join(root, 'artifacts') + path.sep;
if (!out.startsWith(artifacts)) throw new Error('Output must stay beneath repository artifacts/');
function https(value) {
  if (!value) return null;
  const u = new URL(value);
  if (u.protocol !== 'https:' || u.username || u.password || u.search || u.hash) throw new Error('Publication URLs must be HTTPS without credentials/query/fragment');
  return u;
}
const base = https(args['base-url']); const repo = https(args['repository-url']); const download = https(args['download-url']);
if (repo && (repo.hostname !== 'github.com' || !/^\/[\w.-]+\/[\w.-]+\/?$/.test(repo.pathname))) throw new Error('Repository URL must name a GitHub owner/repository');
if ([base, repo, download].some(Boolean) && ![base, repo, download].every(Boolean)) throw new Error('A publication build requires all three URLs');
const draft = !base; const version = args.version ?? '1.0.0-alpha.1';
if (!/^\d+\.\d+\.\d+(?:-[\w.]+)?$/.test(version)) throw new Error('Invalid version');
if (download && (!download.href.startsWith(repo.href.replace(/\/$/, '') + '/releases/download/') || !download.pathname.endsWith(`/OVERSEER-${version}-win-x64.zip`))) throw new Error('Download must be the versioned application ZIP in this repository');
if (base && !base.pathname.endsWith('/')) base.pathname += '/';

const e = s => String(s).replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;');
const repoUrl = repo?.href.replace(/\/$/, '');
const zipName = `OVERSEER-${version}-win-x64.zip`;
const arrow = '<span class="arrow" aria-hidden="true">↗</span>';
const down = '<span class="arrow" aria-hidden="true">↓</span>';
const downloadLink = download
  ? `<a class="button primary" href="${e(download.href)}"><span>Download for Windows</span><span class="meta">x64 · ${e(version)}</span>${down}</a>`
  : '<span class="button unavailable" aria-disabled="true">Download pending qualification</span>';
const sourceButton = repoUrl ? `<a class="button ghost" href="${e(repoUrl)}"><span>Source on GitHub</span>${arrow}</a>` : '';
const support = repoUrl
  ? `<a href="${e(repoUrl)}/issues/new/choose">Report a bug</a> · <a href="${e(repoUrl)}/security/advisories/new">Report a vulnerability privately</a>`
  : 'Public support and private security reporting open at publication.';

// Positions are percentages of the 1440 × 1100 window screenshot.
const markers = [[34, 9.5], [62, 14], [30.5, 30.6], [29.5, 47.5], [98.75, 72]];
const pins = markers.map(([x, y], i) => `<span class="marker" style="left:${x}%;top:${y}%" aria-hidden="true">${i + 1}</span>`).join('');

const hudShot = (alt, eager) => `<img class="img-dark" src="assets/hud-window-dark.png" width="1440" height="1100" alt="${alt}"${eager ? ' fetchpriority="high"' : ' loading="lazy"'}><img class="img-light" src="assets/hud-window-light.png" width="1440" height="1100" alt="${alt}"${eager ? '' : ' loading="lazy"'}>`;

const index = `
<section class="hero">
  <div class="wrap">
    <div>
      <p class="label tick">Windows HUD for AI coding agents</p>
      <h1>Know when your agents need you<span class="dot">.</span></h1>
      <p class="lead">A small window that sits on your Windows desktop, shows what your coding agents are doing, and puts the ones <strong>waiting on you</strong> at the top.</p>
      <ul class="specs"><li>Codex · Claude Code · Claude Projects · DeepSeek Harness</li></ul>
      <div class="actions">${downloadLink}${sourceButton}</div>
      <p class="fine">${draft ? 'Local alpha preparation preview. No public release verified.' : `Free, MIT, runs locally. Unsigned alpha: <a href="install.html#download">check the download</a> first.`}</p>
    </div>
    <figure class="frame notch">
      <div class="frame-bar"><span class="label"><b>Live view</b> · follows this page's theme</span><span class="label">Sample tasks</span></div>
      <div class="shot">${hudShot('The OVERSEER window: 3 tasks need you and 4 are working, listed under the projects Aurora, Webots harness and TCS pipeline.', true)}<span class="glow" aria-hidden="true"></span></div>
    </figure>
  </div>
</section>

<section class="section" id="tour">
  <div class="wrap">
    <div class="bar"><span class="bar-title">What you're looking at</span><span class="label">Tour / 05</span></div>
    <p class="bar-note">Sample tasks. Every part shown is in the current alpha. Try the theme switch up top.</p>
    <div class="tour">
      <figure class="frame notch">
        <div class="frame-bar"><span class="label"><b>OVERSEER</b> · window mode</span><span class="label">Light &amp; dark</span></div>
        <div class="shot"><div class="pins">${hudShot('OVERSEER window with numbered markers on the counts, coverage, views, a task and the status light.', false)}${pins}</div></div>
      </figure>
      <ol class="legend">
        <li><div><h3>Counts</h3><p>Tasks that need you, and tasks still working, across every project.</p></div></li>
        <li><div><h3>Coverage</h3><p>If a source goes quiet it says <span class="chip amber">Limited coverage</span>, never a fake "nothing running".</p></div></li>
        <li><div><h3>Views</h3><p><strong>Now</strong>, <strong>Recent</strong> and <strong>This session</strong>, filtered to <em>Needs you</em> or <em>Working</em>.</p></div></li>
        <li><div><h3>Projects &amp; tasks</h3><p>Grouped by project, with what each agent is doing: <span class="chip green">Running tests</span> or <span class="chip red">Needs input</span>.</p></div></li>
        <li><div><h3>Status light</h3><p>The edge strip follows the overall state, readable from the corner of your eye.</p></div></li>
      </ol>
    </div>
  </div>
</section>

<section class="section alt" id="day">
  <div class="wrap">
    <div class="section-head"><div><p class="label amber">In practice</p><h2>A day with three agents<span class="dot">.</span></h2></div><p>Three agents in three repos, and you working in a fourth window.</p></div>
    <ol class="day">
      <li><span class="label">Start</span><time>09:12</time><p>Kick off Codex, Claude Code and a DSH sweep. Collapse OVERSEER into a corner.</p><p class="hud">0 Needs you · 3 Working</p></li>
      <li class="hot"><span class="label">Interrupt</span><time>10:40</time><p>Codex asks a question. It jumps to the top, the light changes, a voice cue plays if unmuted.</p><p class="hud">1 Needs you · Needs input</p></li>
      <li><span class="label">Gap</span><time>14:05</time><p>A source stops reporting. The HUD says so instead of quietly dropping its tasks.</p><p class="hud">● Limited coverage</p></li>
      <li><span class="label">Wrap up</span><time>17:30</time><p>Check <strong>This session</strong> for what finished. Close it; it keeps watching from the tray.</p><p class="hud">Recent · History</p></li>
    </ol>
  </div>
</section>

<section class="section" id="sources">
  <div class="wrap">
    <div class="bar"><span class="bar-title">Works with</span><span class="label">Sources / 04</span></div>
    <p class="bar-note">Read-only: OVERSEER never answers prompts or controls an agent.</p>
    <div class="rows">
      <article class="row"><span class="num">01</span><div><h3>Codex</h3><p>Found automatically. Approval prompts not detected.</p></div><span class="tag on">Automatic</span></article>
      <article class="row"><span class="num">02</span><div><h3>Claude Code</h3><p>Included hooks. Questions and plan approvals; other permission prompts unverified.</p></div><span class="tag">Experimental</span></article>
      <article class="row"><span class="num">03</span><div><h3>Claude Projects</h3><p>Opt-in. Reads the Threads / Overview views open in the desktop app.</p></div><span class="tag">Experimental</span></article>
      <article class="row"><span class="num">04</span><div><h3>DeepSeek Harness</h3><p>Included plugin. Observer 1.1.0 targets Desktop 0.2.0-rc.2 question metadata.</p></div><span class="tag">Experimental</span></article>
    </div>
  </div>
</section>

<section class="section alt" id="people">
  <div class="wrap">
    <div class="bar"><span class="bar-title">People &amp; credits</span><span class="label">Contributors / 03</span></div>
    <p class="bar-note">Thanks to the contributors whose testing and feedback shaped every alpha.</p>
    <div class="people">
      <a class="person lead-person notch" href="https://github.com/WinChurchill"><img class="avatar" src="assets/people/winchurchill.png" width="104" height="104" alt="GitHub avatar of Winston Lin" loading="lazy"><span><h3>Winston Lin</h3><span class="label">Creator · maintainer</span></span>${arrow}</a>
      <a class="person" href="https://github.com/lanbodikai"><img class="avatar" src="assets/people/lanbodikai.png" width="104" height="104" alt="GitHub avatar of lanbodikai" loading="lazy"><span><h3>lanbodikai</h3><span class="label">Contributor · tester</span></span>${arrow}</a>
      <a class="person" href="https://github.com/qinyif001"><img class="avatar" src="assets/people/qinyif001.png" width="104" height="104" alt="GitHub avatar of qinyif001" loading="lazy"><span><h3>qinyif001</h3><span class="label">Contributor · tester</span></span>${arrow}</a>
    </div>
    <p class="credits-line">Built on <a href="https://github.com/LH-03/codex-monitor-hud">codex-monitor-hud</a> by LH-03 (MIT) · voice cues recorded with <a href="https://huggingface.co/hexgrad/Kokoro-82M">Kokoro</a> · built with help from <a href="https://claude.com/claude-code">Claude Code</a>, <a href="https://openai.com/codex/">Codex</a> and <a href="https://www.deepseek.com/">DeepSeek</a>${repoUrl ? ` · <a href="${e(repoUrl)}/blob/main/CONTRIBUTING.md">Contribute</a>` : ''}</p>
  </div>
</section>

<section class="cta">
  <div class="wrap">
    <h2>Stop checking every terminal<span class="dot">.</span></h2>
    <div><p>No installer. Extract, run, done. <a href="install.html">Install guide</a></p><div class="actions">${downloadLink}</div></div>
  </div>
</section>`;

const install = `
<section class="doc-hero"><div class="wrap"><p class="label tick">Guide / Windows x64</p><h1>Install &amp; troubleshoot<span class="dot">.</span></h1><p class="lead">The app is a self-contained ZIP: no installer, no .NET or Node needed. The optional agent bridges need Node in the agent's own environment.</p></div></section>
<div class="wrap doc">
  <nav class="toc" aria-label="On this page"><p class="label">On this page</p><ol><li><a href="#download">Download &amp; check</a></li><li><a href="#run">First run</a></li><li><a href="#sources">Connect sources</a></li><li><a href="#troubleshooting">Troubleshooting</a></li><li><a href="#update">Update &amp; remove</a></li><li><a href="#help">Get help</a></li></ol></nav>
  <div>
    <section id="download"><h2>Download &amp; check</h2>
      <ol class="numbered">
        <li><span>Download <code>${e(zipName)}</code> and <code>${e(zipName)}.sha256</code>. ${draft ? 'Use the private test handoff until a public release exists.' : 'Use the release assets, not GitHub\'s source-code ZIP.'}</span></li>
        <li><span>In PowerShell, in the download folder, print the hash and compare it with the <code>.sha256</code> file:</span></li>
      </ol>
      <pre><code>Get-FileHash .\\${e(zipName)} -Algorithm SHA256</code></pre>
      <p>With the GitHub CLI you can also check where it was built:</p>
      <pre><code>gh attestation verify ${e(zipName)} --repo ${e(repoUrl ? repoUrl.replace('https://github.com/', '') : 'OWNER/REPO')}</code></pre>
      <p class="note"><strong>Unsigned</strong>This alpha is not code-signed, so Windows may show an unknown-publisher or SmartScreen warning. Keep Windows protections on; if it is blocked, report it.</p>
      <div class="actions">${downloadLink}</div>
    </section>
    <section id="run"><h2>First run</h2>
      <ol class="numbered">
        <li><span>Extract the whole ZIP into a folder you own, for example under <code>%LOCALAPPDATA%\\Programs</code>.</span></li>
        <li><span>Run <code>TheOverseer.App.exe</code> as a normal user.</span></li>
        <li><span>Open <strong>⋯ → Projects &amp; sources…</strong> to choose what to watch. The coverage indicator shows what it can see.</span></li>
        <li><span>Voice starts muted. Open <strong>⋯ → Light &amp; voice…</strong>, preview the included Kokoro voice, then unmute.</span></li>
      </ol>
      <p>Tested target: Windows x64. ARM64, x86, macOS and Linux are not supported yet.</p>
    </section>
    <section id="sources"><h2>Connect sources</h2>
      <p>Codex sessions on Windows are found automatically. The others are optional and <strong>experimental</strong>. Keep a bridge's folder at a permanent path, preview its changes before applying, and restart OVERSEER after installing, removing or moving one.</p>
      <h3>Claude Code (desktop Code tab or editor)</h3>
      <p>Follow <code>integrations/claude-code/README.md</code> in the ZIP. Run the installer without <code>--apply</code> first to preview. The hooks need Node 18+ where the Claude session runs. Desktop chat MCP settings do not configure Code hooks. WSL, SSH and cloud sessions are not supported yet.</p>
      <h3>Claude Projects (desktop app)</h3>
      <p>Turn on <strong>Observe loaded Claude Projects desktop views</strong> in Projects &amp; sources and restart OVERSEER. Open <strong>Threads / Overview</strong> in Claude. Views that are closed or unloaded show as unknown, not as zero.</p>
      <h3>DeepSeek Harness Desktop</h3>
      <p>Follow <code>integrations/deepseek-harness/README.md</code>. Install from Desktop's Plugins page (or its bundled CLI with Desktop fully closed) into the <code>desktop</code> profile. Needs Node 20+. Observer 1.1.0 reports approval and question waits using Desktop 0.2.0-rc.2 metadata. Missing metadata reduces coverage instead of guessing.</p>
    </section>
    <section id="troubleshooting"><h2>Troubleshooting</h2>
      <details><summary>Coverage says limited or offline</summary><p>Open the coverage indicator and read the reason and suggested fix. Check the chosen folders or bridge, then restart after setup changes. A missing source never counts as zero work.</p></details>
      <details><summary>A Codex approval doesn't show up</summary><p>Codex approval detection is not supported. A normal tool call is not treated as a pending approval.</p></details>
      <details><summary>I closed a Codex question card</summary><p>Closing the card leaves no record that the question was cancelled. After 15 minutes it shows as <em>Input status unconfirmed</em>. Unrelated chat text never counts as an answer.</p></details>
      <details><summary>Claude Projects is missing</summary><p>The observer only reads Threads / Overview views that are loaded in the Claude desktop app, without bringing Claude to the front. The Projects folder alone has no task status. No browser relay is used.</p></details>
      <details><summary>No sound</summary><p>Voice starts muted. Choose <em>Use included Kokoro voice</em>, press Preview, then check the device and provider status in Light &amp; voice. Windows speech is optional.</p></details>
      <details><summary>I moved the app or a bridge folder</summary><p>Fix shortcuts and startup, rebind bridge paths using the bridge's guide, then restart both the agent host and OVERSEER.</p></details>
      <details><summary>Windows blocks the app</summary><p>Note the exact warning or policy and report it. Don't turn off Windows protections to get past it.</p></details>
    </section>
    <section id="update"><h2>Update &amp; remove</h2>
      <p><strong>Update:</strong> Exit, back up <code>%LOCALAPPDATA%\\OVERSEER</code>, extract the new version into a new folder, then fix startup and bridge paths. Keep the old folder and backup until you're happy.</p>
      <p><strong>Roll back:</strong> Exit, restore the backup and start the old copy.</p>
      <p><strong>Remove:</strong> turn off startup, uninstall any hooks or plugins, Exit, and delete the app folder. Deleting saved data is optional; your other host settings stay untouched.</p>
    </section>
    <section id="help"><h2>Get help</h2>
      <p>${support}</p>
      <p>Include the version and ZIP hash, Windows build, display scale, agent versions and steps to reproduce. Never upload raw sessions, credentials or full settings files.</p>
    </section>
  </div>
</div>`;

const privacy = `
<section class="doc-hero"><div class="wrap"><p class="label tick">Policy / plain language</p><h1>Privacy &amp; licensing<span class="dot">.</span></h1><p class="lead">OVERSEER watches metadata on your own machine. It has no account, no server and no analytics.</p></div></section>
<div class="wrap doc">
  <nav class="toc" aria-label="On this page"><p class="label">On this page</p><ol><li><a href="#observe">What it reads</a></li><li><a href="#storage">Where data lives</a></li><li><a href="#voice">Voice</a></li><li><a href="#license">Licensing</a></li><li><a href="#report">Reporting</a></li></ol></nav>
  <div>
    <section id="observe"><h2>What it reads</h2>
      <p>OVERSEER never answers approvals, sends prompts or controls an agent. It reads local Codex session files; the optional bridges receive host events or hooks and keep only bounded metadata. Prompts, answers, tool output and other sensitive fields can be present in that input, but they are not copied into its records, history, voice lines or diagnostics.</p>
      <p>The window can show task titles, IDs and project paths. Bridge spools hold IDs, timestamps, states and project metadata. Treat settings and spools as personal data, not anonymous data.</p>
    </section>
    <section id="storage"><h2>Where data lives</h2>
      <p>Settings, window placement, bounded history and logs, recordings and spools live under <code>%LOCALAPPDATA%\\OVERSEER</code>, or the data folder you choose. The app folder is separate. Exit the app and stop bridges before deleting data.</p>
      <p>The app makes no telemetry or analytics requests. This website has no analytics, scripts, external fonts or trackers; its host may keep ordinary access logs.</p>
    </section>
    <section id="voice"><h2>Voice</h2>
      <p>Voice is muted for new profiles. The included voice is nine prerecorded Kokoro lines that play locally with no model or network. Windows speech, local voice packs and your own recordings also play locally.</p>
      <p class="note"><strong>Network</strong>Only if you configure and select the optional TTS endpoint: the app sends that endpoint generic notification text, an event code and a <code>Task N</code> alias. The provider sees the request and your network address. Task titles, prompts, answers and tool output are never included.</p>
      <p>If a provider fails or voice is muted, the app says so. It never silently switches to another provider.</p>
    </section>
    <section id="license"><h2>Licensing</h2>
      <p>OVERSEER's own code, docs and generated artwork are MIT licensed, © 2026 Winston Lin. Adapted Codex monitoring logic keeps the MIT notice of codex-monitor-hud. The bundled .NET runtime's terms ship inside each app ZIP.</p>
      <p>${repoUrl ? `<a href="${e(repoUrl)}/blob/main/LICENSE">Project license</a> · <a href="${e(repoUrl)}/blob/main/THIRD_PARTY_NOTICES.md">Third-party notices</a>` : 'LICENSE and THIRD_PARTY_NOTICES.md are included in the candidate and source repository.'}</p>
    </section>
    <section id="report"><h2>Reporting</h2>
      <p>${support}</p>
      <p>Remove paths, names and IDs before sharing evidence. Never post credentials or raw session data in a public issue.</p>
    </section>
  </div>
</div>`;

const pages = [
  { file: 'index.html', title: 'OVERSEER — Windows HUD for AI coding agents', description: 'A small Windows window that shows which Codex, Claude Code and other AI coding agent tasks are working, and which are waiting on you. Local, free and open source.', body: index },
  { file: 'install.html', title: 'Install & troubleshoot — OVERSEER', description: 'Download and verify the self-contained OVERSEER Windows x64 ZIP, connect Codex, Claude Code, Claude Projects and DeepSeek Harness, and fix common problems.', body: install },
  { file: 'privacy.html', title: 'Privacy & licensing — OVERSEER', description: 'What OVERSEER reads and stores on your machine, what the optional network voice sends, and the MIT and upstream licenses.', body: privacy },
];

const nav = file => [['index.html', 'Overview'], ['install.html', 'Install'], ['privacy.html', 'Privacy']]
  .map(([href, label]) => `<a href="${href}"${href === file ? ' aria-current="page"' : ''}>${label}</a>`).join('')
  + (repoUrl ? `<a class="ext" href="${e(repoUrl)}">GitHub ${arrow}</a>` : '');

const footer = `<footer class="site-footer"><div class="wrap">
  <div><a class="brand" href="index.html">OVERSEER<span class="dot">.</span></a><p>A local Windows HUD that shows what your AI coding agents are doing and when they need you.</p></div>
  <div><h2>Product</h2><ul><li><a href="install.html">Install</a></li><li><a href="install.html#troubleshooting">Troubleshooting</a></li><li><a href="privacy.html">Privacy</a></li>${repoUrl ? `<li><a href="${e(repoUrl)}/releases">Releases</a></li>` : ''}</ul></div>
  <div><h2>Project</h2><ul>${repoUrl ? `<li><a href="${e(repoUrl)}">Source</a></li><li><a href="${e(repoUrl)}/issues">Issues</a></li><li><a href="${e(repoUrl)}/blob/main/LICENSE">MIT license</a></li>` : '<li><span>Source URL pending</span></li>'}</ul></div>
</div><div class="legal"><div class="wrap"><span class="label">OVERSEER · ${e(version)}</span><span class="label">MIT · © 2026 Winston Lin · Read-only agent observation</span></div></div></footer>`;

fs.mkdirSync(out, { recursive: true });
fs.rmSync(path.join(out, 'assets'), { recursive: true, force: true });
fs.cpSync(path.join(root, 'site/assets'), path.join(out, 'assets'), { recursive: true });
for (const p of pages) {
  const canonical = base ? new URL(p.file === 'index.html' ? '' : p.file, base).href : null;
  const image = base ? new URL('assets/og-card.png', base).href : null;
  const metadata = canonical
    ? `<link rel="canonical" href="${e(canonical)}"><meta property="og:url" content="${e(canonical)}"><meta property="og:image" content="${e(image)}"><meta property="og:image:width" content="1200"><meta property="og:image:height" content="630"><meta property="og:image:alt" content="OVERSEER: know when your agents need you. The HUD window with sample tasks."><meta name="twitter:card" content="summary_large_image">`
    : '<meta name="robots" content="noindex,nofollow">';
  const html = `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>${e(p.title)}</title><meta name="description" content="${e(p.description)}"><meta name="theme-color" content="#111613" media="(prefers-color-scheme: dark)"><meta name="theme-color" content="#f0eee7" media="(prefers-color-scheme: light)"><meta property="og:title" content="${e(p.title)}"><meta property="og:description" content="${e(p.description)}"><meta property="og:type" content="website">${metadata}<link rel="icon" href="assets/favicon.svg" type="image/svg+xml"><link rel="stylesheet" href="assets/style.css"></head>
<body><a class="skip" href="#main">Skip to content</a>
<input type="checkbox" id="theme-flip" class="theme-flip" aria-label="Switch between light and dark theme">
<header class="site-header"><div class="wrap"><a class="brand" href="index.html" aria-label="OVERSEER home">OVERSEER<span class="dot">.</span></a><nav aria-label="Main">${nav(p.file)}<label class="theme-toggle" for="theme-flip" title="Switch theme"><span class="half" aria-hidden="true"></span><span class="to-light">Light</span><span class="to-dark">Dark</span></label></nav></div></header>
<div class="sub-bar"><div class="wrap"><span class="label amber">See the work. Answer when it matters.</span><span class="label">OVERSEER / ${e(version)}</span></div></div>
${draft ? '<aside class="draft" aria-label="Publication status">LOCAL PREVIEW · qualification pending · not a public release</aside>' : ''}<main id="main">${p.body}</main>
${footer}</body></html>
`;
  fs.writeFileSync(path.join(out, p.file), html);
}
const sitemap = path.join(out, 'sitemap.xml');
if (base) fs.writeFileSync(sitemap, `<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">${pages.map(p => `<url><loc>${e(new URL(p.file === 'index.html' ? '' : p.file, base).href)}</loc></url>`).join('')}</urlset>\n`);
else if (fs.existsSync(sitemap)) fs.unlinkSync(sitemap);
fs.writeFileSync(path.join(out, 'site-build.json'), JSON.stringify({ draft, version, baseUrl: base?.href ?? null, repositoryUrl: repoUrl ?? null, downloadUrl: download?.href ?? null, pages: pages.map(p => p.file) }, null, 2) + '\n');
console.log(`Built ${pages.length} static pages (${draft ? 'local noindex draft' : 'publication configuration'}) into ${out}`);
