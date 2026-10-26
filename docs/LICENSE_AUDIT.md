# Windows distribution license audit — Battery Forecast

Audit started 2026-10-25 on commit `824dce5`; native-runtime review extended 2026-10-26. This report distinguishes inspected source,
verified package material and unresolved distribution questions. It is a technical review of
license evidence, not a representation that every possible use is legally cleared.

## Confirmed findings and changes

| Finding | Evidence | Correction |
| --- | --- | --- |
| Original copyright line altered with a parenthetical | Till Dusk LICENSE at pinned commit vs fork LICENSE | Restore exact `Copyright (c) 2026 Matteo Bonfanti`; Mattboxx contributions remain separate |
| .NET inventory mostly license names/URLs | Previous tools/licenses.py DOTNET list | Collect original license texts, third-party notices and nuspec copyright metadata into the install |
| Runtime third-party notices omitted | Self-contained runtime packages and WPF/WinForms source notices | Include actual runtime LICENSE/THIRD-PARTY-NOTICES and pinned desktop notices |
| Runtime dependencies could be silently omitted by a development blacklist | packaging and pluggy are present in the engine analysis | Derive Python distributions from PyInstaller Analysis-00.toc, including metadata and native DLL sources |
| Missing model/Python licenses did not fail packaging | Previous optional exists checks | Missing required originals, unknown .NET package versions, changed originals and corrupt archives stop the build |
| Nested license copies were removed without checking preservation | Previous strip_nested | Compare every removed file against its archived original hash first |
| License README referenced Autonomia and macOS runtime paths | licenses/README-it.txt | Correct product name and actual offline Windows license paths |
| Windows SDK/WinRT projections not listed | Published deps.json and binary product versions | Include SDK terms supplied by package license URL and CsWinRT MIT original at embedded source revision |
| Intel OpenMP DLLs were absent from the PyTorch legal tree | Exact DLL hashes match official Intel OpenMP 2026.1.1 wheel | Preserve full August 2024 Intel EULA and third-party notices, with hash-pinned provenance |
| ARM64 ArmPL DLL had no supplied agreement | DLL matches the official ArmPL 24.10 MSI byte-for-byte | Preserve the actual Simplified EULA for Free of Charge Arm Redistributables and third-party notices |
| VC++ runtime agreements were only indirect in Python notices | Native file versions 14.40/14.51 and Python Additional Conditions | Include original Microsoft 2022/2026 agreements (original DOCX and exact paragraph-text extraction) |
| OS UCRT/API-set copies were redundantly redistributed | PyInstaller binary analysis on supported Windows 10+ | Exclude these OS-supplied copies; retain Python/VC runtime components |
| Native DLL/PYD coverage was not individually checked | No per-file mapping in previous legal inventory | Schema 2 maps every frozen engine native binary to documents and hashes; unknown/changed vendor binaries fail |
| Silent updates bypassed recipient terms | Previously `/SILENT` updater, no LicenseFile | Approved IT/EN component-terms page; visible updates; legacy alpha.2 updater reopens an interactive wizard; other unattended installs require explicit acceptance |
| Publisher metadata could suggest original-author publishing | Authors flowed into CompanyName; inconsistent AppPublisher | Mattboxx is the Windows publisher, original and port copyrights retained separately |
| Public wording could imply all bundled components were MIT/open source | Website tagline and duplicated README licensing | Scope MIT to project code; disclose model and native-runtime terms and independent donations |

## Original project and independent identity

Till Dusk revision `0e78703264c4d5f8b71ab6cf3425bdf3bb3c973b` is MIT. Its complete original
LICENSE is preserved in `licenses/vendor/Till-Dusk-original-LICENSE.txt`, including its disclaimer.
Battery Forecast retains its name, icon, branding, free distribution and Mattboxx Ko-fi link.
Mattboxx's original contributions have a separate copyright. No endorsement by Matteo Bonfanti or
Mistral AI is claimed. Donations support the independent Windows port, not the original authors.
The code's MIT grant permits distribution and commercial uses subject to preserving the notice;
it does not grant rights to third-party trademarks or override model/runtime terms.

## Timestral: code and weights reviewed separately

