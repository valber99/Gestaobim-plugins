import mt_notifications
import os
import shutil
import System
import traceback
import clr

clr.AddReference("System.Windows.Forms")

from System.Windows.Forms import DialogResult, SaveFileDialog

import System.Windows
from System.Collections.Generic import List
from pyrevit import DB, UI

import mt_dialogs
import mt_wpf

from mt_services import (
    apply_appearance_changes,
    apply_identity_changes,
    apply_material_changes,
    duplicate_material,
    get_texture_slot_availability,
    get_fill_patterns,
    paint_material_on_faces,
    pop_errors,
    read_appearance_asset,
    read_appearance_editable,
    set_texture_slot,
)
from mt_favorites import (
    DB_PATH,
    add_or_update_favorite,
    get_or_create_favorite_material,
    is_favorite,
    list_favorites,
    load_db,
    remove_favorite,
)
from mt_modeless import hold, in_revit
from mt_texture_editor import show_texture_editor
from mt_uihelpers import (
    PropItem,
    attach_to_revit,
    file_name,
    get_schema_capabilities,
    is_solid_fill_pattern_item,
    make_checker_brush,
    make_empty_pattern_item,
    make_gradient_brush,
    make_texture_preview_brush,
    parse_hex_color,
    resolve_texture_path,
    to_brush,
)


