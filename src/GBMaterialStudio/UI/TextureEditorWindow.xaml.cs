using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Gestaobim.MaterialStudio.Revit;

namespace Gestaobim.MaterialStudio.UI;

/// <summary>Edits the transform, brightness and repeat settings of one texture slot.</summary>
public partial class TextureEditorWindow : Window
{
    private readonly Document _doc;
    private readonly Material _material;
    private readonly TextureRole _role;
    private readonly TextureSlotData _state;

    /// <summary>True when the texture was changed and applied.</summary>
    public bool Changed { get; private set; }

    public TextureEditorWindow(Document doc, Material material, TextureRole role)
    {
        _doc = doc;
        _material = material;
        _role = role;
        _state = MaterialServices.ReadTextureSlotData(doc, material, role);
        InitializeComponent();
        Title = role switch
        {
            TextureRole.Bump => "Textura bump",
            TextureRole.Cutout => "Textura de recorte",
            TextureRole.Transparency => "Textura de transparencia",
            _ => "Textura principal",
        };
        RefreshFields();
        BtnChangeSource.Click += (_, _) => OnChangeSource();
        SliderBrightness.ValueChanged += (_, _) => OnBrightness();
        ChkInvert.Click += (_, _) => { _state.Invert = ChkInvert.IsChecked == true; RefreshPreview(); };
        ChkScaleLock.Click += (_, _) => { if (ChkScaleLock.IsChecked == true) TxtScaleY.Text = TxtScaleX.Text; };
        TxtScaleX.LostFocus += (_, _) => { if (ChkScaleLock.IsChecked == true) TxtScaleY.Text = TxtScaleX.Text; };
        BtnResetTransforms.Click += (_, _) => { _state.OffsetXCm = 0; _state.OffsetYCm = 0; _state.Rotation = 0; RefreshFields(); };
        BtnResetScale.Click += (_, _) => { _state.SizeXCm = 100; _state.SizeYCm = 100; _state.ScaleLock = true; RefreshFields(); };
        BtnResetRepeat.Click += (_, _) => { _state.RepeatU = true; _state.RepeatV = true; _state.LinkTransforms = false; RefreshFields(); };
        BtnApply.Click += (_, _) => OnApply();
        BtnClose.Click += (_, _) => Close();
        Dialogs.Attach(this);
    }

    private static string Fixed(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static double ToDouble(string text, double fallback) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private void RefreshPreview()
    {
        TexturePreviewLarge.Background = Visuals.TextureBrush(_state.Path) ?? Visuals.ToBrush("#D8D8D8");
        var brightness = Math.Max(0.0, Math.Min(1.0, _state.Brightness));
        TexturePreviewLarge.Opacity = Math.Max(0.15, brightness);
        TexturePreviewOverlay.Background = Visuals.ToBrush(_state.Invert ? "#FFFFFF" : "#000000");
        TexturePreviewOverlay.Opacity = _state.Invert ? 0.20 : 0.0;
        var resolved = TexturePaths.Resolve(_state.Path);
        TxtSource.Text = resolved.Length > 0 ? resolved : _state.Path;
        TexturePreviewLarge.UpdateLayout();
        TexturePreviewOverlay.UpdateLayout();
    }

    private void RefreshFields()
    {
        RefreshPreview();
        SliderBrightness.Value = Math.Max(0, Math.Min(100, _state.Brightness * 100.0));
        TxtBrightness.Text = ((int)SliderBrightness.Value).ToString(CultureInfo.InvariantCulture);
        ChkInvert.IsChecked = _state.Invert;
        ChkLinkTransforms.IsChecked = _state.LinkTransforms;
        TxtOffsetX.Text = Fixed(_state.OffsetXCm);
        TxtOffsetY.Text = Fixed(_state.OffsetYCm);
        TxtRotation.Text = Fixed(_state.Rotation);
        TxtScaleX.Text = Fixed(_state.SizeXCm);
        TxtScaleY.Text = Fixed(_state.SizeYCm);
        ChkScaleLock.IsChecked = _state.ScaleLock;
        ChkRepeatU.IsChecked = _state.RepeatU;
        ChkRepeatV.IsChecked = _state.RepeatV;
    }

    private void ReadForm()
    {
        _state.Brightness = SliderBrightness.Value / 100.0;
        _state.Invert = ChkInvert.IsChecked == true;
        _state.LinkTransforms = ChkLinkTransforms.IsChecked == true;
        _state.OffsetXCm = ToDouble(TxtOffsetX.Text, _state.OffsetXCm);
        _state.OffsetYCm = ToDouble(TxtOffsetY.Text, _state.OffsetYCm);
        _state.Rotation = ToDouble(TxtRotation.Text, _state.Rotation);
        _state.SizeXCm = Math.Max(0.01, ToDouble(TxtScaleX.Text, _state.SizeXCm));
        _state.SizeYCm = Math.Max(0.01, ToDouble(TxtScaleY.Text, _state.SizeYCm));
        _state.ScaleLock = ChkScaleLock.IsChecked == true;
        _state.RepeatU = ChkRepeatU.IsChecked == true;
        _state.RepeatV = ChkRepeatV.IsChecked == true;
    }

    private void OnChangeSource()
    {
        var path = Dialogs.PickFile("jpg;png;bmp;tif;tga;exr;hdr", "Selecionar textura");
        if (path is null) return;
        _state.Path = path;
        Changed = true;
        RefreshFields();
    }

    private void OnBrightness()
    {
        _state.Brightness = SliderBrightness.Value / 100.0;
        TxtBrightness.Text = ((int)SliderBrightness.Value).ToString(CultureInfo.InvariantCulture);
        RefreshPreview();
    }

    private void OnApply()
    {
        ReadForm();
        Failures.Pop();
        if (MaterialServices.ApplyTextureSlotData(_doc, _material, _role, _state))
        {
            Changed = true;
            Notifications.Toast("Textura atualizada.", "Editor de textura");
            Close();
            return;
        }
        var reasons = Failures.Pop();
        Dialogs.Alert("Nao foi possivel atualizar este slot de textura.\n\n" +
                      (reasons.Count > 0 ? string.Join("\n", reasons) : "Motivo nao informado pelo Revit."), "Editor de textura");
    }
}
