from System.Collections.Generic import List

import mt_cursor
import mt_notifications
import System

from Autodesk.Revit.Exceptions import OperationCanceledException
from pyrevit import DB, UI

import mt_dialogs
import mt_wpf

from mt_uihelpers import (
    APPEARANCE_SKIP,
    SCHEMA_EXTRA_PROPS,
    PatternItem,
    PropItem,
    format_asset_property,
    make_pattern_preview,
)


_ERRORS = []


def _record(step, error):
    try:
        text = str(error)
    except Exception:
        text = repr(error)
    _ERRORS.append("{0}: {1}".format(step, text))


def pop_errors():
    """Return and clear the failure reasons recorded by the apply_* functions."""
    items = list(_ERRORS)
    del _ERRORS[:]
    return items


def _unique_name(existing_names, base):
    if base not in existing_names:
        return base
    index = 2
    while "{0}_{1}".format(base, index) in existing_names:
        index += 1
    return "{0}_{1}".format(base, index)


class MaterialFaceFilter(UI.Selection.ISelectionFilter):
    __namespace__ = mt_wpf.NAMESPACE

    def __init__(self, doc):
        self.doc = doc

    def AllowElement(self, element):
        return True

    def AllowReference(self, reference, xyz_point):
        try:
            element = self.doc.GetElement(reference)
            geo_obj = element.GetGeometryObjectFromReference(reference)
            if geo_obj and hasattr(geo_obj, "MaterialElementId"):
                mat_id = geo_obj.MaterialElementId
                return mat_id is not None and mat_id != DB.ElementId.InvalidElementId
        except Exception:
            pass
        return False


class PaintFaceFilter(UI.Selection.ISelectionFilter):
    __namespace__ = mt_wpf.NAMESPACE

    def __init__(self, doc):
        self.doc = doc

    def AllowElement(self, element):
        return True

    def AllowReference(self, reference, xyz_point):
        try:
            element = self.doc.GetElement(reference)
            face = element.GetGeometryObjectFromReference(reference)
            return face is not None
        except Exception:
            return False


def get_fill_patterns(doc):
    patterns = []
    for elem in DB.FilteredElementCollector(doc).OfClass(DB.FillPatternElement):
        pattern = elem.GetFillPattern()
        if pattern:
            name = "<Solid Fill>" if pattern.IsSolidFill else pattern.Name
            drafting = pattern.Target == DB.FillPatternTarget.Drafting
            patterns.append(
                PatternItem(name, elem.Id, make_pattern_preview(elem), drafting)
            )
    return sorted(patterns, key=lambda x: x.Name.lower())


def get_material_from_face(doc, reference):
    element = doc.GetElement(reference)
    face = element.GetGeometryObjectFromReference(reference)
    mat_id = getattr(face, "MaterialElementId", None)
    if mat_id and mat_id != DB.ElementId.InvalidElementId:
        return doc.GetElement(mat_id)
    if isinstance(element, DB.FamilyInstance):
        param = element.get_Parameter(DB.BuiltInParameter.MATERIAL_ID_PARAM)
        if param:
            eid = param.AsElementId()
            if eid and eid != DB.ElementId.InvalidElementId:
                return doc.GetElement(eid)
    return None


def read_appearance_asset(doc, material):
    props = []
    if material.AppearanceAssetId == DB.ElementId.InvalidElementId:
        return "", props
    appearance_elem = doc.GetElement(material.AppearanceAssetId)
    if not appearance_elem:
        return "", props
    asset = appearance_elem.GetRenderingAsset()
    if not asset:
        return "", props

    schema_name = ""
    schema_prop = asset.FindByName("BaseSchema")
    if schema_prop:
        schema_name = schema_prop.Value
    allowed_extra_props = SCHEMA_EXTRA_PROPS.get(schema_name, [])
    textures = collect_asset_textures(asset)

    try:
        for i in range(asset.Size):
            prop = asset.Get(i)
            name = prop.Name
            if name in APPEARANCE_SKIP:
                continue
            if allowed_extra_props and name not in allowed_extra_props:
                continue
            display = format_asset_property(prop)
            if display is not None:
                props.append(PropItem(name, display))
    except Exception:
        pass

    for role_key in ["main", "bump", "cutout", "transparency"]:
        path_value = textures.get(role_key, "")
        if path_value:
            props.append(PropItem("texture_{}".format(role_key), path_value))

    return schema_name, props


