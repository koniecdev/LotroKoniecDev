# Spec 0003: Large exported.txt upload (lift the 30 MB cap)

- **Status:** Implemented
- **Date:** 2026-06-28
- **Author:** ticket-worker
- **Ticket:** #208 (Critical bug — exported.txt is too large to upload)
- **Related:** spec 0001 (import lifecycle), KittySaver `CreateCatGalleryItem` (RequestSizeLimit mirror), branch `208-critical-bug-exportedtxt-is-too-large-to-upload`

## Business context

An admin updates the catalog by uploading a fresh `exported.txt` through the Blazor SSR import form,
which forwards it to the TMS API's import endpoint (spec 0001). The export is ~80 MB today and grows
as the game adds text, but both hops cap a request body at Kestrel's 30 MB default, so the upload is
rejected before it can be imported — the core update loop is blocked. This is a critical bug.

## Goal

An admin can upload the entire `exported.txt` (~80 MB, with headroom to grow) in a single operation
and have it imported, without the request being rejected for its size or aborted by a client timeout.

## In scope

- Lift the request-body and multipart form-length limits to a shared ceiling on **both** hops:
  the Frontend host (browser → Blazor SSR form) and the TMS API import endpoint (Frontend → API).
- A single shared ceiling constant (`ImportUploadLimits.MaxUploadBytes` = 256 MB), with the API side
  configurable via `Import:MaxUploadBytes` so ops can lift it further without a code change.
- Make the Frontend's resilience (Polly) timeout upload-aware: a multipart upload gets a wide budget
  (the default 10 s would abort an ~80 MB upload + its synchronous server-side import).
- A clean, actionable `413 Payload Too Large` when an upload does exceed the ceiling.

## Out of scope

- A **chunked / resumable upload protocol** (e.g. tus). The present need is "the cap blocks 80 MB",
  not "uploads are interrupted"; a single admin uploads occasionally over a reliable connection, and
  the house YAGNI rule says pick the simple path. Revisit only if real uploads prove unreliable.
- Streaming the import row-by-row off the socket — the framework still buffers the upload to a temp
  file (spills past 64 KB), which is fine for an admin-only, infrequent operation.
- Changing the import's semantics — it is already atomic (one DB transaction, all-or-nothing,
  idempotent re-upload; spec 0001). "Atomically" here means the whole file in one request, which the
  single multipart POST already delivers.
- Ingress/reverse-proxy body limits (Caddy streams with no default cap; ACA ingress limits are large
  and configured in infra, not app code) — a deployment concern, noted in the runbook if it bites.

## Business rules & edge cases

- An upload at or under the ceiling is accepted and imported as before.
- An upload over the ceiling is rejected with `413 Payload Too Large` (a ProblemDetails the Frontend
  surfaces) — consistently, whether Kestrel's request-body cap (real 413) or the multipart
  form-length cap (an `InvalidDataException` that minimal-API binding wraps as a 400) trips first.
- The endpoint stays admin-only and rate-limited, which bounds the abuse surface of a high ceiling.
- No server-side request timeout is added: a full-catalog import legitimately runs for minutes.

## Contract

- **Trigger:** `POST /api/v1/game-versions/{id}/import` (multipart `file`), unchanged route.
- **Input:** `ImportExportedTexts.Command(GameVersionId, Stream, bool AllowMassRemoval)` — unchanged.
- **Output:** `ImportSummary` (200) — unchanged.
- **Errors:** existing 401/403/404/422; **new** `413 Payload Too Large` when the body exceeds the
  configured ceiling.
- **Config:** `ImportUploadLimits.MaxUploadBytes` (shared const, 256 MB) is the API default for
  `Import:MaxUploadBytes` and the Frontend's Kestrel + form ceiling.
- **Files touched:** none on disk beyond the framework's transient upload temp file.

## Acceptance criteria

- [x] An `exported.txt` well over the legacy 30 MB cap is accepted by the API import endpoint
      (request-body + multipart limits raised to the configured ceiling).
- [x] The Frontend host accepts and forwards a body over the legacy 30 MB cap (Kestrel + form limits
      raised to the same ceiling).
- [x] An upload exceeding the configured ceiling is rejected with `413` and persists no rows.
- [x] A multipart upload is granted a far wider client timeout than an ordinary JSON call, so a large
      upload + import is not aborted mid-flight.

## Open questions

- **How should "atomic large upload" work — single-request (raise limits) or chunked/resumable?**
  Resolved by derivation, not invention: the house YAGNI rule ("pick the simple path; don't add infra
  without a real present need") plus the context (zero users, a single admin, occasional uploads over
  a reliable link, an already-atomic import) point to single-request + raised limits. A chunked
  protocol is deferred (Out of scope). Flagged here so the user can veto before merge if they actually
  want resumable uploads.

## Assumptions

- The export stays a single plain-text `||` file uploaded whole (spec 0001 contract); it does not
  become a multi-part or compressed artifact.
- 256 MB (≈3× today's ~80 MB) is enough headroom for years; if not, it is a one-line config bump.
