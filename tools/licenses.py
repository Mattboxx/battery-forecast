"""Offline legal inventory of the published app and PyInstaller analysis.

Original texts only. Missing notices, unaudited .NET versions and corrupt license
archives fail packaging. See docs/LICENSE_AUDIT.md for evidence and limitations.
"""
import argparse
import ast
import hashlib
import json
import os
import re
import shutil
import sys
import zipfile
from importlib import metadata
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LEGAL = ("LICENSE", "LICENCE", "COPYING", "NOTICE", "COPYRIGHT")


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def legal_file(path):
    return any(k in path.name.upper() for k in LEGAL) and path.suffix.lower() not in {".py", ".pyc", ".dll"}


def verify_sources():
    manifest = json.loads((ROOT / "licenses/sources.json").read_text(encoding="utf-8"))
    for item in manifest:
        p = ROOT / "licenses" / item["file"]
        if not p.is_file() or sha(p) != item["sha256"]:
            raise ValueError(f"Audited original missing or changed: {item['file']}")
    for item in manifest:
        if item.get("derived_from", "").endswith(".docx"):
            import xml.etree.ElementTree as ET
            original_docx = ROOT / "licenses/vendor" / item["derived_from"]
            with zipfile.ZipFile(original_docx) as archive:
                document = ET.fromstring(archive.read("word/document.xml"))
            ns = {"w": "http://schemas.openxmlformats.org/wordprocessingml/2006/main"}
            text = "\n".join("".join(t.text or "" for t in p.findall(".//w:t", ns))
                             for p in document.findall(".//w:p", ns)) + "\n"
            if (ROOT / "licenses" / item["file"]).read_text(encoding="utf-8") != text:
                raise ValueError(f"Original DOCX text extraction changed: {item['file']}")
    original = (ROOT / "licenses/vendor/Till-Dusk-original-LICENSE.txt").read_text(encoding="utf-8")
    current = (ROOT / "LICENSE").read_text(encoding="utf-8")
    if "Copyright (c) 2026 Matteo Bonfanti" not in current.splitlines():
        raise ValueError("Original copyright must be preserved verbatim")
    if original.split("Permission is hereby", 1)[1].split("\n---", 1)[0] not in current:
        raise ValueError("Original MIT permission/disclaimer changed")
    return manifest


def analysis_paths(toc):
    data = ast.literal_eval(toc.read_text(encoding="utf-8"))
    found = set()
    def visit(value):
        if isinstance(value, (list, tuple)):
            if len(value) == 3 and all(isinstance(x, str) for x in value) and value[2] in {"PYMODULE", "PYSOURCE", "BINARY", "EXTENSION", "DATA"}:
                found.add(os.path.normcase(os.path.abspath(value[1])))
            else:
                for child in value:
                    visit(child)
    visit(data)
    if not found:
        raise ValueError("Empty PyInstaller inventory")
    return found


def python_licenses(out, toc):
    included = analysis_paths(toc)
    result = []
    for dist in sorted(metadata.distributions(), key=lambda d: d.metadata["Name"].lower()):
        files = list(dist.files or [])
        hits = [f for f in files if os.path.normcase(os.path.abspath(dist.locate_file(f))) in included]
        # Includes the bootloader/runtime exception even if a hook is absent from the analysis.
        if not hits and dist.metadata["Name"].lower() != "pyinstaller":
            continue
        licenses = [f for f in files if legal_file(Path(f)) and Path(dist.locate_file(f)).is_file()]
        if not licenses:
            raise ValueError(f"No original license text for bundled {dist.metadata['Name']}")
        archive = out / "python" / f"{dist.metadata['Name']}-{dist.version}-licenses.zip"
        archive.parent.mkdir(exist_ok=True)
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
            for f in sorted(licenses):
                # Keep original relative names and bytes, with stable zip timestamps.
                name = str(f).replace("\\", "/")
                if ".." in Path(name).parts or Path(name).is_absolute():
                    raise ValueError(f"Unsafe archive path: {name}")
                z.writestr(zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0)), Path(dist.locate_file(f)).read_bytes(), compress_type=zipfile.ZIP_DEFLATED)
        result.append({"name": dist.metadata["Name"], "version": dist.version,
                       "license": dist.metadata.get("License-Expression") or (dist.metadata.get("License") or "See original texts").splitlines()[0][:120],
                       "inventory_hits": len(hits), "archive": archive.relative_to(out).as_posix(),
                       "sha256": sha(archive), "texts": len(licenses)})
    if not {"torch", "numpy", "timestral"}.issubset({p["name"].lower() for p in result}):
        raise ValueError("Core engine packages missing from inventory")
    return result


