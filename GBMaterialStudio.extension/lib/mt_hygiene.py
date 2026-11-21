# -*- coding: utf-8 -*-
"""Revit side of material organization: usage, duplicates, purge and batch rename."""
import hashlib

from pyrevit import DB
from System.Collections.Generic import HashSet

import mt_hygiene_core as core
from mt_services import collect_asset_textures
from mt_uihelpers import element_id_value


class MaterialRow(object):
    """One material in the organizer. Attributes are bound by the WPF list."""

    def __init__(self, material, usage, protected=False, duplicate_of=''):
        self.Id = material.Id
        self.Name = material.Name
        self.MaterialClass = material.MaterialClass or ''
        self.Usage = usage
        if protected:
            self.UsageText = 'em uso (Revit)'
        else:
            self.UsageText = str(usage) if usage else 'nao usado'
        self.DuplicateOf = duplicate_of
        self.NewName = ''
        self.State = ''
        self.Key = element_id_value(material.Id)


def _pattern_name(doc, element_id):
    if element_id == DB.ElementId.InvalidElementId:
        return ''
    try:
        pattern = doc.GetElement(element_id).GetFillPattern()
        return pattern.Name if pattern else ''
    except Exception:
        return ''


def _color_text(color):
    if color is None or not color.IsValid:
        return '-'
    return '{0},{1},{2}'.format(color.Red, color.Green, color.Blue)


def material_signature(doc, material):
    """Text describing what the material looks like, independent of ids and of the project.

    Two materials with the same signature render and draw the same way.
    """
    parts = [
        _color_text(material.Color), material.Transparency, material.Shininess,
        material.Smoothness, material.MaterialClass, material.MaterialCategory,
        _pattern_name(doc, material.SurfaceForegroundPatternId),
        _color_text(material.SurfaceForegroundPatternColor),
        _pattern_name(doc, material.SurfaceBackgroundPatternId),
        _color_text(material.SurfaceBackgroundPatternColor),
        _pattern_name(doc, material.CutForegroundPatternId),
        _color_text(material.CutForegroundPatternColor),
        _pattern_name(doc, material.CutBackgroundPatternId),
        _color_text(material.CutBackgroundPatternColor),
    ]
    asset_element = doc.GetElement(material.AppearanceAssetId) if \
        material.AppearanceAssetId != DB.ElementId.InvalidElementId else None
    asset = asset_element.GetRenderingAsset() if asset_element else None
    if asset is not None:
        schema = asset.FindByName('BaseSchema')
        parts.append(schema.Value if schema else '')
        for prop_name in ['generic_diffuse', 'ceramic_color', 'metal_color', 'glass_color',
                          'generic_roughness', 'generic_reflectivity', 'generic_transparency']:
            prop = asset.FindByName(prop_name)
            if prop is None:
                continue
            try:
                parts.append(_color_text(prop.GetValueAsColor()))
            except Exception:
                try:
                    parts.append('{0:.3f}'.format(float(prop.Value)))
                except Exception:
                    parts.append('?')
        textures = collect_asset_textures(asset)
        parts.extend((textures.get(key) or '').lower() for key in sorted(textures))
    return u'|'.join(u'{0}'.format(part) for part in parts)


def signature_hash(doc, material):
    return hashlib.sha1(material_signature(doc, material).encode('utf-8')).hexdigest()


def _referenced_by_parameters(element, material_values):
    """Material ids held in the element's ElementId parameters.

    GetMaterialIds does not report these (family material parameters, for
    example), so a purge based on it alone would delete materials in use.
    """
    found = set()
    for parameter in element.Parameters:
        try:
            if parameter.StorageType == DB.StorageType.ElementId:
                value = element_id_value(parameter.AsElementId())
                if value in material_values:
                    found.add(value)
        except Exception:
            pass
    return found


def collect_usage(doc, material_values):
    """Count how many elements and types reference each material (by id value).

    material_values: set of the integer values of every material id in doc.
    """
    counts = {}

    def bump(values):
        for value in values:
            counts[value] = counts.get(value, 0) + 1

    types = DB.FilteredElementCollector(doc).WhereElementIsElementType()
    instances = DB.FilteredElementCollector(doc).WhereElementIsNotElementType()
    for element in types:
        used = set()
        try:
            used.update(element_id_value(i) for i in element.GetMaterialIds(False))
        except Exception:
            pass
        used.update(_referenced_by_parameters(element, material_values))
        bump(used)
    for element in instances:
        used = set()
        try:
            used.update(element_id_value(i) for i in element.GetMaterialIds(False))
            used.update(element_id_value(i) for i in element.GetMaterialIds(True))
        except Exception:
            pass
        if isinstance(element, DB.FamilyInstance):
            used.update(_referenced_by_parameters(element, material_values))
        bump(used)
    for category in doc.Settings.Categories:
        for current in [category] + list(category.SubCategories):
            try:
                if current.Material is not None:
                    bump([element_id_value(current.Material.Id)])
            except Exception:
                pass
    return counts


