# -*- coding: utf-8 -*-
import json
import os

import pytest

import mt_favorite_store as store


def empty():
    return {'version': 4, 'favorites': []}


def backups(folder):
    return [n for n in os.listdir(str(folder)) if '.bak.' in n]


def test_missing_file_uses_default_factory(tmp_path):
    assert store.load(str(tmp_path / 'f.json'), empty) == empty()


def test_mutate_creates_file_without_backup(tmp_path):
    path = str(tmp_path / 'f.json')
    store.mutate(path, empty, lambda db: db['favorites'].append({'id': '1'}))
    assert store.load(path, empty)['favorites'] == [{'id': '1'}]
    assert not backups(tmp_path)


def test_second_mutation_keeps_a_backup_of_the_previous_content(tmp_path):
    path = str(tmp_path / 'f.json')
    store.mutate(path, empty, lambda db: db['favorites'].append({'id': '1'}))
    store.mutate(path, empty, lambda db: db['favorites'].append({'id': '2'}))
    found = backups(tmp_path)
    assert len(found) == 1
    with open(os.path.join(str(tmp_path), found[0])) as stream:
        assert json.load(stream)['favorites'] == [{'id': '1'}]


def test_corrupt_database_raises_and_is_never_replaced(tmp_path):
    path = tmp_path / 'f.json'
    path.write_text('{broken', encoding='utf-8')
    with pytest.raises(store.StoreError):
        store.load(str(path), empty)
    with pytest.raises(store.StoreError):
        store.mutate(str(path), empty, lambda db: None)
    assert path.read_text(encoding='utf-8') == '{broken'


def test_newer_version_is_refused(tmp_path):
    path = tmp_path / 'f.json'
    path.write_text(json.dumps({'version': 5, 'favorites': []}), encoding='utf-8')
    with pytest.raises(store.StoreError):
        store.load(str(path), empty)


@pytest.mark.parametrize('payload', [
    [], {'favorites': {}}, {'favorites': [1]}, {'favorites': [], 'version': -1},
    {'favorites': [], 'version': True}, {'favorites': [], 'version': 1.5},
])
def test_invalid_shapes_are_rejected(tmp_path, payload):
    path = tmp_path / 'f.json'
    path.write_text(json.dumps(payload), encoding='utf-8')
    with pytest.raises(store.StoreError):
        store.load(str(path), empty)


def test_non_finite_numbers_are_rejected(tmp_path):
    path = tmp_path / 'f.json'
    path.write_text('{"favorites": [{"x": NaN}]}', encoding='utf-8')
    with pytest.raises(store.StoreError):
        store.load(str(path), empty)


def test_failing_callback_writes_nothing(tmp_path):
    path = str(tmp_path / 'f.json')
    store.mutate(path, empty, lambda db: db['favorites'].append({'id': '1'}))
    with open(path, 'rb') as stream:
        before = stream.read()

    def boom(db):
        db['favorites'].append({'id': '2'})
        raise RuntimeError('boom')

    with pytest.raises(RuntimeError):
        store.mutate(path, empty, boom)
    with open(path, 'rb') as stream:
        assert stream.read() == before
    assert not [n for n in os.listdir(str(tmp_path)) if n.endswith('.tmp')]


def test_callback_producing_an_invalid_database_is_not_written(tmp_path):
    path = str(tmp_path / 'f.json')
    store.mutate(path, empty, lambda db: None)
    with open(path, 'rb') as stream:
        before = stream.read()
    with pytest.raises(store.StoreError):
        store.mutate(path, empty, lambda db: db.__setitem__('favorites', 'oops'))
    with open(path, 'rb') as stream:
        assert stream.read() == before


def test_held_lock_times_out_and_is_not_stolen(tmp_path, monkeypatch):
    path = str(tmp_path / 'f.json')
    lock = path + '.lock'
    with open(lock, 'wb') as stream:
        stream.write(b'someone-else')
    monkeypatch.setattr(store, 'LOCK_TIMEOUT_SECONDS', 0.1)
    with pytest.raises(store.LockTimeoutError):
        store.mutate(path, empty, lambda db: None)
    with open(lock, 'rb') as stream:
        assert stream.read() == b'someone-else'


def test_lock_is_released_after_a_mutation(tmp_path):
    path = str(tmp_path / 'f.json')
    store.mutate(path, empty, lambda db: None)
    assert not os.path.exists(path + '.lock')
