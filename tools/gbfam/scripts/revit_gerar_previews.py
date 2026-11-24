# -*- coding: utf-8 -*-
"""Gera previews PNG para familias .rfa que nao trazem miniatura, dentro do Revit.

Fluxo:
    1. gbfam missing --out missing.json          (lista as familias sem preview)
    2. Rodar ESTE script no Revit (pyRevit: Script console ou botao)
    3. gbfam import-previews --dir <pasta_saida>  (grava os PNGs no banco)

Cada PNG sai como <sha256>.png, que e a chave que o gbfam usa para casar com o banco.
O script NUNCA salva nem altera os .rfa originais: abre, cria uma vista 3D so em memoria,
exporta a imagem e fecha o documento sem salvar.

Configuracao: edite as duas constantes abaixo, ou defina as variaveis de ambiente
GBFAM_MISSING e GBFAM_OUT antes de abrir o Revit.
"""
import io
import json
import os
import glob
import traceback

import clr  # noqa: F401  (IronPython)
from System.Collections.Generic import List
from Autodesk.Revit.DB import (
    BuiltInCategory,
    DisplayStyle,
    ElementId,
    FilteredElementCollector,
    ImageExportOptions,
    ExportRange,
    ImageFileType,
    ImageResolution,
    ZoomFitType,
    Transaction,
    View3D,
    ViewFamily,
    ViewFamilyType,
    OpenOptions,
    ModelPath,
    ModelPathUtils,
    DetachFromCentralOption,
)

_REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
MISSING_JSON = os.environ.get("GBFAM_MISSING", os.path.join(_REPO_ROOT, "banco_familias", "missing.json"))
OUT_DIR = os.environ.get("GBFAM_OUT", os.path.join(_REPO_ROOT, "banco_familias", "previews_gerados"))
PIXELS = 512


def get_app():
    try:
        from pyrevit import HOST_APP
        return HOST_APP.app
    except Exception:
        return __revit__.Application  # noqa: F821


def find_3d_view(doc):
    """Reaproveita uma vista 3D existente; senao cria uma isometrica (apenas em memoria)."""
    for v in FilteredElementCollector(doc).OfClass(View3D):
        if not v.IsTemplate and v.CanBePrinted:
            return v, False
    vft = None
    for t in FilteredElementCollector(doc).OfClass(ViewFamilyType):
        if t.ViewFamily == ViewFamily.ThreeDimensional:
            vft = t
            break
    if vft is None:
        return None, False
    t = Transaction(doc, "gbfam preview")
    t.Start()
    try:
        v = View3D.CreateIsometric(doc, vft.Id)
        try:
            v.DisplayStyle = DisplayStyle.Shading
        except Exception:
            pass
        t.Commit()
        return v, True
    except Exception:
        t.RollBack()
        return None, False


def export_view(doc, view, out_png):
    base = os.path.splitext(out_png)[0]
    opts = ImageExportOptions()
    opts.ExportRange = ExportRange.SetOfViews
    opts.SetViewsAndSheets(List[ElementId]([view.Id]))
    opts.FilePath = base
    opts.HLRandWFViewsFileType = ImageFileType.PNG
    opts.ShadowViewsFileType = ImageFileType.PNG
    opts.ImageResolution = ImageResolution.DPI_150
    opts.ZoomType = ZoomFitType.FitToPage
    opts.PixelSize = PIXELS
    doc.ExportImage(opts)
    # O Revit acrescenta sufixo ao nome (ex.: "<base>-Vista 3D.png"): renomeia para o nome final.
    produced = [p for p in glob.glob(base + "*.png") if p != out_png]
    if produced:
        if os.path.exists(out_png):
            os.remove(out_png)
        os.rename(produced[0], out_png)
        for extra in produced[1:]:
            os.remove(extra)
    return os.path.exists(out_png)


def main():
    app = get_app()
    if not os.path.isdir(OUT_DIR):
        os.makedirs(OUT_DIR)
    with io.open(MISSING_JSON, "r", encoding="utf-8") as f:
        items = json.load(f)

    ok, fail, skipped = 0, 0, 0
    for it in items:
        out_png = os.path.join(OUT_DIR, it["sha"] + ".png")
        if os.path.exists(out_png):
            skipped += 1
            continue
        doc = None
        try:
            mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(it["path"])
            opts = OpenOptions()
            doc = app.OpenDocumentFile(mp, opts)
            if not doc.IsFamilyDocument:
                raise Exception("nao e documento de familia")
            view, _created = find_3d_view(doc)
            if view is None:
                raise Exception("sem vista 3D")
            if export_view(doc, view, out_png):
                ok += 1
            else:
                raise Exception("exportacao nao gerou arquivo")
        except Exception as e:
            fail += 1
            print("FALHOU: {} -> {}".format(it["path"], e))
            traceback.print_exc()
        finally:
            if doc is not None:
                try:
                    doc.Close(False)  # False = NAO salvar
                except Exception:
                    pass
    print("previews gerados: {} | ja existiam: {} | falhas: {}".format(ok, skipped, fail))
    print("pasta: " + OUT_DIR)


main()
