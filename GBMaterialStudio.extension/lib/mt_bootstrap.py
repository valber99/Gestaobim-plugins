# -*- coding: utf-8 -*-
"""Loads the extension modules in dependency order, fresh on every button click."""
import importlib.util
import os
import sys

MODULES = [
    'mt_notifications', 'mt_settings', 'mt_hygiene_core', 'mt_wpf', 'mt_dialogs',
    'mt_cursor', 'mt_uihelpers', 'mt_modeless', 'mt_services', 'mt_hygiene', 'mt_library',
    'mt_types', 'mt_favorite_store', 'mt_favorites_legacy', 'mt_favorites',
    'mt_texture_editor', 'mt_editor', 'mt_favorites_browser', 'mt_organizer',
]


def load_all(lib):
    """Put lib on sys.path and (re)execute every module, so edits and updates apply at once."""
    if lib not in sys.path:
        sys.path.insert(0, lib)
    for name in MODULES:
        sys.modules.pop(name, None)
    for name in MODULES:
        spec = importlib.util.spec_from_file_location(name, os.path.join(lib, name + '.py'))
        module = importlib.util.module_from_spec(spec)
        sys.modules[name] = module
        spec.loader.exec_module(module)
