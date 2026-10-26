import importlib.util
import json
from pathlib import Path
import zipfile
import pytest

spec = importlib.util.spec_from_file_location("collect_licenses", Path(__file__).parents[1] / "licenses.py")
legal = importlib.util.module_from_spec(spec)
spec.loader.exec_module(legal)


def test_audited_originals_and_exact_original_copyright():
    assert len(legal.verify_sources()) >= 10


def test_missing_or_changed_original_stops_build(tmp_path, monkeypatch):
    (tmp_path / "licenses").mkdir()
    (tmp_path / "licenses/sources.json").write_text(json.dumps([{"file":"missing.txt","sha256":"bad"}]))
    monkeypatch.setattr(legal, "ROOT", tmp_path)
    with pytest.raises(ValueError, match="missing or changed"):
        legal.verify_sources()


def test_missing_packaged_file_stops_verification(tmp_path):
    (tmp_path / "inventory.json").write_text(json.dumps({"files":{"missing.txt":"bad"}}))
    with pytest.raises(ValueError, match="Missing/changed"):
        legal.verify_bundle(tmp_path)


def test_corrupt_archive_stops_verification(tmp_path):
    archive = tmp_path / "legal.zip"
    archive.write_bytes(b"not a zip archive")
    (tmp_path / "inventory.json").write_text(json.dumps({"files":{"legal.zip":legal.sha(archive)}}))
    with pytest.raises(zipfile.BadZipFile):
        legal.verify_bundle(tmp_path)


def test_analysis_uses_module_binary_and_metadata_sources(tmp_path):
    toc = tmp_path / "Analysis-00.toc"
    toc.write_text(repr(([('module', 'module.py', 'PYMODULE'), ('native', 'native.dll', 'BINARY')],)),encoding="utf-8")
    assert len(legal.analysis_paths(toc)) == 2


def test_native_unknown_source_stops_packaging(tmp_path, monkeypatch):
    root=tmp_path/"repo"; (root/"licenses").mkdir(parents=True)
    (root/"licenses/native-components.json").write_text("{}")
    internal=tmp_path/"engine";internal.mkdir();(internal/"unknown.dll").write_bytes(b"native")
    source=tmp_path/"outside";source.mkdir();(source/"unknown.dll").write_bytes(b"native")
    toc=tmp_path/"Analysis-00.toc"
    toc.write_text(repr(([('unknown.dll',str(source/'unknown.dll'),'BINARY')],)),encoding="utf-8")
    monkeypatch.setattr(legal,"ROOT",root)
    monkeypatch.setattr(legal.metadata,"distributions",lambda:[])
    with pytest.raises(ValueError,match="Unreviewed native"):
        legal.native_inventory(internal,toc,[])


@pytest.mark.parametrize("name", ["libiomp5md.dll", "torch_cpu.dll"])
def test_changed_vendor_binary_requires_new_review(tmp_path, name):
    internal=tmp_path/"engine";internal.mkdir();(internal/name).write_bytes(b"changed vendor runtime")
    toc=tmp_path/"Analysis-00.toc";toc.write_text(repr(([],)),encoding="utf-8")
    with pytest.raises(ValueError,match="Re-audit changed vendor binary"):
        legal.native_inventory(internal,toc,[])


def test_windows_os_contracts_are_not_shipped(tmp_path):
    internal=tmp_path/"engine";internal.mkdir();(internal/"ucrtbase.dll").write_bytes(b"os runtime")
    toc=tmp_path/"Analysis-00.toc";toc.write_text(repr(([],)),encoding="utf-8")
    with pytest.raises(ValueError,match="OS-supplied Windows runtime"):
        legal.native_inventory(internal,toc,[])


def test_installer_terms_preserve_applicable_original_texts(tmp_path):
    originals = {
        "LICENSE-MIT.txt": "Original project MIT grant\n",
        "Python-LICENSE.txt": "Python original and Additional Conditions\n",
        "vendor/Timestral-28M-LICENSE.txt": "Model original revenue condition\n",
        "vendor/Intel-OpenMP-EULA.txt": "Intel original agreement\n",
    }
    for name, text in originals.items():
        path = tmp_path / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
    legal.write_installer_terms(tmp_path, [{"path": "torch/lib/libiomp5md.dll", "component": "Intel OpenMP", "license_documents": ["vendor/Intel-OpenMP-EULA.txt"]}])
    for lang in ("en", "it"):
        text = (tmp_path / f"INSTALLER-TERMS-{lang}.txt").read_text(encoding="utf-8-sig")
        assert all(original in text for original in originals.values())
        assert "torch/lib/libiomp5md.dll: Intel OpenMP" in text
        assert "ArmPL" not in text


def test_schema2_requires_both_installer_term_documents(tmp_path):
    files = {}
    for name in ("LICENSE-MIT.txt", "Python-LICENSE.txt", "Timestral-28M-weights-LICENSE.txt", "THIRD-PARTY-NOTICES.txt"):
        path = tmp_path / name
        path.write_text("original text")
        files[name] = legal.sha(path)
    (tmp_path / "inventory.json").write_text(json.dumps({"schema": 2, "files": files}))
    with pytest.raises(ValueError, match="Mandatory installer component terms"):
        legal.verify_bundle(tmp_path)
