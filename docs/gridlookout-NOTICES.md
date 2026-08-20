# GridLookout — Third-Party Notices

> Ships as `NOTICES.md` at the install root (`%ProgramFiles%\GridLookout\`), next to
> `LICENSE`/`LICENSE-TRIAL`, staged into the MSI at build time; referenced from
> `COMMERCIAL-LICENSE.md` ("Terms summary") so a buyer can review it
> pre-purchase. Every statement cites its source.

GridLookout is a product of IT42 d.o.o. It is an independent integration built on the
Milestone Integration Platform (MIP) SDK. GridLookout is not a Milestone Systems product and
is not endorsed by or affiliated with Milestone Systems A/S. A separately licensed Milestone
XProtect system is required; GridLookout does not include, replace, or modify any Milestone
licensing.

---

## 1. Milestone MIP SDK runtime (Milestone Systems A/S)

This product includes runtime components of the Milestone MIP SDK, version 25.2.3, obtained
from the NuGet packages `MilestoneSystems.VideoOS.Platform` and
`MilestoneSystems.VideoOS.Platform.SDK` (nuget.org, publisher `milestonesys`):

- `VideoOS.*.dll` (Platform, Platform.SDK, Toolkit, Management, Common, UI, Utilities,
  Telemetry families), `CoreToolkits.dll`, `ToolkitFactoryProvider.dll`, `IMV1.dll`, and the
  locale resource folders.
- Copyright © 2025 Milestone Systems A/S. All rights reserved.

**Redistribution authorization.** These components are redistributed under the *User License
Agreement for Milestone MIP SDK Templates and for MilestoneSystems.VideoOS. MIP SDK and
MilestoneSystems.VideoOS. Mobile SDK Packages* (License Agreement 20250424), shipped as
`MIPSDK_EULA.txt` inside both NuGet packages and displayed on their nuget.org pages
(https://www.nuget.org/packages/MilestoneSystems.VideoOS.Platform.SDK/). Section "Download,
Installation and Use", paragraph 4:

> "You may redistribute to 3rd parties only the parts of the Product that are utilized by
> your application, provided that all relevant third parties' licensing agreements are
> included in such redistribution. For MilestoneSystems.VideoOS.Platform.SDK and
> MilestoneSystems.VideoOS.Mobile.SDK packages specifically the licensing agreements are to
> include, but not limited to, those licenses listed in the '3rd party software terms and
> conditions.txt' file included in these packages."

GridLookout complies by (i) shipping only the SDK payload its application uses (the build
trims unused SDK files — see `GridLookout.csproj`, `TrimUnusedSdkPayload`), and (ii) shipping
Milestone's `3rd_party_software_terms_and_conditions.txt` and this notices file in the
install folder.

**Use restriction passed through to the end customer** (same agreement, paragraphs 3 and 5):
the Milestone SDK components may only be used in connection with Milestone XProtect products
and Milestone Husky NVR products (or approved OEM versions), and their use is further subject
to the Milestone End-user License Agreement covering the customer's XProtect product
(https://www.milestonesys.com/support/resources/download-software/).

## 2. Third-party software bundled by Milestone inside the MIP SDK

The Milestone SDK payload itself contains third-party software. Milestone's own notices file,
`3rd_party_software_terms_and_conditions.txt`, is installed alongside this product and
contains the license texts. Components in the GridLookout install footprint covered by that
file are listed below. As with the SDK payload above, this table lists only components that
actually ship in the trimmed build — entries in Milestone's terms file with no corresponding
file in GridLookout's install footprint (e.g. the C5 collection library, NAudio, and the FFmpeg
DLLs the trim removes) are intentionally omitted. The five FFmpeg DLLs listed BELOW do ship:
the MIP SDK's media layer references them at load time, so they must be present even though
GridLookout's JPEG-only playback path never routes video through their decoders:

| Component | Shipped files | License (per Milestone's terms file) |
|---|---|---|
| FFmpeg | `avcodec-61.dll`, `avformat-61.dll`, `avutil-59.dll`, `swresample-5.dll`, `swscale-8.dll` | GNU LGPL v2.1 — source: https://github.com/FFmpeg/FFmpeg |
| BtbN FFmpeg build scripts | (used to produce the FFmpeg binaries) | MIT, © 2020–2021 BtbN |
| Apache Xerces-C++ / XQilla | `xerces-c-vc143_3_3_0.dll`, `xqilla-vc143_234_330.dll` | Apache License 2.0 |
| Json.NET (Newtonsoft.Json) | `Newtonsoft.Json.dll` | MIT |
| Autofac | `Autofac.dll` | MIT |

**FFmpeg (LGPL-2.1) specifics.** The FFmpeg libraries are unmodified binaries distributed by
Milestone as separate, dynamically linked DLLs. The complete LGPL-2.1 license text is included
in the installed `3rd_party_software_terms_and_conditions.txt`; corresponding source code is
available from https://github.com/FFmpeg/FFmpeg (builds per https://github.com/BtbN/FFmpeg-Builds).
Nothing in the GridLookout license restricts replacing these DLLs with modified versions of
the same libraries, or reverse engineering them for debugging such modifications, as
permitted by LGPL-2.1; any future revision of the GridLookout commercial license carrying a
no-reverse-engineering clause must carve these LGPL components out of it.

## 3. Components in the Milestone SDK payload NOT covered by Milestone's terms file

Package inspection found these files in the SDK `dependencies` payload (and therefore in the
GridLookout install footprint) that Milestone's terms file does **not** list. The MIP SDK EULA
paragraph 4 quoted above requires "all relevant third parties' licensing agreements" to be
included in a redistribution, so they are named here:

| Component | Shipped files | License |
|---|---|---|
| Microsoft Visual C++ Runtime | `msvcp140*.dll`, `vcruntime140*.dll`, `concrt140.dll`, `vccorlib140.dll` | Microsoft Visual Studio license, Distributable Code (https://learn.microsoft.com/en-us/visualstudio/releases/2022/redistribution); © Microsoft Corporation |

The Intel® Media SDK runtime (`libmfxsw64.dll`, `libmfxaudiosw64.dll`, the
`mfxplugin64_hevcd_sw.dll` HEVC decoder plugin) and the NVIDIA CUDA/NPP runtime
(`cudart64_12.dll`, `nppc64_12.dll`, `nppig64_12.dll`) that the MIP SDK bundle contains are
**deliberately excluded** from the GridLookout install footprint (`GridLookout.csproj`,
`TrimUnusedSdkPayload`): GridLookout consumes JPEG streams only and exercises no
hardware-accelerated or HEVC decode path — live-verified against a running XProtect system
with the trimmed payload (2026-08-18). They therefore appear in no table here.

## 4. Other open-source and redistributable components (direct NuGet dependencies)

These ship in the install folder as dependencies of the MIP SDK packages. As above, this table
lists only the components with a corresponding file in the trimmed build — the SDK's Azure.Core/
Azure.Identity, Microsoft.Identity.Client, JsonPatch, Owin/Microsoft.Owin, and
System.Web.Http(.Owin) dependency chain is pulled in by SDK features GridLookout's build trims
and none of those assemblies ship, so they are not listed. License texts:
MIT — https://opensource.org/license/mit; Apache-2.0 — https://www.apache.org/licenses/LICENSE-2.0.

| Component | License |
|---|---|
| Microsoft.ApplicationInsights 2.22.0 | MIT, © Microsoft Corporation |
| Microsoft.IdentityModel.* / System.IdentityModel.Tokens.Jwt | MIT, © Microsoft Corporation |
| Microsoft.Extensions.* abstractions, Microsoft.Bcl.AsyncInterfaces, System.* BCL packages (System.Text.Json, System.Memory, System.Buffers, …) | MIT, © .NET Foundation and contributors |
| Microsoft ASP.NET Web API client (System.Net.Http.Formatting) | Microsoft .NET Library license (distributable code), © Microsoft Corporation |
| Microsoft.Xaml.Behaviors | MIT, © Microsoft Corporation |
| Newtonsoft.Json | MIT, © James Newton-King |

### MIT License (applies to each component above marked MIT)

> Permission is hereby granted, free of charge, to any person obtaining a copy of this
> software and associated documentation files (the "Software"), to deal in the Software
> without restriction, including without limitation the rights to use, copy, modify, merge,
> publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
> to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or
> substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
> INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
> PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
> FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
> OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
> DEALINGS IN THE SOFTWARE.

## 5. Telemetry disclosure

GridLookout registers itself with the customer's own Milestone XProtect system using the MIP
SDK integration identity mechanism ("Installed Integration Insights"). The customer's XProtect
Management Server — under the customer's control — may in turn report installed-integration
identity to Milestone's licensing web service (see Milestone's MIP SDK Getting Started guide,
"Installed Integration Insights"). GridLookout itself sends nothing to IT42 d.o.o. or any third
party. `Microsoft.ApplicationInsights.dll` is present only as a mandatory MIP SDK dependency;
GridLookout does not configure or use it. This paragraph and the security guide's "no telemetry"
section describe the same facts and are maintained together.

## 6. Milestone program status

The authority IT42 d.o.o. relies on to redistribute the MIP SDK components is the
**redistribution grant in the MIP SDK EULA itself** (quoted in section 1 above), which carries
no partner-program condition. Supporting context, not itself the grant: the MIP SDK Getting
Started guide states membership is not required "to review the MIP SDK or to develop internal
solutions"
(https://download.milestonesys.com/MIPSDK/MilestoneMIPSDK_GettingStartedGuide_en-US.pdf), and
Milestone's partner page lists "Develop and sell independently" as a supported path
(https://www.milestonesys.com/partners/become-a-partner/technology-partner/).

IT42 d.o.o. distributes GridLookout as an independent integration under the redistribution
grant quoted in section 1. This reading has not yet been independently confirmed by counsel
(the commercial agreement template's §2.6 states the same status); the shipped `MIPSDK_EULA.txt`
is the controlling text.

---

*Document version: 2026-08-18. Sources: `MIPSDK_EULA.txt` (License Agreement 20250424)
in `MilestoneSystems.VideoOS.Platform[.SDK]` 25.2.3 NuGet packages;
`3rd_party_software_terms_and_conditions.txt` (same package, `content/`); Milestone MIP SDK
Getting Started guide; milestonesys.com partner pages; per-package `.nuspec` license
declarations in the NuGet cache.*
