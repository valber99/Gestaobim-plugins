# -*- coding: utf-8 -*-
import os

import clr
clr.AddReference('PresentationFramework')
clr.AddReference('PresentationCore')
clr.AddReference('WindowsBase')
import System
import System.Windows
import System.Windows.Media
import System.Windows.Media.Imaging

from pyrevit import DB

import mt_wpf

_PATTERN_PREVIEW_CACHE = {}


class PatternItem(System.Object):
    """A fill pattern in a combo box. A .NET type, so WPF can bind its brush."""
    __namespace__ = mt_wpf.NAMESPACE

    def __init__(self, name, elem_id, preview_brush=None, drafting=True):
        self._name = name
        self.Id = elem_id
        self._preview_brush = preview_brush
        self.Drafting = drafting

    Name = mt_wpf.clr_property(System.String, '_name', '')
    PreviewBrush = mt_wpf.clr_property(System.Windows.Media.Brush, '_preview_brush')


def element_id_value(element_id):
    """Integer value of an ElementId on any Revit version (Value from 2024, IntegerValue before)."""
    try:
        return int(element_id.Value)
    except AttributeError:
        return element_id.IntegerValue


def element_name(element):
    return element.Name


def attach_to_revit(window):
    """Parent the window to Revit's main window so it never hides behind it."""
    mt_wpf.attach_to_revit(window)


class PropItem(object):
    def __init__(self, key, value):
        self.Key = PROP_DISPLAY_NAMES.get(key, key)
        self.Value = str(value)


SCHEMA_EXTRA_PROPS = {
    "GenericSchema": [
        "surfacealbedo",
        "surfaceanisotropy",
        "surfacecutout",
        "surfacendf_type",
        "surfacerotation",
        "surfaceroughness",
        "transparentcolor",
        "transparentdistance",
        "transparentior",
        "interiormodel",
        "PatternOffset",
    ],
    "CeramicSchema": ["PatternOffset", "interiormodel"],
    "MetalSchema": [
        "surfacealbedo",
        "surfaceanisotropy",
        "surfacendf_type",
        "surfacerotation",
        "surfaceroughness",
        "interiormodel",
    ],
    "GlassSchema": [
        "transparentcolor",
        "transparentdistance",
        "transparentior",
        "interiormodel",
    ],
}


PROP_DISPLAY_NAMES = {
    "ceramic_pattern_amount": "Quantidade Padrao",
    "ceramic_pattern": "Padrao",
    "ceramic_bump_amount": "Intensidade Bump",
    "ceramic_bump": "Bump",
    "ceramic_ao_on": "Occlusao Ambiental",
    "ceramic_ao_distance": "Distancia AO",
    "ceramic_ao_samples": "Amostras AO",
    "ceramic_ao_details": "Detalhes AO",
    "ceramic_roundcorners_on": "Cantos Arredondados",
    "ceramic_roundcorners_radius": "Raio Cantos",
    "ceramic_roundcorners_allow_different_materials": "Materiais Diferentes nos Cantos",
    "ceramic_application": "Aplicacao",
    "ceramic_type": "Tipo",
    "ceramic_color_by_object": "Cor por Objeto",
    "ceramic_color_colorspace": "Espaco de Cor",
    "ceramic_refl_depth": "Profundidade Reflexo",
    "reflection_glossy_samples": "Amostras Reflexo",
    "generic_diffuse": "Cor Difusa",
    "generic_roughness": "Rugosidade",
    "generic_reflectivity": "Refletividade",
    "generic_glossiness": "Brilho",
    "generic_transparency": "Transparencia",
    "generic_bump": "Bump",
    "generic_cutout": "Recorte",
    "PatternOffset": "Offset Padrao",
    "interiormodel": "Modelo Interior",
    "surfacealbedo": "Albedo da Superficie",
    "surfaceanisotropy": "Anisotropia da Superficie",
    "surfacecutout": "Recorte da Superficie",
    "surfacendf_type": "Tipo NDF da Superficie",
    "surfacerotation": "Rotacao da Superficie",
    "surfaceroughness": "Rugosidade da Superficie",
    "transparentcolor": "Cor da Transparencia",
    "transparentdistance": "Distancia da Transparencia",
    "transparentior": "IOR da Transparencia",
    "texture_main": "Textura Principal",
    "texture_bump": "Textura Bump",
    "texture_cutout": "Textura de Recorte",
    "texture_transparency": "Textura de Transparencia",
}