def dotnet_licenses(out, publish):
    deps_files = list(publish.glob("*.deps.json"))
    if len(deps_files) != 1:
        raise ValueError("Exactly one published deps.json required")
    deps = json.loads(deps_files[0].read_text(encoding="utf-8"))
    cache = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages"))
    rows = []
    for key, lib in sorted(deps["libraries"].items()):
        if lib["type"] == "project":
            continue
        name, version = key.rsplit("/", 1)
        name = name.removeprefix("runtimepack.")
        package = cache / name.lower() / version
        if not package.is_dir():
            raise ValueError(f"Original restored package unavailable: {key}")
        dest = out / "dotnet" / f"{name}-{version}"
        dest.mkdir(parents=True, exist_ok=True)
        for f in package.glob("*.nuspec"):
            shutil.copyfile(f, dest / f.name)
        originals = [p for p in package.rglob("*") if p.is_file() and legal_file(p)]
        if name.startswith("Microsoft.NETCore.App.Runtime."):
            if version != "10.0.13":
                raise ValueError(f"Audit new runtime version before packaging: {version}")
            if not any(p.name == "THIRD-PARTY-NOTICES.TXT" for p in originals):
                raise ValueError(".NET runtime third-party notices missing")
        elif name.startswith("Microsoft.WindowsDesktop.App.Runtime."):
            if version != "10.0.13":
                raise ValueError(f"Audit new desktop runtime version: {version}")
            originals += [ROOT / "licenses/vendor" / x for x in ["wpf-LICENSE.txt", "wpf-NOTICES.txt", "winforms-LICENSE.txt", "winforms-NOTICES.txt"]]
        elif name == "Microsoft.Windows.SDK.NET.Ref" and version == "10.0.19041.57":
            originals += [ROOT / "licenses/vendor/Windows-SDK-license.rtf", ROOT / "licenses/vendor/CsWinRT-LICENSE.txt"]
        elif name.startswith("Microsoft.Data.Sqlite") and version == "10.0.0":
            originals += [ROOT / "licenses/vendor/efcore-LICENSE.txt"]
        elif name.startswith("SQLitePCLRaw.") and version == "2.1.14":
            originals += [ROOT / "licenses/vendor/SQLitePCLRaw-LICENSE.txt"]
            if name == "SQLitePCLRaw.lib.e_sqlite3":
                originals += [ROOT / "licenses/vendor/SQLite-public-domain.html"]
        else:
            raise ValueError(f"Unreviewed bundled .NET component: {key}")
        if not originals:
            raise ValueError(f"No license for {key}")
        for f in originals:
            shutil.copyfile(f, dest / f.name)
        rows.append({"name": name, "version": version, "folder": dest.relative_to(out).as_posix()})
    return rows


def windows_file_version(path):
    import ctypes as c
    version = c.WinDLL("version.dll")
    version.GetFileVersionInfoSizeW.argtypes = [c.c_wchar_p, c.POINTER(c.c_uint)]
    version.GetFileVersionInfoW.argtypes = [c.c_wchar_p, c.c_uint, c.c_uint, c.c_void_p]
    version.VerQueryValueW.argtypes = [c.c_void_p, c.c_wchar_p, c.POINTER(c.c_void_p), c.POINTER(c.c_uint)]
    handle = c.c_uint()
    size = version.GetFileVersionInfoSizeW(str(path), c.byref(handle))
    if not size:
        raise ValueError(f"Missing Microsoft runtime version: {path.name}")
    data = c.create_string_buffer(size)
    if not version.GetFileVersionInfoW(str(path), 0, size, data):
        raise ValueError(f"Cannot read Microsoft runtime version: {path.name}")
    pointer, length = c.c_void_p(), c.c_uint()
    if not version.VerQueryValueW(data, "\\", c.byref(pointer), c.byref(length)) or length.value < 16:
        raise ValueError(f"Invalid Microsoft runtime version: {path.name}")
    fixed = c.cast(pointer, c.POINTER(c.c_uint))
    return (fixed[2] >> 16, fixed[2] & 65535, fixed[3] >> 16, fixed[3] & 65535)


