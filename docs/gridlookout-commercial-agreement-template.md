# GridLookout Commercial License Agreement — TEMPLATE

> **DRAFT — FOR REVIEW BY OWNER/LEGAL COUNSEL BEFORE FIRST USE.**
> This is a template for internal drafting and buyer preview only. It has not been reviewed by a
> lawyer. Do not send it as a binding instrument, do not attach it to an invoice, and do not
> treat it as enforceable until counsel has reviewed and, where marked, completed it. Every
> `[BRACKETED]` field is a placeholder to fill in per deal; every **[COUNSEL: ...]** note flags a
> clause the owner or counsel must confirm, adjust, or strike before this is used with a real
> customer.

---

## GridLookout Commercial License Agreement

This Commercial License Agreement ("**Agreement**") is entered into as of **[EFFECTIVE DATE]**
("**Effective Date**") by and between:

**Licensor**: IT42 d.o.o., a company organized under the laws of the Republic of Croatia,
registered address **[IT42 D.O.O. REGISTERED ADDRESS]**, company registration number (MBS)
**[MBS NUMBER]**, VAT/OIB **[OIB NUMBER]** ("**IT42**" or "**Licensor**").

**Licensee**: **[CUSTOMER LEGAL NAME]**, a **[CUSTOMER ENTITY TYPE]** organized under the laws
of **[CUSTOMER JURISDICTION]**, registered address **[CUSTOMER ADDRESS]**, company registration
number **[CUSTOMER REG. NUMBER]** ("**Licensee**" or "**Customer**").

IT42 and Licensee are each a "**Party**" and together the "**Parties**."

### Recitals

A. IT42 is the developer and licensor of GridLookout, a Windows application that displays live
video from a Milestone XProtect system on one or more monitors (the "**Software**").

B. IT42 distributes the Software's noncommercial-use rights under the PolyForm Noncommercial
License 1.0.0 (the "**Noncommercial License**"), which does not permit business, government, or
other commercial use.

C. Licensee wishes to use the Software for commercial purposes and IT42 is willing to grant a
commercial license on the terms of this Agreement.

The Parties agree as follows.

---

### 1. Definitions

**1.1 "Wall-Controller PC"** means one physical or virtual Windows machine on which the Software
is installed and actively running at any given time. A Wall-Controller PC may drive any number
of attached monitors and display any number of cameras; neither monitor count nor camera count
affects the license count.

**1.2 "License Metric"** is the Wall-Controller PC, as defined in the "License metric" section of
the GridLookout Commercial Licensing document as delivered with the Software
(`COMMERCIAL-LICENSE.md`; the same document is tracked in the source repository as
`COMMERCIAL-LICENSE.md`), incorporated here by reference.

**1.3 "License Tier"** means the specific licensed quantity and scope purchased by Licensee, as
set out in **Exhibit A** — one of: Single Controller, Site Pack, 10-Pack, or a Fleet/volume
license per a separate quote.

**1.4 "Documentation"** means the user guide, admin guide, security document, and compatibility
document shipped with the Software.

**1.5 "Update"** means a new build of the Software that IT42 makes generally available to
licensees of the same major version line, excluding any separately priced new product.

---

### 2. Grant of License

**2.1 Scope.** Subject to Licensee's payment of the applicable fees and compliance with this
Agreement, IT42 grants Licensee a **non-exclusive, non-transferable (except per § 2.4),
perpetual** license to install and run the Software on up to the number of Wall-Controller PCs
specified in Exhibit A, solely for Licensee's own internal or customer-facing operational use
(e.g., monitoring walls, retail/back-office displays, control rooms).

**2.2 Perpetual scope.** This license does not expire and is not subject to renewal. It covers
the version of the Software current as of purchase plus all Updates released during the Update
period defined in § 4. A lapsed customer (one who does not renew Maintenance under § 4.2) keeps
a perpetual license to the last version received — this Agreement does not revoke or time-limit
use of that version.

**2.3 Attached hardware.** A Wall-Controller PC may be replaced (hardware refresh, reinstall, OS
migration) at no additional cost; the license follows the Licensee's usage, not a specific piece
of hardware, provided the total count of Wall-Controller PCs concurrently running the Software
does not exceed the licensed quantity.

**2.4 Transfer.** Licensee may not assign, sublicense, rent, lease, or otherwise transfer this
license to a third party without IT42's prior written consent, except to a successor in a merger,
acquisition, or sale of substantially all of Licensee's assets, provided the successor agrees in
writing to be bound by this Agreement. **[COUNSEL: confirm whether IT42 wants a transfer fee or
notice-only requirement for the M&A carve-out.]**

**2.5 Precedence over the Noncommercial License.** For every Wall-Controller PC covered by a
License Tier under this Agreement, this Agreement — not the Noncommercial License — governs
Licensee's use of the Software. The Noncommercial License continues to apply, unchanged, to any
use of the Software by Licensee (or anyone else) that is not covered by a paid License Tier
under this Agreement (for example, personal evaluation copies outside the scope of § 3, or use
by any other entity). Nothing in this Agreement grants rights broader than the number of
Wall-Controller PCs actually licensed under Exhibit A.