APPEARANCE_SKIP = [
    "BaseSchema",
    "SchemaVersion",
    "version",
    "revision",
    "assettype",
    "localtype",
    "keyword",
    "category",
    "localname",
    "UIName",
    "Hidden",
    "mode",
    "common_Shared_Asset",
    "common_Tint_toggle",
    "common_Tint_color",
    "common_Tint_color_colorspace",
    "ImplementationPreview",
    "ImplementationOGS",
    "ImplementationGeneric",
    "ImplementationMentalRay",
    "AdvancedUIDefinition",
    "UIDefinition",
    "AssetLibID",
    "VersionGUID",
    "ExchangeGUID",
    "thumbnail",
    "swatch",
    "description",
    "generic_diffuse",
    "generic_roughness",
    "generic_reflectivity",
    "generic_glossiness",
    "generic_transparency",
    "ceramic_color",
    "ceramic_bump_amount",
    "ceramic_pattern_amount",
    "ceramic_ao_on",
    "ceramic_ao_distance",
    "ceramic_ao_samples",
    "ceramic_ao_details",
    "ceramic_roundcorners_on",
    "ceramic_roundcorners_radius",
    "ceramic_roundcorners_allow_different_materials",
    "ceramic_application",
    "ceramic_type",
    "ceramic_color_by_object",
    "ceramic_color_colorspace",
    "ceramic_refl_depth",
    "reflection_glossy_samples",
]


def to_brush(color_code):
    return System.Windows.Media.BrushConverter().ConvertFromString(color_code)


def make_gradient_brush(color_left, color_right, w=56, h=20):
    group = System.Windows.Media.DrawingGroup()
    group.Children.Add(
        System.Windows.Media.GeometryDrawing(
            to_brush(color_left),
            None,
            System.Windows.Media.RectangleGeometry(System.Windows.Rect(0, 0, w, h)),
        )
    )
    group.Children.Add(
        System.Windows.Media.GeometryDrawing(
            to_brush(color_right),
            None,
            System.Windows.Media.RectangleGeometry(
                System.Windows.Rect(w / 2.0, 0, w / 2.0, h)
            ),
        )
    )
    brush = System.Windows.Media.DrawingBrush(group)
    brush.Stretch = System.Windows.Media.Stretch.Fill
    return brush


def make_checker_brush(color_a, color_b, w=56, h=20, size=8):
    group = System.Windows.Media.DrawingGroup()
    for y in range(0, h, size):
        for x in range(0, w, size):
            color = color_a if ((x / size) + (y / size)) % 2 < 1 else color_b
            group.Children.Add(
                System.Windows.Media.GeometryDrawing(
                    to_brush(color),
                    None,
                    System.Windows.Media.RectangleGeometry(
                        System.Windows.Rect(x, y, size, size)
                    ),
                )
            )
    brush = System.Windows.Media.DrawingBrush(group)
    brush.Stretch = System.Windows.Media.Stretch.Fill
    return brush


def make_texture_preview_brush(image_path):
    try:
        resolved_path = resolve_texture_path(image_path)
        if not resolved_path or not System.IO.File.Exists(resolved_path):
            return None
        stream = System.IO.FileStream(
            resolved_path,
            System.IO.FileMode.Open,
            System.IO.FileAccess.Read,
            System.IO.FileShare.ReadWrite,
        )
        try:
            bitmap = System.Windows.Media.Imaging.BitmapFrame.Create(
                stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad,
            )
            bitmap.Freeze()
        finally:
            stream.Close()
        brush = System.Windows.Media.ImageBrush(bitmap)
        brush.Stretch = System.Windows.Media.Stretch.UniformToFill
        brush.Freeze()
        return brush
    except Exception:
        return None


