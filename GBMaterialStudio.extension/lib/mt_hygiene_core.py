# -*- coding: utf-8 -*-
"""Pure-Python rules for organizing materials. No Revit dependency.

Runs on IronPython 2.7 inside Revit and on CPython 3 in the test suite.
"""
from __future__ import unicode_literals

import re

CASE_MODES = ('keep', 'title', 'upper', 'lower', 'sentence')
_LOWER_WORDS = frozenset(['de', 'da', 'do', 'das', 'dos', 'e', 'em', 'com', 'para', 'a', 'o'])

STATUS_OK = 'ok'
STATUS_UNCHANGED = 'unchanged'
STATUS_EMPTY = 'empty'
STATUS_CONFLICT = 'conflict'


class RenameRules(object):
    def __init__(self, find='', replace='', use_regex=False, ignore_case=True,
                 prefix='', suffix='', case='keep', collapse_spaces=True):
        if case not in CASE_MODES:
            raise ValueError('Modo de caixa desconhecido: {0}'.format(case))
        self.find = find or ''
        self.replace = replace or ''
        self.use_regex = bool(use_regex)
        self.ignore_case = bool(ignore_case)
        self.prefix = prefix or ''
        self.suffix = suffix or ''
        self.case = case
        self.collapse_spaces = bool(collapse_spaces)


def _title(text):
    words = text.split(' ')
    result = []
    for index, word in enumerate(words):
        lowered = word.lower()
        if index and lowered in _LOWER_WORDS:
            result.append(lowered)
        else:
            result.append(lowered[:1].upper() + lowered[1:])
    return ' '.join(result)


def _apply_case(text, mode):
    if mode == 'upper':
        return text.upper()
    if mode == 'lower':
        return text.lower()
    if mode == 'title':
        return _title(text)
    if mode == 'sentence':
        lowered = text.lower()
        return lowered[:1].upper() + lowered[1:]
    return text


def apply_rules(name, rules):
    """Return the new name. Raises ValueError for an invalid regular expression."""
    text = name or ''
    if rules.find:
        flags = re.IGNORECASE if rules.ignore_case else 0
        try:
            if rules.use_regex:
                text = re.sub(rules.find, rules.replace, text, flags=flags)
            else:
                replacement = rules.replace
                text = re.sub(re.escape(rules.find), lambda match: replacement,
                              text, flags=flags)
        except re.error as error:
            raise ValueError('Expressao regular invalida: {0}'.format(error))
    if rules.collapse_spaces:
        text = re.sub(r'\s+', ' ', text)
    text = _apply_case(text.strip(), rules.case)
    return rules.prefix + text + rules.suffix


class RenameStep(object):
    def __init__(self, key, old, new, status):
        self.key = key
        self.old = old
        self.new = new
        self.status = status


def plan_renames(items, rules, all_names):
    """Plan a batch rename.

    items: list of (key, current_name) being renamed.
    all_names: every material name in the document.
    A name is a conflict when another material that is not part of the batch
    already uses it, or when two renamed items would end up with the same name.
    Comparison ignores case. Names freed by the batch can be reused, so swaps
    and chains are valid; the caller must apply them in two phases.
    """
    moving = set(old.lower() for _, old in items)
    occupied = set(name.lower() for name in all_names) - moving
    proposed = []
    for key, old in items:
        proposed.append((key, old, apply_rules(old, rules)))
    counts = {}
    for _, _, new in proposed:
        counts[new.lower()] = counts.get(new.lower(), 0) + 1
    steps = []
    for key, old, new in proposed:
        if not new.strip():
            status = STATUS_EMPTY
        elif new == old:
            status = STATUS_UNCHANGED
        elif new.lower() in occupied or counts[new.lower()] > 1:
            status = STATUS_CONFLICT
        else:
            status = STATUS_OK
        steps.append(RenameStep(key, old, new, status))
    return steps


class Candidate(object):
    def __init__(self, key, name, signature, usage):
        self.key = key
        self.name = name
        self.signature = signature
        self.usage = usage


class DuplicateGroup(object):
    def __init__(self, primary, duplicates):
        self.primary = primary
        self.duplicates = duplicates


def group_duplicates(candidates):
    """Group candidates sharing a signature.

    The primary of a group is the most used one; ties go to the shortest name,
    then alphabetical order, so the result is stable.
    """
    buckets = {}
    for candidate in candidates:
        buckets.setdefault(candidate.signature, []).append(candidate)
    groups = []
    for members in buckets.values():
        if len(members) < 2:
            continue
        ordered = sorted(members, key=lambda c: (-c.usage, len(c.name), c.name.lower()))
        groups.append(DuplicateGroup(ordered[0], ordered[1:]))
    groups.sort(key=lambda g: g.primary.name.lower())
    return groups
