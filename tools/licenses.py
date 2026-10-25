"""Offline legal inventory of the published app and PyInstaller analysis.

Original texts only. Missing notices, unaudited .NET versions and corrupt license
archives fail packaging. See docs/LICENSE_AUDIT.md for evidence and limitations.
"""
import argparse
import ast
import hashlib
import json
import os
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
    strip_nested(internal, out, packages)
    inventory = {"schema": 1, "python_version": sys.version.split()[0], "python": packages, "dotnet": dotnet,
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
    lines += ["", "Python: Python-LICENSE.txt (includes bundled third-party notices).",
              "Inno Setup: Inno-Setup-LICENSE.txt when an installer is built.",
              "PyInstaller's original license includes the bootloader exception; no change to its source is made.",
              "Donations to Mattboxx support this independent Windows port, not the original authors."]
    (out / "THIRD-PARTY-NOTICES.txt").write_text("\n".join(lines)+"\n", encoding="utf-8")
    inventory["files"] = {p.relative_to(out).as_posix(): sha(p) for p in sorted(out.rglob("*")) if p.is_file() and p.name != "inventory.json"}
    (out / "inventory.json").write_text(json.dumps(inventory, indent=2)+"\n", encoding="utf-8")
    verify_bundle(out)
    print(f"Legal inventory: {len(packages)} Python distributions, {len(dotnet)} .NET components")


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