def write_installer_terms(out, native):
    documents = {"LICENSE-MIT.txt", "vendor/Timestral-28M-LICENSE.txt", "Python-LICENSE.txt"}
    for row in native:
        documents.update(p for p in row["license_documents"] if p.endswith(".txt") and p.startswith("vendor/") and "NOTICES" not in p)
    order = ["LICENSE-MIT.txt", "vendor/Timestral-28M-LICENSE.txt", "Python-LICENSE.txt"]
    order += sorted(documents - set(order))
    headers = {
        "en": """BATTERY FORECAST — PROJECT LICENSE AND THIRD-PARTY COMPONENT TERMS

Battery Forecast's project code is MIT-licensed, with original copyright preserved.
It is an independent Windows adaptation by Mattboxx of Till Dusk by Matteo Bonfanti.

By selecting acceptance, you agree to the applicable original terms reproduced below for the
respective third-party components in this installer. These terms apply to their own components;
they do not add restrictions to Battery Forecast's MIT-licensed project code.
The Timestral model weights are under Modified MIT, including the company/employer monthly
revenue condition. Native runtimes have separate supplier agreements. All original documents
and notices are also installed in the licenses folder for offline reading.

Files and their applicable supplier terms:
""",
        "it": """BATTERY FORECAST — LICENZA DEL PROGETTO E TERMINI DEI COMPONENTI

Il codice del progetto Battery Forecast è MIT, con il copyright originale conservato.
È un adattamento Windows indipendente di Mattboxx di Till Dusk, creato da Matteo Bonfanti.

Selezionando l'accettazione, accetti i termini originali applicabili riprodotti di seguito per i
rispettivi componenti di terze parti presenti nell'installer. Si applicano ai propri componenti;
non aggiungono restrizioni al codice MIT del progetto Battery Forecast.
I pesi Timestral hanno una Modified MIT, inclusa la condizione sul fatturato mensile di azienda
o datore di lavoro. I runtime nativi hanno accordi dei fornitori separati. Tutti gli originali
e gli avvisi vengono installati nella cartella licenses e sono leggibili offline.
I testi originali inglesi sono riprodotti integralmente sotto questa introduzione.

File e condizioni dei rispettivi fornitori:
""",
    }
    mapping = "\n".join(f"- {row['path']}: {row['component']}" for row in native)
    body = []
    for name in order:
        data = (out / name).read_bytes()
        try:
            text = data.decode("utf-8-sig")
        except UnicodeDecodeError:
            text = data.decode("cp1252")
        body.append("\n\n===== " + name + " =====\n\n" + text)
    for language, header in headers.items():
        (out / f"INSTALLER-TERMS-{language}.txt").write_text(header + mapping + "".join(body), encoding="utf-8-sig")


def native_inventory(internal, toc, packages):
    """Every frozen DLL/PYD must have an identified source and original legal documents.

    Vendor exceptions take precedence over wheel ownership: a DLL being in torch's RECORD
    does not mean torch's top-level BSD license covers that DLL.
    """
    vendor = json.loads((ROOT / "licenses/native-components.json").read_text(encoding="utf-8"))
    destinations = {}
    def walk(value):
        if isinstance(value, (tuple, list)):
            if len(value) == 3 and all(isinstance(x, str) for x in value) and value[2] in {"BINARY", "EXTENSION"}:
                destinations[value[0].replace("\\", "/").lower()] = Path(value[1])
            else:
                for child in value:
                    walk(child)
    walk(ast.literal_eval(toc.read_text(encoding="utf-8")))
    owners = {}
    archived = {item["name"].lower(): item for item in packages}
    for dist in metadata.distributions():
        item = archived.get(dist.metadata["Name"].lower())
        if item:
            for file in dist.files or []:
                owners[os.path.normcase(os.path.abspath(dist.locate_file(file)))] = item
    rows = []
    for binary in sorted(internal.rglob("*")):
        if not binary.is_file() or binary.suffix.lower() not in {".dll", ".pyd"}:
            continue
        rel = binary.relative_to(internal).as_posix()
        source = destinations.get(rel.lower())
        digest = sha(binary)
        name = binary.name.lower()
        if name == "ucrtbase.dll" or name.startswith("api-ms-win-"):
            raise ValueError(f"OS-supplied Windows runtime must not be bundled: {rel}")
        if name in vendor:
            item = vendor[name]
            if "variants" in item:
                item = item["variants"].get(digest)
                if item is None:
                    raise ValueError(f"Re-audit changed vendor binary: {rel}")
            if "sha256" in item and digest != item["sha256"]:
                raise ValueError(f"Re-audit changed vendor binary: {rel}")
            component, texts = item["component"], item["licenses"]
        elif re.fullmatch(r"(?:vcruntime|msvcp|concrt|vcomp)140[^/]*\.dll", name):
            version = windows_file_version(binary)
            if version[0] != 14:
                raise ValueError(f"Re-audit Microsoft runtime version: {binary.name} {version}")
            year = 2026 if version[1] >= 50 else 2022
            component = "Microsoft Visual C++ runtime " + ".".join(map(str, version))
            texts = ["Python-LICENSE.txt", f"vendor/MSVC-{year}-LICENSE.txt", f"vendor/MSVC-{year}-LICENSE.docx"]
        elif source is None:
            raise ValueError(f"No PyInstaller source for native binary: {rel}")
        elif os.path.normcase(os.path.abspath(source)) in owners:
            item = owners[os.path.normcase(os.path.abspath(source))]
            component, texts = f"{item['name']} {item['version']}", [item["archive"]]
        elif source.resolve().is_relative_to(Path(sys.base_prefix).resolve()):
            component, texts = f"Python {sys.version.split()[0]}", ["Python-LICENSE.txt"]
        else:
            raise ValueError(f"Unreviewed native binary: {rel}")
        rows.append({"path": rel, "sha256": digest, "component": component, "license_documents": texts})
    if not rows:
        raise ValueError("Empty native runtime inventory")
    return rows


