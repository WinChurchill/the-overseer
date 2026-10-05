# Project website

Three static pages; no browser JavaScript, analytics, external fonts or runtime
package dependencies. Assets include sanitized compact HUD fixture renders (same
compact HUD presentation retained in RC10); synthetic counts do not demonstrate live host qualification.

```powershell
node scripts/Build-Site.mjs
node scripts/Check-Site.mjs
python -m http.server 8765 --bind 127.0.0.1 --directory artifacts/site
```

Open http://127.0.0.1:8765. Default build is a noindex local draft with no fake
repository/download URL. Do not upload the draft. For publication, set all URLs:

```powershell
node scripts/Build-Site.mjs --base-url=https://OWNER.github.io/REPOSITORY/ --repository-url=https://github.com/OWNER/REPOSITORY --download-url=https://github.com/OWNER/REPOSITORY/releases/download/v1.0.0-rc.10/OVERSEER-1.0.0-rc.10-win-x64.zip --version=1.0.0-rc.10
node scripts/Check-Site.mjs --publication
```

Replace OWNER/REPOSITORY and final version with approved identities. The builder
requires the versioned application ZIP and produces canonical/Open Graph metadata
and a sitemap for the configured project subpath. It uses no origin-wide robots.txt:
a project-subdirectory robots.txt does not control origin crawling. Search indexing
and actual external URL accessibility are checked after publication, not locally.

The prepared Pages workflow is manual and rejects private repositories or missing
configuration. It uploads only artifacts/site after validation. Before enabling it,
verify private security reporting, release/download URL and GitHub variables:
OVERSEER_SITE_URL, OVERSEER_REPOSITORY_URL, OVERSEER_DOWNLOAD_URL. Configure the
github-pages environment with owner approval as account features permit. Deployment
is an explicit publication step; a private repository's Pages site may be public.

The preview says unsigned; a signed release requires updating that statement and
requalifying the release before publishing. Synthetic PNGs are below 1 MB and can
serve as social previews; a dedicated 1280x640 card is optional future polish.