**2.6 Third-party components.** The Software includes components of the Milestone MIP SDK and
other third-party libraries, each subject to its own license terms as identified in the
`3rd_party_software_terms_and_conditions.txt` file shipped with the Software. This Agreement
does not grant, and should not be read as granting, any rights in those third-party components
beyond what their own license terms permit; IT42's right to redistribute the Milestone MIP SDK
components is separately governed by IT42's agreement with Milestone Systems A/S.
**[COUNSEL: this Agreement currently assumes but does not independently verify IT42 holds MIP SDK
redistribution rights under the Milestone developer/technology-partner program — confirm before
this clause goes out to a real customer; see the North Star doc's L7 tracking item.]**

---

### 3. Evaluation Use

Before purchasing, Licensee may evaluate the Software in a commercial environment for up to
30 days under the separate **PolyForm Free Trial License 1.0.0**, obtained directly from IT42.
That evaluation is governed by the Free Trial License's own terms, not this Agreement, and ends
automatically at the earlier of 30 days or execution of this Agreement (which supersedes it for
the licensed Wall-Controller PCs going forward).

---

### 4. Updates and Maintenance

**4.1 Included Update period.** The first twelve (12) months from the Effective Date include all
Updates IT42 releases for the purchased major version line, at no additional cost.

**4.2 Optional Maintenance.** After the initial 12 months, Licensee may renew annually at 20% of
the then-applicable license fee for the licensed quantity (subject to a minimum annual fee of
€150), which includes continued Updates, compatibility releases, and email support for the
renewal period. Maintenance is optional; declining or lapsing it does not affect the perpetual
license under § 2.2, only continued access to new Updates and support.

**4.3 Support during year one.** The scope of support included during the initial 12 months is
as stated in Licensee's quote (Exhibit A). **[COUNSEL/OWNER: this Agreement intentionally does
not assert a specific year-one support SLA here — confirm what year-one support commitment, if
any, IT42 intends to standardize before this goes to a customer; see north-star L9 tracking
item.]**

---

### 5. Permitted and Prohibited Use

**5.1 Permitted use.** Licensee may install, run, and configure the Software on the licensed
number of Wall-Controller PCs; connect it to Licensee's own Milestone XProtect system(s); and
make configuration changes (recorder `$layout{}` matrix entries, monitor assignment, etc.) as
described in the Documentation.

**5.2 Prohibited use.** Licensee may not: (a) reverse engineer, decompile, or disassemble the
Software except to the extent applicable law makes this restriction unenforceable; (b) remove or
obscure any copyright, trademark, or proprietary notice; (c) use the Software to build a
competing product; (d) redistribute the Software to any party not covered by this Agreement,
except as expressly permitted in writing by IT42; (e) use the Software on more concurrently
running Wall-Controller PCs than licensed under Exhibit A; or (f) use the Software in any manner
that violates applicable law, including export control and sanctions law.

**5.3 Milestone XProtect license required.** The Software requires a separately and independently
licensed Milestone XProtect system. This Agreement does not include, extend, replace, or modify
any Milestone license, and IT42 makes no representation about Licensee's Milestone licensing.

---

### 6. Fees and Payment

**6.1 Fees.** Licensee shall pay the fees set out in Exhibit A, in the currency and on the
payment terms stated in IT42's invoice. **[COUNSEL: confirm standard payment terms — e.g., net 30
from invoice date — and late-payment interest, if any, consistent with Croatian commercial law.]**

**6.2 Taxes.** Fees are exclusive of VAT and any other applicable taxes, which Licensee is
responsible for in addition to the stated fees, except taxes on IT42's net income.

---

### 7. Warranty Disclaimer

**EXCEPT AS EXPRESSLY STATED IN THIS AGREEMENT, THE SOFTWARE IS PROVIDED "AS IS," WITHOUT
WARRANTY OF ANY KIND, WHETHER EXPRESS, IMPLIED, OR STATUTORY, INCLUDING WITHOUT LIMITATION ANY
WARRANTY OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, TITLE, OR NON-INFRINGEMENT. IT42
DOES NOT WARRANT THAT THE SOFTWARE WILL BE UNINTERRUPTED, ERROR-FREE, OR THAT ANY DEFECT WILL BE
CORRECTED.** Nothing in this section excludes or limits any warranty that cannot lawfully be
excluded or limited under the law applicable to a consumer or as otherwise mandated by
applicable law. **[COUNSEL: confirm this disclaimer's enforceability for a B2B Croatian/EU
counterparty and whether any statutory conformity warranty period under Croatian law
(Zakon o obveznim odnosima / consumer-adjacent B2B norms) must be separately preserved.]**

---

### 8. Limitation of Liability