def strip_nested(internal, out, packages):
    # Delete only metadata license copies already preserved byte-for-byte in an archive.
    covered = {}
    for item in packages:
        with zipfile.ZipFile(out / item["archive"]) as z:
            for name in z.namelist():
                covered[name] = hashlib.sha256(z.read(name)).hexdigest()
    for folder in internal.glob("*.dist-info/licenses"):
        files = [p for p in folder.rglob("*") if p.is_file()]
        if all(covered.get(p.relative_to(internal).as_posix()) == sha(p) for p in files):
            resolved = folder.resolve()
            if not resolved.is_relative_to(internal.resolve()):
                raise ValueError("License folder escapes engine directory")
            shutil.rmtree(resolved)
        else:
            raise ValueError(f"Cannot remove unarchived legal material: {folder.name}")


def collect(out, internal, publish, toc, inno_license=None):
    verify_sources()
    out.mkdir(parents=True, exist_ok=True)
    shutil.copytree(ROOT / "licenses", out, dirs_exist_ok=True)
    shutil.copyfile(ROOT / "LICENSE", out / "LICENSE-MIT.txt")
    model = publish / "engine/model/LICENSE"
    if not model.is_file() or model.read_text(encoding="utf-8") != (ROOT / "licenses/vendor/Timestral-28M-LICENSE.txt").read_text(encoding="utf-8"):
        raise ValueError("Distributed model license differs from audited revision")
    shutil.copyfile(model, out / "Timestral-28M-weights-LICENSE.txt")
    py_license = Path(sys.base_prefix) / "LICENSE.txt"
    if not py_license.is_file():
        raise ValueError("Original Python license missing")
    shutil.copyfile(py_license, out / "Python-LICENSE.txt")
    if inno_license:
        shutil.copyfile(inno_license, out / "Inno-Setup-LICENSE.txt")
    packages = python_licenses(out, toc)
    dotnet = dotnet_licenses(out, publish)
    native = native_inventory(internal, toc, packages)
    strip_nested(internal, out, packages)
    write_installer_terms(out, native)
    inventory = {"schema": 2, "native_binaries": native, "python_version": sys.version.split()[0], "python": packages, "dotnet": dotnet,
                 "pyinstaller_analysis_sha256": sha(toc), "published_deps_sha256": sha(next(publish.glob("*.deps.json")))}
    lines = ["Battery Forecast — third-party components", "", "Original notices are readable offline in this folder.",
             "Python license archives preserve original relative filenames; open with Windows Explorer.",
             "The MIT license for this project's code does not replace the licenses below.",
             "Till Dusk: Copyright (c) 2026 Matteo Bonfanti. Independent Windows contributions: Mattboxx.",
             "Timestral code: Apache-2.0. Model weights: Modified MIT, including company/employer revenue condition.",
             "See vendor/Timestral-28M-LICENSE.txt and MODEL_INFO.json for the exact terms and revision.", "", "Bundled Python distributions:"]
    lines += [f"- {x['name']} {x['version']}: {x['license']} — {x['archive']}" for x in packages]
    lines += ["", ".NET components (original package metadata and legal texts):"]
    lines += [f"- {x['name']} {x['version']}: {x['folder']}" for x in dotnet]
    lines += ["", "Native runtime components (each DLL/PYD is mapped in inventory.json):"]
    lines += ["- " + component for component in sorted({row["component"] for row in native})]
    lines += ["Microsoft Visual C++ runtime: Python-LICENSE.txt Additional Conditions; version-specific original agreements listed per native binary.",
              "Intel OpenMP and ArmPL, when present, have separate proprietary terms and downstream redistribution obligations.",
              "The installer displays applicable original component terms; unattended installs require explicit acceptance.",
              "Including the texts does not by itself demonstrate recipient agreement to those terms."]
    lines += ["", "Python: Python-LICENSE.txt (includes bundled third-party notices).",
              "Inno Setup: Inno-Setup-LICENSE.txt when an installer is built.",
              "PyInstaller's original license includes the bootloader exception; no change to its source is made.",
              "Donations to Mattboxx support this independent Windows port, not the original authors."]
    (out / "THIRD-PARTY-NOTICES.txt").write_text("\n".join(lines)+"\n", encoding="utf-8")
    inventory["files"] = {p.relative_to(out).as_posix(): sha(p) for p in sorted(out.rglob("*")) if p.is_file() and p.name != "inventory.json"}
    (out / "inventory.json").write_text(json.dumps(inventory, indent=2)+"\n", encoding="utf-8")
    verify_bundle(out)
    print(f"Legal inventory: {len(packages)} Python distributions, {len(dotnet)} .NET components, {len(native)} native engine binaries")


