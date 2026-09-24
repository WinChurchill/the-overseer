# NOTICE

**OVERSEER Claude Code hook bridge** - original code by THE OVERSEER PROJECT.

Copyright (c) THE OVERSEER PROJECT.

## Third-party product notice

Claude Code is a third-party product of Anthropic. It is **not** redistributed with this
integration, and this integration is not affiliated with, endorsed by, or sponsored by
Anthropic.

* No Claude Code source code, binary, package, SDK, documentation text or asset is copied into
  this directory.
* The event mapping in `lib/mapping.mjs` and the tables in `README.md` are interoperability
  facts derived from the public documentation at
  <https://code.claude.com/docs/en/hooks> and
  <https://code.claude.com/docs/en/tools-reference.md> (retrieved 2026-09-24), used to describe
  compatibility with that product.
* "Claude", "Claude Code" and "Anthropic" are trademarks of Anthropic. They are used here only
  to identify the third-party product this integration talks to.

## Dependencies

None. Every script uses only the Node.js standard library, installs nothing with npm, and makes
no network calls at hook runtime.

## Fixtures

Everything under `fixtures/` is synthetic and sanitized. Session ids, paths, prompts, tool
arguments, tool outputs, messages and transcripts are invented placeholders (for example
`SANITIZED_PROMPT_TEXT`). No real session, transcript, credential or user data is included.
