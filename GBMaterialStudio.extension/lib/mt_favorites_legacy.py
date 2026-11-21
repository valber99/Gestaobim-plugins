import json
import os
import re
import unicodedata
from datetime import datetime

from System.IO import Directory, File
from System.Text import Encoding

from pyrevit import DB

from mt_services import (
    apply_appearance_changes,
    apply_identity_changes,
    apply_material_changes,
    change_texture,
    read_appearance_editable,
)


APPDATA_DIR = os.path.join(
    os.environ.get("APPDATA", os.path.expanduser("~")),
    "pyRevit",
    "MaterialTools",
)
DB_PATH = os.path.join(APPDATA_DIR, "favorites.json")
UTF8 = Encoding.UTF8

try:
    unicode
except NameError:
    unicode = str

try:
    basestring
except NameError:
    basestring = (str, bytes)

try:
    unichr
except NameError:
    unichr = chr


class FavoriteItem(object):
    def __init__(self, data):
        self.data = data
        self.Id = data.get("id")
        self.Name = data.get("name", "")
        self.NameKey = data.get("name_key", "")
        self.Source = data.get("source_doc", "")
        self.UpdatedAt = data.get("updated_at", "")

    @property
    def Display(self):
        if self.Source:
            return "{} | {}".format(self.Name, self.Source)
        return self.Name


def _default_db():
    return {"version": 3, "mode": "snapshot", "favorites": []}


def _to_unicode(value):
    if value is None:
        return ""
    try:
        return unicode(value)
    except Exception:
        pass
    try:
        return value.ToString()
    except Exception:
        pass
    try:
        return "".join([unichr(ord(ch)) for ch in value])
    except Exception:
        return ""


def _ascii_text(value):
    text = _to_unicode(value)
    try:
        text = unicodedata.normalize("NFKD", text)
        text = "".join([c for c in text if not unicodedata.combining(c)])
    except Exception:
        pass
    try:
        ascii_bytes = text.encode("ascii", "ignore")
        try:
            return ascii_bytes.decode("ascii")
        except Exception:
            return str(ascii_bytes)
    except Exception:
        try:
            return str(text)
        except Exception:
            return ""


def _safe_text(value):
    text = _to_unicode(value)
    if isinstance(text, bytes):
        try:
            return text.decode("utf-8")
        except Exception:
            try:
                return text.decode("latin-1")
            except Exception:
                return ""
    return text


def _normalize_name(value):
    text = _ascii_text(value)
    return re.sub(r"\s+", " ", text).strip().lower()


