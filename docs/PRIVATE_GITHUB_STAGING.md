# Private GitHub staging

On 2026-10-05 Winston authorized creation and upload of the private repository
[WinChurchill/the-overseer](https://github.com/WinChurchill/the-overseer).
He selected full history with only Astra/DeepSeek identities replaced by his
verified WinChurchill identity in the upload copy. The original local history,
branches, qualification tags, candidate ZIPs and owner results remain intact.

## Attribution and source identity

Only the exact `Astra <astra@local>` and `DeepSeek <deepseek@local>` author/committer
identities are replaced. Other identities, author/commit dates, messages, trees
and parent relationships are preserved. Contributor identity comes from the
human's verified Git identity, never the AI tool used. Future collaborators use
their own identities; the initial attribution correction does not credit their
future contributions to Winston.

Changing commit metadata changes commit hashes, including descendant hashes.
The ignored staging evidence records the old-to-new mapping and verifies every
tree and mapped parent. The uploaded `main` matches the reviewed local baseline;
the current cleanup and staging branches remain available for review. No local
main merge or force-push is part of this action. Only intended branches are
uploaded; old tags, artifacts, owner logs, credentials and personal profiles are
not included.

The private RC13 ZIP still identifies its original tested source `c0ced607` and
fixed archive hash. Metadata-normalized GitHub commits are traceable to it by
the local mapping; they are not a new qualification of signed or CI-built bytes.
Future release packages must record their actual GitHub source commit and pass
the exact-byte and owner gates. No release assets are uploaded in this source
staging step.

## Working after the upload

The normalized upload branch is `staging/private-upload`; the older original
branches remain as local evidence. Use that branch or the fetched GitHub refs
as the base for future GitHub work. A normalized `github/main` local alias can
track GitHub's `main` while the original local `main` remains preserved. Do not
push the original historical branches or qualification tags into this repository.

GitHub's Verify workflow is configured for private pushes and manual runs. Results
are recorded in local staging evidence and the release ledger. Pages stays disabled and the existing
website workflow requires a public repository and an explicit publication input.
Private source upload does not authorize a public repository, website, release,
announcement or adding collaborators.

The initial hosted Verify run built successfully and passed 748 of 749 tests. Its
resize-retention fixture requested height 810, while native WPF limited the hosted
window to 788. That fixture now chooses a size within the actual work area and
also verifies saved height. The separate scale/work-area theory still checks
clamping. This changes a test only; no application code or RC13 bytes change.

The next run exposed timing assumptions in two asynchronous tests. Snapshot
publication now uses a bounded completion signal and verifies loop shutdown;
the completed-monitor test drains posted outcomes until failure arrives, then
rejects another late snapshot. Its queued dispatcher is thread-safe. No tests
are skipped and application behavior stays unchanged.

GitHub refused main branch protection with HTTP 403 because this account requires
Pro for protection on private repositories. Main review remains a documented
manual requirement. No purchase or visibility change was made.

## Verified hosted result

[Windows Verify](https://github.com/WinChurchill/the-overseer/actions/runs/37413476257) passed on
`0dbf6d078847ad92f93548b4031d3bc1b2ae6838`: clean build, 749 application tests, both bridge suites,
report/upgrade controls and static website/publication controls. This is hosted
source/test evidence; final CI-produced release bytes and H1–H16 remain pending.
A later documentation-only commit records this result without changing the app.

## Email privacy plan before publication

On November 10, 2026 Winston requested a plan to remove personal addresses before
public exposure. The reviewed private history contained two author/committer
addresses outside GitHub's no-reply domain, across 233 commits. The count will
change as private review fixes are committed. The addresses are kept out of this
document and public evidence.

1. Keep the existing repository private and preserve a local Git bundle containing
   the selected refs. Record the original refs and source-to-archive mapping.
2. Confirm Winston's verified GitHub no-reply address for future commits. Any
   collaborator retains their own attribution and chooses their own public address.
3. Prepare an isolated mirror with only intended public refs. Replace the exact
   private author and committer addresses with the approved no-reply address.
   Review workstation path redactions separately. Keep names, dates, tree content
   and parent structure except for the explicitly reviewed privacy changes.
4. Produce an old-to-new commit/ref mapping. Verify all source trees and parent
   relationships, rerun the history/payload scans, and inspect the proposed public
   refs for personal paths and addresses. Preserve upstream notices and attribution.
5. Build and qualify a fresh candidate from the resulting source SHA. Rewriting
   metadata changes descendant hashes, so earlier ZIPs retain their original source
   identity and qualification records.
6. Review the resulting refs, mappings and release packet with Winston before any
   replacement remote, visibility change or publication. The original private
   history remains available for audit.

On November 11 Winston authorized privacy cleanup for a private release review.
A separate, local, main-only copy now replaces the owner's private address with
the account's ID-based GitHub no-reply address and redacts the exact workstation
tokens from historical text. The audit distinguishes GitHub's official service
committer from a personal address and preserves that service identity.

The original private repository and its refs remain intact. The review packet
contains the original bundle, sanitized bundle, commit mapping, and verification
of every tree against the exact redactions, identity/date/message preservation,
and mapped parent structure. Rewritten commits do not retain the originals' GitHub
signature verification. The publication copy also excludes the two personal-website
reference PNGs under `design/reference/` from every commit. All remaining current
files, including the application and its sample HUD screenshots, are unchanged.

Before public exposure, review the sanitized refs and use a clean publication
repository or a separately approved replacement process. Merely force-pushing a
rewritten branch cannot establish removal of old GitHub PR refs, cached commits,
Actions logs or artifacts. No force-push, remote replacement or visibility change
is authorized by preparing this packet. Keep the existing repository private.
