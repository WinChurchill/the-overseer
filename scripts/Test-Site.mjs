#!/usr/bin/env node
// Meaningful controls for accidental draft publication, URL identity and escaping.
import assert from 'node:assert/strict';import {spawnSync} from 'node:child_process';import fs from 'node:fs';import path from 'node:path';import {fileURLToPath} from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');let controls=0;
const run=(script,args,ok=true)=>{const p=spawnSync(process.execPath,[path.join(root,'scripts',script),...args],{cwd:root,encoding:'utf8'});assert.equal(p.status===0,ok,p.stderr+p.stdout);controls++;return p;};
run('Build-Site.mjs',['--output=artifacts/site-controls/draft']);run('Check-Site.mjs',['--output=artifacts/site-controls/draft']);run('Check-Site.mjs',['--output=artifacts/site-controls/draft','--publication'],false);
// A valid DOM/SEO report must not certify stale product capability copy.
const draftHome=fs.readFileSync(path.join(root,'artifacts/site-controls/draft/index.html'),'utf8');
const draftInstall=fs.readFileSync(path.join(root,'artifacts/site-controls/draft/install.html'),'utf8');
assert.match(draftHome,/Observer 1\.1\.0 targets Desktop 0\.2\.0-rc\.2 question metadata/);
assert.match(draftInstall,/Missing metadata reduces coverage/);
assert.doesNotMatch(draftHome+draftInstall,/human input events are not emitted/);
controls++;
const urls=['--version=1.0.0-rc.9','--base-url=https://example.test/overseer/','--repository-url=https://github.com/example/overseer','--download-url=https://github.com/example/overseer/releases/download/v1.0.0-rc.9/OVERSEER-1.0.0-rc.9-win-x64.zip'];
run('Build-Site.mjs',['--output=artifacts/site-controls/public',...urls]);run('Check-Site.mjs',['--output=artifacts/site-controls/public','--publication']);
run('Build-Site.mjs',['--output=../site-escape'],false);run('Build-Site.mjs',['--version=<script>'],false);run('Build-Site.mjs',['--base-url=https://example.test/'],false);
run('Build-Site.mjs',['--output=artifacts/site-controls/wrong-version',...urls,'--version=1.0.0-rc.10'],false);
run('Build-Site.mjs',['--output=artifacts/site-controls/insecure',...urls.map(v=>v.replace('https://example.test','http://example.test'))],false);
// Rebuilding a publication directory as draft must remove the stale sitemap.
run('Build-Site.mjs',['--output=artifacts/site-controls/public']);run('Check-Site.mjs',['--output=artifacts/site-controls/public']);
console.log(`${controls} site publication controls passed`);
