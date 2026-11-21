# -*- coding: utf-8 -*-
"""User settings stored as JSON. No Revit dependency.

An unreadable settings file is never overwritten: defaults are returned and the
file is left for the user to inspect.
"""
import io
import json
import os

DEFAULTS = {'library_dir': ''}


def base_dir():
    return os.path.join(os.environ.get('APPDATA', os.path.expanduser('~')),
                        'pyRevit', 'MaterialTools')


def settings_path():
    return os.environ.get('GB_MATERIAL_SETTINGS') or os.path.join(base_dir(), 'gb-settings.json')


def load():
    data = dict(DEFAULTS)
    try:
        with io.open(settings_path(), 'r', encoding='utf-8-sig') as stream:
            stored = json.load(stream)
    except (IOError, OSError, ValueError):
        return data
    if isinstance(stored, dict):
        for key in DEFAULTS:
            if key in stored:
                data[key] = stored[key]
    return data


def get(key):
    return load()[key]


def save(**changes):
    for key in changes:
        if key not in DEFAULTS:
            raise KeyError(key)
    path = settings_path()
    folder = os.path.dirname(path)
    if folder and not os.path.isdir(folder):
        os.makedirs(folder)
    data = load()
    data.update(changes)
    text = json.dumps(data, ensure_ascii=False, indent=2)
    if isinstance(text, bytes):
        text = text.decode('utf-8')
    with io.open(path, 'w', encoding='utf-8') as stream:
        stream.write(text)
    return data