def verify_bundle(out):
    inventory = json.loads((out / "inventory.json").read_text(encoding="utf-8"))
    for rel, expected in inventory["files"].items():
        p = out / rel
        if not p.is_file() or sha(p) != expected:
            raise ValueError(f"Missing/changed legal file: {rel}")
        if p.suffix == ".zip":
            with zipfile.ZipFile(p) as z:
                if z.testzip() or not z.namelist():
                    raise ValueError(f"Invalid legal archive: {rel}")
    required = {"LICENSE-MIT.txt", "Python-LICENSE.txt", "Timestral-28M-weights-LICENSE.txt", "THIRD-PARTY-NOTICES.txt"}
    if not required.issubset(inventory["files"]):
        raise ValueError("Mandatory legal texts absent from inventory")
    if inventory.get("schema", 1) >= 2:
        terms = {"INSTALLER-TERMS-en.txt", "INSTALLER-TERMS-it.txt"}
        if not terms.issubset(inventory["files"]):
            raise ValueError("Mandatory installer component terms absent from inventory")
        natives = inventory.get("native_binaries", [])
        if not natives:
            raise ValueError("Missing native binary inventory")
        internal = out.parent / "engine/_internal"
        for row in natives:
            if not row["license_documents"] or any(text not in inventory["files"] for text in row["license_documents"]):
                raise ValueError(f"Missing native license document: {row['path']}")
            if internal.is_dir() and (not (internal / row["path"]).is_file() or sha(internal / row["path"]) != row["sha256"]):
                raise ValueError(f"Missing/changed native binary: {row['path']}")
        if internal.is_dir():
            actual = {p.relative_to(internal).as_posix() for p in internal.rglob("*") if p.is_file() and p.suffix.lower() in {".dll", ".pyd"}}
            if actual != {row["path"] for row in natives}:
                raise ValueError("Uninventoried native binary in installed engine")
    if not inventory["python"] or not inventory["dotnet"]:
        raise ValueError("Empty runtime inventory")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("out", nargs="?", type=Path)
    parser.add_argument("internal", nargs="?", type=Path)
    parser.add_argument("--publish", type=Path)
    parser.add_argument("--toc", type=Path)
    parser.add_argument("--inno-license", type=Path)
    parser.add_argument("--check-sources", action="store_true")
    parser.add_argument("--verify", action="store_true")
    args = parser.parse_args()
    if args.check_sources:
        verify_sources(); print("Audited originals verified")
    elif args.verify and args.out:
        verify_bundle(args.out); print("Packaged legal inventory verified")
    elif args.out and args.internal and args.publish and args.toc:
        collect(args.out, args.internal, args.publish, args.toc, args.inno_license)
    else:
        parser.error("Supply out, internal, --publish and --toc, or a verification mode")
