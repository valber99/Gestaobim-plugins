# -*- coding: utf-8 -*-
import json

import pytest

import mt_settings


@pytest.fixture
def path(tmp_path, monkeypatch):
    target = tmp_path / 'sub' / 'gb-settings.json'
    monkeypatch.setenv('GB_MATERIAL_SETTINGS', str(target))
    return target


def test_defaults_when_file_is_missing(path):
    assert mt_settings.load() == {'library_dir': ''}


def test_save_creates_folder_and_round_trips_unicode(path):
    mt_settings.save(library_dir=u'C:\\Biblioteca Gest\u00e3o')
    assert mt_settings.get('library_dir') == u'C:\\Biblioteca Gest\u00e3o'
    assert json.loads(path.read_text(encoding='utf-8'))['library_dir'].endswith(u'Gest\u00e3o')


def test_unknown_key_is_rejected(path):
    with pytest.raises(KeyError):
        mt_settings.save(nope=1)


def test_corrupt_file_gives_defaults_and_is_left_untouched(path):
    path.parent.mkdir(parents=True)
    path.write_text('{not json', encoding='utf-8')
    assert mt_settings.load() == {'library_dir': ''}
    assert path.read_text(encoding='utf-8') == '{not json'


def test_unknown_keys_in_file_are_ignored(path):
    path.parent.mkdir(parents=True)
    path.write_text(json.dumps({'library_dir': 'X', 'other': 1}), encoding='utf-8')
    assert mt_settings.load() == {'library_dir': 'X'}
