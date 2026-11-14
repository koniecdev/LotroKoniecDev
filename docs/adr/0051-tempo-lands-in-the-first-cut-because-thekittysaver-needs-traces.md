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

| Component | ADR-0050 predicted | Measured peak | Ceiling now |
|---|---|---|---|
| **Alloy** | ~120 MiB | **285 MiB** | **512 MiB** |
| Grafana | ~120 MiB | **215 MiB** | 384 MiB |
| Prometheus | ~250 MiB | 102 MiB | 256 MiB |
| Loki | ~200 MiB | 182 MiB | 192 MiB |
| Tempo | — | 95 MiB | 192 MiB |
| **Total** | ~690 MiB predicted | **~660 MiB steady** | **1536 MiB** |

Two of the five predictions were wrong in the dangerous direction. **Grafana** was underestimated by
80% and would have been OOM-killed against its proposed 256 MiB ceiling. **Alloy** was worse: it was
found sitting at **253 MiB against a 256 MiB ceiling — 99%** — and would have been killed within the
hour, which for an agent means telemetry stops *silently*, the one failure mode an observability
component must not have. Its cadvisor exporter keeps an in-memory ring of per-container stats;
cutting `storage_duration` from 5m to 2m brought it to 169 MiB.

Prometheus, meanwhile, was overestimated by two and a half times. And Tempo — the component
alternative F called the heaviest — is among the *smallest* things in the stack at this volume.

