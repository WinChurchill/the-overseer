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
are recorded in the
local staging evidence and release ledger. Pages stays disabled and the existing
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
