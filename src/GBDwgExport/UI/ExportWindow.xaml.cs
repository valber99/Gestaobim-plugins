using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Autodesk.Revit.DB;
using Gestaobim.DwgExport.Core;
using Gestaobim.DwgExport.Revit;

namespace Gestaobim.DwgExport.UI;

/// <summary>Pick sheets or views, how to write them and where. Returns an <see cref="ExportRequest"/>; the export runs after it closes.</summary>
public partial class ExportWindow : Window
{
    private const string NoSetup = "(padrão do plugin)";

    private readonly string _projectName;
    private readonly ExportSettings _settings;
    private readonly IReadOnlyList<ExportItem> _sheets;
    private readonly Lazy<IReadOnlyList<ExportItem>> _views;
    private ICollectionView? _view;
    private bool _ready;

    public ExportRequest? Request { get; private set; }

    public ExportWindow(Document doc, ExportSettings settings, bool startWithViews)
    {
        InitializeComponent();
        Dialogs.Attach(this);

        _settings = settings;
        _projectName = string.IsNullOrWhiteSpace(doc.Title) ? "Projeto" : Path.GetFileNameWithoutExtension(doc.Title);
        _sheets = SheetSource.Sheets(doc);
        _views = new Lazy<IReadOnlyList<ExportItem>>(() => SheetSource.Views(doc));

        SetupBox.Items.Add(NoSetup);
        foreach (var name in SheetSource.RevitSetups(doc)) SetupBox.Items.Add(name);
        ApplySettingsToUi(settings);

        _ready = true;
        (startWithViews ? ViewsRadio : SheetsRadio).IsChecked = true;
        ShowItems(startWithViews);
        RefreshMode();
    }

    private void ApplySettingsToUi(ExportSettings settings)
    {
        SetupBox.SelectedItem = SetupBox.Items.Contains(settings.RevitSetup) ? settings.RevitSetup : NoSetup;
        FolderBox.Text = string.IsNullOrWhiteSpace(settings.Folder)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : settings.Folder;
        FormatBox.SelectedIndex = settings.Dxf ? 1 : 0;
        PatternBox.SelectedIndex = settings.Pattern == NamePattern.NumberOnly ? 1 : 0;
        OpenBox.IsChecked = settings.OpenFolder;
        (settings.Mode switch
        {
            OutputMode.MultipleLayouts => LayoutsRadio,
            OutputMode.ModelSpace => ModelRadio,
            OutputMode.SingleLayout => SingleRadio,
            _ => SeparateRadio,
        }).IsChecked = true;
    }