def revit_unused_ids(doc):
    """Integer ids Revit's own Purge Unused would remove.

    A material only counts as unused when this scan and Revit agree. Revit sees
    uses this module cannot (nested families, defaults), and this module's scan
    is the second check. Raises RuntimeError on Revit versions without the API.
    """
    try:
        unused = doc.GetUnusedElements(HashSet[DB.ElementId]())
    except AttributeError:
        raise RuntimeError('Organizar materiais exige o Revit 2024 ou mais novo.')
    return set(element_id_value(i) for i in unused)


def analyze(doc):
    """Return one MaterialRow per material, with usage and the material each duplicates."""
    materials = list(DB.FilteredElementCollector(doc).OfClass(DB.Material))
    usage = collect_usage(doc, set(element_id_value(m.Id) for m in materials))
    purgeable = revit_unused_ids(doc)
    rows = {}
    candidates = []
    for material in materials:
        count = usage.get(element_id_value(material.Id), 0)
        protected = count == 0 and element_id_value(material.Id) not in purgeable
        rows[element_id_value(material.Id)] = MaterialRow(
            material, 1 if protected else count, protected)
        try:
            signature = material_signature(doc, material)
        except Exception:
            continue
        candidates.append(core.Candidate(element_id_value(material.Id), material.Name, signature, count))
    for group in core.group_duplicates(candidates):
        for duplicate in group.duplicates:
            rows[duplicate.key].DuplicateOf = group.primary.name
    return sorted(rows.values(), key=lambda row: row.Name.lower())


def delete_unused(doc, rows):
    """Delete unused materials and the appearance assets nobody else uses.

    Only rows with Usage == 0 are touched. One undo entry.
    Returns (deleted_names, failures, deleted_asset_count).
    """
    targets = [row for row in rows if row.Usage == 0]
    if not targets:
        return [], [], 0
    transaction = DB.Transaction(doc, 'Excluir materiais nao usados')
    transaction.Start()
    deleted, failures = [], []
    asset_ids = set()
    try:
        for row in targets:
            material = doc.GetElement(row.Id)
            if material is None:
                continue
            if material.AppearanceAssetId != DB.ElementId.InvalidElementId:
                asset_ids.add(material.AppearanceAssetId)
            guard = DB.SubTransaction(doc)
            guard.Start()
            try:
                removed = doc.Delete(row.Id)
                if removed.Count != 1:
                    guard.RollBack()
                    failures.append(u'{0}: a exclusao removeria outros {1} elemento(s)'.format(
                        row.Name, removed.Count - 1))
                    continue
                guard.Commit()
                deleted.append(row.Name)
            except Exception as error:
                guard.RollBack()
                failures.append(u'{0}: {1}'.format(row.Name, error))
        in_use = set(element_id_value(m.AppearanceAssetId)
                     for m in DB.FilteredElementCollector(doc).OfClass(DB.Material))
        assets_deleted = 0
        for asset_id in asset_ids:
            if element_id_value(asset_id) in in_use or doc.GetElement(asset_id) is None:
                continue
            try:
                doc.Delete(asset_id)
                assets_deleted += 1
            except Exception:
                pass
        transaction.Commit()
        return deleted, failures, assets_deleted
    except Exception:
        transaction.RollBack()
        raise


def rename(doc, steps, rows):
    """Apply planned renames in two phases, so swaps and chains never collide.

    Only steps with status ok are applied. One undo entry. Returns the number renamed.
    """
    todo = [step for step in steps if step.status == core.STATUS_OK]
    if not todo:
        return 0
    ids = dict((row.Key, row.Id) for row in rows)
    transaction = DB.Transaction(doc, 'Renomear materiais')
    transaction.Start()
    try:
        elements = [(doc.GetElement(ids[step.key]), step) for step in todo]
        for element, step in elements:
            if element is None:
                raise ValueError(u'O material {0} nao existe mais. Atualize a lista.'.format(step.old))
        for index, (element, _) in enumerate(elements):
            element.Name = u'__gb_tmp_{0}'.format(index)
        for element, step in elements:
            element.Name = step.new
        transaction.Commit()
        return len(todo)
    except Exception:
        transaction.RollBack()
        raise
