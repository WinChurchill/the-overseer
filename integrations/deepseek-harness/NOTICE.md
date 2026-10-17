# NOTICE

## Origin

`overseer-dsh-observer` is original code written by **THE OVERSEER PROJECT**.

It is not copied, extracted, vendored, or derived from the DeepSeek Harness source tree. No file
from `deepseek-harness` is redistributed here, in source or compiled form, and this directory
contains no third-party code.

The DeepSeek Harness reference checkout used while writing this plugin lives outside this
repository at `.tools/deepseek-harness-reference` and was read **read-only** to confirm public
contracts: the session event names and envelope, the `SessionEventMap` members used here, the
approval event fields, and the profile bundle patch format. Those contracts are described in
comments in `src/` and in `README.md`, with the pinned commit
`46a7f68b0922371ce7144b668b90e377d8e799f4`. Facts and interface names are not copied code.

## DeepSeek Harness

The DeepSeek Harness (`dsh`) is a separate project by DeepSeek, distributed by its own authors
under its own license (<https://github.com/deepseek-ai/deepseek-harness>). This repository does not
redistribute it, does not install it, and does not modify the user's `dsh` installation or
`DSH_HOME`. The plugin loads inside a user's existing `dsh` host at the user's explicit,
reversible choice (see `README.md` §8).

The plugin imports **nothing** from `dsh` at runtime: it uses Node's standard library only and
subscribes to the documented emit-mode session events by name.
Question detection additionally reads the already-materialized native metadata cache
inspected in installed Desktop 0.2.0-rc.2; no host implementation is redistributed.

## Dependencies

None. `package.json` declares no `dependencies`, no `devDependencies`, and no
`peerDependencies`. Tests run on Node's built-in runner (`node --test`).

## Data

The spool contains status metadata only — no message bodies, no reasoning text, no tool arguments
or outputs, no credentials (see `README.md` §9). It is written to an OVERSEER-owned directory on
the local machine and is never transmitted by this plugin.

## Project license

This original bridge is MIT licensed, copyright (c) 2026 Winston Lin.
The complete grant is included in the adjacent LICENSE file.