def _json_safe(value):
    if value is None:
        return None
    if isinstance(value, dict):
        return {_ascii_text(k): _json_safe(v) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [_json_safe(v) for v in value]
    if isinstance(value, bool):
        return bool(value)
    try:
        return int(value)
    except Exception:
        pass
    try:
        return float(value)
    except Exception:
        pass
    return _safe_text(value)


def load_db():
    try:
        if not File.Exists(DB_PATH):
            return _default_db()
        return json.loads(File.ReadAllText(DB_PATH, UTF8))
    except Exception:
        return _default_db()


def save_db(data):
    if not Directory.Exists(APPDATA_DIR):
        Directory.CreateDirectory(APPDATA_DIR)
    text = json.dumps(_json_safe(data), ensure_ascii=False, indent=2, sort_keys=True)
    File.WriteAllText(DB_PATH, text, UTF8)


def _color_to_dict(color_value):
    if color_value is None:
        return None
    return {
        "r": int(color_value.Red),
        "g": int(color_value.Green),
        "b": int(color_value.Blue),
    }


def _dict_to_color(data):
    if not data:
        return None
    return DB.Color(int(data.get("r", 0)), int(data.get("g", 0)), int(data.get("b", 0)))


def _pattern_name(doc, elem_id):
    if elem_id is None or elem_id == DB.ElementId.InvalidElementId:
        return ""
    try:
        elem = doc.GetElement(elem_id)
        if elem:
            pattern = elem.GetFillPattern()
            if pattern:
                return _ascii_text(pattern.Name)
    except Exception:
        pass
    return ""


def _find_pattern_id_by_name(doc, pattern_name):
    if not pattern_name:
        return DB.ElementId.InvalidElementId
    target = _normalize_name(pattern_name)
    for elem in DB.FilteredElementCollector(doc).OfClass(DB.FillPatternElement):
        try:
            pattern = elem.GetFillPattern()
            if pattern and _normalize_name(pattern.Name) == target:
                return elem.Id
        except Exception:
            pass
    return DB.ElementId.InvalidElementId


def _get_param(material, bip):
    try:
        param = material.get_Parameter(bip)
        return _ascii_text(param.AsString()) if param else ""
    except Exception:
        return ""


def _get_lookup(material, name):
    try:
        param = material.LookupParameter(name)
        return _ascii_text(param.AsString()) if param else ""
    except Exception:
        return ""


def serialize_material(doc, material):
    appearance = read_appearance_editable(doc, material)
    name_text = _safe_text(material.Name)
    return {
        "id": _normalize_name(name_text),
        "name": name_text,
        "name_key": _normalize_name(name_text),
        "source_doc": _safe_text(doc.Title),
        "updated_at": datetime.utcnow().isoformat(),
        "graphics": {
            "color": _color_to_dict(material.Color),
            "transparency": int(material.Transparency),
            "shininess": int(material.Shininess),
            "smoothness": int(material.Smoothness),
            "use_render": bool(
                getattr(material, "UseRenderAppearanceForShading", False)
            ),
            "surface_fg_pattern": _pattern_name(
                doc, material.SurfaceForegroundPatternId
            ),
            "surface_bg_pattern": _pattern_name(
                doc, material.SurfaceBackgroundPatternId
            ),
            "cut_fg_pattern": _pattern_name(doc, material.CutForegroundPatternId),
            "cut_bg_pattern": _pattern_name(doc, material.CutBackgroundPatternId),
            "surface_fg_color": _color_to_dict(material.SurfaceForegroundPatternColor),
            "surface_bg_color": _color_to_dict(material.SurfaceBackgroundPatternColor),
            "cut_fg_color": _color_to_dict(material.CutForegroundPatternColor),
            "cut_bg_color": _color_to_dict(material.CutBackgroundPatternColor),
        },
        "identity": {
            "description": _get_param(
                material, DB.BuiltInParameter.ALL_MODEL_DESCRIPTION
            ),
            "manufacturer": _get_param(
                material, DB.BuiltInParameter.ALL_MODEL_MANUFACTURER
            ),
            "model": _get_param(material, DB.BuiltInParameter.ALL_MODEL_MODEL),
            "url": _get_param(material, DB.BuiltInParameter.ALL_MODEL_URL),
            "mark": _get_param(material, DB.BuiltInParameter.ALL_MODEL_MARK),
            "comments": _get_lookup(material, "Comments"),
            "material_class": _safe_text(material.MaterialClass),
            "category": _safe_text(material.MaterialCategory),
        },
        "appearance": {
            "schema": _safe_text(appearance.get("schema", "")),
            "diffuse_r": float(appearance.get("diffuse_r", 0.93)),
            "diffuse_g": float(appearance.get("diffuse_g", 0.93)),
            "diffuse_b": float(appearance.get("diffuse_b", 0.93)),
            "roughness": float(appearance.get("roughness", 0.5)),
            "reflectivity": float(appearance.get("reflectivity", 0.0)),
            "glossiness": float(appearance.get("glossiness", 0.0)),
            "transparency": float(appearance.get("transparency", 0.0)),
            "has_texture": bool(appearance.get("has_texture", False)),
            "texture_path": _safe_text(appearance.get("texture_path", "")),
        },
    }


def list_favorites():
    db = load_db()
    items = [FavoriteItem(x) for x in db.get("favorites", [])]
    return sorted(items, key=lambda x: x.Name.lower())


def is_favorite(material):
    key = _normalize_name(material.Name)
    return any(x.get("name_key") == key for x in load_db().get("favorites", []))


def add_or_update_favorite(doc, material):
    db = load_db()
    data = serialize_material(doc, material)
    favorites = db.get("favorites", [])
    replaced = False
    for idx, item in enumerate(favorites):
        if item.get("name_key") == data["name_key"]:
            favorites[idx] = data
            replaced = True
            break
    if not replaced:
        favorites.append(data)
    db["favorites"] = favorites
    save_db(db)
    return FavoriteItem(data)


def remove_favorite(material_or_id):
    if isinstance(material_or_id, basestring):
        key = _normalize_name(material_or_id)
    else:
        key = _normalize_name(getattr(material_or_id, "Name", ""))
    db = load_db()
    db["favorites"] = [x for x in db.get("favorites", []) if x.get("name_key") != key]
    save_db(db)


def import_favorite_to_doc(doc, favorite_data):
    target_key = favorite_data.get("name_key", "")
    if not target_key:
        return None
    for material in DB.FilteredElementCollector(doc).OfClass(DB.Material):
        try:
            if _normalize_name(material.Name) == target_key:
                return material
        except Exception:
            pass

    return None


def _unique_material_name(doc, base_name):
    base_name = base_name or "Material"
    existing_names = set()
    for material in DB.FilteredElementCollector(doc).OfClass(DB.Material):
        try:
            existing_names.add(material.Name)
        except Exception:
            pass
    if base_name not in existing_names:
        return base_name
    index = 2
    while True:
        candidate = "{}_{}".format(base_name, index)
        if candidate not in existing_names:
            return candidate
        index += 1


def create_material_from_favorite(doc, favorite_data):
    base_name = favorite_data.get("name", "Material") or "Material"
    new_name = _unique_material_name(doc, base_name)
    t = DB.Transaction(doc, "Criar Material Favorito")
    t.Start()
    try:
        new_id = DB.Material.Create(doc, new_name)
        t.Commit()
    except Exception:
        t.RollBack()
        return None

    material = doc.GetElement(new_id)
    if not material:
        return None

    if apply_favorite_to_material(doc, material, favorite_data):
        return doc.GetElement(new_id)

    cleanup = DB.Transaction(doc, "Limpar Material Favorito Incompleto")
    cleanup.Start()
    try:
        doc.Delete(new_id)
        cleanup.Commit()
    except Exception:
        cleanup.RollBack()
    return None


def get_or_create_favorite_material(doc, favorite_data):
    existing = import_favorite_to_doc(doc, favorite_data)
    if existing:
        return existing
    return create_material_from_favorite(doc, favorite_data)


def apply_favorite_to_material(doc, target_material, favorite_data):
    graphics = favorite_data.get("graphics")
    identity = favorite_data.get("identity")
    appearance = favorite_data.get("appearance")
    applied_any = False

    if graphics:
        color = graphics.get("color") or {"r": 128, "g": 128, "b": 128}
        graphics_ok = apply_material_changes(
            doc,
            target_material.Id,
            int(color.get("r", 128)),
            int(color.get("g", 128)),
            int(color.get("b", 128)),
            int(graphics.get("transparency", 0)),
            int(graphics.get("shininess", 64)),
            int(graphics.get("smoothness", 50)),
            bool(graphics.get("use_render", False)),
            _find_pattern_id_by_name(doc, graphics.get("surface_fg_pattern", "")),
            _find_pattern_id_by_name(doc, graphics.get("surface_bg_pattern", "")),
            _find_pattern_id_by_name(doc, graphics.get("cut_fg_pattern", "")),
            _find_pattern_id_by_name(doc, graphics.get("cut_bg_pattern", "")),
            _dict_to_color(graphics.get("surface_fg_color")),
            _dict_to_color(graphics.get("surface_bg_color")),
            _dict_to_color(graphics.get("cut_fg_color")),
            _dict_to_color(graphics.get("cut_bg_color")),
        )
        applied_any = applied_any or graphics_ok

    if appearance:
        appearance_ok = apply_appearance_changes(
            doc,
            target_material.AppearanceAssetId,
            float(appearance.get("diffuse_r", 0.93)),
            float(appearance.get("diffuse_g", 0.93)),
            float(appearance.get("diffuse_b", 0.93)),
            float(appearance.get("roughness", 0.5)),
            float(appearance.get("reflectivity", 0.0)),
            float(appearance.get("glossiness", 0.0)),
            float(appearance.get("transparency", 0.0)),
        )
        applied_any = applied_any or appearance_ok
        texture_path = appearance.get("texture_path", "")
        if texture_path and os.path.exists(texture_path):
            try:
                texture_ok = change_texture(doc, target_material, texture_path)
                applied_any = applied_any or texture_ok
            except Exception:
                pass

    if identity:
        identity_ok = apply_identity_changes(
            doc,
            target_material.Id,
            target_material.Name,
            identity.get("description", ""),
            identity.get("manufacturer", ""),
            identity.get("model", ""),
            identity.get("url", ""),
            identity.get("material_class", ""),
            identity.get("category", ""),
            identity.get("mark", ""),
            identity.get("comments", ""),
        )
        applied_any = applied_any or identity_ok

    return applied_any