def make_pattern_preview(fill_pattern_element):
    if not fill_pattern_element:
        return None

    try:
        cache_key = element_id_value(fill_pattern_element.Id)
        if cache_key in _PATTERN_PREVIEW_CACHE:
            return _PATTERN_PREVIEW_CACHE[cache_key]
    except Exception:
        cache_key = None

    try:
        pattern = fill_pattern_element.GetFillPattern()
        if not pattern:
            return None
        if pattern.IsSolidFill:
            solid = to_brush("#BFC7D1")
            if cache_key is not None:
                _PATTERN_PREVIEW_CACHE[cache_key] = solid
            return solid

        width = 56.0
        height = 28.0
        group = System.Windows.Media.DrawingGroup()
        group.ClipGeometry = System.Windows.Media.RectangleGeometry(
            System.Windows.Rect(0, 0, width, height)
        )
        group.Children.Add(
            System.Windows.Media.GeometryDrawing(
                to_brush("#FAFAFA"),
                None,
                System.Windows.Media.RectangleGeometry(
                    System.Windows.Rect(0, 0, width, height)
                ),
            )
        )

        def _dash_style(segments):
            if not segments:
                return None
            values = []
            for seg in segments:
                try:
                    seg_val = float(seg)
                except Exception:
                    continue
                if abs(seg_val) < 1e-6:
                    values.extend([1.0, 1.8])
                else:
                    values.append(max(1.0, min(14.0, abs(seg_val) * 8.0)))
            if not values:
                return None
            return System.Windows.Media.DashStyle(System.Array[System.Double](values), 0)

        def _dot_pen(color):
            pen = System.Windows.Media.Pen(to_brush(color), 1.2)
            try:
                pen.StartLineCap = System.Windows.Media.PenLineCap.Round
                pen.EndLineCap = System.Windows.Media.PenLineCap.Round
            except Exception:
                pass
            return pen

        def _add_grid_preview(grid, pen_index):
            try:
                angle = float(grid.Angle)
            except Exception:
                angle = 0.0
            if abs(angle) > 6.283185307179586:
                angle = angle * 3.141592653589793 / 180.0

            try:
                spacing = abs(float(grid.Offset))
            except Exception:
                spacing = 1.0
            spacing_px = max(4.0, min(18.0, spacing * 24.0))

            try:
                origin_u = float(grid.Origin.U)
                origin_v = float(grid.Origin.V)
            except Exception:
                origin_u = 0.0
                origin_v = 0.0
            origin_shift = ((origin_u + origin_v) * 12.0) % spacing_px if spacing_px else 0.0

            segments = None
            try:
                segments = list(grid.GetSegments())
            except Exception:
                segments = None

            color = "#46515C" if pen_index == 0 else "#66717C"
            pen = System.Windows.Media.Pen(
                to_brush(color), 1.1 if pen_index == 0 else 0.9
            )
            dash_style = _dash_style(segments)
            if dash_style is not None:
                pen.DashStyle = dash_style

            dir_x = System.Math.Cos(angle)
            dir_y = System.Math.Sin(angle)
            norm_x = -dir_y
            norm_y = dir_x
            center_x = width / 2.0
            center_y = height / 2.0
            line_half = 120.0
            is_dot_pattern = False
            if segments:
                try:
                    is_dot_pattern = all(abs(float(seg)) < 1e-6 for seg in segments)
                except Exception:
                    is_dot_pattern = False

            for idx in range(-12, 13):
                offset = origin_shift + idx * spacing_px
                base_x = center_x + norm_x * offset
                base_y = center_y + norm_y * offset
                if is_dot_pattern:
                    dot_size = 1.6 if pen_index == 0 else 1.2
                    group.Children.Add(
                        System.Windows.Media.GeometryDrawing(
                            to_brush(color),
                            None,
                            System.Windows.Media.EllipseGeometry(
                                System.Windows.Point(base_x, base_y),
                                dot_size,
                                dot_size,
                            ),
                        )
                    )
                    continue
                x1 = base_x - dir_x * line_half
                y1 = base_y - dir_y * line_half
                x2 = base_x + dir_x * line_half
                y2 = base_y + dir_y * line_half
                group.Children.Add(
                    System.Windows.Media.GeometryDrawing(
                        None,
                        pen,
                        System.Windows.Media.LineGeometry(
                            System.Windows.Point(x1, y1),
                            System.Windows.Point(x2, y2),
                        ),
                    )
                )

        try:
            grids = list(pattern.GetFillGrids())
        except Exception:
            grids = []

        if grids:
            for idx, grid in enumerate(grids[:4]):
                _add_grid_preview(grid, idx)
        else:
            pen = System.Windows.Media.Pen(to_brush("#46515C"), 1.0)
            for y in range(6, 28, 6):
                group.Children.Add(
                    System.Windows.Media.GeometryDrawing(
                        None,
                        pen,
                        System.Windows.Media.LineGeometry(
                            System.Windows.Point(2, y),
                            System.Windows.Point(54, y),
                        ),
                    )
                )

        brush = System.Windows.Media.DrawingBrush(group)
        brush.ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute
        brush.Viewport = System.Windows.Rect(0, 0, width, height)
        brush.Stretch = System.Windows.Media.Stretch.Fill
        try:
            group.Freeze()
            brush.Freeze()
        except Exception:
            pass
        if cache_key is not None:
            _PATTERN_PREVIEW_CACHE[cache_key] = brush
        return brush
    except Exception:
        return None


def is_solid_fill_pattern_item(item):
    if not item:
        return False
    name = getattr(item, "Name", "")
    if name in ["<Solid Fill>", "<Preenchimento solido>"]:
        return True
    return False


