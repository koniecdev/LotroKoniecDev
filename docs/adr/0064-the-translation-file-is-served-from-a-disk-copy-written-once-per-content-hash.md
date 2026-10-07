# ADR-0064: The Translation File Is Served from a Disk Copy, Written Once per Content Hash

**Status:** Accepted
**Date:** 2026-10-07
**Decision-makers:** Solo maintainer (ticket #715)
**Related:** TranslationSystem.API (`Features/TranslationFiles/GetTranslationFile.cs`,
`TranslationFileDiskCache.cs`, `TranslationFileDiskCacheSettings.cs`); ADR-0007 (read projections),
ADR-0021 (the debounced rebuild and its single-instance assumption), ADR-0041 (no gateway);
spec 0001; tickets #286 (PERF-01), #391 (AUDIT-SEC-01), #708, #715 (PERF-09), #716 (PERF-10)

## Context

When a player's copy of the translation file is out of date, the API sends the whole file again. Until
now it read the whole file from the database for every such player. At full size the file is about
82 MB, and one read briefly took about three times that in memory. On the day of a game update every
player's copy goes out of date at the same moment, so a few dozen players could take gigabytes at
once, on a box with 3.7 GiB that also runs TheKittySaver.

How it worked: `GetTranslationFile` answers a matching `If-None-Match` with a 304, and that path reads
only the hash column (PERF-01, #286). That part is cheap and stays. Every other request ran a second
query that loaded the `Content` column into a `string`. Npgsql buffers the column's ~82 MB of UTF-8
(the patcher's own estimate, `TranslationFileDownloader.MaxResponseContentBytes`), and .NET then holds
it as UTF-16, so one request held roughly 250 MB until it ended.

The rate limiter does not help. `fixed-by-ip` counts requests per address, and the update-day crowd is
many addresses with one request each.

The ticket named three tools: a concurrency limiter with a queue, a copy of the file on the box, and a
Caddy response cache keyed by the ETag. It also asked to size any limit from a measurement (#716), not
from a guess.

## Decision

### 1. A full download streams a copy on the API's own disk

`TranslationFileDiskCache` keeps the file as `<directory>/pl-<HASH>.txt`, one file per content hash.
The endpoint opens it and returns it with `Results.Stream`, so a download reads the file in small
pieces and never holds the whole body. When the copy is missing, it is written once from the
database. Nothing reads the `Content` column again until the hash changes.

### 2. One write at a time, and the others wait for it

A gate lets one write run. A request that finds no copy waits at the gate. Inside it, the cache:

1. looks for the copy again, because the request before it may have just written it;
2. reads the current hash, which is cheap, and uses that copy if it exists, because a rebuild may
   have replaced the file after the request read its hash;
3. only then loads the content, writes it to a temporary file, flushes it to disk, hashes the bytes on
   disk, closes the file and renames it to its final name;
4. opens the copy again, read-only, for the response. On Windows a reader must share write access with
   any open handle that can write, so a response served from the writer's handle would lock every
   other download out of the file.

The write runs on the host's lifetime, not on the caller's token. The CLI gives up after 10 seconds
(`InfrastructureDependencyInjection.CreateHttpClient`), and waking a suspended Neon database alone can
take about 30. A write tied to its caller would be thrown away, and every waiter after it would load
the whole file again. The token is `ApplicationStopped`, not `ApplicationStopping`: a deploy first
lets running requests finish, and a write cut at the start of that drain would turn each waiting
download into a 500.

### 3. A file name is a promise

A copy gets its final name only after the SHA-256 of the bytes on disk equals the stored hash. So a
file with that name always holds exactly the bytes the ETag promises. The comparison ignores case, as
the patcher's does. If the stored hash does not match the stored content, or is not a hex SHA-256 at
all, the request fails with a 500 and an error log, instead of serving a file the patcher would refuse.

The contract with the patcher does not change: the ETag is still the strong, hex SHA-256 of the UTF-8
body with no BOM (AUDIT-SEC-01, #391). The `Content-Type` is still `text/plain; charset=utf-8`.

### 4. The ETag names the copy that is sent

If a rebuild lands between the hash lookup and the copy, the cache hands out the newer copy, and the
endpoint tags the response with the newer hash. Body and tag always match.

### 5. Each process owns a private folder, and old copies are removed

By default each API process makes its own folder in the temp folder with
`Directory.CreateTempSubdirectory`, on its first write, and deletes it when the host stops. The name
is random and, on Linux and macOS, only the owner can enter the folder. A fixed name in a shared
`/tmp` would let another local user create the folder first and place a file there, or a link to the
API's own secrets, under the public ETag, and the API would serve it to anyone. It also keeps two API
processes on one machine (the main checkout and a worktree, for example) from deleting each other's
files. In a container the folder lives in the container's own writable layer. It is not a volume on
purpose: the copy can always be written again, and the database stays the only source.

`TranslationFileDiskCache:Directory` can name a folder instead. It is used as it is, so it must belong
to one API process alone. The integration tests set it so they can look inside.

After each write, the cache deletes every other file for that language in its folder, including a
temporary file a failed write left behind. A download still reading an old copy keeps it until it
ends: the files are opened with `FileShare.Delete`, and an open handle outlives its name. This is the
same single-instance assumption the rebuild already makes (ADR-0021 §5).

### 6. No separate concurrency limiter, and no guessed number

The ticket wanted a limiter so that the next caller waits "instead of everyone allocating at once".
The gate in §2 does that for the only large allocation left: one load from the database per content
hash, while everyone else waits. After the copy exists, a download allocates nothing that grows with
the file, so there is no per-request memory left to size a limit by.

A cap on the number of downloads running at once could still matter for bandwidth. That is a
different question, and the load test in #716 answers it. This ADR does not pick a number for it.

Two costs per download remain, and neither is memory that grows with the file. Response compression
is on for `text/plain`, so a client that sends `Accept-Encoding` (a browser) gets the file compressed
on the fly, which costs CPU per download. The CLI sends no `Accept-Encoding`, so the update-day crowd
does not pay it. And the rebuild and the first load after it use separate gates, so the worst peak is
one rebuild plus one load, about 500 MB at full size. That is bounded and does not grow with the crowd.

## Rejected alternatives

- **A Caddy response cache keyed by the ETag.** The stock Caddy image has no HTTP cache. It would need
  a custom build with a third-party module, on the one ingress the box shares with TheKittySaver. It
  would also put a component between the client and the API, which ADR-0041 §1 rules out, and its key
  would be the ETag, which is the patcher's integrity hash and so an application contract, not a
  transport detail. The disk copy gives the same result inside the API, with no amendment.
- **One shared copy in memory.** Simple, but it keeps about 82 MB on the managed heap for as long as
  the process lives, on a box with no memory to spare. The kernel also keeps the disk copy in its page
  cache, but it can drop that cache under pressure.
- **The rebuild worker writes the file.** A new container starts with an empty disk, so the endpoint
  would still need its own way to write the copy, and the same file would have two writers. The write
  on first use covers rebuilds, restarts and deploys alike. Writing the copy at rebuild time stays
  open as a small later step if the first download after a rebuild turns out to be slow.
- **ASP.NET's `ConcurrencyLimiter` with a queue.** It needs a number nobody has measured yet (#716). An
  endpoint's own rate limit policy also replaces its group's, so it would silently drop `fixed-by-ip`
  for this endpoint unless both were chained into one policy.
- **Streaming the column out of Npgsql** (`CommandBehavior.SequentialAccess` with `GetStream`). That
  would make even the one load per hash small, but it needs raw ADO.NET on the read side with table
  and column names written by hand. The one load is bounded, runs alone, and costs less than the
  rebuild that ran in the same process just before it.

## Consequences

### Good

- Memory for a full download no longer grows with the file, so a crowd of players costs about the
  same as one.
- While the disk accepts the write, the database sends the content once per content hash per
  process, not once per player.
- The 304 path is unchanged: it still reads only the hash.
- The patcher's integrity check and the CLI's decoding see exactly the same bytes and headers.

### Bad, and accepted

- The first full download after a rebuild or a restart waits for the write: one load, one write and
  one hash of the whole file. Requests behind it wait too. A CLI that gives up uses its local file, as
  it already does when the server is slow (spec 0001).
- The container keeps one copy on disk, about 82 MB at full size, and briefly a second one during a
  write. If the disk is full, the write fails and full downloads answer 500 until there is space; the
  304 path keeps working. Before this change a full disk did not stop downloads.
- Nothing remembers a failed write. While writes keep failing, every full download loads the content
  from the database again, one at a time behind the gate, and then answers 500. Memory stays bounded,
  and the database does the same work per download as before this change. A waiting client that gives
  up leaves the queue. A memory of recent failures was left out: it is one more piece of state for a
  fault that needs an operator anyway.
- A process that crashes leaves its private folder behind. In a container it is gone with the next
  deploy, which creates a new container.
- The one load per hash still holds the whole content in memory once, about 250 MB at full size. It is
  bounded and never runs twice at the same time.
- A second API process in the same container would need its own directory.

### Follow-up

- #716 measures the update-day crowd on the local parity stack. It should run once more on top of this
  change, and it decides whether downloads also need a cap for bandwidth.
