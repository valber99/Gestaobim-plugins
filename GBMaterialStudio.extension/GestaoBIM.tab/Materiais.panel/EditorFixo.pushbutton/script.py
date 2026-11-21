# -*- coding: utf-8 -*-
__persistentengine__ = True
import imp
import os
import sys
from Autodesk.Revit.Exceptions import OperationCanceledException
from pyrevit import revit, UI, forms

lib = os.path.join(os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..', '..')), 'lib')
imp.load_source('mt_bootstrap', os.path.join(lib, 'mt_bootstrap.py')).load_all(lib)
import mt_cursor
import mt_editor
import mt_services

TITLE = u'Gestão BIM | Editor fixo'

uidoc = revit.uidoc
if uidoc is None:
    forms.alert('Abra um documento e selecione uma face com material.', title=TITLE, exitscript=True)
doc = uidoc.Document
try:
    reference = mt_cursor.pick_face(uidoc, UI.Selection.ObjectType.Face,
        mt_services.MaterialFaceFilter(doc), 'Selecione uma face para editar o material')
except OperationCanceledException:
    sys.exit()
except Exception as error:
    forms.alert('Nao foi possivel selecionar a face.\n\n{0}'.format(error),
        title=TITLE, exitscript=True)
material = mt_services.get_material_from_face(doc, reference)
if material is None:
    forms.alert('A face selecionada nao possui um material editavel.', title=TITLE, exitscript=True)
try:
    mt_editor.show_material_editor(doc, uidoc, material,
        os.path.join(lib, 'MaterialEditorWindow.xaml'), modeless=True)
except Exception as error:
    forms.alert('Nao foi possivel abrir o editor de materiais.\n\n{0}'.format(error),
        title=TITLE)
