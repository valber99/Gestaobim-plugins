# -*- coding: utf-8 -*-
"""Apply a material to layers of compound types (walls, floors, roofs, ceilings)."""
from pyrevit import DB

from mt_uihelpers import element_id_value, element_name

_FUNCTION_NAMES = {
    'Structure': u'Estrutura',
    'Substrate': u'Substrato',
    'Insulation': u'Isolamento',
    'Finish1': u'Acabamento 1',
    'Finish2': u'Acabamento 2',
    'Membrane': u'Membrana',
    'StructuralDeck': u'Deck estrutural',
}

_COMPOUND_CLASSES = (DB.WallType, DB.FloorType, DB.RoofType, DB.CeilingType)


class LayerRow(object):
    def __init__(self, element_type, index, function, thickness_mm, material_name):
        self.Type = element_type
        self.Index = index
        self.Function = function
        self.ThicknessMm = thickness_mm
        self.MaterialName = material_name

    @property
    def Display(self):
        category = self.Type.Category.Name if self.Type.Category else u''
        return u'{0} : {1}  |  Camada {2}: {3} - {4:g} mm - {5}'.format(
            category, element_name(self.Type), self.Index + 1, self.Function,
            self.ThicknessMm, self.MaterialName)


def all_compound_types(doc):
    types = []
    for cls in _COMPOUND_CLASSES:
        types.extend(DB.FilteredElementCollector(doc).OfClass(cls))
    return [t for t in types if t.GetCompoundStructure() is not None]


def types_of_elements(doc, element_ids):
    """Distinct compound types used by the given elements, in a stable order."""
    seen = {}
    for element_id in element_ids:
        element = doc.GetElement(element_id)
        if element is None:
            continue
        element_type = doc.GetElement(element.GetTypeId())
        if isinstance(element_type, _COMPOUND_CLASSES) and element_type.GetCompoundStructure() is not None:
            seen[element_id_value(element_type.Id)] = element_type
    return sorted(seen.values(),
                  key=lambda t: (t.Category.Name if t.Category else u'', element_name(t)))


def _millimeters(value):
    return DB.UnitUtils.ConvertFromInternalUnits(value, DB.UnitTypeId.Millimeters)


def layer_rows(doc, element_types):
    rows = []
    for element_type in element_types:
        structure = element_type.GetCompoundStructure()
        for index in range(structure.LayerCount):
            material = doc.GetElement(structure.GetMaterialId(index))
            function = _FUNCTION_NAMES.get(str(structure.GetLayerFunction(index)),
                                           str(structure.GetLayerFunction(index)))
            rows.append(LayerRow(element_type, index, function,
                                 _millimeters(structure.GetLayerWidth(index)),
                                 material.Name if material else u'<sem material>'))
    return rows


def assign_material(doc, rows, material_id):
    """Set the material on the chosen layers. One undo entry.

    Returns (layers_changed, failures). A type that Revit rejects is skipped and
    reported, the others are still changed.
    """
    by_type = {}
    for row in rows:
        by_type.setdefault(element_id_value(row.Type.Id), (row.Type, []))[1].append(row.Index)
    transaction = DB.Transaction(doc, 'Aplicar material em camadas')
    transaction.Start()
    changed, failures = 0, []
    try:
        for element_type, indexes in by_type.values():
            try:
                structure = element_type.GetCompoundStructure()
                for index in indexes:
                    structure.SetMaterialId(index, material_id)
                element_type.SetCompoundStructure(structure)
                changed += len(indexes)
            except Exception as error:
                failures.append(u'{0}: {1}'.format(element_name(element_type), error))
        transaction.Commit()
    except Exception:
        transaction.RollBack()
        raise
    return changed, failures
