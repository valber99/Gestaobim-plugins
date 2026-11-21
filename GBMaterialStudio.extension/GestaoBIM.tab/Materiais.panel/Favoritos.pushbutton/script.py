# -*- coding: utf-8 -*-
import imp
import os
from pyrevit import revit, forms

lib = os.path.join(os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..', '..')), 'lib')
imp.load_source('mt_bootstrap', os.path.join(lib, 'mt_bootstrap.py')).load_all(lib)
import mt_favorites_browser

if revit.doc is None or revit.doc.IsFamilyDocument:
    forms.alert('Abra um projeto para acessar e carregar seus materiais favoritos.',
                title=u'Gestão BIM', exitscript=True)
mt_favorites_browser.show_favorites(revit.doc, revit.uidoc)
