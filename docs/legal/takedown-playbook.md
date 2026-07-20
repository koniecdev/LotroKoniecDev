# Takedown / C&D response playbook

> **Internal operational document** (spec 0011, LEGAL-13). It exists so that a takedown
> request is handled calmly and fast, not improvised under stress. The 48-hour
> acknowledgment target below is an **internal operational target we hold ourselves to —
> not a public commitment or SLA**; every public-facing text (ToS, website, replies) says
> only "promptly" / "niezwłocznie". **Not legal advice** — for anything beyond
> straightforward compliance (an actual lawsuit threat, a demand for damages, a request
> that looks abusive), consult a Polish IP lawyer before replying beyond the acknowledgment.

## Principles

1. **Acknowledge within 48 hours** of the notice arriving, even if the compliance action
   itself needs longer. Use template A below.
2. **Comply first, argue never.** The project operates on tolerated, not licensed, ground
   (spec 0011). A justified rightsholder request is executed, not debated; we do not file
   DMCA counter-notices and we do not negotiate scope — if the request is broader than
   expected, we comply with what is asked and may ask *afterwards* whether a reduced form
   is acceptable.
3. **Keep records of all correspondence** — see [Record keeping](#record-keeping). Never
   store correspondence in this public repository.
4. **One voice.** Only the Operator (Artur Koniec, koniecdev@gmail.com — the contact
   published in the ToS and `THIRD-PARTY-NOTICES.md`) responds. Replies are short, polite,
   factual, in English (or Polish if the notice is in Polish).

## Where a notice can arrive

| Channel | What it looks like | Notes |
|---|---|---|
| E-mail to koniecdev@gmail.com | direct C&D / removal request | the contact published in ToS §IP clause and `THIRD-PARTY-NOTICES.md` |
| GitHub DMCA process | notice forwarded by GitHub, or content pre-emptively disabled | GitHub normally gives the repo owner ~1 business day to remove the named content before disabling the whole repository — act inside that window |
| Domain registrar / .pl registry | complaint routed via DOMENY.TV or NASK | see [Domain contacts](#domain-contacts--lotro-translatorpl) |
| Hosting provider | abuse report routed by Hetzner | prod + staging VPS boxes |

## The flow

1. **Record** the notice immediately (save the original message, note the arrival date).
2. **Acknowledge within 48 h** — template A. Do not promise specific actions yet if the
   scope is unclear; "reviewing and will follow up shortly" is enough.
3. **Classify** the request against the table below and execute the immediate action.
   When in doubt about scope, take the *broader* action first (it can be undone later);
   never leave the named content up while deliberating.
4. **Verify** the content is actually unreachable (each row lists a check).
5. **Confirm compliance** — template B, listing concretely what was done and when.
6. **Record the outcome** and any follow-up obligations (e.g. "do not re-publish X").

## Compliance actions by request type

> Box addresses, SSH access and stack layout: `docs/deployment/runbook.md`. Every action
> that touches the deployed stack must be executed on **both boxes** (prod *and* staging —
> staging serves the same endpoints publicly on `*.staging.lotro-translator.pl`).

### 1. The distributed translation file / distribution endpoint

Request names the downloadable `polish.txt` artifact or its endpoint
(`GET https://tms.lotro-translator.pl/api/v1/translation-files/polish`).

- **Immediate (minutes):** SSH to the box, stop the TMS API:
  `cd /opt/lotro && docker compose -f compose.hetzner.yaml stop tms-api`.
  This takes the whole TMS API offline (the site degrades but stays up) — acceptable for
  the hours until the surgical fix ships.
- **Surgical follow-up (same day):** remove or 410-gate the endpoint in
  `src/TranslationSystem/LotroKoniecDev.TranslationSystem.API/Features/TranslationFiles/GetTranslationFile.cs`,
  ship through the normal CD, then `docker compose -f compose.hetzner.yaml start tms-api`.
  Deleting the `PrecomputedTranslationFiles` row alone is **not** sufficient — the
  projector regenerates the artifact after the next approve/write.
- **Verify:** `curl -s -o /dev/null -w "%{http_code}" https://tms.lotro-translator.pl/api/v1/translation-files/polish`
  returns a non-2xx code; repeat for staging.

### 2. A named repository asset

Request names a specific file in the public repo — the realistic target is
`datexport.dll` (+ its runtime DLLs), per `THIRD-PARTY-NOTICES.md` our standing
commitment is removal promptly on request.

- **Immediate:** delete the named file(s) at HEAD on a branch, open a PR, squash-merge it
  without waiting for the normal review cadence (compliance overrides process). Removing
  `datexport.dll` does **not** break the build (it is a `None Update` item + P/Invoke,
  resolved at runtime) — the patcher's `export`/`patch` simply stop working on Windows
  boxes, which is the point of complying.
- **History:** HEAD-only removal is the default (spec 0011 Q1/Q4). If the notice
  explicitly demands erasure from history, use `git filter-repo`, force-push, and contact
  GitHub Support to purge cached views and forks — GitHub's DMCA page documents this path.
- **Verify:** the file 404s on `https://github.com/koniecdev/LotroKoniecDev` at HEAD;
  releases/artifacts that bundled it are deleted or re-cut.

### 3. The repository as a whole

- **Immediate:** `gh repo edit koniecdev/LotroKoniecDev --visibility private --accept-visibility-change-consequences`.
  This detaches forks' network references and invalidates existing clones' pull access;
  it is reversible if a reduced-scope agreement is reached later.
- **Verify:** the repo URL returns 404 in a logged-out browser.

### 4. The website / platform

- **Immediate:** SSH to the box, `cd /opt/lotro && docker compose -f compose.hetzner.yaml down`
  (**never `-v`** — that would destroy the DB/keyring volumes; compliance means
  unreachable, not data destruction). Repeat on staging.
- **Verify:** `https://lotro-translator.pl`, `https://tms.lotro-translator.pl` and
  `https://auth.lotro-translator.pl` all fail to connect (Caddy is part of the stack, so
  `down` kills TLS termination too).

### 5. The domain itself

A demand to stop using the `lotro-translator.pl` name cannot be executed with a shell
command: acknowledge, take the site down (action 4) so the domain serves nothing, and
handle the transfer/lapse through the registrar (below). Do not renew if the demand
stands (renewal date is in the registry WHOIS).

## Reply templates

### Template A — acknowledgment (within 48 h)

```text
Subject: Re: <original subject> — acknowledgment of receipt

Dear <name / Sir or Madam>,

Thank you for your notice dated <date> regarding <the content/asset named>.
I confirm receipt and am reviewing it now.

lotro-translator.pl is a non-commercial fan project, and it is my intention to
cooperate fully and promptly with legitimate rightsholder requests. I will follow
up shortly with the concrete actions taken.

Kind regards,
Artur Koniec
Operator, lotro-translator.pl
koniecdev@gmail.com
```

### Template B — compliance confirmation

```text
Subject: Re: <original subject> — compliance confirmation

Dear <name / Sir or Madam>,

Following your notice dated <date>, the following actions were completed as of
<date, time, timezone>:

- <e.g. distribution of the translation file at
  https://tms.lotro-translator.pl/api/v1/translation-files/polish has been disabled>
- <e.g. the file <path> has been removed from the repository>

Please let me know if any further action is required; any remaining request will
be handled promptly.

Kind regards,
Artur Koniec
Operator, lotro-translator.pl
koniecdev@gmail.com
```

## Reference links

- **GitHub DMCA takedown policy** (process, the ~1-business-day owner window, history/cache
  purge path): <https://docs.github.com/en/site-policy/content-removal-policies/dmca-takedown-policy>
- **Hetzner abuse desk** (how a hosting-level complaint reaches us): <https://www.hetzner.com/legal/report-abuse/>
- Deployment/operations detail (boxes, SSH, stack): `docs/deployment/runbook.md`

## Domain contacts — lotro-translator.pl

Verified against the .pl registry WHOIS (`whois -h whois.dns.pl lotro-translator.pl`),
2026-07-14 — re-check before relying on it, the registrar can change:

- **Registrar:** DOMENY.TV (MSERWIS Sp. z o.o.), ul. Stacyjna 1/63, 53-613 Wrocław —
  panel <https://www.domeny.tv/>, info@domeny.tv, +48 71 718 13 10. Domain-level actions
  (transfer, lapse, contact updates) go through the registrar panel.
- **Registry:** NASK (dns.pl) — .pl WHOIS and dispute information at
  <https://dns.pl/en/whois>. Formal .pl domain disputes go to arbitration courts NASK
  lists; if it ever gets that far, that is lawyer territory, not a playbook step.
- **Nameservers:** `ns1.webio.pl` / `ns2.webio.pl` — DNS record changes go through the
  panel described in the runbook ("DNS first" section).

## Record keeping

- Keep the full thread (original notice, our replies, delivery timestamps) in the mail
  archive **and** mirrored to a local, non-public folder — `intel/legal/<YYYY-MM-DD>-<sender>/`
  (`intel/` is gitignored). Nothing about an ongoing legal contact is ever committed to
  this public repository beyond neutral code/content changes.
- For every executed action, record: what, when (timestamp + timezone), by which command,
  and the verification output (the `curl` code, the 404 screenshot).
- If content was removed on request, note it as a standing obligation: it must not be
  re-added later by a routine refactor or an autonomous loop session.
