# GridLookout — Commercial Licensing

GridLookout is free for personal, hobby, research, and other noncommercial use under
[PolyForm Noncommercial 1.0.0](LICENSE). **Any business, government, or otherwise commercial
use requires a commercial license** purchased from the author (IT42 d.o.o., Croatia).

A draft of the agreement you'll sign is available for review before you buy:
[GridLookout Commercial License Agreement (template)](docs/gridlookout-commercial-agreement-template.md).

## License metric: the wall-controller PC

One license covers **one Windows machine running GridLookout** — with **unlimited attached
monitors and unlimited cameras** on that machine. Monitors and camera counts never change the
price; only the number of PCs running the software does. A licensed PC can be replaced
(hardware refresh, reinstall) at no cost — the license moves with you, not with the box.

## Pricing (EUR, excl. VAT)

Every license is available two ways: **perpetual** (one-time, yours forever, first 12 months of
updates included) or **yearly subscription** (all updates and support included while active).

| License | Perpetual | Yearly subscription | What it covers |
|---|---|---|---|
| **Single Controller** | **€500** | **€180/yr** | One wall-controller PC |
| **5-Pack** | **€2,100** (€420/controller) | **€750/yr** (€150/controller) | Five wall-controller PCs, one end customer, any sites — plus one assisted deployment/configuration session |
| **10-Pack** | **€3,600** (€360/controller) | **€1,200/yr** (€120/controller) | Ten wall-controller PCs, one end customer, any sites — plus a named technical contact, a rollout review, and next-business-day operational response |

**Fleets beyond ten controllers** — one master agreement; the first ten at the 10-Pack price,
each further controller at marginal per-controller pricing:

| Controllers | Perpetual, per controller | Yearly, per controller |
|---|---|---|
| 11–25 | €330 | €110/yr |
| 26–50 | €285 | €95/yr |
| 51–100 | €225 | €75/yr |

Estates beyond **100 controllers** are priced under an individually negotiated master agreement —
contact us; the marginal tiers above are the published pricing up to the 100-controller mark.
Commissioning, validation, and SLA support beyond the terms above are available as separate line
items.

Maintenance for perpetual licenses (optional, after the included first year): **20% of the license
price per year (€150/yr minimum)** — continued updates, security and XProtect-compatibility
releases, and email support. A subscription always includes all of that while active, plus license
rehosting when a controller PC is replaced.

**Which one do you need?**

- **One to four walls → Single Controller licenses** (perpetual if the wall is a long-lived
  fixture, yearly if you prefer opex or expect the estate to change).
- **Five or more walls → 5-Pack / 10-Pack**, any mix of sites under one end customer.
- **Larger fleets** → contact us for the master agreement; the marginal tiers above are the
  published ceiling, not an opening position.

## Terms summary

- **Perpetual**: a purchased license never expires. The first 12 months of updates are
  included; support during that period is as stated in your quote. After the first 12 months,
  updates and support continue only with active maintenance — a lapsed customer keeps using the
  version they have, forever.
- **Yearly subscription**: licensed while the subscription is active, including all updates,
  security and compatibility releases, email support, and rehosting. A lapsed subscription means
  commercial use must stop (unlike a perpetual license) — there is still no activation server or
  phone-home involved (see the next bullet); the term is enforced by the agreement itself, so
  air-gapped sites remain fully supported on subscription too.
- **No telemetry, no activation server**: licensing is enforced by the commercial agreement
  itself — the software never phones home to check or report license status. Air-gapped sites
  are fully supported. (The integration does identify itself to the customer's own Milestone
  XProtect system it logs into, and that system's own telemetry channel — a setting entirely
  under the customer's control — may in turn report installed integrations to Milestone;
  see the Security & Network Behavior guide's "Network connections" for the full disclosure. This is separate
  from, and does not compromise, the no-activation-server licensing model above.)
- **A separately licensed Milestone XProtect system is required.** GridLookout does not
  include, replace, or modify any Milestone licensing. Redistribution of the bundled Milestone
  MIP SDK runtime is authorized by the MIP SDK license agreement; see the third-party notices
  file — `NOTICES.md` at the application's install root once installed, `docs/gridlookout-NOTICES.md`
  in this repo — for the authorization statement and all third-party licenses.
- Provided **as-is, without warranty**; see the license agreement issued with your invoice
  for the complete terms (the template above shows the terms that agreement is drawn from).
- **30-day commercial evaluation**: before buying, a prospective commercial customer may run
  GridLookout in a commercial environment for up to 30 days under the
  [PolyForm Free Trial License 1.0.0](LICENSE-TRIAL) — obtained directly from the licensor
  (contact address below), not redistributed by an integrator. The trial license permits evaluation use only and does not
  permit further distribution of the software. At the end of the evaluation window you must
  either purchase a commercial license or stop commercial use; continuing commercial use beyond
  the trial period without either is not licensed.

## How to buy

Email **info@it42.hr** with:

1. Number of wall-controller PCs and sites
2. Your company name and billing details
3. (Optional) your Milestone integrator, if one is deploying it for you — integrator
   partners receive reseller terms

You receive a quote, then an invoice and the signed license agreement. Integrators: ask
about the partner discount and deal registration.