def _infer_texture_role(name_hint):
    name_hint = (name_hint or "").lower()
    if any(
        x in name_hint
        for x in [
            "diffuse",
            "albedo",
            "color",
            "surfacealbedo",
            "ceramic_color",
            "metal_color",
            "glass_color",
        ]
    ):
        return "main"
    if "bump" in name_hint or "normal" in name_hint:
        return "bump"
    if "cutout" in name_hint or "opacity" in name_hint:
        return "cutout"
    if "transparen" in name_hint:
        return "transparency"
    return "other"


def _safe_prop_string(prop):
    try:
        return prop.Value
    except Exception:
        try:
            return str(prop)
        except Exception:
            return ""


def _collect_asset_textures_from_asset(asset, role_hint, found, depth):
    if asset is None or depth > 6:
        return
    try:
        for i in range(asset.Size):
            prop = asset.Get(i)
            prop_name = getattr(prop, "Name", "")
            role = role_hint or _infer_texture_role(prop_name)

            if prop_name == "unifiedbitmap_Bitmap":
                path_value = _safe_prop_string(prop)
                if path_value:
                    key = (
                        role
                        if role in ["main", "bump", "cutout", "transparency"]
                        else "main"
                    )
                    if not found.get(key):
                        found[key] = path_value

            try:
                if prop.NumberOfConnectedProperties > 0:
                    connected = prop.GetSingleConnectedAsset()
                    if connected is not None:
                        _collect_asset_textures_from_asset(
                            connected,
                            _infer_texture_role(prop_name),
                            found,
                            depth + 1,
                        )
            except Exception:
                pass
    except Exception:
        pass


def collect_asset_textures(asset):
    found = {"main": "", "bump": "", "cutout": "", "transparency": ""}
    _collect_asset_textures_from_asset(asset, None, found, 0)
    return found


def _match_role(target_role, role):
    if target_role == role:
        return True
    if target_role == "main" and role in ["main", "other"]:
        return True
    return False


def _find_connected_bitmap_asset(asset, target_role, depth, role_hint=None):
    if asset is None or depth > 6:
        return None
    try:
        for i in range(asset.Size):
            prop = asset.Get(i)
            prop_name = getattr(prop, "Name", "")
            role = role_hint or _infer_texture_role(prop_name)
            try:
                if prop.NumberOfConnectedProperties > 0:
                    connected = prop.GetSingleConnectedAsset()
                    if connected is not None:
                        if getattr(
                            connected, "Name", ""
                        ) == "UnifiedBitmapSchema" and _match_role(target_role, role):
                            return connected
                        nested = _find_connected_bitmap_asset(
                            connected,
                            target_role,
                            depth + 1,
                            _infer_texture_role(prop_name),
                        )
                        if nested is not None:
                            return nested
            except Exception:
                pass
    except Exception:
        pass
    return None


def _get_bitmap_prop(bitmap_asset, prop_name):
    try:
        return bitmap_asset.FindByName(prop_name)
    except Exception:
        return None


def _distance_to_cm(value):
    try:
        return DB.UnitUtils.ConvertFromInternalUnits(value, DB.UnitTypeId.Centimeters)
    except Exception:
        return value


def _cm_to_internal(value):
    try:
        return DB.UnitUtils.ConvertToInternalUnits(value, DB.UnitTypeId.Centimeters)
    except Exception:
        return value


