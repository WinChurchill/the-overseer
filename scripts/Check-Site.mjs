#!/usr/bin/env node
// Static structure/link/metadata checks; browser a11y and live URLs remain separate.
import fs from 'node:fs';import path from 'node:path';import {fileURLToPath} from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const output=process.argv.find(a=>a.startsWith('--output='))?.slice(9)??'artifacts/site';
const dir=path.resolve(root,output);const publication=process.argv.includes('--publication');
const failures=[];let checks=0;const check=(ok,name)=>{checks++;if(!ok)failures.push(name);};
const manifest=JSON.parse(fs.readFileSync(path.join(dir,'site-build.json'),'utf8'));
check(!publication||!manifest.draft,'publication must not use draft URLs');
const titles=new Set(),descriptions=new Set();
for(const file of manifest.pages){const html=fs.readFileSync(path.join(dir,file),'utf8');
const title=html.match(/<title>(.*?)<\/title>/s)?.[1];const description=html.match(/name="description" content="([^"]+)"/)?.[1];
check(!!title&&!titles.has(title),file+': unique title');titles.add(title);check(!!description&&!descriptions.has(description),file+': unique description');descriptions.add(description);
check(/<html lang="en">/.test(html),file+': language');check(/name="viewport"/.test(html),file+': viewport');check((html.match(/<h1[ >]/g)??[]).length===1,file+': one H1');
check(/<main id="main">/.test(html)&&/class="skip"/.test(html),file+': landmarks and skip link');check(!/<script\b|<iframe\b/.test(html),file+': no browser scripts/embedded tracking');
if(file==='install.html')check(html.includes('Get-FileHash .\\OVERSEER-'),file+': usable Windows hash command');
for(const img of html.matchAll(/<img\b([^>]+)>/g)){check(/alt="[^"]+"/.test(img[1]),file+': image alt');check(/width="\d+"/.test(img[1])&&/height="\d+"/.test(img[1]),file+': image dimensions');}
for(const m of html.matchAll(/(?:href|src)="([^"]+)"/g)){const value=m[1];if(value.startsWith('https://'))continue;if(value.startsWith('#')){check(html.includes(`id="${value.slice(1)}"`),file+': fragment '+value);continue;}check(!value.startsWith('http:')&&!value.startsWith('//'),file+': secure URLs');const target=path.resolve(dir,path.dirname(file),value.split('#')[0]);check(target.startsWith(dir+path.sep)&&fs.existsSync(target),file+': local link '+value);}
if(manifest.draft){check(/name="robots" content="noindex,nofollow"/.test(html),file+': draft noindex');check(!/rel="canonical"/.test(html),file+': no fabricated draft canonical');check(/Download pending qualification/.test(html)||file==='privacy.html',file+': truthful download');}
else{const url=new URL(file==='index.html'?'':file,manifest.baseUrl).href;check(html.includes(`rel="canonical" href="${url}"`),file+': canonical project subpath');check(html.includes('property="og:image"')&&html.includes('name="twitter:card"'),file+': social metadata');check(!html.includes('noindex'),file+': publication indexable');}
}
const sitemap=path.join(dir,'sitemap.xml');check(manifest.draft?!fs.existsSync(sitemap):fs.existsSync(sitemap),'sitemap follows publication config');check(!fs.existsSync(path.join(dir,'robots.txt')),'no ineffective project-subdirectory robots.txt');
const result={checks,failed:failures.length,failures,draft:manifest.draft,scope:'static only; live links, browser interaction and deployment are separately qualified'};
fs.writeFileSync(path.join(dir,'site-checks.json'),JSON.stringify(result,null,2)+'\n');console.log(JSON.stringify(result));if(failures.length)process.exitCode=1;