    private void OnExportSettings(object sender, RoutedEventArgs e)
    {
        var file = Dialogs.PickFile(save: true, "Configurações GB DWG Export (*.zip)|*.zip", "Exportar configurações", "GBDwgExport-configuracoes.zip");
        if (file is null) return;
        _settings.Folder = FolderBox.Text.Trim();
        _settings.Dxf = FormatBox.SelectedIndex == 1;
        _settings.Pattern = PatternBox.SelectedIndex == 1 ? NamePattern.NumberOnly : NamePattern.NumberAndName;
        _settings.Mode = Mode;
        _settings.RevitSetup = SetupBox.SelectedItem as string is { } chosen && chosen != NoSetup ? chosen : "";
        _settings.OpenFolder = OpenBox.IsChecked == true;
        _settings.Save();
        try
        {
            SettingsTransfer.Export(_settings, file, typeof(ExportWindow).Assembly.GetName().Version?.ToString(3) ?? "0");
            Dialogs.Alert($"Configurações salvas em:\n{file}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Dialogs.Alert($"Não foi possível salvar o arquivo de configurações:\n{error.Message}");
        }
    }

    private void OnImportSettings(object sender, RoutedEventArgs e)
    {
        var file = Dialogs.PickFile(save: false, "Configurações GB DWG Export (*.zip)|*.zip", "Importar configurações");
        if (file is null) return;
        try
        {
            var imported = SettingsTransfer.Import(file);
            imported.Save();
            ApplySettingsToUi(imported);
            if (ViewsSelected && Mode is OutputMode.MultipleLayouts or OutputMode.SingleLayout)
                ModelRadio.IsChecked = true;
            RefreshMode();
            Dialogs.Alert("Configurações importadas e aplicadas.");
        }
        catch (TransferException error)
        {
            Dialogs.Alert(error.Message);
        }
    }

    private bool ViewsSelected => ViewsRadio.IsChecked == true;

    private OutputMode Mode =>
        LayoutsRadio.IsChecked == true ? OutputMode.MultipleLayouts
        : ModelRadio.IsChecked == true ? OutputMode.ModelSpace
        : SingleRadio.IsChecked == true ? OutputMode.SingleLayout
        : OutputMode.SeparateFiles;

    private IEnumerable<ExportItem> Current => (ViewsSelected ? _views.Value : _sheets);

    private void ShowItems(bool views)
    {
        var items = views ? _views.Value : _sheets;
        _view = CollectionViewSource.GetDefaultView(items);
        _view.Filter = Matches;
        ItemsList.ItemsSource = _view;
        GroupColumn.Width = views ? 140 : 0;
        NumberColumn.Width = views ? 0 : 110;
        UpdateCount();
    }

    private bool Matches(object candidate)
    {
        var text = SearchBox.Text.Trim();
        if (text.Length == 0) return true;
        var item = (ExportItem)candidate;
        return item.Number.Contains(text, StringComparison.CurrentCultureIgnoreCase)
               || item.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
               || item.Group.Contains(text, StringComparison.CurrentCultureIgnoreCase);
    }

    private void UpdateCount()
    {
        var total = Current.Count();
        var picked = Current.Count(item => item.IsChecked);
        CountText.Text = $"{picked} de {total} marcadas";
        ExportButton.IsEnabled = picked > 0;
    }

    private void RefreshMode()
    {
        var merging = Mode != OutputMode.SeparateFiles;
        ModeHint.Text = Mode switch
        {
            OutputMode.MultipleLayouts => "Um só DWG: cada folha vira um layout igual à do Revit (carimbo, tabelas, textos e viewports, no mesmo papel), e todas as vistas ficam lado a lado no model space.",
            OutputMode.ModelSpace => ViewsSelected
                ? "Um só DWG com as vistas marcadas lado a lado no model space."
                : "Um só DWG com todas as vistas das folhas lado a lado no model space, sem layouts.",
            OutputMode.SingleLayout => "Um só DWG com as folhas lado a lado num único layout, e todas as vistas lado a lado no model space.",
            _ => ViewsSelected ? "Cada vista vira um arquivo, exatamente como o Revit exporta." : "Cada folha vira um arquivo, exatamente como o Revit exporta.",
        };
        var modeAvailable = CadcraftAvailable || !merging;
        StatusText.Text = modeAvailable ? "" : "O motor de mesclagem não foi encontrado. Rode o instalador de novo.";
    }

    private static bool CadcraftAvailable => Bridge.CadcraftBridge.Find() is not null;

    private void OnSourceChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ShowItems(ViewsSelected);
        // Views have no paper: they are merged side by side in model space, or kept as separate files.
        foreach (var radio in new[] { LayoutsRadio, SingleRadio }) radio.IsEnabled = !ViewsSelected;
        if (ViewsSelected && Mode is OutputMode.MultipleLayouts or OutputMode.SingleLayout) ModelRadio.IsChecked = true;
        RefreshMode();
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (_ready) RefreshMode();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => _view?.Refresh();

    private void OnItemToggled(object sender, RoutedEventArgs e) => UpdateCount();

    private void OnCheckAll(object sender, RoutedEventArgs e)
    {
        foreach (var item in _view!.Cast<ExportItem>()) item.IsChecked = true;
        UpdateCount();
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        foreach (var item in Current) item.IsChecked = false;
        UpdateCount();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var folder = Dialogs.PickFolder(FolderBox.Text, "Pasta de destino da exportação");
        if (folder is not null) FolderBox.Text = folder;
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var folder = FolderBox.Text.Trim();
        if (folder.Length == 0 || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            Dialogs.Alert("Escolha uma pasta de destino válida.");
            return;
        }
        if (Mode != OutputMode.SeparateFiles && !CadcraftAvailable)
        {
            Dialogs.Alert("O motor de mesclagem (cadcraft-cli) não foi encontrado. Rode o instalador do Gestão BIM de novo.");
            return;
        }

        var items = Current.Where(item => item.IsChecked).ToList();
        if (items.Count == 0) return;

        _settings.Folder = folder;
        _settings.Dxf = FormatBox.SelectedIndex == 1;
        _settings.Pattern = PatternBox.SelectedIndex == 1 ? NamePattern.NumberOnly : NamePattern.NumberAndName;
        _settings.Mode = Mode;
        _settings.RevitSetup = SetupBox.SelectedItem as string is { } chosen && chosen != NoSetup ? chosen : "";
        _settings.OpenFolder = OpenBox.IsChecked == true;
        _settings.Save();

        Request = new ExportRequest(items, _settings.Mode, _settings.Dxf, _settings.Pattern, folder, _settings.RevitSetup, _projectName);
        DialogResult = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