def read_texture_slot_data(doc, material, slot_name):
    result = {
        "path": "",
        "brightness": 1.0,
        "invert": False,
        "link_transforms": False,
        "offset_x_cm": 0.0,
        "offset_y_cm": 0.0,
        "rotation": 0.0,
        "size_x_cm": 0.0,
        "size_y_cm": 0.0,
        "scale_lock": False,
        "repeat_u": True,
        "repeat_v": True,
    }
    if material.AppearanceAssetId == DB.ElementId.InvalidElementId:
        return result
    appearance_elem = doc.GetElement(material.AppearanceAssetId)
    if not appearance_elem:
        return result
    asset = appearance_elem.GetRenderingAsset()
    if not asset:
        return result
    bitmap_asset = _find_connected_bitmap_asset(asset, slot_name, 0)
    if bitmap_asset is None:
        return result

    ub = DB.Visual.UnifiedBitmap
    mappings = [
        (ub.UnifiedbitmapBitmap, "path", None),
        (ub.UnifiedbitmapRGBAmount, "brightness", float),
        (ub.UnifiedbitmapInvert, "invert", bool),
        (ub.TextureLinkTextureTransforms, "link_transforms", bool),
        (ub.TextureRealWorldOffsetX, "offset_x_cm", _distance_to_cm),
        (ub.TextureRealWorldOffsetY, "offset_y_cm", _distance_to_cm),
        (ub.TextureWAngle, "rotation", float),
        (ub.TextureRealWorldScaleX, "size_x_cm", _distance_to_cm),
        (ub.TextureRealWorldScaleY, "size_y_cm", _distance_to_cm),
        (ub.TextureScaleLock, "scale_lock", bool),
        (ub.TextureURepeat, "repeat_u", bool),
        (ub.TextureVRepeat, "repeat_v", bool),
    ]
    for api_name, key, caster in mappings:
        prop = _get_bitmap_prop(bitmap_asset, api_name)
        if prop is None:
            continue
        try:
            val = prop.Value
            result[key] = caster(val) if caster else val
        except Exception:
            pass
    return result


def get_texture_slot_availability(doc, material):
    slots = {
        "main": False,
        "bump": False,
        "cutout": False,
        "transparency": False,
    }
    if material.AppearanceAssetId == DB.ElementId.InvalidElementId:
        return slots
    appearance_elem = doc.GetElement(material.AppearanceAssetId)
    if not appearance_elem:
        return slots
    asset = appearance_elem.GetRenderingAsset()
    if not asset:
        return slots
    for slot_name in slots.keys():
        slots[slot_name] = _find_connected_bitmap_asset(asset, slot_name, 0) is not None
    return slots


def _abort_edit(edit_scope, transaction, step_name="aparencia", error=None):
    if error is not None:
        _record(step_name, error)
    for step in (edit_scope.Dispose, transaction.RollBack):
        try:
            step()
        except Exception:
            pass