def make_empty_pattern_item():
    return PatternItem("<Nenhum>", DB.ElementId.InvalidElementId, to_brush("#FFFFFF"))


def format_double_array(values):
    try:
        return ", ".join(["{:.3f}".format(v) for v in values])
    except Exception:
        return None


def format_asset_property(prop):
    try:
        color = prop.GetValueAsColor()
        return "RGB({}, {}, {})".format(color.Red, color.Green, color.Blue)
    except Exception:
        pass
    try:
        values = prop.GetValueAsDoubles()
        if values:
            return format_double_array(values)
    except Exception:
        pass
    try:
        value = prop.Value
        if value is None:
            return None
        text = str(value).strip()
        if not text:
            return None
        if (
            "Autodesk.Revit.DB.Visual.AssetProperty" in text
            or "Autodesk.Revit.DB.DoubleArray" in text
        ):
            return None
        return text
    except Exception:
        pass
    try:
        text = str(prop).strip()
        if not text:
            return None
        if (
            "Autodesk.Revit.DB.Visual.AssetProperty" in text
            or "Autodesk.Revit.DB.DoubleArray" in text
        ):
            return None
        return text
    except Exception:
        return None


def parse_hex_color(hex_str):
    hex_str = hex_str.lstrip("#")
    if len(hex_str) == 8:
        return int(hex_str[2:4], 16), int(hex_str[4:6], 16), int(hex_str[6:8], 16)
    if len(hex_str) == 6:
        return int(hex_str[0:2], 16), int(hex_str[2:4], 16), int(hex_str[4:6], 16)
    return None, None, None


def get_schema_capabilities(schema_name, has_texture):
    caps = {
        "color": True,
        "roughness": False,
        "reflectivity": False,
        "glossiness": False,
        "transparency": False,
        "texture": has_texture,
        "color_label": "Cor Difusa (Render)",
        "props_label": "Propriedades (Render)",
        "texture_label": "Textura (Preview)",
    }
    if schema_name == "GenericSchema":
        caps.update(
            {
                "roughness": True,
                "reflectivity": True,
                "glossiness": True,
                "transparency": True,
            }
        )
    elif schema_name == "CeramicSchema":
        caps.update(
            {
                "color_label": "Cor/Textura Ceramica",
                "props_label": "Propriedades Ceramicas",
                "texture": True,
            }
        )
    elif schema_name == "MetalSchema":
        caps.update(
            {
                "roughness": True,
                "color_label": "Cor do Metal",
                "props_label": "Propriedades do Metal",
            }
        )
    elif schema_name == "GlassSchema":
        caps.update(
            {
                "transparency": True,
                "color_label": "Cor do Vidro",
                "props_label": "Propriedades do Vidro",
            }
        )
    if has_texture:
        caps["color"] = False
    return caps


def file_name(path_text):
    try:
        return os.path.basename(resolve_texture_path(path_text) or path_text)
    except Exception:
        return path_text


def _texture_search_roots():
    roots = []
    for env_name in ["CommonProgramFiles", "CommonProgramFiles(x86)"]:
        base = os.environ.get(env_name, "")
        if base:
            roots.append(os.path.join(base, "Autodesk Shared", "Materials", "Textures"))
            roots.append(os.path.join(base, "Autodesk Shared", "Materials"))
    unique_roots = []
    for root in roots:
        if root and root not in unique_roots and os.path.isdir(root):
            unique_roots.append(root)
    return unique_roots


def _texture_candidates(path_text):
    raw = str(path_text).strip().strip('"')
    for chunk in raw.split("|"):
        candidate = chunk.strip()
        if not candidate:
            continue
        candidate = os.path.expandvars(candidate)
        candidate = os.path.expanduser(candidate)
        if candidate.lower().startswith("file://"):
            try:
                candidate = System.Uri(candidate).LocalPath
            except Exception:
                pass
        candidate = candidate.replace("/", "\\")
        yield candidate


def resolve_texture_path(path_text):
    try:
        if not path_text:
            return ""
        last_candidate = ""
        roots = _texture_search_roots()
        for candidate in _texture_candidates(path_text):
            last_candidate = candidate
            if System.IO.File.Exists(candidate):
                return candidate

            parts = candidate.split("\\", 1)
            if len(parts) == 2 and parts[0] in ["1", "2", "3"]:
                for root in roots:
                    joined = os.path.join(root, candidate)
                    if System.IO.File.Exists(joined):
                        return joined

            for root in roots:
                joined = os.path.join(root, candidate)
                if System.IO.File.Exists(joined):
                    return joined

        return last_candidate
    except Exception:
        return path_text
