# ADR-0051: Tempo lands in the first cut after all, because TheKittySaver's epic is built on traces

**Status:** Accepted
**Date:** 2026-08-29
**Decision-makers:** Solo maintainer
**Amends:** [ADR-0050](0050-the-observability-backend-runs-on-the-staging-box.md) §6 and alternative F
**Related:** #707 (OBS-00), #710, #711, koniecdev/TheKittySaver#508 / #510 / #511

## Context

ADR-0050 §6 deferred Tempo and the OTLP leg to a second cut. The reasoning was sound **for this
repo**: all four alert rules the Azure migration deleted are reachable from container logs and host
metrics alone, Tempo is the heaviest component, and with no users traces buy the least of anything
in the epic. Alternative F rejected it on the memory budget — including it put the
all-ceilings-at-once total at roughly the measured headroom.

Two things changed between writing that and building it.

**1. The sister epic is not optional about traces.** koniecdev/TheKittySaver#508 names them in its
acceptance criteria, #510 is titled around the OTLP leg, and its criterion is a request producing a
trace correlated to its log line. ADR-0050 anticipated rewriting #712's criteria to logs plus
metrics — that is this repo's call to make. It is not TheKittySaver's, and deferring here would have
left that epic unable to close on a decision taken in a repo it does not own.

**2. The memory prediction was wrong, and in both directions.** §5 said its expected column was a
prediction to be corrected by measurement rather than trusted. Measured on `lotro-staging` on
2026-08-29, first run, all five components up:

| Component | ADR-0050 predicted | Measured RSS | Ceiling now |
|---|---|---|---|
| Grafana | ~120 MiB | **215 MiB** | 384 MiB |
| Loki | ~200 MiB | 182 MiB | 384 MiB |
| Prometheus | ~250 MiB | 99 MiB | 320 MiB |
| Alloy | ~120 MiB | 144 MiB | 256 MiB |
| Tempo | — | **18 MiB** | 256 MiB |
| **Total** | ~690 MiB predicted | **658 MiB measured** | **1600 MiB** |

Grafana was underestimated by 80% and would have been OOM-killed against its proposed 256 MiB
ceiling. Prometheus was overestimated by two and a half times. And Tempo — the component alternative
F called the heaviest — is the **smallest thing in the stack** at this volume: a trickle of spans
from six containers keeps it near idle.

The box has 3.7 GiB with 1.7 GiB available alongside both application stacks, plus the 2 GiB of swap
#708 added.

## Decision

**Tempo runs in the first cut**, pinned to the 2.x line, single binary, local blocks, 14-day
retention matching Loki's so a log line and its trace expire together.

Every component keeps a hard `mem_limit`, re-derived from the measurement above rather than from the
prediction. The total ceiling is **1600 MiB**, which is lower than ADR-0050 §5's 1536 MiB budget plus
a Tempo the ADR never budgeted — the correction paid for the addition.

The rest of ADR-0050 stands unchanged and is explicitly **not** reopened: the backend still runs on
staging (§1), still ships as its own compose project (§2), still keeps Caddy as the only component on
more than one network (§3), and logs still have exactly one path (§7).

### What §7 turns into, now that the OTLP leg is real

ADR-0050 §7 said the OTLP leg could not land until each app's Serilog OTLP log sink had a switch
separate from its trace/metric exporter, because the two share one `if`. That is still true of the
code — and it is now **also enforced at the agent**, which is the stronger place for it.

Alloy accepts OTLP log records and drops them. An app that turns its own log sink on — deliberately,
or because its build predates the separate switch — gets a clean 200 and its records go nowhere,
instead of every line landing in Loki twice next to the stdout copy. Accepting-then-dropping rather
than refusing is the point: a refusal leaves the app retrying an export forever and writing that
failure into the logs it can still ship.

This makes the code change a tidiness fix rather than a prerequisite. TheKittySaver has since made it
anyway (its #509 added `OTEL_LOGS_EXPORTER`); this repo's three `Program.cs` files still share the
one `if`, and #712 can take it at leisure.

## Consequences

### Positive

- TheKittySaver#508 can close on its own acceptance criteria instead of on a rewrite of them.
- Log-to-trace correlation works today for every service that logs compact JSON, through one derived
  field on the Loki datasource — `@tr` is already in the line, as ADR-0050 §Context noted.
- The memory table in ADR-0050 §5 is replaced by a measurement, and the component that was closest to
  its ceiling was found before it was OOM-killed rather than after.
- The one-path-for-logs invariant no longer depends on every app being configured correctly.

### Negative / Accepted Trade-offs

- **A fifth component to run, patch and reason about.** Accepted: it is the smallest one.
- **Tempo is pinned to 2.x.** Tempo 3.0 replaced the single-binary ingester/compactor configuration
  with a backend-scheduler and block-builder architecture — `ingester` and `compactor` are no longer
  top-level fields and the process refuses to parse a config containing them. Moving to 3.x is a
  rework this fleet has no reason to take on for a trickle of spans, so it is deferred with eyes open,
  and the 2.x line will eventually stop receiving fixes.
- **Traces are 14 days and die with the staging box**, exactly like the logs. Unchanged from
  ADR-0050.

## Alternatives Considered

### A. Hold the line on ADR-0050 §6 and rewrite TheKittySaver#508's criteria

The disciplined-looking option. **Rejected.** It resolves a conflict between a decision and a
requirement by editing the requirement, in a repo that did not take the decision — and it would have
been done overnight with no owner awake to agree to it. The measurement removed the constraint that
motivated §6 anyway, so the deferral would have been kept for its own sake.

### B. Tempo with the OTLP metrics leg but no trace storage

Nonsense on inspection: the metrics were already available from cheaper sources, and traces were the
only thing the leg was actually needed for.

### C. Raise the ceilings instead of re-deriving them

Would have hidden the Grafana underestimate rather than found it. The ceilings exist so that
monitoring can never be what takes the box down; a ceiling set by guesswork and then raised on
contact is not that.

## References

- ADR-0050 — the decision this amends; everything except §6 and alternative F stands
- koniecdev/TheKittySaver#508 / #510 / #511 — the sister epic whose criteria forced the question
- `compose.observability.yaml` — the ceilings this ADR sets
- Measurement: `docker stats` on `lotro-staging`, 2026-08-29, all five components up