def apply_texture_slot_data(doc, material, slot_name, data):
    if material.AppearanceAssetId == DB.ElementId.InvalidElementId:
        return False
    t = DB.Transaction(doc, "Editar Textura")
    t.Start()
    edit_scope = DB.Visual.AppearanceAssetEditScope(doc)
    try:
        editable = edit_scope.Start(material.AppearanceAssetId)
        bitmap_asset = _find_connected_bitmap_asset(editable, slot_name, 0)
        if bitmap_asset is None:
            edit_scope.Commit(False)
            t.RollBack()
            _record("textura", "slot sem textura conectada")
            return False

        ub = DB.Visual.UnifiedBitmap
        values = [
            (ub.UnifiedbitmapBitmap, data.get("path", "")),
            (ub.UnifiedbitmapRGBAmount, float(data.get("brightness", 1.0))),
            (ub.UnifiedbitmapInvert, bool(data.get("invert", False))),
            (ub.TextureLinkTextureTransforms, bool(data.get("link_transforms", False))),
            (
                ub.TextureRealWorldOffsetX,
                _cm_to_internal(float(data.get("offset_x_cm", 0.0))),
            ),
            (
                ub.TextureRealWorldOffsetY,
                _cm_to_internal(float(data.get("offset_y_cm", 0.0))),
            ),
            (ub.TextureWAngle, float(data.get("rotation", 0.0))),
            (
                ub.TextureRealWorldScaleX,
                _cm_to_internal(max(0.01, float(data.get("size_x_cm", 1.0)))),
            ),
            (
                ub.TextureRealWorldScaleY,
                _cm_to_internal(max(0.01, float(data.get("size_y_cm", 1.0)))),
            ),
            (ub.TextureScaleLock, bool(data.get("scale_lock", False))),
            (ub.TextureURepeat, bool(data.get("repeat_u", True))),
            (ub.TextureVRepeat, bool(data.get("repeat_v", True))),
        ]

        for api_name, new_value in values:
            prop = _get_bitmap_prop(bitmap_asset, api_name)
            if prop is None:
                continue
            try:
                prop.Value = new_value
            except Exception:
                pass

        edit_scope.Commit(True)
        t.Commit()
        return True
    except Exception as error:
        _abort_edit(edit_scope, t, "textura", error)
        return False


def _set_texture_on_asset(asset, target_role, image_path, depth):
    if asset is None or depth > 6:
        return False
    try:
        for i in range(asset.Size):
            prop = asset.Get(i)
            prop_name = getattr(prop, "Name", "")
            role = _infer_texture_role(prop_name)

            if prop_name == "unifiedbitmap_Bitmap":
                effective_role = target_role or role
                if effective_role == target_role or (
                    target_role == "main" and effective_role == "other"
                ):
                    try:
                        prop.Value = image_path
                        return True
                    except Exception:
                        pass

            try:
                if prop.NumberOfConnectedProperties > 0:
                    connected = prop.GetSingleConnectedAsset()
                    if connected is not None:
                        nested_role = role if role != "other" else target_role
                        if _set_texture_on_asset(
                            connected,
                            target_role if target_role else nested_role,
                            image_path,
                            depth + 1,
                        ):
                            return True
            except Exception:
                pass
    except Exception:
        pass
    return False


def set_texture_slot(doc, material, slot_name, image_path):
    if not image_path:
        return False
    try:
        if not System.IO.File.Exists(image_path):
            return False
    except Exception:
        return False
    if material.AppearanceAssetId == DB.ElementId.InvalidElementId:
        return False
    t = DB.Transaction(doc, "Alterar Textura")
    t.Start()
    edit_scope = DB.Visual.AppearanceAssetEditScope(doc)
    try:
        editable = edit_scope.Start(material.AppearanceAssetId)
        changed = _set_texture_on_asset(editable, slot_name, image_path, 0)
        if changed:
            edit_scope.Commit(True)
            t.Commit()
            return True
        edit_scope.Commit(False)
        t.RollBack()
        _record("textura", "nenhum slot compativel encontrado")
        return False
    except Exception as error:
        _abort_edit(edit_scope, t, "textura", error)
        return False


