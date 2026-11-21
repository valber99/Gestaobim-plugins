# -*- coding: utf-8 -*-
"""The extension runs on the CPython engine of pyRevit (Python 3.12 inside Revit).

pyrevit.forms and a few IronPython habits do not work there, so reject them early.
"""
import ast
import glob
import os
import re
import xml.etree.ElementTree as ElementTree

import pytest

ROOT = os.path.join(os.path.dirname(__file__), '..', 'GBMaterialStudio.extension')
PY_FILES = sorted(glob.glob(os.path.join(ROOT, '**', '*.py'), recursive=True))
SCRIPTS = sorted(glob.glob(os.path.join(ROOT, '**', 'script.py'), recursive=True))
XAML_FILES = sorted(glob.glob(os.path.join(ROOT, '**', '*.xaml'), recursive=True))


def rel(path):
    return os.path.relpath(path, ROOT)


def read(path):
    with open(path, 'r', encoding='utf-8') as stream:
        return stream.read()


@pytest.mark.parametrize('path', PY_FILES, ids=rel)
def test_file_parses_as_python_312(path):
    ast.parse(read(path), path, feature_version=(3, 12))


@pytest.mark.parametrize('path', PY_FILES, ids=rel)
def test_no_ironpython_or_unsupported_pyrevit_apis(path):
    tree = ast.parse(read(path), path)
    found = []
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            found += [a.name for a in node.names if a.name in ('imp', 'pyrevit.forms')]
        elif isinstance(node, ast.ImportFrom):
            if node.module == 'pyrevit.forms' or (
                    node.module == 'pyrevit' and any(a.name == 'forms' for a in node.names)):
                found.append('from pyrevit import forms')
            if node.module == '__future__':
                found.append('from __future__')
        elif isinstance(node, ast.Name) and node.id in ('basestring', 'unicode', 'unichr', 'xrange', 'long'):
            found.append(node.id)
    assert found == []


@pytest.mark.parametrize('path', SCRIPTS, ids=rel)
def test_button_scripts_select_the_cpython_engine(path):
    first = read(path).splitlines()[0]
    assert first.strip() == '#! python3'


@pytest.mark.parametrize('path', SCRIPTS, ids=rel)
def test_button_scripts_do_not_exit_the_interpreter(path):
    text = read(path)
    assert 'sys.exit' not in text and 'exitscript' not in text


@pytest.mark.parametrize('path', XAML_FILES, ids=rel)
def test_xaml_is_well_formed(path):
    ElementTree.parse(path)


def test_every_module_in_the_bootstrap_list_exists_and_every_module_is_listed():
    lib = os.path.join(ROOT, 'lib')
    bootstrap = read(os.path.join(lib, 'mt_bootstrap.py'))
    listed = set(re.findall(r"'(mt_[a-z_]+)'", bootstrap)) - {'mt_bootstrap'}
    on_disk = set(os.path.splitext(os.path.basename(p))[0]
                  for p in glob.glob(os.path.join(lib, 'mt_*.py'))) - {'mt_bootstrap'}
    assert listed == on_disk


def test_there_are_files_to_check():
    assert len(PY_FILES) > 15 and len(SCRIPTS) == 5 and len(XAML_FILES) == 4
