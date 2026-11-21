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

def main():
    load_extension()
    from pyrevit import DB, revit
    import mt_dialogs
    import mt_favorites
    import mt_types
    from mt_uihelpers import element_name

    title = u'Gest\u00e3o BIM | Aplicar material em tipo'
    star = u'\u2605 '
    doc = revit.doc
    if doc is None or doc.IsFamilyDocument:
        mt_dialogs.alert(u'Abra um projeto para aplicar materiais em tipos.', title=title)
        return

    selected = list(revit.uidoc.Selection.GetElementIds())
    element_types = mt_types.types_of_elements(doc, selected) if selected else []
    if not element_types:
        available = dict(
            (u'{0} : {1}'.format(t.Category.Name if t.Category else u'', element_name(t)), t)
            for t in mt_types.all_compound_types(doc))
        if not available:
            mt_dialogs.alert(u'O projeto nao tem tipos de parede, piso, cobertura ou forro.', title=title)
            return
        names = mt_dialogs.select_from_list(
            sorted(available, key=lambda s: s.lower()), multiselect=True, title=title,
            button_name=u'Escolher camadas')
        if not names:
            return
        element_types = [available[n] for n in names]

    layers = dict((row.Display, row) for row in mt_types.layer_rows(doc, element_types))
    chosen_names = mt_dialogs.select_from_list(
        sorted(layers, key=lambda s: s.lower()), multiselect=True,
        title=u'Camadas que receberao o material', button_name=u'Escolher material')
    if not chosen_names:
        return
    chosen_rows = [layers[n] for n in chosen_names]

    choices = {}
    for material in DB.FilteredElementCollector(doc).OfClass(DB.Material):
        choices[material.Name] = ('material', material)
    try:
        for favorite in mt_favorites.list_favorites():
            choices[u'{0}{1}  (favorito)'.format(star, favorite.Name)] = ('favorite', favorite.data)
    except Exception:
        pass
    picked = mt_dialogs.select_from_list(
        sorted(choices, key=lambda s: s.lower()), multiselect=False,
        title=u'Material de destino', button_name=u'Aplicar')
    if not picked:
        return

    kind, value = choices[picked]
    if kind == 'favorite':
        try:
            target = mt_favorites.get_or_create_favorite_material(doc, value)
        except Exception as error:
            mt_dialogs.alert(u'Nao foi possivel carregar o favorito.\n\n{0}'.format(error), title=title)
            return
    else:
        target = value

    try:
        changed, failures = mt_types.assign_material(doc, chosen_rows, target.Id)
    except Exception as error:
        mt_dialogs.alert(u'Nada foi alterado.\n\n{0}'.format(error), title=title)
        return
    message = u'{0} camada(s) agora usam {1}.'.format(changed, target.Name)
    if failures:
        message += u'\n\nNao alterados:\n' + u'\n'.join(failures)
    mt_dialogs.alert(message, title=title)


main()