**8.1 Cap.** Except for the carve-outs in § 8.2, each Party's total aggregate liability arising
out of or related to this Agreement, whether in contract, tort, or otherwise, shall not exceed
the total license fees actually paid by Licensee to IT42 in the twelve (12) months preceding the
event giving rise to the claim.

**8.2 Carve-outs.** Nothing in this Agreement limits or excludes either Party's liability for:
(a) death or personal injury caused by negligence; (b) fraud or fraudulent misrepresentation;
(c) gross negligence or willful misconduct; or (d) any other liability that cannot lawfully be
limited or excluded under the law applicable to this Agreement.

**8.3 Exclusion of indirect damages.** Except for the carve-outs in § 8.2, neither Party is
liable for any indirect, incidental, consequential, special, or punitive damages, or for loss of
profits, revenue, data, or business opportunity, even if advised of the possibility of such
damages. **[COUNSEL: confirm the cap and carve-out list against current Croatian/EU mandatory-
liability rules before use — this is drafted to the general shape counsel typically requires, not
verified against current statute.]**

---

### 9. Compliance Verification

Rather than an audit-rights clause (the Software has no activation server or usage-reporting
mechanism to audit against), Licensee agrees to self-certify its Wall-Controller PC count to
IT42 upon reasonable written request, no more than once per twelve-month period, absent a
good-faith belief of non-compliance. **[COUNSEL/OWNER: confirm this self-certification approach
is sufficient, or whether IT42 wants a traditional audit clause instead.]**

---

### 10. Term and Termination

**10.1 Term.** This Agreement is effective as of the Effective Date and continues until
terminated as provided below; the license grant in § 2 is perpetual and survives termination of
this Agreement except as stated in § 10.3.

**10.2 Termination for breach.** Either Party may terminate this Agreement on written notice if
the other Party materially breaches it and fails to cure within thirty (30) days of written
notice describing the breach.

**10.3 Effect of termination for Licensee's uncured breach.** If IT42 terminates under § 10.2 for
Licensee's uncured breach, the license grant in § 2 terminates and Licensee must cease using the
Software and destroy or return all copies. Termination for any other reason (e.g., non-renewal of
Maintenance) does not affect the perpetual license under § 2.2.

---

### 11. Governing Law and Disputes

**11.1 Governing law.** This Agreement is governed by the laws of the **Republic of Croatia**,
without regard to its conflict-of-laws principles, and without regard to the United Nations
Convention on Contracts for the International Sale of Goods.

**11.2 Jurisdiction.** The Parties submit to the exclusive jurisdiction of the competent courts
of **[COUNSEL: confirm venue — e.g., the Commercial Court in Zagreb / Trgovački sud u Zagrebu]**
for any dispute arising out of or relating to this Agreement, except that either Party may seek
injunctive relief in any court of competent jurisdiction to protect its intellectual property
rights.

---

### 12. General

**12.1 Entire Agreement.** This Agreement, including Exhibit A, is the entire agreement between
the Parties regarding the Software's commercial licensing and supersedes all prior discussions,
negotiations, and agreements on that subject.

**12.2 Amendment.** This Agreement may only be amended in writing signed by both Parties.

**12.3 Severability.** If any provision of this Agreement is held unenforceable, the remaining
provisions remain in full force, and the unenforceable provision is replaced with an enforceable
provision that most closely reflects the Parties' original intent.

**12.4 Notices.** Notices under this Agreement must be in writing and sent to the addresses set
out above, or to such other address as either Party designates in writing.

**12.5 No waiver.** No failure or delay by either Party in exercising any right under this
Agreement operates as a waiver of that right.

---

## Exhibit A — License Details

| Field | Value |
|---|---|
| License Tier | **[Single Controller / Site Pack / 10-Pack / Fleet]** |
| Licensed Wall-Controller PC count | **[N]** |
| Site restriction (Site Pack only) | **[SITE NAME/ADDRESS, if applicable]** |
| License fee | **[€ AMOUNT]** (excl. VAT) |
| Invoice/payment reference | **[INVOICE NUMBER]** |
| Year-one support scope | **[DESCRIBE — see § 4.3]** |
| Maintenance opted in at signing? | **[YES/NO]** |
| Integrator of record (if any) | **[INTEGRATOR NAME, if applicable]** |

---

## Signatures

**For IT42 d.o.o.:**

Signature: _______________________________

Name: **[SIGNATORY NAME]**

Title: **[SIGNATORY TITLE]**

Date: **[DATE]**

**For [CUSTOMER LEGAL NAME]:**

Signature: _______________________________

Name: **[SIGNATORY NAME]**

Title: **[SIGNATORY TITLE]**

Date: **[DATE]**

---

*This template is derived from `COMMERCIAL-LICENSE.md`'s published pricing and
license-metric terms as of this revision. If those terms change, this template must be updated
in the same pass (or an executed agreement predating the change continues to govern under its
own terms — this template is not itself a signed instrument).*
