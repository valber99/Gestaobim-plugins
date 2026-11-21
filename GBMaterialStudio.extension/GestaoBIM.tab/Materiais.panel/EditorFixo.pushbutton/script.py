#! python3
# -*- coding: utf-8 -*-
import importlib.util
import os

LIB = os.path.join(os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..', '..')), 'lib')


def load_extension():
    spec = importlib.util.spec_from_file_location('mt_bootstrap', os.path.join(LIB, 'mt_bootstrap.py'))
    bootstrap = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(bootstrap)
    bootstrap.load_all(LIB)

def main(modeless):
    load_extension()
    from Autodesk.Revit.Exceptions import OperationCanceledException
    from pyrevit import UI, revit
    import mt_cursor
    import mt_dialogs
    import mt_editor
    import mt_services

    title = u'Gest\u00e3o BIM | ' + (u'Editor fixo' if modeless else u'Editar material')
    uidoc = revit.uidoc
    if uidoc is None:
        mt_dialogs.alert(u'Abra um documento e selecione uma face com material.', title=title)
        return
    doc = uidoc.Document
    try:
        reference = mt_cursor.pick_face(
            uidoc, UI.Selection.ObjectType.Face, mt_services.MaterialFaceFilter(doc),
            u'Selecione uma face para editar o material')
    except OperationCanceledException:
        return
    except Exception as error:
        mt_dialogs.alert(u'Nao foi possivel selecionar a face.\n\n{0}'.format(error), title=title)
        return
    material = mt_services.get_material_from_face(doc, reference)
    if material is None:
        mt_dialogs.alert(u'A face selecionada nao possui um material editavel.', title=title)
        return
    try:
        outcome = mt_editor.show_material_editor(
            doc, uidoc, material, os.path.join(LIB, 'MaterialEditorWindow.xaml'), modeless=modeless)
        if not modeless and outcome and outcome.get('open_native'):
            command = UI.RevitCommandId.LookupPostableCommandId(UI.PostableCommand.Materials)
            if command is not None:
                uidoc.Application.PostCommand(command)
    except Exception as error:
        mt_dialogs.alert(u'Nao foi possivel abrir o editor de materiais.\n\n{0}'.format(error), title=title)

main(modeless=True)