def show_material_editor(doc, uidoc, material, xaml_path, modeless=False):
    """Open the material editor.

    modeless=False blocks until the window closes (the caller is inside a valid
    API context the whole time). modeless=True returns at once and keeps the
    window open next to Revit; handlers that touch the model are wrapped with
    api() so they run inside an ExternalEvent.
    """
    api = in_revit if modeless else (lambda function: function)
    window = mt_wpf.Window(xaml_path)
    texture_editor_xaml = os.path.join(
        os.path.dirname(xaml_path), "TextureEditorWindow.xaml"
    )
    editor_request = {"open_native": False, "next_material": None, "paint": None}
    state = {
        "r": material.Color.Red,
        "g": material.Color.Green,
        "b": material.Color.Blue,
        "transparency": material.Transparency,
        "shininess": material.Shininess,
        "smoothness": material.Smoothness,
        "surface_fg_color": material.SurfaceForegroundPatternColor,
        "surface_bg_color": material.SurfaceBackgroundPatternColor,
        "cut_fg_color": material.CutForegroundPatternColor,
        "cut_bg_color": material.CutBackgroundPatternColor,
    }
    appearance = read_appearance_editable(doc, material)
    app_state = dict(appearance)

    def update_color_display():
        hex_color = "#%02X%02X%02X" % (state["r"], state["g"], state["b"])
        brush = to_brush(hex_color)
        window.ColorSwatch.Background = brush
        window.ColorPreview.Background = brush
        window.ColorRGB.Text = "{}, {}, {}".format(state["r"], state["g"], state["b"])
        window.ColorHex.Text = hex_color

    def update_render_color_display():
        r = int(app_state["diffuse_r"] * 255)
        g = int(app_state["diffuse_g"] * 255)
        b = int(app_state["diffuse_b"] * 255)
        window.RenderColorPreview.Background = to_brush("#%02X%02X%02X" % (r, g, b))
        window.RenderColorRGB.Text = "{:.2f}, {:.2f}, {:.2f}".format(
            app_state["diffuse_r"], app_state["diffuse_g"], app_state["diffuse_b"]
        )

    def update_pattern_preview(control_name, item):
        getattr(window, control_name).Background = (
            item.PreviewBrush if item else to_brush("#FFFFFF")
        )

    def set_swatch(control_name, color_value, fallback="#FFFFFF"):
        swatch = getattr(window, control_name)
        if color_value is None:
            swatch.Background = to_brush(fallback)
            return
        swatch.Background = to_brush(
            "#%02X%02X%02X" % (color_value.Red, color_value.Green, color_value.Blue)
        )

    def update_pattern_controls(combo_name, preview_name, button_name, none_item_ref):
        combo = getattr(window, combo_name)
        button = getattr(window, button_name)
        item = combo.SelectedItem or none_item_ref
        update_pattern_preview(preview_name, item)
        button.IsEnabled = item.Id != DB.ElementId.InvalidElementId

    def set_combo_to_pattern(combo, pattern_item):
        combo.SelectedItem = (
            pattern_item if pattern_item in list(combo.ItemsSource) else none_item
        )

    def find_pattern_item_by_name(pattern_name):
        for item in pattern_list:
            if getattr(item, "Name", "") == pattern_name:
                return item
        return None

    def find_solid_pattern_item():
        for item in pattern_list:
            if is_solid_fill_pattern_item(item):
                return item
        return find_pattern_item_by_name("<Solid Fill>")

    def current_pattern_summary():
        surface_name = getattr(
            window.CmbSurfacePattern.SelectedItem or none_item, "Name", "<Nenhum>"
        )
        surface_bg_name = getattr(
            window.CmbSurfaceBgPattern.SelectedItem or none_item, "Name", "<Nenhum>"
        )
        cut_name = getattr(
            window.CmbCutPattern.SelectedItem or none_item, "Name", "<Nenhum>"
        )
        cut_bg_name = getattr(
            window.CmbCutBgPattern.SelectedItem or none_item, "Name", "<Nenhum>"
        )
        return (
            "Superficie FG: {0} | Superficie BG: {1} | Corte FG: {2} | Corte BG: {3}"
        ).format(surface_name, surface_bg_name, cut_name, cut_bg_name)

    def update_pattern_info():
        window.PatternInfo.Text = current_pattern_summary()

    def update_roughness_preview(val):
        dark = int(220 - (120 * val))
        light = int(245 - (80 * val))
        window.PreviewRoughness.Background = make_gradient_brush(
            "#{:02X}{:02X}{:02X}".format(light, light, light),
            "#{:02X}{:02X}{:02X}".format(dark, dark, dark),
        )

    def update_reflectivity_preview(val):
        blue = int(190 + (65 * val))
        window.PreviewReflectivity.Background = make_gradient_brush(
            "#EAEAEA", "#{:02X}{:02X}{:02X}".format(180, 210, blue)
        )

    def update_glossiness_preview(val):
        high = int(210 + (45 * val))
        low = int(190 - (50 * val))
        window.PreviewGlossiness.Background = make_gradient_brush(
            "#{:02X}{:02X}{:02X}".format(low, low, low),
            "#{:02X}{:02X}{:02X}".format(high, high, high),
        )

    def update_transparency_preview(val):
        alpha_mix = int(255 - (110 * val))
        window.PreviewTransparency.Background = make_checker_brush(
            "#F2F2F2", "#{:02X}{:02X}{:02X}".format(alpha_mix, alpha_mix, 255)
        )

    def set_preview_enabled(preview_name, is_enabled):
        preview = getattr(window, preview_name)
        preview.Opacity = 1.0 if is_enabled else 0.35
        preview.BorderBrush = to_brush("#CCCCCC" if is_enabled else "#DDDDDD")

    def configure_appearance_ui():
        visible = System.Windows.Visibility.Visible
        collapsed = System.Windows.Visibility.Collapsed
        caps = get_schema_capabilities(app_state["schema"], app_state["has_texture"])
        editable_parts = []
        for key, label in [
            ("color", "cor"),
            ("roughness", "rugosidade"),
            ("reflectivity", "refletividade"),
            ("glossiness", "brilho"),
            ("transparency", "transparencia"),
            ("texture", "textura"),
        ]:
            if caps.get(key):
                editable_parts.append(label)
        window.GrpRenderColor.Header = caps["color_label"]
        window.GrpRenderProps.Header = caps["props_label"]
        window.GrpTexturePreview.Header = caps["texture_label"]
        window.SchemaCapsInfo.Text = (
            "Recursos editaveis neste schema: {0}.".format(
                ", ".join(editable_parts) if editable_parts else "nenhum"
            )
        )
        window.BtnPickRenderColor.IsEnabled = caps["color"]
        window.BtnChangeTexture.IsEnabled = caps["texture"]
        window.SliderRoughness.IsEnabled = caps["roughness"]
        window.SliderReflectivity.IsEnabled = caps["reflectivity"]
        window.SliderGlossiness.IsEnabled = caps["glossiness"]
        window.SliderRenderTransp.IsEnabled = caps["transparency"]
        window.RowRoughness.Visibility = visible if caps["roughness"] else collapsed
        window.RowReflectivity.Visibility = (
            visible if caps["reflectivity"] else collapsed
        )
        window.RowGlossiness.Visibility = visible if caps["glossiness"] else collapsed
        window.RowRenderTransp.Visibility = (
            visible if caps["transparency"] else collapsed
        )
        has_any_prop = (
            caps["roughness"]
            or caps["reflectivity"]
            or caps["glossiness"]
            or caps["transparency"]
        )
        window.GrpRenderProps.Visibility = visible if has_any_prop else collapsed
        window.GrpTexturePreview.Visibility = visible if caps["texture"] else collapsed
        window.GrpTextureEditor.Visibility = visible
        set_preview_enabled("PreviewRoughness", caps["roughness"])
        set_preview_enabled("PreviewReflectivity", caps["reflectivity"])
        set_preview_enabled("PreviewGlossiness", caps["glossiness"])
        set_preview_enabled("PreviewTransparency", caps["transparency"])
        if app_state["has_texture"]:
            window.TextureInfo.Text = (
                "Schema: {} | textura conectada controla a aparencia principal.".format(
                    app_state["schema"] or "N/A"
                )
            )
        elif not caps["color"]:
            window.TextureInfo.Text = "Schema: {} | este material nao usa cor manual nesta propriedade.".format(
                app_state["schema"] or "N/A"
            )
        else:
            window.TextureInfo.Text = "Schema: {} | cor editavel.".format(
                app_state["schema"] or "N/A"
            )

    def refresh_favorites_ui():
        try:
            db_data = load_db()
            window.LstFavorites.ItemsSource = mt_wpf.net_list(list_favorites(db_data))
            count = len(db_data.get("favorites", []))
            current_is_favorite = is_favorite(material, db_data)
            window.FavoritesInfo.Text = (
                "Biblioteca protegida por backup e escrita atomica. "
                "Pintar favorito transfere o material nativo com o projeto de origem aberto no mesmo Revit. "
                "Localizar usa a identidade original, nunca apenas o nome. "
                "Snapshots antigos sao preservados, mas precisam ser favoritados novamente na origem para transferencia completa. "
                "Itens salvos: {}. Arquivo: {}".format(count, DB_PATH)
            )
            window.BtnFavorite.Content = (
                "[Favorito]" if current_is_favorite else "Favoritar"
            )
            window.BtnFavorite.Background = (
                to_brush("#FFF1CC") if current_is_favorite else to_brush("#E1E1E1")
            )
            window.BtnFavorite.BorderBrush = (
                to_brush("#D2A23A") if current_is_favorite else to_brush("#CCCCCC")
            )
            window.ColorSwatch.BorderBrush = (
                to_brush("#D2A23A") if current_is_favorite else to_brush("#999999")
            )
            window.FavoriteState.Text = (
                "Material favorito salvo" if current_is_favorite else ""
            )
        except Exception as ex:
            window.FavoritesInfo.Text = "Erro ao carregar favoritos: {}".format(ex)

    def update_texture_slots_ui():
        textures = app_state.get("textures", {}) or {}
        slot_caps = get_texture_slot_availability(doc, material)
        main_path = textures.get("main", "")
        bump_path = textures.get("bump", "")
        cutout_path = textures.get("cutout", "")
        transp_path = textures.get("transparency", "")
        main_resolved = resolve_texture_path(main_path)
        bump_resolved = resolve_texture_path(bump_path)
        cutout_resolved = resolve_texture_path(cutout_path)
        transp_resolved = resolve_texture_path(transp_path)

        window.TxtMainTexture.Text = (
            file_name(main_path) if main_path else "Sem textura"
        )
        window.TxtBumpTexture.Text = (
            file_name(bump_path) if bump_path else "Sem textura"
        )
        window.TxtCutoutTexture.Text = (
            file_name(cutout_path) if cutout_path else "Sem textura"
        )
        window.TxtTransparencyTexture.Text = (
            file_name(transp_path) if transp_path else "Sem textura"
        )

        window.BtnSaveMainTexture.IsEnabled = bool(
            main_resolved and os.path.exists(main_resolved)
        )
        window.BtnSaveBumpTexture.IsEnabled = bool(
            bump_resolved and os.path.exists(bump_resolved)
        )
        window.BtnSaveCutoutTexture.IsEnabled = bool(
            cutout_resolved and os.path.exists(cutout_resolved)
        )
        window.BtnSaveTransparencyTexture.IsEnabled = bool(
            transp_resolved and os.path.exists(transp_resolved)
        )
        window.BtnChangeMainTexture.IsEnabled = bool(slot_caps.get("main"))
        window.BtnChangeBumpTexture.IsEnabled = bool(slot_caps.get("bump"))
        window.BtnChangeCutoutTexture.IsEnabled = bool(slot_caps.get("cutout"))
        window.BtnChangeTransparencyTexture.IsEnabled = bool(
            slot_caps.get("transparency")
        )

        slot_count = len(
            [x for x in [main_path, bump_path, cutout_path, transp_path] if x]
        )
        window.TextureSlotsInfo.Text = (
            (
                "Texturas detectadas: {0} | Slots editaveis: principal={1}, bump={2}, recorte={3}, transparencia={4}"
            ).format(
                slot_count,
                "sim" if slot_caps.get("main") else "nao",
                "sim" if slot_caps.get("bump") else "nao",
                "sim" if slot_caps.get("cutout") else "nao",
                "sim" if slot_caps.get("transparency") else "nao",
            )
            if slot_count or any(slot_caps.values())
            else "Nenhuma textura detectada automaticamente e nenhum slot editavel foi localizado neste material."
        )

    def save_texture_slot(slot_name):
        textures = app_state.get("textures", {}) or {}
        source_path = resolve_texture_path(textures.get(slot_name, ""))
        if not source_path or not os.path.exists(source_path):
            mt_dialogs.alert("Nao ha textura salva neste slot.", title="Texturas")
            return
        dialog = SaveFileDialog()
        dialog.Title = "Salvar textura"
        dialog.FileName = os.path.basename(source_path)
        if dialog.ShowDialog() == DialogResult.OK:
            try:
                shutil.copyfile(source_path, dialog.FileName)
            except (IOError, OSError) as error:
                mt_dialogs.alert("Nao foi possivel salvar a textura.\n\n{}".format(error), title="Texturas")
                return
            mt_notifications.toast("Textura salva em {}".format(dialog.FileName), title="Texturas")

    def change_texture_slot(slot_name, success_message):
        img_path = mt_dialogs.pick_file(
            file_ext="jpg;png;bmp;tif;tga;exr;hdr",
            title="Selecionar Textura",
        )
        if not img_path:
            return
        if set_texture_slot(doc, material, slot_name, img_path):
            app_state.setdefault("textures", {})[slot_name] = img_path
            if slot_name == "main":
                app_state["has_texture"] = True
                app_state["texture_path"] = img_path
                window.TexturePreview.Background = make_texture_preview_brush(
                    img_path
                ) or to_brush("#F0F0F0")
                window.TexturePreviewInfo.Text = file_name(img_path)
            update_texture_slots_ui()
            configure_appearance_ui()
            mt_notifications.toast(success_message, title="Sucesso")
        else:
            reasons = pop_errors()
            mt_dialogs.alert(
                "Nao foi possivel trocar a textura deste slot.\n\n{}".format(
                    "\n".join(reasons) if reasons else "Verifique se o arquivo existe."
                ),
                title="Texturas",
            )

    def on_surface_solid(sender, args):
        solid_item = find_solid_pattern_item()
        set_combo_to_pattern(window.CmbSurfacePattern, solid_item)
        update_pattern_controls(
            "CmbSurfacePattern", "SurfaceFgPatternPreview", "BtnSurfaceFgColor", none_item
        )
        update_pattern_info()

    def on_surface_clear_bg(sender, args):
        set_combo_to_pattern(window.CmbSurfaceBgPattern, none_item)
        update_pattern_controls(
            "CmbSurfaceBgPattern", "SurfaceBgPatternPreview", "BtnSurfaceBgColor", none_item
        )
        update_pattern_info()

    def on_cut_solid(sender, args):
        solid_item = find_solid_pattern_item()
        set_combo_to_pattern(window.CmbCutPattern, solid_item)
        update_pattern_controls(
            "CmbCutPattern", "CutFgPatternPreview", "BtnCutFgColor", none_item
        )
        update_pattern_info()

    def on_copy_surface_to_cut(sender, args):
        set_combo_to_pattern(window.CmbCutPattern, window.CmbSurfacePattern.SelectedItem)
        set_combo_to_pattern(window.CmbCutBgPattern, window.CmbSurfaceBgPattern.SelectedItem)
        state["cut_fg_color"] = state["surface_fg_color"]
        state["cut_bg_color"] = state["surface_bg_color"]
        set_swatch("CutFgColorSwatch", state["cut_fg_color"])
        set_swatch("CutBgColorSwatch", state["cut_bg_color"])
        update_pattern_controls(
            "CmbCutPattern", "CutFgPatternPreview", "BtnCutFgColor", none_item
        )
        update_pattern_controls(
            "CmbCutBgPattern", "CutBgPatternPreview", "BtnCutBgColor", none_item
        )
        update_pattern_info()

    update_color_display()
    window.MaterialName.Text = material.Name
    window.MaterialClass.Text = "{} | {}".format(
        material.MaterialClass, material.MaterialCategory
    )
    window.ChkRenderAppearance.IsChecked = material.UseRenderAppearanceForShading
    window.BtnPickColor.IsEnabled = not material.UseRenderAppearanceForShading
    window.SliderTransparency.Value = state["transparency"]
    window.ValTransparency.Text = "{}%".format(state["transparency"])
    window.SliderShininess.Value = state["shininess"]
    window.ValShininess.Text = str(state["shininess"])
    window.SliderSmoothness.Value = state["smoothness"]
    window.ValSmoothness.Text = str(state["smoothness"])

    none_item = make_empty_pattern_item()
    all_patterns = get_fill_patterns(doc)
    pattern_list = [none_item] + all_patterns
    cut_pattern_list = [none_item] + [item for item in all_patterns if item.Drafting]
    window.CmbSurfacePattern.ItemsSource = mt_wpf.net_list(pattern_list)
    window.CmbSurfaceBgPattern.ItemsSource = mt_wpf.net_list(pattern_list)
    window.CmbCutPattern.ItemsSource = mt_wpf.net_list(cut_pattern_list)
    window.CmbCutBgPattern.ItemsSource = mt_wpf.net_list(cut_pattern_list)
    for item in pattern_list:
        if item.Id == material.SurfaceForegroundPatternId:
            window.CmbSurfacePattern.SelectedItem = item
        if item.Id == material.SurfaceBackgroundPatternId:
            window.CmbSurfaceBgPattern.SelectedItem = item
        if item.Id == material.CutForegroundPatternId:
            window.CmbCutPattern.SelectedItem = item
        if item.Id == material.CutBackgroundPatternId:
            window.CmbCutBgPattern.SelectedItem = item
    update_pattern_controls(
        "CmbSurfacePattern", "SurfaceFgPatternPreview", "BtnSurfaceFgColor", none_item
    )
    update_pattern_controls(
        "CmbSurfaceBgPattern", "SurfaceBgPatternPreview", "BtnSurfaceBgColor", none_item
    )
    update_pattern_controls(
        "CmbCutPattern", "CutFgPatternPreview", "BtnCutFgColor", none_item
    )
    update_pattern_controls(
        "CmbCutBgPattern", "CutBgPatternPreview", "BtnCutBgColor", none_item
    )
    update_pattern_info()
    set_swatch("SurfaceFgColorSwatch", state["surface_fg_color"])
    set_swatch("SurfaceBgColorSwatch", state["surface_bg_color"])
    set_swatch("CutFgColorSwatch", state["cut_fg_color"])
    set_swatch("CutBgColorSwatch", state["cut_bg_color"])

    schema_name, appearance_props = read_appearance_asset(doc, material)
    window.SchemaName.Text = schema_name if schema_name else "N/A"
    window.AppearanceProps.ItemsSource = mt_wpf.net_list(appearance_props or [PropItem("Info", "Sem propriedades extras relevantes para este schema.")])
    update_render_color_display()
    window.SliderRoughness.Value = app_state["roughness"] * 100
    window.ValRoughness.Text = "{:.0f}%".format(app_state["roughness"] * 100)
    update_roughness_preview(app_state["roughness"])
    window.SliderReflectivity.Value = app_state["reflectivity"] * 100
    window.ValReflectivity.Text = "{:.0f}%".format(app_state["reflectivity"] * 100)
    update_reflectivity_preview(app_state["reflectivity"])
    window.SliderGlossiness.Value = app_state["glossiness"] * 100
    window.ValGlossiness.Text = "{:.0f}%".format(app_state["glossiness"] * 100)
    update_glossiness_preview(app_state["glossiness"])
    window.SliderRenderTransp.Value = app_state["transparency"] * 100
    window.ValRenderTransp.Text = "{:.0f}%".format(app_state["transparency"] * 100)
    update_transparency_preview(app_state["transparency"])
    if appearance["has_texture"]:
        texture_brush = make_texture_preview_brush(appearance["texture_path"])
        resolved_preview_path = resolve_texture_path(appearance["texture_path"])
        window.TexturePreview.Background = texture_brush or to_brush("#F0F0F0")
        window.TexturePreviewInfo.Text = (
            file_name(appearance["texture_path"])
            if texture_brush
            else "Nao foi possivel gerar preview da textura: {}".format(
                resolved_preview_path
            )
        )
    else:
        window.TexturePreview.Background = to_brush("#F0F0F0")
        window.TexturePreviewInfo.Text = "Sem textura conectada."
    update_texture_slots_ui()
    configure_appearance_ui()
    refresh_favorites_ui()

    window.TxtName.Text = material.Name
    window.TxtClass.Text = material.MaterialClass
    window.TxtCategory.Text = material.MaterialCategory
    for bip, ctrl in [
        (DB.BuiltInParameter.ALL_MODEL_DESCRIPTION, window.TxtDescription),
        (DB.BuiltInParameter.ALL_MODEL_MANUFACTURER, window.TxtManufacturer),
        (DB.BuiltInParameter.ALL_MODEL_MODEL, window.TxtModel),
        (DB.BuiltInParameter.ALL_MODEL_URL, window.TxtURL),
        (DB.BuiltInParameter.ALL_MODEL_MARK, window.TxtMark),
    ]:
        param = material.get_Parameter(bip)
        if param and param.AsString():
            ctrl.Text = param.AsString()
    comments_param = material.LookupParameter("Comments")
    if comments_param and comments_param.AsString():
        window.TxtComments.Text = comments_param.AsString()

    def on_pick_hex(target, swatch_name, is_render=False):
        result = mt_dialogs.ask_for_color()
        if not (result and hasattr(result, "startswith") and result.startswith("#")):
            return
        r, g, b = parse_hex_color(result)
        if r is None:
            return
        if target == "graphics":
            state["r"], state["g"], state["b"] = r, g, b
            update_color_display()
        elif target == "render":
            app_state["diffuse_r"] = r / 255.0
            app_state["diffuse_g"] = g / 255.0
            app_state["diffuse_b"] = b / 255.0
            update_render_color_display()
        else:
            state[target] = DB.Color(r, g, b)
            set_swatch(swatch_name, state[target])

    def on_change_texture(sender, args):
        change_texture_slot("main", "Textura principal alterada!")

    def open_texture_editor(slot_name):
        if show_texture_editor(doc, material, slot_name, texture_editor_xaml):
            editor_request["next_material"] = doc.GetElement(material.Id)
            window.Close()

    def on_open_main_texture_editor(sender, args):
        open_texture_editor("main")

    def apply_all_steps():
        steps = [
            lambda: apply_material_changes(
                doc,
                material.Id,
                state["r"],
                state["g"],
                state["b"],
                state["transparency"],
                state["shininess"],
                state["smoothness"],
                bool(window.ChkRenderAppearance.IsChecked),
                (window.CmbSurfacePattern.SelectedItem or none_item).Id,
                (window.CmbSurfaceBgPattern.SelectedItem or none_item).Id,
                (window.CmbCutPattern.SelectedItem or none_item).Id,
                (window.CmbCutBgPattern.SelectedItem or none_item).Id,
                state["surface_fg_color"],
                state["surface_bg_color"],
                state["cut_fg_color"],
                state["cut_bg_color"],
            ),
            lambda: apply_appearance_changes(
                doc,
                material.AppearanceAssetId,
                app_state["diffuse_r"],
                app_state["diffuse_g"],
                app_state["diffuse_b"],
                app_state["roughness"],
                app_state["reflectivity"],
                app_state["glossiness"],
                app_state["transparency"],
            ),
            lambda: apply_identity_changes(
                doc,
                material.Id,
                window.TxtName.Text,
                window.TxtDescription.Text,
                window.TxtManufacturer.Text,
                window.TxtModel.Text,
                window.TxtURL.Text,
                window.TxtClass.Text,
                window.TxtCategory.Text,
                window.TxtMark.Text,
                window.TxtComments.Text,
            ),
        ]
        return all([step() for step in steps])

    def on_apply(sender, args):
        pop_errors()
        group = DB.TransactionGroup(doc, "Editar material")
        group.Start()
        try:
            applied = apply_all_steps()
        except Exception as error:
            applied = False
            pop_errors()
            reasons = [str(error)]
        else:
            reasons = pop_errors()
        if not applied:
            group.RollBack()
            mt_dialogs.alert(
                "Nenhuma alteracao foi aplicada.\n\n{}".format(
                    "\n".join(reasons) if reasons else "Motivo nao informado pelo Revit."
                ),
                title="Editar material",
            )
            return
        group.Assimilate()
        mt_dialogs.alert("Material atualizado com sucesso!", title="Sucesso")

    def on_new_material(sender, args):
        new_name = mt_dialogs.ask_for_string(
            default="{}_copy".format(material.Name),
            prompt="Nome do novo material",
            title="Novo Material",
        )
        if not new_name:
            return
        pop_errors()
        new_material = duplicate_material(doc, material, new_name)
        if not new_material:
            reasons = pop_errors()
            mt_dialogs.alert(
                "Nao foi possivel criar o novo material.\n\n{}".format("\n".join(reasons)),
                title="Erro",
            )
            return
        mt_notifications.toast("Material criado: {}".format(new_material.Name), title="Sucesso")
        editor_request["next_material"] = new_material
        window.Close()

    def on_toggle_favorite(sender, args):
        try:
            if is_favorite(material):
                if not mt_dialogs.alert("Remover este favorito da biblioteca? Um backup sera preservado.", title="Favoritos", yes=True, no=True):
                    return
                remove_favorite(material)
                mt_notifications.toast("Material removido dos favoritos.", title="Favoritos")
            else:
                add_or_update_favorite(doc, material)
                mt_notifications.toast("Material salvo nos favoritos.", title="Favoritos")
            refresh_favorites_ui()
        except Exception as ex:
            mt_dialogs.alert("Erro ao alterar favorito: {}".format(ex), title="Favoritos")

    def on_refresh_favorites(sender, args):
        refresh_favorites_ui()

    def on_remove_favorite(sender, args):
        item = window.LstFavorites.SelectedItem
        if not item:
            mt_dialogs.alert("Selecione um favorito na lista.", title="Favoritos")
            return
        if not mt_dialogs.alert("Remover este favorito da biblioteca? Um backup sera preservado.", title="Favoritos", yes=True, no=True):
            return
        try:
            remove_favorite(item.Id)
        except Exception as error:
            mt_dialogs.alert(str(error), title="Favoritos")
            return
        refresh_favorites_ui()
        mt_notifications.toast("Favorito removido.", title="Favoritos")

    def on_import_favorite(sender, args):
        item = window.LstFavorites.SelectedItem
        if not item:
            mt_dialogs.alert("Selecione um favorito na lista.", title="Favoritos")
            return
        try:
            target_material = get_or_create_favorite_material(doc, item.data)
        except Exception as error:
            mt_dialogs.alert(str(error), title="Favoritos - transferencia cancelada")
            return
        if not target_material:
            mt_dialogs.alert(
                "Este favorito salva apenas o nome do material. "
                "Nenhum material com esse nome foi encontrado no projeto atual.",
                title="Favoritos",
            )
            return
        mt_notifications.toast(
            "Material localizado no projeto: {}".format(target_material.Name),
            title="Favoritos",
        )
        editor_request["next_material"] = target_material
        window.Close()

    def on_paint_favorite(sender, args):
        item = window.LstFavorites.SelectedItem
        if not item:
            mt_dialogs.alert("Selecione um favorito na lista.", title="Favoritos")
            return

        try:
            favorite_material = get_or_create_favorite_material(doc, item.data)
        except Exception as error:
            mt_dialogs.alert(str(error), title="Favoritos - transferencia cancelada")
            return
        if not favorite_material:
            mt_dialogs.alert(
                "Nao foi possivel carregar o favorito para pintura.",
                title="Favoritos",
            )
            return

        editor_request["paint"] = (
            favorite_material,
            item.Name,
            "Pintando com favorito: {}. Clique nas faces e pressione ESC para sair.".format(
                favorite_material.Name
            ),
        )
        window.Close()

    def on_paint(sender, args):
        editor_request["paint"] = (
            material,
            material.Name,
            "Modo Paint ativo. Clique nas faces e pressione ESC para sair.",
        )
        window.Close()

    def on_editor(sender, args):
        selection_ids = List[DB.ElementId]()
        selection_ids.Add(material.Id)
        uidoc.Selection.SetElementIds(selection_ids)
        editor_request["open_native"] = True
        window.Close()

    def on_close(sender, args):
        window.Close()

    window.SliderTransparency.ValueChanged += lambda s, a: [
        state.__setitem__("transparency", int(window.SliderTransparency.Value)),
        setattr(
            window.ValTransparency,
            "Text",
            "{}%".format(int(window.SliderTransparency.Value)),
        ),
    ]
    window.SliderShininess.ValueChanged += lambda s, a: [
        state.__setitem__("shininess", int(window.SliderShininess.Value)),
        setattr(window.ValShininess, "Text", str(int(window.SliderShininess.Value))),
    ]
    window.SliderSmoothness.ValueChanged += lambda s, a: [
        state.__setitem__("smoothness", int(window.SliderSmoothness.Value)),
        setattr(window.ValSmoothness, "Text", str(int(window.SliderSmoothness.Value))),
    ]
    window.SliderRoughness.ValueChanged += lambda s, a: [
        app_state.__setitem__("roughness", window.SliderRoughness.Value / 100.0),
        setattr(
            window.ValRoughness, "Text", "{:.0f}%".format(window.SliderRoughness.Value)
        ),
        update_roughness_preview(window.SliderRoughness.Value / 100.0),
    ]
    window.SliderReflectivity.ValueChanged += lambda s, a: [
        app_state.__setitem__("reflectivity", window.SliderReflectivity.Value / 100.0),
        setattr(
            window.ValReflectivity,
            "Text",
            "{:.0f}%".format(window.SliderReflectivity.Value),
        ),
        update_reflectivity_preview(window.SliderReflectivity.Value / 100.0),
    ]
    window.SliderGlossiness.ValueChanged += lambda s, a: [
        app_state.__setitem__("glossiness", window.SliderGlossiness.Value / 100.0),
        setattr(
            window.ValGlossiness,
            "Text",
            "{:.0f}%".format(window.SliderGlossiness.Value),
        ),
        update_glossiness_preview(window.SliderGlossiness.Value / 100.0),
    ]
    window.SliderRenderTransp.ValueChanged += lambda s, a: [
        app_state.__setitem__("transparency", window.SliderRenderTransp.Value / 100.0),
        setattr(
            window.ValRenderTransp,
            "Text",
            "{:.0f}%".format(window.SliderRenderTransp.Value),
        ),
        update_transparency_preview(window.SliderRenderTransp.Value / 100.0),
    ]
    window.ChkRenderAppearance.Click += lambda s, a: setattr(
        window.BtnPickColor, "IsEnabled", not window.ChkRenderAppearance.IsChecked
    )
    window.CmbSurfacePattern.SelectionChanged += lambda s, a: update_pattern_controls(
        "CmbSurfacePattern", "SurfaceFgPatternPreview", "BtnSurfaceFgColor", none_item
    )
    window.CmbSurfacePattern.SelectionChanged += lambda s, a: update_pattern_info()
    window.CmbSurfaceBgPattern.SelectionChanged += lambda s, a: update_pattern_controls(
        "CmbSurfaceBgPattern", "SurfaceBgPatternPreview", "BtnSurfaceBgColor", none_item
    )
    window.CmbSurfaceBgPattern.SelectionChanged += lambda s, a: update_pattern_info()
    window.CmbCutPattern.SelectionChanged += lambda s, a: update_pattern_controls(
        "CmbCutPattern", "CutFgPatternPreview", "BtnCutFgColor", none_item
    )
    window.CmbCutPattern.SelectionChanged += lambda s, a: update_pattern_info()
    window.CmbCutBgPattern.SelectionChanged += lambda s, a: update_pattern_controls(
        "CmbCutBgPattern", "CutBgPatternPreview", "BtnCutBgColor", none_item
    )
    window.CmbCutBgPattern.SelectionChanged += lambda s, a: update_pattern_info()
    window.BtnSurfaceSolid.Click += on_surface_solid
    window.BtnSurfaceClearBg.Click += on_surface_clear_bg
    window.BtnCutSolid.Click += on_cut_solid
    window.BtnCopySurfaceToCut.Click += on_copy_surface_to_cut
    window.BtnPickColor.Click += lambda s, a: on_pick_hex("graphics", None)
    window.BtnPickRenderColor.Click += lambda s, a: on_pick_hex("render", None)
    window.BtnSurfaceFgColor.Click += lambda s, a: on_pick_hex(
        "surface_fg_color", "SurfaceFgColorSwatch"
    )
    window.BtnSurfaceBgColor.Click += lambda s, a: on_pick_hex(
        "surface_bg_color", "SurfaceBgColorSwatch"
    )
    window.BtnCutFgColor.Click += lambda s, a: on_pick_hex(
        "cut_fg_color", "CutFgColorSwatch"
    )
    window.BtnCutBgColor.Click += lambda s, a: on_pick_hex(
        "cut_bg_color", "CutBgColorSwatch"
    )
    window.BtnChangeTexture.Click += api(on_change_texture)
    window.TexturePreview.MouseLeftButtonUp += api(on_open_main_texture_editor)
    window.BtnChangeMainTexture.Click += api(lambda s, a: change_texture_slot(
        "main", "Textura principal alterada!"
    ))
    window.BtnChangeBumpTexture.Click += api(lambda s, a: change_texture_slot(
        "bump", "Textura bump alterada!"
    ))
    window.BtnChangeCutoutTexture.Click += api(lambda s, a: change_texture_slot(
        "cutout", "Textura de recorte alterada!"
    ))
    window.BtnChangeTransparencyTexture.Click += api(lambda s, a: change_texture_slot(
        "transparency", "Textura de transparencia alterada!"
    ))
    window.BtnSaveMainTexture.Click += lambda s, a: save_texture_slot("main")
    window.BtnSaveBumpTexture.Click += lambda s, a: save_texture_slot("bump")
    window.BtnSaveCutoutTexture.Click += lambda s, a: save_texture_slot("cutout")
    window.BtnSaveTransparencyTexture.Click += lambda s, a: save_texture_slot(
        "transparency"
    )
    window.BtnFavorite.Click += api(on_toggle_favorite)
    window.BtnPaint.Click += on_paint
    window.BtnApply.Click += api(on_apply)
    window.BtnNewMaterial.Click += api(on_new_material)
    window.BtnRefreshFavorites.Click += api(on_refresh_favorites)
    window.BtnRemoveFavorite.Click += api(on_remove_favorite)
    window.BtnImportFavorite.Click += api(on_import_favorite)
    window.BtnPaintFavorite.Click += api(on_paint_favorite)
    window.BtnClose.Click += on_close
    window.BtnEditor.Click += api(on_editor)

    def finish():
        if editor_request["next_material"] is not None:
            return show_material_editor(
                doc, uidoc, editor_request["next_material"], xaml_path, modeless
            )
        if editor_request["paint"] is not None:
            paint_material, paint_label, message = editor_request["paint"]
            mt_notifications.toast(message, title="Paint")
            paint_material_on_faces(doc, uidoc, paint_material, paint_label=paint_label)
        if modeless and editor_request["open_native"]:
            command = UI.RevitCommandId.LookupPostableCommandId(UI.PostableCommand.Materials)
            if command is not None:
                uidoc.Application.PostCommand(command)
        return editor_request

    attach_to_revit(window)
    if modeless:
        window.Closed += in_revit(lambda s, a: finish())
        hold(window)
        window.Show()
        return editor_request
    window.ShowDialog()
    return finish()