def read_appearance_editable(doc, material):
    result = {
        "diffuse_r": 0.93,
        "diffuse_g": 0.93,
        "diffuse_b": 0.93,
        "roughness": 0.5,
        "reflectivity": 0.0,
        "glossiness": 0.0,
        "transparency": 0.0,
        "has_texture": False,
        "texture_path": "",
        "textures": {"main": "", "bump": "", "cutout": "", "transparency": ""},
        "schema": "",
    }
    if material.AppearanceAssetId == DB.ElementId.InvalidElementId:
        return result
    appearance_elem = doc.GetElement(material.AppearanceAssetId)
    if not appearance_elem:
        return result
    asset = appearance_elem.GetRenderingAsset()
    if not asset:
        return result

    schema_prop = asset.FindByName("BaseSchema")
    if schema_prop:
        result["schema"] = schema_prop.Value

    result["textures"] = collect_asset_textures(asset)
    result["texture_path"] = result["textures"].get("main", "")
    result["has_texture"] = bool(result["texture_path"])

    for prop_name in ["generic_diffuse", "ceramic_color", "metal_color", "glass_color"]:
        prop = asset.FindByName(prop_name)
        if prop is None:
            continue
        try:
            color = prop.GetValueAsColor()
            result["diffuse_r"] = color.Red / 255.0
            result["diffuse_g"] = color.Green / 255.0
            result["diffuse_b"] = color.Blue / 255.0
        except Exception:
            try:
                doubles = prop.GetValueAsDoubles()
                if doubles and len(doubles) >= 3:
                    result["diffuse_r"] = doubles[0]
                    result["diffuse_g"] = doubles[1]
                    result["diffuse_b"] = doubles[2]
            except Exception:
                pass
        break

    for prop_name, key in [
        ("generic_roughness", "roughness"),
        ("metal_roughness", "roughness"),
    ]:
        prop = asset.FindByName(prop_name)
        if prop is not None:
            try:
                result[key] = float(prop.Value)
            except Exception:
                pass
    for prop_name, key in [
        ("generic_reflectivity", "reflectivity"),
        ("generic_glossiness", "glossiness"),
        ("generic_transparency", "transparency"),
        ("surfaceroughness", "roughness"),
    ]:
        prop = asset.FindByName(prop_name)
        if prop is not None:
            try:
                result[key] = float(prop.Value)
            except Exception:
                pass
    return result


def apply_appearance_changes(
    doc,
    material_id,
    diffuse_r,
    diffuse_g,
    diffuse_b,
    roughness,
    reflectivity,
    glossiness,
    transparency,
):
    if material_id == DB.ElementId.InvalidElementId:
        return True
    appearance_elem = doc.GetElement(material_id)
    if not appearance_elem:
        return True
    asset = appearance_elem.GetRenderingAsset()
    if not asset:
        return True

    schema_name = ""
    schema_prop = asset.FindByName("BaseSchema")
    if schema_prop:
        schema_name = schema_prop.Value

    rough_prop_name = None
    refl_prop_name = None
    gloss_prop_name = None
    transp_prop_name = None
    if schema_name == "GenericSchema":
        color_prop_name = "generic_diffuse"
        rough_prop_name = "generic_roughness"
        refl_prop_name = "generic_reflectivity"
        gloss_prop_name = "generic_glossiness"
        transp_prop_name = "generic_transparency"
    elif schema_name == "CeramicSchema":
        color_prop_name = "ceramic_color"
    elif schema_name == "MetalSchema":
        color_prop_name = "metal_color"
        rough_prop_name = "metal_roughness"
    elif schema_name == "GlassSchema":
        color_prop_name = "glass_color"
        transp_prop_name = "glass_transmittance"
    else:
        color_prop_name = "generic_diffuse"
        rough_prop_name = "generic_roughness"
        refl_prop_name = "generic_reflectivity"
        gloss_prop_name = "generic_glossiness"
        transp_prop_name = "generic_transparency"

    t = DB.Transaction(doc, "Alterar Appearance")
    t.Start()
    edit_scope = DB.Visual.AppearanceAssetEditScope(doc)
    try:
        editable = edit_scope.Start(material_id)
        try:
            color_prop = editable.FindByName(color_prop_name)
            if color_prop is not None:
                revit_color = DB.Color(
                    System.Convert.ToByte(diffuse_r * 255),
                    System.Convert.ToByte(diffuse_g * 255),
                    System.Convert.ToByte(diffuse_b * 255),
                )
                color_prop.SetValueAsColor(revit_color)
        except Exception:
            try:
                color_prop = editable.FindByName(color_prop_name)
                if color_prop is not None:
                    values = List[System.Double](
                        [diffuse_r, diffuse_g, diffuse_b, 1.0]
                    )
                    color_prop.SetValueAsDoubles(values)
            except Exception:
                pass

        for prop_name, val in [
            (rough_prop_name, roughness),
            (refl_prop_name, reflectivity),
            (gloss_prop_name, glossiness),
            (transp_prop_name, transparency),
        ]:
            if not prop_name:
                continue
            try:
                prop = editable.FindByName(prop_name)
                if prop is not None:
                    prop.Value = val
            except Exception:
                pass

        edit_scope.Commit(True)
        t.Commit()
        return True
    except Exception as error:
        _abort_edit(edit_scope, t, "aparencia", error)
        return False


