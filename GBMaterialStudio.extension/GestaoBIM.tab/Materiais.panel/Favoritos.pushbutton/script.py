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
    from pyrevit import revit
    import mt_dialogs
    import mt_favorites_browser

    if revit.doc is None or revit.doc.IsFamilyDocument:
        mt_dialogs.alert(u'Abra um projeto para acessar e carregar seus materiais favoritos.')
        return
    mt_favorites_browser.show_favorites(revit.doc, revit.uidoc)


main()