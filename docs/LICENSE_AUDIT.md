# Windows distribution license audit — Battery Forecast

Audit started 2026-10-25 on commit `824dce5`. This report distinguishes inspected source,
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
  notices, are copied byte-for-byte into ZIP archives. Their bundled native components are covered by
  those upstream legal trees, rather than assigned the package's top-level license indiscriminately.
- Every other Python distribution found in PyInstaller's module/binary/data inventory receives its own
  original legal files, version and archive hash. No development blacklist decides legal coverage.
- PyInstaller 6.22.3: full original COPYING.txt, including bootloader exception, included for traceability;
  no PyInstaller source modifications. Its exception is [documented upstream](https://pyinstaller.org/en/stable/license.html).
- Inno Setup: installed compiler's original license included when building an installer; built-in notices
  are not removed from the setup/uninstaller binaries.

Original downloads, resolved URLs where relevant, and SHA-256 are in `licenses/sources.json`.
Vendor files use Git `-text` to preserve exact bytes. Subsequent packaging runs use these local originals,
not network downloads. Runtime upgrades require updating the audit allowlist instead of silently
reusing old notices. `inventory.json` records versions and hashes of the collected legal material.

## Verification and remaining limits

- Baseline: 90 C# and 22 Python tests passed before changes; CI and Pages succeeded on 824dce5.
- Original legal source hashes and exact MIT notice/disclaimer checked automatically.
- Trial legal collection against the existing x64 alpha.1 publish and PyInstaller analysis completed;
  30 Python distributions and 9 .NET components recorded, archives verified. This is **not** verification
  of a newly built Battery Forecast installer.
- Complete new x64 package build and installed-content checks: pending.
- ARM64 package and installed-content checks: pending on native Windows ARM CI.
- No installer is executed over the user's existing alpha.1 installation during this audit.
- Long-term accuracy of battery estimates, clean offline VM behavior, and a legal opinion about employer
  eligibility or SDK terms are outside the evidence established here. No blanket compliance certification.

The source-level omissions identified above have concrete corrections. The binary distribution remains
provisionally assessed until the per-architecture packaging and installed-file checks pass.
