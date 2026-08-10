# Enhanced diagnostics and analytics

This branch defines a short-message, copy-first failure workflow for TarkovMonitor.

## User-facing contract

- The message board shows a concise, stable description and diagnostic code.
- Exception messages include a `Copy diagnostics` action. The copied block contains the outer and inner exception chain, HResult values, socket error codes when available, operation context, endpoint host metadata, timing, and runtime information.
- Copying is best-effort and cannot throw back into the client if the clipboard is unavailable.
- Repeated failures with the same diagnostic key are collapsed for 30 seconds and update the existing message instead of flooding the board.
- The `Disclaimer` button opens the detached native `Disclaimer Information` window. It explains what is collected, what is redacted, and what the user should review before sharing.

## Privacy and retention

- API keys, authorization and bearer values, cookies, session tokens, account/profile/remote identifiers, IP addresses, query strings, and full user paths are redacted before clipboard or file persistence.
- Endpoints retain only scheme, host, and port. Invalid endpoint values are replaced with `[UNPARSED_ENDPOINT]`.
- Diagnostics are written locally under `%LOCALAPPDATA%\\TarkovMonitor\\Diagnostics`; there is no automatic upload.
- `diagnostics.jsonl` and `analytics.jsonl` are independently bounded at 1 MB per file with five rotated files.
- The in-memory message board is bounded at 200 messages and recent diagnostic deduplication state is bounded at 500 entries.
- Analytics contains event dimensions and timing only; it does not contain exception chains.

## Issue #194 resolution

Issue [#194](https://github.com/the-hideout/TarkovMonitor/issues/194) reported that the first visible matching message was delayed until `MatchingCompleted`/`MatchFound`.

The watcher now publishes `MatchingStarted` when `LocationLoaded` identifies a live matching state. For a late-starting monitor, the same state is evaluated when initial log reading completes. A fallback is emitted immediately before `MatchFound` when the earlier location event was not observed. The completion message remains in place.

The policy suppresses notifications for initial historical replay, past-log processing, already-published state, and a raid that has already entered its starting countdown. The normal path and fallback are both deduplicated so one raid cannot create duplicate matching-started messages.

## Failure-boundary evaluation

The branch routes application, watcher/log parsing, Tarkov Tracker, tarkov.dev, WebSocket, update, media, filesystem, UI, and startup WebView2 failures through stable diagnostic codes. Wrapper exceptions preserve their inner exception instead of flattening it into a string. Previously silent local task-state and player-profile lookup failures now enter the same pipeline while retaining their existing nonfatal fallback behavior.

Missing `data` envelopes now produce an actionable `InvalidDataException` instead of a null dereference. Optional API arrays are initialized safely, empty-language settings skip translation requests, and level lookup returns a safe value when level data is not available. Concurrent failures retain the maximum occurrence count, and failing notification subscribers are isolated and recorded as `TM-UI-005`/`TM-UI-006` so they cannot replace the original failure. Snackbar work is dispatched through the Blazor UI context.

The tarkov.dev JSON client also now models the current single `data` envelope correctly. Traders and hideout responses are direct dictionaries under `data`; tasks, maps, and items are content objects under `data`. The prior double-envelope assumption produced a `NullReferenceException` in `GetTraders` after a successful response, which is the failure represented by diagnostic code `TM-API-TARKOVDEV-001` and operation `UpdateApiData`.

## Validation

The test project contains 17 deterministic tests covering realistic synthetic cases: current API envelopes and partial payloads, missing data, translation nulls, inner exceptions, sensitive-value redaction, endpoint normalization, privacy-safe diagnostic keys, persistence failure, file rotation, concurrent failure collapse, bounded message retention, concurrent message snapshots, copy-button safety, notification-subscriber failure, truncation, and the Issue #194 matching policy. A live EFT run is still required to validate event ordering against current game logs; no runtime installation or launch is performed by this branch work.

For a branch-local manual run, use `tools\\Launch-EnhancedDiagnostics.ps1` or the desktop shortcut `TarkovMonitor Enhanced Diagnostics.lnk`. The launcher runs the checked-out project and does not replace the installed TarkovMonitor runtime.