def apply_material_changes(
    doc,
    material_id,
    r,
    g,
    b,
    transparency,
    shininess,
    smoothness,
    use_render,
    surface_fg_id,
    surface_bg_id,
    cut_fg_id,
    cut_bg_id,
    surface_fg_color,
    surface_bg_color,
    cut_fg_color,
    cut_bg_color,
):
    t = DB.Transaction(doc, "Editar Material")
    t.Start()
    try:
        material = doc.GetElement(material_id)
        material.Color = DB.Color(
            System.Convert.ToByte(r), System.Convert.ToByte(g), System.Convert.ToByte(b)
        )
        material.Transparency = transparency
        material.Shininess = shininess
        material.Smoothness = smoothness
        try:
            material.UseRenderAppearanceForShading = use_render
        except Exception:
            pass
        if surface_fg_id is not None:
            material.SurfaceForegroundPatternId = surface_fg_id
        if surface_bg_id is not None:
            material.SurfaceBackgroundPatternId = surface_bg_id
        if cut_fg_id is not None:
            material.CutForegroundPatternId = cut_fg_id
        if cut_bg_id is not None:
            material.CutBackgroundPatternId = cut_bg_id
        if surface_fg_color is not None:
            material.SurfaceForegroundPatternColor = surface_fg_color
        if surface_bg_color is not None:
            material.SurfaceBackgroundPatternColor = surface_bg_color
        if cut_fg_color is not None:
            material.CutForegroundPatternColor = cut_fg_color
        if cut_bg_color is not None:
            material.CutBackgroundPatternColor = cut_bg_color
        t.Commit()
        return True
    except Exception as error:
        _record("graficos", error)
        t.RollBack()
        return False


def apply_identity_changes(
    doc,
    material_id,
    name,
    description,
    manufacturer,
    model,
    url,
    material_class,
    category,
    mark,
    comments,
):
    t = DB.Transaction(doc, "Editar Identidade")
    t.Start()
    try:
        mat = doc.GetElement(material_id)
        name = (name or "").strip()
        if not name:
            raise ValueError("o nome do material nao pode ficar vazio")
        if name != mat.Name:
            taken = set(m.Name for m in DB.FilteredElementCollector(doc).OfClass(DB.Material))
            if name in taken:
                raise ValueError("ja existe um material chamado '{0}'".format(name))
            mat.Name = name
        for bip, val in [
            (DB.BuiltInParameter.ALL_MODEL_DESCRIPTION, description),
            (DB.BuiltInParameter.ALL_MODEL_MANUFACTURER, manufacturer),
            (DB.BuiltInParameter.ALL_MODEL_MODEL, model),
            (DB.BuiltInParameter.ALL_MODEL_URL, url),
            (DB.BuiltInParameter.ALL_MODEL_MARK, mark),
        ]:
            param = mat.get_Parameter(bip)
            if param:
                param.Set(val)
        comments_param = mat.LookupParameter("Comments")
        if comments_param:
            comments_param.Set(comments)
        mat.MaterialClass = material_class
        mat.MaterialCategory = category
        t.Commit()
        return True
    except Exception as error:
        _record("identidade", error)
        t.RollBack()
        return False


