# -*- coding: utf-8 -*-
"""The extension runs on IronPython 2.7 inside Revit. Reject syntax it cannot parse."""
import ast
import glob
import os

import pytest

ROOT = os.path.join(os.path.dirname(__file__), '..', 'GBMaterialStudio.extension')
FILES = sorted(glob.glob(os.path.join(ROOT, '**', '*.py'), recursive=True))
ASYNC_NODES = (ast.Nonlocal, ast.AsyncFunctionDef, ast.Await, ast.AsyncFor, ast.AsyncWith)


def violations(tree):
    found = []
    for node in ast.walk(tree):
        line = getattr(node, 'lineno', 0)
        if isinstance(node, ast.JoinedStr):
            found.append((line, 'f-string'))
        elif isinstance(node, ast.AnnAssign):
            found.append((line, 'variable annotation'))
        elif isinstance(node, ast.NamedExpr):
            found.append((line, 'walrus operator'))
        elif isinstance(node, ASYNC_NODES):
            found.append((line, type(node).__name__))
        elif isinstance(node, ast.YieldFrom):
            found.append((line, 'yield from'))
        elif isinstance(node, ast.Raise) and node.cause is not None:
            found.append((line, 'raise from'))
        elif isinstance(node, (ast.FunctionDef, ast.Lambda)):
            args = node.args
            if args.kwonlyargs or args.posonlyargs:
                found.append((line, 'keyword-only or positional-only arguments'))
            if isinstance(node, ast.FunctionDef):
                every = args.args + args.kwonlyargs + [a for a in (args.vararg, args.kwarg) if a]
                if node.returns is not None or any(a.annotation is not None for a in every):
                    found.append((line, 'type annotation'))
        elif isinstance(node, (ast.List, ast.Tuple, ast.Set)) and any(
                isinstance(e, ast.Starred) for e in node.elts) and isinstance(
                    getattr(node, 'ctx', ast.Load()), ast.Load):
            found.append((line, 'unpacking inside a literal'))
        elif isinstance(node, ast.Dict) and any(key is None for key in node.keys):
            found.append((line, '** unpacking inside a dict literal'))
    return found


@pytest.mark.parametrize('path', FILES, ids=lambda p: os.path.relpath(p, ROOT))
def test_file_uses_only_python_27_syntax(path):
    with open(path, 'rb') as stream:
        tree = ast.parse(stream.read(), path)
    assert violations(tree) == []


def test_there_are_python_files_to_check():
    assert len(FILES) > 10