| Component | Revision | Evidence |
| --- | --- | --- |
| Inference code | a534bdbd244d64f3d9d743f46c7bf6fc330ed4ff | Apache-2.0 original LICENSE; installed direct_url.json matches; no separate NOTICE in that source tree |
| Timestral-28M | 49b26409ac87b5935539a5d8ffe7f397b720db47 | Original Modified MIT LICENSE compared with locally downloaded model; hash in MODEL_INFO.json |
| Timestral-5M | bdd20cfc15a3fea0e4cb5ccef37f8d3ca7aa335f | Same original license, reviewed independently; development comparison model, not shipped in default installer |

The weights are **not standard MIT**. Clause 2 refers to the global consolidated monthly revenue of
the user's company **or employer**, exceeding USD 20 million (or equivalent) **for the preceding month**.
It also refers to derivatives, modifications and combined works based on the model. The full exact
English terms are preserved, including the original wording's reference to “(b)”. No editorial
correction or narrowing of those terms is applied. Individual eligibility, employer circumstances
and the interpretation of combined works have not been established by this technical audit.
A different license from Mistral may be necessary for affected users. Free distribution or donations
do not automatically remove that condition. See original [28M terms](https://huggingface.co/mistralai/Timestral-28M/blob/49b26409ac87b5935539a5d8ffe7f397b720db47/LICENSE).

## Runtime and dependency evidence

- `.NET` self-contained runtime 10.0.13: original LICENSE and THIRD-PARTY-NOTICES from restored runtime pack.
- WPF/Windows Forms 10.0.13: original pack license plus MIT and third-party notices from dotnet source
  revision `4099f83977f4d29604a9aeb971fb0a7bc1d5f699` recorded by its nuspec.
- Microsoft.Data.Sqlite/Core 10.0.0: MIT source text from the nuspec repository revision
  `b0f34d51fccc69fd334253924abd8d6853fad7aa`; original nuspec copyright retained.
- SQLitePCLRaw family 2.1.14: Apache-2.0 from package metadata; complete original Apache text preserved.
  Its nuspec does not identify a source commit and no v2.1.14 tag was found. The collected license text
  comes from source revision `ca835d21508bff43121c65081035840ac5006c4c` (v2.1.12), **not a claim that
  the 2.1.14 binaries were built from that commit**. Original package metadata is included unchanged.
- SQLite: original public-domain declaration saved from sqlite.org; no synthetic replacement license.
- Windows SDK .NET Ref 10.0.19041.57: original SDK license at the package's license URL (RTF), plus
  CsWinRT MIT at revision `8649ee3eeb2445ca2a36d80d878ef60b96a6c65d`, embedded in WinRT.Runtime 2.2.0.
  The SDK terms are separate from the project's MIT. Redistribution must follow their applicable terms.
- Python 3.12.10: complete LICENSE.txt from the actual runtime, including bundled component notices.
- PyTorch 2.14.1+cpu and NumPy 2.5.4: their installed original license trees, including nested third-party
  notices, are copied byte-for-byte into ZIP archives. Ownership of a wheel does not prove that its
  top-level license covers every DLL. Intel OpenMP, statically linked oneMKL and ArmPL are explicitly handled separately below;
  NumPy's OpenBLAS and bundled notices remain in its complete original legal tree.
- Every other Python distribution found in PyInstaller's module/binary/data inventory receives its own
  original legal files, version and archive hash. No development blacklist decides legal coverage.
- PyInstaller 6.22.3: full original COPYING.txt, including bootloader exception, included for traceability;
  no PyInstaller source modifications. Its exception is [documented upstream](https://pyinstaller.org/en/stable/license.html).
- Inno Setup: installed compiler's original license included when building an installer; built-in notices
  are not removed from the setup/uninstaller binaries.

### Native-runtime original sources and recipient terms

- **Intel oneMKL 2026.1 (x64, statically linked):** the actual `torch.__config__.show()` records
  product build `20260612`; the official `mkl-include==2026.1.0` header confirms that build date.
  Its original Intel Simplified Software License (October 2022) is copied byte-for-byte from the
  official `mkl-static==2026.1.0` wheel. It is distinct from OpenMP's Developer Tools EULA and from
  Apache-licensed oneDNN. The exact x64 `torch_cpu.dll` hash is mapped to the PyTorch archive plus
  oneMKL terms. Original Intel 2026 documentation notices are included as supplementary HTML;
  those notices are not claimed to identify an exact static-library build. The ARM64 DLL has its
  own separately pinned hash and ArmPL mapping, without falsely assigning oneMKL to that build.

- **Intel OpenMP 2026.1.1 (x64):** `libiomp5md.dll` and `libiompstubs5md.dll` match the official PyPI
  wheel byte-for-byte. Full `LICENSE.txt` and `third-party-programs.txt` are preserved. The actual agreement
  is Intel's Developer Tools EULA, August 2024, not the PyTorch BSD license. Section 2.1.D(2) requires a
  downstream restriction for redistributable binaries, and section 7.3 requires the liability limitations
  to bind recipients. Original terms are shown by the installer; their legal effectiveness is not certified.
- **ArmPL 24.10.0 (ARM64):** `armpl_lp64.dll` SHA-256
  `2913a6f3b11d43695f4a4cfe501e67e3d67ce14f652e0b99bdbe38d0564ba665` matches the DLL in the official
  ArmPL 24.10 Windows MSI. The MSI database was read without executing it, then its embedded CAB extracted.
  The preserved agreement is **Simplified End User License Agreement for Free of Charge Arm Redistributables**,
  together with its original third-party licenses. Section 2.1(a) addresses recipient acceptance and binding
  obligations; section 2.2 includes reverse-engineering restrictions subject to overriding applicable law.
- **Microsoft VC++:** Python's Additional Conditions are retained. Original Visual C++ 2015–2022 and
  Visual C++ v14 (2026) runtime agreements are saved from Microsoft's official license pages, including
  their DOCX originals and mechanically extracted paragraph text. Native PE versions select the applicable
  reviewed agreement in the inventory. Distributor eligibility and its own development-tool entitlements
  have not been established by collecting these texts.

`licenses/native-components.json` pins Intel/Arm and PyTorch CPU DLL hashes and their documents. `licenses/sources.json`
records the exact vendor download, wheel/MSI hash and original member names. No license has been invented
or summarized as a substitute for the original. Build collection is offline from these checked originals.
`INSTALLER-TERMS-it.txt` / `INSTALLER-TERMS-en.txt` combine an explicitly scoped introduction with complete
applicable originals; those originals and notices also remain separately readable in the installed folder.
The user approved this installer-only flow change on 2026-10-26. The MIT grant on the project code is unchanged.
Silent CI installs use `/ACCEPTCOMPONENTTERMS`; absence is rejected. Legacy alpha.2's silent updater opens the
visible wizard instead of treating silence as agreement. See [installer implementation](INSTALLER_TERMS_PLAN.it.md).

Original downloads, resolved URLs where relevant, and SHA-256 are in `licenses/sources.json`.
Vendor files use Git `-text` to preserve exact bytes. Subsequent packaging runs use these local originals,
not network downloads. Runtime upgrades require updating the audit allowlist instead of silently
reusing old notices. `inventory.json` records versions and hashes of the collected legal material.

## Verification and remaining limits

- Existing alpha.2 release: GitHub run `40250510086` on `6e8f73e` passed x64 and ARM64 package builds,
  frozen-engine smoke tests, actual installation, installed-file verification, reinstall and uninstall.
  This establishes the previous installer behavior, **not** the new native-license corrections.
- Current source changes: **128 C#**, **36 forecast Python**, **11 license-tool tests** passed locally.
  Original source hashes and exact original MIT notice/disclaimer pass automatic verification.
- A preliminary local x64 alpha.3 package collected 30 Python distributions, 9 .NET components and 58
  native engine binaries. It preceded the final terms-page change and is not published.
- Corrected 0.9.0 x64 build, final frozen-engine test and per-architecture installation checks: in progress.
  The result must be recorded separately before publication; no untested local alpha.3 binary is a release asset.
- No installer is executed over the user's existing installation during this audit. Native ARM64 verification
  uses disposable GitHub-hosted Windows ARM runners. CI verifies files and execution, not human comprehension.
- The visible wizard and migration from an old updater still require a manual walkthrough; compiled setup and
  silent CI tests alone do not establish that a person read or legally accepted every term.
- Model accuracy gains have not been measured. Offline model flags are tested; a clean VM with network adapters
  disabled has not been established by those flags. Code signing remains absent.
- A legal review of distributor entitlement (Microsoft/Intel/Arm), model employer/revenue eligibility,
  applicable SDK terms and effectiveness of downstream agreements remains open. Free distribution and Ko-fi
  donations do not remove third-party conditions. This report gives no blanket compliance certification.

The audit corrects confirmed missing texts, attribution and technical packaging gaps. Source and binary
verification are distinct; distribution is provisionally assessed subject to the explicit limits above.