_COPIED_ATTRIBUTES = [
    "SurfaceForegroundPatternId",
    "SurfaceBackgroundPatternId",
    "CutForegroundPatternId",
    "CutBackgroundPatternId",
    "SurfaceForegroundPatternColor",
    "SurfaceBackgroundPatternColor",
    "CutForegroundPatternColor",
    "CutBackgroundPatternColor",
    "MaterialClass",
    "MaterialCategory",
]

_COPIED_PARAMETERS = [
    DB.BuiltInParameter.ALL_MODEL_DESCRIPTION,
    DB.BuiltInParameter.ALL_MODEL_MANUFACTURER,
    DB.BuiltInParameter.ALL_MODEL_MODEL,
    DB.BuiltInParameter.ALL_MODEL_URL,
    DB.BuiltInParameter.ALL_MODEL_MARK,
]


def _copy_material_properties(doc, source, target, appearance_name):
    target.Color = source.Color
    target.Transparency = source.Transparency
    target.Shininess = source.Shininess
    target.Smoothness = source.Smoothness
    for attr in ["UseRenderAppearanceForShading"] + _COPIED_ATTRIBUTES:
        try:
            setattr(target, attr, getattr(source, attr))
        except Exception:
            pass
    for bip in _COPIED_PARAMETERS:
        src_param = source.get_Parameter(bip)
        dst_param = target.get_Parameter(bip)
        if src_param and dst_param and not dst_param.IsReadOnly:
            dst_param.Set(src_param.AsString() or "")
    src_comments = source.LookupParameter("Comments")
    dst_comments = target.LookupParameter("Comments")
    if src_comments and dst_comments and not dst_comments.IsReadOnly:
        dst_comments.Set(src_comments.AsString() or "")
    if source.AppearanceAssetId != DB.ElementId.InvalidElementId:
        source_asset = doc.GetElement(source.AppearanceAssetId)
        if source_asset:
            taken = set(
                a.Name for a in DB.FilteredElementCollector(doc).OfClass(DB.AppearanceAssetElement)
            )
            target.AppearanceAssetId = source_asset.Duplicate(
                _unique_name(taken, appearance_name + "_Appearance")
            ).Id


def duplicate_material(doc, source_material, new_name):
    new_name = (new_name or "").strip()
    if not new_name:
        _record("duplicar", "o nome do novo material nao pode ficar vazio")
        return None
    taken = set(m.Name for m in DB.FilteredElementCollector(doc).OfClass(DB.Material))
    if new_name in taken:
        _record("duplicar", "ja existe um material chamado '{0}'".format(new_name))
        return None
    t = DB.Transaction(doc, "Duplicar Material")
    t.Start()
    try:
        new_mat = doc.GetElement(DB.Material.Create(doc, new_name))
        _copy_material_properties(doc, source_material, new_mat, new_name)
        t.Commit()
        return new_mat
    except Exception as error:
        _record("duplicar", error)
        t.RollBack()
        return None


def change_texture(doc, material, image_path):
    return set_texture_slot(doc, material, "main", image_path)


def paint_material_on_faces(doc, uidoc, material, paint_label=None):
    painted = 0
    label = paint_label or material.Name
    while True:
        try:
            ref = mt_cursor.pick_face(uidoc,
                UI.Selection.ObjectType.Face,
                PaintFaceFilter(doc),
                "Pintando: {} | Clique em uma face. ESC para sair.".format(label),
            )
        except OperationCanceledException:
            break
        except Exception as error:
            mt_dialogs.alert("Nao foi possivel selecionar a face.\n\n{0}".format(error),
                        title="Paint")
            break
        t = DB.Transaction(doc, "Pintar Material")
        try:
            element = doc.GetElement(ref)
            face = element.GetGeometryObjectFromReference(ref)
            t.Start()
            doc.Paint(element.Id, face, material.Id)
            t.Commit()
            painted += 1
        except Exception:
            try:
                t.RollBack()
            except Exception:
                pass
    if painted > 0:
        mt_notifications.toast(
            "{} face(s) pintadas com {}.".format(painted, label),
            title="Paint",
        )
