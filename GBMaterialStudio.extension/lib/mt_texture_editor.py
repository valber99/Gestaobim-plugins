import mt_notifications
import os

from pyrevit import forms

from mt_services import apply_texture_slot_data, pop_errors, read_texture_slot_data
from mt_uihelpers import (
    attach_to_revit,
    file_name,
    make_texture_preview_brush,
    resolve_texture_path,
    to_brush,
)


def _to_float(text_value, default):
    try:
        return float(str(text_value).replace(",", "."))
    except Exception:
        return default


def show_texture_editor(doc, material, slot_name, xaml_path):
    data = read_texture_slot_data(doc, material, slot_name)
    window = forms.WPFWindow(xaml_path)
    state = dict(data)
    changed = {"value": False}

    slot_title = {
        "main": "Textura principal",
        "bump": "Textura bump",
        "cutout": "Textura de recorte",
        "transparency": "Textura de transparencia",
    }.get(slot_name, "Textura")
    window.Title = slot_title

    def refresh_preview():
        path_value = state.get("path", "")
        brush = make_texture_preview_brush(path_value)
        window.TexturePreviewLarge.Background = brush or to_brush("#D8D8D8")
        brightness = max(0.0, min(1.0, state.get("brightness", 1.0)))
        window.TexturePreviewLarge.Opacity = max(0.15, brightness)
        if state.get("invert", False):
            window.TexturePreviewOverlay.Background = to_brush("#FFFFFF")
            window.TexturePreviewOverlay.Opacity = 0.20
        else:
            window.TexturePreviewOverlay.Background = to_brush("#000000")
            window.TexturePreviewOverlay.Opacity = 0.0
        resolved = resolve_texture_path(path_value)
        window.TxtSource.Text = resolved or path_value or ""
        window.TexturePreviewLarge.UpdateLayout()
        window.TexturePreviewOverlay.UpdateLayout()

    def refresh_fields():
        refresh_preview()
        window.SliderBrightness.Value = max(
            0, min(100, state.get("brightness", 1.0) * 100.0)
        )
        window.TxtBrightness.Text = str(int(window.SliderBrightness.Value))
        window.ChkInvert.IsChecked = bool(state.get("invert", False))
        window.ChkLinkTransforms.IsChecked = bool(state.get("link_transforms", False))
        window.TxtOffsetX.Text = "{:.2f}".format(state.get("offset_x_cm", 0.0))
        window.TxtOffsetY.Text = "{:.2f}".format(state.get("offset_y_cm", 0.0))
        window.TxtRotation.Text = "{:.2f}".format(state.get("rotation", 0.0))
        window.TxtScaleX.Text = "{:.2f}".format(state.get("size_x_cm", 1.0))
        window.TxtScaleY.Text = "{:.2f}".format(state.get("size_y_cm", 1.0))
        window.ChkScaleLock.IsChecked = bool(state.get("scale_lock", False))
        window.ChkRepeatU.IsChecked = bool(state.get("repeat_u", True))
        window.ChkRepeatV.IsChecked = bool(state.get("repeat_v", True))

    def read_form_to_state():
        state["brightness"] = window.SliderBrightness.Value / 100.0
        state["invert"] = bool(window.ChkInvert.IsChecked)
        state["link_transforms"] = bool(window.ChkLinkTransforms.IsChecked)
        state["offset_x_cm"] = _to_float(
            window.TxtOffsetX.Text, state.get("offset_x_cm", 0.0)
        )
        state["offset_y_cm"] = _to_float(
            window.TxtOffsetY.Text, state.get("offset_y_cm", 0.0)
        )
        state["rotation"] = _to_float(
            window.TxtRotation.Text, state.get("rotation", 0.0)
        )
        state["size_x_cm"] = max(
            0.01, _to_float(window.TxtScaleX.Text, state.get("size_x_cm", 1.0))
        )
        state["size_y_cm"] = max(
            0.01, _to_float(window.TxtScaleY.Text, state.get("size_y_cm", 1.0))
        )
        state["scale_lock"] = bool(window.ChkScaleLock.IsChecked)
        state["repeat_u"] = bool(window.ChkRepeatU.IsChecked)
        state["repeat_v"] = bool(window.ChkRepeatV.IsChecked)

    def on_change_source(sender, args):
        path_value = forms.pick_file(
            file_ext="jpg;png;bmp;tif;tga;exr;hdr",
            title="Selecionar textura",
        )
        if not path_value:
            return
        state["path"] = path_value
        changed["value"] = True
        refresh_fields()

    def on_brightness(sender, args):
        state["brightness"] = window.SliderBrightness.Value / 100.0
        window.TxtBrightness.Text = str(int(window.SliderBrightness.Value))
        refresh_preview()

    def on_invert(sender, args):
        state["invert"] = bool(window.ChkInvert.IsChecked)
        refresh_preview()

    def on_scale_lock(sender, args):
        if window.ChkScaleLock.IsChecked:
            window.TxtScaleY.Text = window.TxtScaleX.Text

    def on_scale_x_lost_focus(sender, args):
        if window.ChkScaleLock.IsChecked:
            window.TxtScaleY.Text = window.TxtScaleX.Text

    def on_reset_transforms(sender, args):
        state["offset_x_cm"] = 0.0
        state["offset_y_cm"] = 0.0
        state["rotation"] = 0.0
        refresh_fields()

    def on_reset_scale(sender, args):
        state["size_x_cm"] = 100.0
        state["size_y_cm"] = 100.0
        state["scale_lock"] = True
        refresh_fields()

    def on_reset_repeat(sender, args):
        state["repeat_u"] = True
        state["repeat_v"] = True
        state["link_transforms"] = False
        refresh_fields()

    def on_apply(sender, args):
        read_form_to_state()
        if apply_texture_slot_data(doc, material, slot_name, state):
            changed["value"] = True
            mt_notifications.toast("Textura atualizada.", title="Editor de textura")
            window.Close()
        else:
            reasons = pop_errors()
            forms.alert(
                "Nao foi possivel atualizar este slot de textura.\n\n{0}".format(
                    "\n".join(reasons) if reasons else "Motivo nao informado pelo Revit."
                ),
                title="Editor de textura",
            )

    def on_close(sender, args):
        window.Close()

    refresh_fields()
    window.BtnChangeSource.Click += on_change_source
    window.SliderBrightness.ValueChanged += on_brightness
    window.ChkInvert.Click += on_invert
    window.ChkScaleLock.Click += on_scale_lock
    window.TxtScaleX.LostFocus += on_scale_x_lost_focus
    window.BtnResetTransforms.Click += on_reset_transforms
    window.BtnResetScale.Click += on_reset_scale
    window.BtnResetRepeat.Click += on_reset_repeat
    window.BtnApply.Click += on_apply
    window.BtnClose.Click += on_close
    attach_to_revit(window)
    window.ShowDialog()
    return changed["value"]
