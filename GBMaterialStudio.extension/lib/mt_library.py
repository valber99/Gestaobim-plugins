# -*- coding: utf-8 -*-
"""Portable material library.

The library is a regular Revit project (GBMaterialLibrary-<version>.rvt). A
favorite stores the UniqueId of its material inside that file, so it can be
transferred to any project without the project it came from being open.
Copies use Revit's native CopyElements, which keeps the full material with its
appearance asset.
"""
import os

from pyrevit import DB, HOST_APP
from System.Collections.Generic import List

import mt_settings
import mt_wpf
from mt_services import collect_asset_textures
from mt_uihelpers import resolve_texture_path

_DEPENDENCIES = ['AppearanceAssetId', 'StructuralAssetId', 'ThermalAssetId',
                 'SurfaceForegroundPatternId', 'SurfaceBackgroundPatternId',
                 'CutForegroundPatternId', 'CutBackgroundPatternId']


class LibraryError(Exception):
    """The library could not be opened, created or updated."""


class _RejectDuplicateTypes(DB.IDuplicateTypeNamesHandler):
    __namespace__ = mt_wpf.NAMESPACE

    def OnDuplicateTypeNamesFound(self, args):
        return DB.DuplicateTypeAction.Abort


def library_dir():
    configured = (mt_settings.get('library_dir') or '').strip()
    return configured or mt_settings.base_dir()


def library_path(app=None):
    app = app or HOST_APP.app
    return os.path.join(library_dir(), 'GBMaterialLibrary-{0}.rvt'.format(app.VersionNumber))


def _open_document(app, path):
    target = os.path.normcase(os.path.abspath(path))
    for candidate in app.Documents:
        if candidate.IsLinked or not candidate.PathName:
            continue
        if os.path.normcase(os.path.abspath(candidate.PathName)) == target:
            return candidate
    return None


class OpenLibrary(object):
    """Context manager: yields the library document, closing it only if it opened it."""

    def __init__(self, app=None, create=False):
        self.app = app or HOST_APP.app
        self.create = create
        self.doc = None
        self.owned = False

    def __enter__(self):
        path = library_path(self.app)
        existing = _open_document(self.app, path)
        try:
            if existing is not None:
                self.doc = existing
            elif os.path.isfile(path):
                self.doc = self.app.OpenDocumentFile(path)
                self.owned = True
            elif self.create:
                folder = os.path.dirname(path)
                if not os.path.isdir(folder):
                    os.makedirs(folder)
                self.doc = self.app.NewProjectDocument(DB.UnitSystem.Metric)
                self.owned = True
                options = DB.SaveAsOptions()
                options.OverwriteExistingFile = False
                self.doc.SaveAs(path, options)
            else:
                raise LibraryError('A biblioteca ainda nao existe: {0}'.format(path))
        except LibraryError:
            raise
        except Exception as error:
            self._close()
            raise LibraryError('Nao foi possivel abrir a biblioteca em {0}: {1}'.format(path, error))
        return self.doc

    def _close(self):
        if self.doc is not None and self.owned:
            try:
                self.doc.Close(False)
            except Exception:
                pass
        self.doc = None

    def __exit__(self, exc_type, exc_value, traceback):
        self._close()


def _material_names(doc):
    return set(m.Name for m in DB.FilteredElementCollector(doc).OfClass(DB.Material))


def _single_material(target, copied_ids):
    materials = [target.GetElement(i) for i in copied_ids]
    materials = [m for m in materials if isinstance(m, DB.Material)]
    if len(materials) != 1:
        raise ValueError('A copia nao produziu exatamente um material.')
    return materials[0]


def transfer_material(source_doc, material, target_doc):
    """Copy a material (with appearance asset and patterns) between two open documents.

    Refuses to reuse or replace a material that already has the same name in the
    target, and checks that texture files exist before changing anything.
    """
    taken = _material_names(target_doc)
    if material.Name in taken:
        raise ValueError('Ja existe material com o nome {0} no destino. Transferencia cancelada '
                         'para nao reutilizar ou substituir outro material.'.format(material.Name))
    asset_element = source_doc.GetElement(material.AppearanceAssetId)
    textures = collect_asset_textures(asset_element.GetRenderingAsset()) if asset_element else {}
    for path in textures.values():
        if path and not resolve_texture_path(path):
            raise ValueError('Textura nao localizada: {0}. Transferencia cancelada para evitar '
                             'perda visual.'.format(path))
    transaction = DB.Transaction(target_doc, 'Transferir material')
    transaction.Start()
    try:
        ids = List[DB.ElementId]()
        ids.Add(material.Id)
        options = DB.CopyPasteOptions()
        options.SetDuplicateTypeNamesHandler(_RejectDuplicateTypes())
        copied = DB.ElementTransformUtils.CopyElements(
            source_doc, ids, target_doc, DB.Transform.Identity, options)
        result = _single_material(target_doc, copied)
        for attribute in _DEPENDENCIES:
            original_id = getattr(material, attribute)
            copied_id = getattr(result, attribute)
            if original_id != DB.ElementId.InvalidElementId:
                if copied_id == DB.ElementId.InvalidElementId or target_doc.GetElement(copied_id) is None:
                    raise ValueError('Dependencia nao transferida: {0}'.format(attribute))
        transaction.Commit()
        return result
    except Exception:
        transaction.RollBack()
        raise


def push_material(source_doc, material, previous_uid=None, app=None):
    """Copy a material into the library and return its UniqueId there.

    previous_uid replaces the library copy made earlier for the same favorite.
    """
    with OpenLibrary(app, create=True) as library:
        transaction = DB.Transaction(library, 'Atualizar biblioteca')
        transaction.Start()
        try:
            if previous_uid:
                old = library.GetElement(previous_uid)
                if isinstance(old, DB.Material):
                    library.Delete(old.Id)
            if material.Name in _material_names(library):
                raise ValueError('Ja existe outro material chamado {0} na biblioteca.'.format(material.Name))
            ids = List[DB.ElementId]()
            ids.Add(material.Id)
            options = DB.CopyPasteOptions()
            options.SetDuplicateTypeNamesHandler(_RejectDuplicateTypes())
            copied = DB.ElementTransformUtils.CopyElements(
                source_doc, ids, library, DB.Transform.Identity, options)
            unique_id = _single_material(library, copied).UniqueId
            transaction.Commit()
        except Exception:
            transaction.RollBack()
            raise
        try:
            library.Save()
        except Exception as error:
            raise LibraryError('Material copiado, mas a biblioteca nao pode ser salva: {0}'.format(error))
        return unique_id


def pull_material(target_doc, library_uid, app=None):
    """Copy a library material into target_doc and return it."""
    with OpenLibrary(app) as library:
        material = library.GetElement(library_uid)
        if not isinstance(material, DB.Material):
            raise ValueError('O material nao existe mais na biblioteca.')
        return transfer_material(library, material, target_doc)


def remove_materials(library_uids, app=None):
    """Delete materials from the library. Missing ones are ignored; returns how many were removed."""
    if not library_uids or not os.path.isfile(library_path(app)):
        return 0
    removed = 0
    with OpenLibrary(app) as library:
        transaction = DB.Transaction(library, 'Limpar biblioteca')
        transaction.Start()
        try:
            for uid in library_uids:
                material = library.GetElement(uid)
                if isinstance(material, DB.Material):
                    library.Delete(material.Id)
                    removed += 1
            transaction.Commit()
        except Exception:
            transaction.RollBack()
            raise
        if removed:
            library.Save()
    return removed
