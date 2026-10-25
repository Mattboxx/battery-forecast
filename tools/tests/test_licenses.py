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