**Alloy is the heaviest component here, not the lightest**, and the ADR predicted the opposite. It
carries the cadvisor exporter across every container on the box *and* the whole OTLP pipeline for
both projects; it went from 144 MiB to 285 MiB the moment the second project started exporting
(#712). It therefore ends up with the largest ceiling and the most headroom, which is also the right
place to spend it: Alloy is the one component whose death stops **all** telemetry, and stops it
silently. Loki and Tempo give the room back — both sit near 90 MiB and neither grows with retention
the way Prometheus does.

The total ceiling is **1536 MiB**, exactly ADR-0050 §5's budget, now covering a Tempo that budget
never included. The correction paid for the addition.

The box has 3.7 GiB with 1.7 GiB available alongside both application stacks, plus the 2 GiB of swap
#708 added.

> **Amendment (2026-09-09, #782): the measurement above is an INGEST measurement, and two ceilings
> derived from it were wrong.** Every number in the table was taken while the stack was receiving
> telemetry and nobody was reading it. Reading costs memory too, in the two components a person
> actually touches. Tempo was **OOM-killed** serving a single Grafana user browsing traces — it went
> from the 95 MiB steady above to `anon-rss:184236kB` against its 192 MiB ceiling — and Grafana was
> found at **86%** of its own in the same session. The kill was silent: the box still had 1.5 GiB
> free, so no fleet rule fired, and Docker restarted the container in place before anything was down
> long enough to notice.
>
> | Component | Ceiling in the table above | Ceiling now | Why |
> |---|---|---|---|
> | Tempo | 192 MiB | **384 MiB** | ingest is 95 MiB; serving a search decompresses blocks on top of it |
> | Grafana | 384 MiB | **512 MiB** | peak follows whoever is looking at it, not the size of the fleet |
> | **Total** | 1536 MiB | **1856 MiB** | still inside the box: worst case 3280 MiB of 3814 MiB with both app stacks at their current usage |
>
> A ceiling alone was not the fix. Tempo's query path also kept Tempo's cluster defaults — 1000
> concurrent search jobs of up to 100 MiB each — so any ceiling was reachable by one query;
> `tempo.yaml` now caps the fan-out, and the cap and the limit ship together.
>
> **What made it silent is now a rule.** A cgroup OOM-kill is invisible to every existing fleet
> rule, and the two obvious metrics do not work: `container_oom_events_total` and
> `container_memory_failcnt` both stayed at **0** across this kill, because the counter lives in the
> cgroup and the restart makes a new one. `container_memory_working_set_bytes` over
> `container_spec_memory_limit_bytes` peaked at **0.9995** and was the only signal that saw it, so
> *Fleet — a container is close to its memory limit* watches that ratio at 85%.
>
> **The uncapped half is still uncapped.** The seven application containers on the staging box run
> with no `mem_limit` at all, so this rule cannot see them and no cgroup bounds them. That is a
> separate decision and a separate ticket — the ceilings here protect the box **from** the
> observability stack, and were never a bound on the applications.

> **Amendment (2026-11-14, #779): a week on both boxes corrects Alloy's row, and after a reboot
> Alloy's working set comes close to its ceiling.** The 285 MiB for Alloy in the table above is a
> first-run `docker stats` reading on one box (2026-08-29, see References). This is a week on both
> boxes, 2026-11-07 to 2026-11-14, read from the staging Prometheus on 2026-11-14. Each box has
> 20,158 of the 20,160 samples a 30 s scrape gives. The prod box was not touched: its agent sends its
> own series to staging, with `box="lotro-prod"`.
>
> | `obs-alloy` | staging (backend + agent) | prod (agent only) |
> |---|---|---|
> | Limit | 512 MiB | 512 MiB |
> | Median working set | 328.2 MiB | 332.5 MiB |
> | p99 working set | 421.7 MiB | 479.8 MiB |
> | **Peak working set** | **492.3 MiB (96.1%)** | **495.3 MiB (96.7%)** |
> | Peak RSS | 291.1 MiB | 260.0 MiB |
> | Peak swap | 72.2 MiB | 16.7 MiB |
> | Time above 85% of the limit | 8.5 min | 367 min |
>
> The peak comes from
> `max by (box) (max_over_time(container_memory_working_set_bytes{name="obs-alloy"}[7d]))`. #779
> holds the other queries.
>
> - **The agent-only box is not lighter.** Its working set matches the backend box's (median
>   332.5 MiB against 328.2 MiB, peak 495.3 MiB against 492.3 MiB). Only its peak RSS is lower.
> - **Alloy's own headroom:** at the median Alloy uses 64 to 65% of its limit. At the week's peak it
>   was 19.7 MiB (staging) and 16.7 MiB (prod) under the limit.
> - **Working set and RSS are different numbers.** The working set is memory use minus inactive file
>   cache. It is what `docker stats`, the 285 MiB above and the 85% rule all read. RSS is the
>   process's own memory in RAM, without file cache and without pages in swap. The limit bounds RAM,
>   not RAM plus swap, because the compose file sets no `memswap_limit`. At the limit the kernel drops
>   file cache and swaps out the process's memory, and it kills only when that is not enough.
> - **No kernel OOM kill was counted on either box.** The host counter `node_vmstat_oom_kill` did not
>   rise in the 15 days Prometheus keeps. Alloy's CPU counter reset only at the two host reboots per
>   box. The host counter starts at 0 on every boot, so a kill in the seconds before a reboot would be
>   lost. And `resets()` cannot tell apart two restarts closer than one scrape.
> - **Both peaks came minutes after a host reboot**, the manual reboots of 2026-11-11 (#1327). In the
>   data kept from before them, no 6-hour maximum on either box went above 374 MiB. After one reboot
>   of four so far (prod, 2026-11-13, at the configured reboot time), the working set stayed near the
>   limit for two 6-hour blocks in a row. The cause of the climb after a reboot was not looked for.
> - **This qualifies the #782 amendment above.** Its "the only signal that saw it" is true of the
>   container-level signals looked at then: `container_oom_events_total` and
>   `container_memory_failcnt`. The host counter `node_vmstat_oom_kill` was not looked at then. It
>   also counts a kill inside a container limit (the kernel's `__oom_kill_process` counts every OOM
>   kill), and Alloy already collects it from the host's `/proc`. Whether it moved on 2026-09-09
>   cannot be checked, because that day is past retention.
>
> The ceiling stays 512 MiB, and nothing is decided here. Not measured: the week of 2026-09-09 that
> #779 first asked for (past retention), the cause of the climb after a reboot, whether the 85% rule
> fired, and the other four components.

## Decision

**Tempo runs in the first cut**, pinned to the 2.x line, single binary, local blocks, 14-day
retention matching Loki's so a log line and its trace expire together.

Every component keeps a hard `mem_limit`, re-derived from the measurement above rather than from the
prediction. The total ceiling was **1536 MiB** — ADR-0050 §5's budget unchanged, now covering a fifth component
it never included — and is **1856 MiB** since the #782 amendment, which corrected the two ceilings that
were derived from an ingest-only measurement.

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
