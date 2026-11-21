# -*- coding: utf-8 -*-
"""Loads the extension modules in dependency order, fresh on every button click."""
import imp
import os
import sys

MODULES = [
    'mt_notifications', 'mt_cursor', 'mt_settings', 'mt_hygiene_core', 'mt_uihelpers',
    'mt_modeless', 'mt_services', 'mt_hygiene', 'mt_library', 'mt_types',
    'mt_favorite_store', 'mt_favorites_legacy', 'mt_favorites', 'mt_texture_editor',
    'mt_editor', 'mt_favorites_browser', 'mt_organizer',
]


def load_all(lib):
    """Put lib on sys.path and (re)load every module."""
    if lib not in sys.path:
        sys.path.insert(0, lib)
    for name in MODULES:
        imp.load_source(name, os.path.join(lib, name + '.py'))
