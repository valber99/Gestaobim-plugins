# -*- coding: utf-8 -*-
import imp
import os
import sys
from pyrevit import DB, revit, forms

lib = os.path.join(os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..', '..')), 'lib')
imp.load_source('mt_bootstrap', os.path.join(lib, 'mt_bootstrap.py')).load_all(lib)
import mt_favorites
import mt_types
from mt_uihelpers import element_name

TITLE = u'Gestão BIM | Aplicar material em tipo'
STAR = u'★ '

doc = revit.doc
if doc is None or doc.IsFamilyDocument:
    forms.alert('Abra um projeto para aplicar materiais em tipos.', title=TITLE, exitscript=True)

selected = list(revit.uidoc.Selection.GetElementIds())
element_types = mt_types.types_of_elements(doc, selected) if selected else []
if not element_types:
    available = dict(
        (u'{0} : {1}'.format(t.Category.Name if t.Category else u'', element_name(t)), t)
        for t in mt_types.all_compound_types(doc))
    if not available:
        forms.alert('O projeto nao tem tipos de parede, piso, cobertura ou forro.',
                    title=TITLE, exitscript=True)
    names = forms.SelectFromList.show(
        sorted(available, key=lambda s: s.lower()), multiselect=True, title=TITLE,
        button_name='Escolher camadas')
    if not names:
        sys.exit()
    element_types = [available[n] for n in names]

layers = dict((row.Display, row) for row in mt_types.layer_rows(doc, element_types))
chosen_names = forms.SelectFromList.show(
    sorted(layers, key=lambda s: s.lower()), multiselect=True,
    title=u'Camadas que receberao o material', button_name='Escolher material')
if not chosen_names:
    sys.exit()
chosen_rows = [layers[n] for n in chosen_names]

choices = {}
for material in DB.FilteredElementCollector(doc).OfClass(DB.Material):
    choices[material.Name] = ('material', material)
try:
    for favorite in mt_favorites.list_favorites():
        choices[u'{0}{1}  (favorito)'.format(STAR, favorite.Name)] = ('favorite', favorite.data)
except Exception:
    pass
picked = forms.SelectFromList.show(
    sorted(choices, key=lambda s: s.lower()), multiselect=False,
    title=u'Material de destino', button_name='Aplicar')
if not picked:
    sys.exit()

kind, value = choices[picked]
if kind == 'favorite':
    try:
        target = mt_favorites.get_or_create_favorite_material(doc, value)
    except Exception as error:
        forms.alert(u'Nao foi possivel carregar o favorito.\n\n{0}'.format(error),
                    title=TITLE, exitscript=True)
else:
    target = value

try:
    changed, failures = mt_types.assign_material(doc, chosen_rows, target.Id)
except Exception as error:
    forms.alert(u'Nada foi alterado.\n\n{0}'.format(error), title=TITLE, exitscript=True)
message = u'{0} camada(s) agora usam {1}.'.format(changed, target.Name)
if failures:
    message += u'\n\nNao alterados:\n' + u'\n'.join(failures)
forms.alert(message, title=TITLE)
