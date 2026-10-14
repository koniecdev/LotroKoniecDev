# ADR-0068: An account runs on the contract, not on consent

**Status:** Accepted
**Date:** 2026-10-12
**Decision-makers:** Solo maintainer (owner decision recorded in #1073, 2026-10-11)
**Related:** AuthSystem.API (`Features/Auth/RegisterUser.cs`, `Pages/Account/Register.cshtml`,
`Pages/Account/PrivacyPolicy.cshtml`), AuthSystem.Domain (`ApplicationUser`), Frontend
(`Components/Pages/Account/Account.razor`); ADR-0023 (expand → backfill → contract), ADR-0031
(two-phase account deletion); tickets #1073, #1037, #1100, #1130, #1134

## Context

Until #1073, registration asked for three ticks: "Akceptuję politykę prywatności", "Wyrażam zgodę na
przetwarzanie moich danych osobowych w celu prowadzenia konta" and the terms of service. The second
one was required, and `RegisterUser.CommandValidator` refused a registration without it. The handler
stored it as `ApplicationUser.DataProcessingConsentGiven` with a date.

The privacy policy did not agree with that form. §03 named only the contract (art. 6 ust. 1 lit. b
RODO) and the legitimate interest in a secure service (lit. f) as legal bases. §06 still said
"Prawo sprzeciwu — wycofasz zgodę na przetwarzanie w dowolnym momencie". That line mixed two
different rights, and the only way to "withdraw" was to delete the account.

GDPR art. 7(4) and the EDPB consent guidelines (05/2020) say a consent the service depends on is not
freely given, and processing the contract needs should rest on the contract, not on consent. A
consent the user must give to get the account is therefore not valid consent. The processing the
account needs is covered by the contract anyway, so the consent added nothing but a promise the site
could not keep.

## Decision

### 1. The account runs on the contract

Running the account and the translation work tied to it rests on art. 6 ust. 1 lit. b RODO (the
contract). Keeping the service secure rests on lit. f (legitimate interest). Registration asks for no
consent to process personal data. Accepting the terms of service stays, because the terms are the
contract.

### 2. No consent is ever a condition of the account

A consent the user must give to get the service is not valid consent. This adds no new rule for
processing the account does not need (the policy's own example is analytics cookies): GDPR art. 7(3)
and the promise already made in policy §05 ("najpierw poprosimy Cię o zgodę") cover it.

### 3. The right to object is about the security processing

§06 offers the objection of art. 21 RODO against the processing for the security of the service, by
e-mail to the address in §08. A user who wants no further processing deletes the account (ADR-0031).
The exact Polish wording is the owner's, in the #1073 ruling.

### 4. The stored consent is removed by expand → contract

`DataProcessingConsentGiven` and `DataProcessingConsentDate` stay in the database until a later
change removes them in at least two deploys (ADR-0023, #1130). Until then:

- registration does not write them, so new accounts keep the column defaults (`false`, `null`);
- the account data export still carries them, because older accounts hold a real value and the
  export is their copy of what we store;
- the erasure still clears them.

## Consequences

### Positive

- The registration form, the privacy policy and the code say the same thing about consent.
- One required tick less at registration.
- A user is never told they can withdraw a consent whose withdrawal only deletes the account.

### Negative / Accepted Trade-offs

- Until #1130 lands, the export shows `dataProcessingConsentGiven: false` for every new account and
  the old value for older accounts. The field means nothing now.
- `RegisterRequest` lost its `AcceptedDataProcessingConsent` field. That breaks no real client: the
  frontend never calls `auth/register`, there are no real users yet, and the API ignores an unknown
  JSON property, so an old client that still sends it registers as before.

## Alternatives Considered

### A. Keep the consent as an extra legal basis

Rejected. It is not freely given and it is the wrong basis (see Context), and its withdrawal can
only end the account, which is not what withdrawing consent means.

### B. Keep the checkbox but make it optional

Rejected. Nothing would read it: the processing the account needs does not depend on it.

### C. Drop the columns in the same change

Rejected. The running app revision still writes the column during a deploy, and ADR-0023 makes a
destructive schema step wait for a later deploy.

## Implementation Notes

- `src/AuthSystem/LotroKoniecDev.AuthSystem.Contracts/Features/Auth/Register/RegisterRequest.cs`,
  `src/AuthSystem/LotroKoniecDev.AuthSystem.API/Features/Auth/RegisterUser.cs`: the field, the
  validator rule and the stamping are gone.
- `src/AuthSystem/LotroKoniecDev.AuthSystem.API/Pages/Account/Register.cshtml(.cs)`: the checkbox and
  its check are gone.
- `src/AuthSystem/LotroKoniecDev.AuthSystem.API/Extensions/DatabaseSeederExtensions.cs`: the seeded
  admin gets no consent.
- `src/AuthSystem/LotroKoniecDev.AuthSystem.API/Pages/Account/PrivacyPolicy.cshtml`: §02, §04 and §06
  carry the owner's wording from #1073.
- `src/Frontend/LotroKoniecDev.Frontend/Components/Pages/Account/Account.razor`: no "Przetwarzanie
  danych" row, so the page never presents this consent as one the user has or lacks. This was chosen
  in the #1073 PR, not ruled by the owner; #1134 asks the owner about the rest of that panel.
- Unchanged until #1130: `ApplicationUser`, `ApplicationUserConfiguration`, `AccountDataExportReader`,
  `AuthDataExportDto`, `AccountErasureService`.

## References

- Tickets: #1073 (this decision, owner comment of 2026-10-11), #1037 (the policy check that found
  the cases), #1100 (users change their own username, which makes §06 "Prawo do sprostowania" true),
  #1130 (dropping the columns), #1134 (the wording questions this ruling left open: the §04 intro,
  the privacy-policy acceptance, the "Zgody" panel).
- ADRs: ADR-0023, ADR-0031.
- GDPR (Regulation (EU) 2016/679): art. 6(1)(b) and (f), art. 7(4), art. 21.
- EDPB Guidelines 05/2020 on consent under Regulation 2016/679 (conditionality of consent).
