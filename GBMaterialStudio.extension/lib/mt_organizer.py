# -*- coding: utf-8 -*-
"""Organize materials: see usage and duplicates, delete unused, rename in batch."""
import os

import clr
clr.AddReference('PresentationCore')
import mt_dialogs
import mt_wpf
from System.Windows.Input import Cursors

import mt_hygiene
import mt_hygiene_core as core
from mt_uihelpers import attach_to_revit

TITLE = u'Gestão BIM | Organizar materiais'
_STATE_TEXT = {
    core.STATUS_OK: u'ok',
    core.STATUS_UNCHANGED: u'igual',
    core.STATUS_EMPTY: u'vazio',
    core.STATUS_CONFLICT: u'conflito',
}


def show_organizer(doc):
    window = mt_wpf.Window(os.path.join(os.path.dirname(__file__), 'OrganizeWindow.xaml'))
    state = {'rows': [], 'visible': []}

    def say(text):
        window.TxtStatus.Text = text

    def summary():
        rows = state['rows']
        unused = len([r for r in rows if r.Usage == 0])
        duplicated = len([r for r in rows if r.DuplicateOf])
        window.TxtSummary.Text = u'{0} materiais  |  {1} nao usados  |  {2} duplicados'.format(
            len(rows), unused, duplicated)

    def apply_filter(sender=None, args=None):
        text = (window.Search.Text or u'').strip().lower()
        mode = window.CmbFilter.SelectedIndex
        visible = []
        for row in state['rows']:
            if mode == 1 and row.Usage != 0:
                continue
            if mode == 2 and not row.DuplicateOf:
                continue
            haystack = u'{0} {1} {2}'.format(row.Name, row.MaterialClass, row.DuplicateOf).lower()
            if text and text not in haystack:
                continue
            visible.append(row)
        state['visible'] = visible
        window.Rows.ItemsSource = mt_wpf.net_list(visible)

    def load(sender=None, args=None):
        window.Cursor = Cursors.Wait
        try:
            state['rows'] = mt_hygiene.analyze(doc)
        except Exception as error:
            state['rows'] = []
            mt_dialogs.alert(u'Nao foi possivel analisar os materiais.\n\n{0}'.format(error), title=TITLE)
        finally:
            window.Cursor = None
        summary()
        apply_filter()
        say(u'')

    def selected_rows():
        return list(window.Rows.SelectedItems)

    def select_where(predicate):
        window.Rows.SelectedItems.Clear()
        count = 0
        for row in state['visible']:
            if predicate(row):
                window.Rows.SelectedItems.Add(row)
                count += 1
        say(u'{0} selecionado(s).'.format(count))

    def on_delete(sender, args):
        chosen = selected_rows()
        removable = [r for r in chosen if r.Usage == 0]
        if not removable:
            say(u'Selecione materiais "nao usados". Os em uso nunca sao excluidos.')
            return
        skipped = len(chosen) - len(removable)
        question = u'Excluir {0} material(is) nao usado(s) e as aparencias que ninguem mais usa?'.format(len(removable))
        if skipped:
            question += u'\n{0} selecionado(s) em uso serao mantidos.'.format(skipped)
        question += u'\n\nVoce pode desfazer com Ctrl+Z.'
        if not mt_dialogs.alert(question, title=TITLE, yes=True, no=True):
            return
        try:
            deleted, failures, assets = mt_hygiene.delete_unused(doc, removable)
        except Exception as error:
            mt_dialogs.alert(u'Nada foi excluido.\n\n{0}'.format(error), title=TITLE)
            return
        load()
        message = u'{0} material(is) e {1} aparencia(s) excluidos.'.format(len(deleted), assets)
        if failures:
            message += u' {0} mantido(s): {1}'.format(len(failures), failures[0])
        say(message)

    def build_rules():
        return core.RenameRules(
            find=window.TxtFind.Text,
            replace=window.TxtReplace.Text,
            use_regex=bool(window.ChkRegex.IsChecked),
            prefix=window.TxtPrefix.Text,
            suffix=window.TxtSuffix.Text,
            case=core.CASE_MODES[max(0, window.CmbCase.SelectedIndex)],
        )

    def plan():
        targets = selected_rows() or list(state['visible'])
        if not targets:
            return [], []
        steps = core.plan_renames([(r.Key, r.Name) for r in targets], build_rules(),
                                  [r.Name for r in state['rows']])
        by_key = dict((r.Key, r) for r in targets)
        for step in steps:
            row = by_key[step.key]
            row.NewName = step.new if step.status != core.STATUS_UNCHANGED else u''
            row.State = _STATE_TEXT[step.status]
        window.Rows.Items.Refresh()
        return targets, steps

    def on_preview(sender, args):
        try:
            targets, steps = plan()
        except ValueError as error:
            mt_dialogs.alert(u'{0}'.format(error), title=TITLE)
            return
        ok = len([s for s in steps if s.status == core.STATUS_OK])
        conflicts = len([s for s in steps if s.status == core.STATUS_CONFLICT])
        say(u'{0} para renomear, {1} em conflito (nome ja existe), {2} sem mudanca.'.format(
            ok, conflicts, len(steps) - ok - conflicts))

    def on_apply_rename(sender, args):
        try:
            targets, steps = plan()
        except ValueError as error:
            mt_dialogs.alert(u'{0}'.format(error), title=TITLE)
            return
        ready = [s for s in steps if s.status == core.STATUS_OK]
        if not ready:
            say(u'Nada para renomear. Confira a pre-visualizacao.')
            return
        conflicts = len([s for s in steps if s.status == core.STATUS_CONFLICT])
        question = u'Renomear {0} material(is)?'.format(len(ready))
        if conflicts:
            question += u'\n{0} em conflito serao ignorados.'.format(conflicts)
        question += u'\n\nVoce pode desfazer com Ctrl+Z.'
        if not mt_dialogs.alert(question, title=TITLE, yes=True, no=True):
            return
        try:
            renamed = mt_hygiene.rename(doc, steps, state['rows'])
        except Exception as error:
            mt_dialogs.alert(u'Nada foi renomeado.\n\n{0}'.format(error), title=TITLE)
            return
        load()
        say(u'{0} material(is) renomeado(s).'.format(renamed))

    window.Search.TextChanged += apply_filter
    window.CmbFilter.SelectionChanged += apply_filter
    window.BtnRefresh.Click += load
    window.BtnSelectUnused.Click += lambda s, a: select_where(lambda r: r.Usage == 0)
    window.BtnSelectDuplicates.Click += lambda s, a: select_where(lambda r: bool(r.DuplicateOf))
    window.BtnDelete.Click += on_delete
    window.BtnPreview.Click += on_preview
    window.BtnApplyRename.Click += on_apply_rename
    window.BtnClose.Click += lambda s, a: window.Close()
    load()
    attach_to_revit(window)
    window.ShowDialog()
