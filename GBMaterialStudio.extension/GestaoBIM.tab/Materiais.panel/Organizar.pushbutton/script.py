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
    import mt_organizer

    if revit.doc is None or revit.doc.IsFamilyDocument:
        mt_dialogs.alert(u'Abra um projeto para organizar seus materiais.')
        return
    mt_organizer.show_organizer(revit.doc)


main()