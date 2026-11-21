# -*- coding: utf-8 -*-
import pytest

import mt_hygiene_core as core


def rules(**kwargs):
    return core.RenameRules(**kwargs)


def test_plain_find_replace_is_literal_and_ignores_case():
    assert core.apply_rules('Concreto (Cinza)', rules(find='(cinza)', replace='\\1')) == 'Concreto \\1'


def test_regex_find_replace_supports_groups():
    r = rules(find=r'^(\w+) (\d+)$', replace=r'\2-\1', use_regex=True)
    assert core.apply_rules('Grama 12', r) == '12-Grama'


def test_invalid_regex_raises_value_error():
    with pytest.raises(ValueError):
        core.apply_rules('x', rules(find='(', use_regex=True))


def test_collapses_spaces_and_trims():
    assert core.apply_rules('  Tinta   Branca ', rules()) == 'Tinta Branca'


def test_prefix_and_suffix_wrap_the_result():
    assert core.apply_rules('Tinta', rules(prefix='GB - ', suffix=' v2')) == 'GB - Tinta v2'


@pytest.mark.parametrize('mode,expected', [
    ('upper', 'TINTA DE PAREDE'),
    ('lower', 'tinta de parede'),
    ('title', 'Tinta de Parede'),
    ('sentence', 'Tinta de parede'),
    ('keep', 'tINTA dE pAREDE'),
])
def test_case_modes(mode, expected):
    assert core.apply_rules('tINTA dE pAREDE', rules(case=mode)) == expected


def test_title_case_keeps_connectors_lowercase_but_not_the_first_word():
    assert core.apply_rules('de porcelanato da obra', rules(case='title')) == 'De Porcelanato da Obra'


def test_unknown_case_mode_is_rejected():
    with pytest.raises(ValueError):
        rules(case='shout')


def statuses(items, r, all_names):
    return [s.status for s in core.plan_renames(items, r, all_names)]


def test_unchanged_when_rules_do_nothing():
    assert statuses([(1, 'A')], rules(), ['A', 'B']) == [core.STATUS_UNCHANGED]


def test_conflict_with_a_material_outside_the_batch():
    assert statuses([(1, 'A')], rules(find='A', replace='B'), ['A', 'B']) == [core.STATUS_CONFLICT]


def test_conflict_ignores_case():
    assert statuses([(1, 'A')], rules(find='A', replace='b'), ['A', 'B']) == [core.STATUS_CONFLICT]


def test_two_items_ending_with_the_same_name_conflict():
    r = rules(find=r'\d+', replace='', use_regex=True)
    assert statuses([(1, 'Grama 1'), (2, 'Grama 2')], r, ['Grama 1', 'Grama 2']) == \
        [core.STATUS_CONFLICT, core.STATUS_CONFLICT]


def test_name_freed_by_the_batch_can_be_reused():
    plan = core.plan_renames([(1, 'A'), (2, 'B')], rules(find='B', replace='C', ignore_case=False), ['A', 'B'])
    assert [s.status for s in plan] == [core.STATUS_UNCHANGED, core.STATUS_OK]
    chain = core.plan_renames([(1, 'A'), (2, 'B')], rules(prefix='x'), ['A', 'B', 'xA'])
    assert [s.status for s in chain] == [core.STATUS_CONFLICT, core.STATUS_OK]


def test_empty_result_is_reported():
    assert statuses([(1, 'X')], rules(find='X', replace=''), ['X']) == [core.STATUS_EMPTY]


def candidate(key, name, signature, usage):
    return core.Candidate(key, name, signature, usage)


def test_duplicate_groups_pick_the_most_used_as_primary():
    groups = core.group_duplicates([
        candidate(1, 'Grama 2', 'verde', 0),
        candidate(2, 'Grama', 'verde', 5),
        candidate(3, 'Concreto', 'cinza', 9),
    ])
    assert len(groups) == 1
    assert groups[0].primary.name == 'Grama'
    assert [d.name for d in groups[0].duplicates] == ['Grama 2']


def test_primary_tie_goes_to_shortest_then_alphabetical_name():
    groups = core.group_duplicates([
        candidate(1, 'Tinta B', 's', 1), candidate(2, 'Tinta', 's', 1), candidate(3, 'Tinta A', 's', 1)])
    assert groups[0].primary.name == 'Tinta'
    assert [d.name for d in groups[0].duplicates] == ['Tinta A', 'Tinta B']


def test_no_duplicates_means_no_groups():
    assert core.group_duplicates([candidate(1, 'A', 'x', 0), candidate(2, 'B', 'y', 0)]) == []
