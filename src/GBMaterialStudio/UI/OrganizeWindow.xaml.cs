using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Gestaobim.MaterialStudio.Core;
using Gestaobim.MaterialStudio.Revit;

namespace Gestaobim.MaterialStudio.UI;

/// <summary>Organize materials: see usage and duplicates, delete the unused, rename in batch.</summary>
public partial class OrganizeWindow : Window
{
    private const string WindowTitle = "Gestão BIM | Organizar materiais";
    private readonly Document _doc;
    private List<MaterialRow> _rows = [];
    private List<MaterialRow> _visible = [];
    private readonly bool _ready;

    public OrganizeWindow(Document doc)
    {
        _doc = doc;
        InitializeComponent();
        Search.TextChanged += (_, _) => ApplyFilter();
        CmbFilter.SelectionChanged += (_, _) => ApplyFilter();
        BtnRefresh.Click += (_, _) => Load();
        BtnSelectUnused.Click += (_, _) => SelectWhere(r => r.Usage == 0);
        BtnSelectDuplicates.Click += (_, _) => SelectWhere(r => r.DuplicateOf.Length > 0);
        BtnDelete.Click += (_, _) => OnDelete();
        BtnPreview.Click += (_, _) => OnPreview();
        BtnApplyRename.Click += (_, _) => OnApplyRename();
        BtnClose.Click += (_, _) => Close();
        _ready = true;
        Load();
        Dialogs.Attach(this);
    }

    public static void Show(Document doc) => new OrganizeWindow(doc).ShowDialog();

    private void Say(string text) => TxtStatus.Text = text;

    private void Summary()
    {
        TxtSummary.Text = $"{_rows.Count} materiais  |  {_rows.Count(r => r.Usage == 0)} nao usados  |  {_rows.Count(r => r.DuplicateOf.Length > 0)} duplicados";
    }

    private void ApplyFilter()
    {
        if (!_ready) return;
        var text = Search.Text.Trim();
        var mode = CmbFilter.SelectedIndex;
        _visible = _rows.Where(row =>
        {
            if (mode == 1 && row.Usage != 0) return false;
            if (mode == 2 && row.DuplicateOf.Length == 0) return false;
            return text.Length == 0 || $"{row.Name} {row.MaterialClass} {row.DuplicateOf}".Contains(text, StringComparison.OrdinalIgnoreCase);
        }).ToList();
        Rows.ItemsSource = _visible;
    }

    private void Load()
    {
        Cursor = Cursors.Wait;
        try
        {
            _rows = Hygiene.Analyze(_doc);
        }
        catch (Exception error)
        {
            _rows = [];
            Dialogs.Alert($"Nao foi possivel analisar os materiais.\n\n{error.Message}", WindowTitle);
        }
        finally
        {
            Cursor = null;
        }
        Summary();
        ApplyFilter();
        Say("");
    }

    private List<MaterialRow> Selected() => Rows.SelectedItems.Cast<MaterialRow>().ToList();

    private void SelectWhere(Func<MaterialRow, bool> predicate)
    {
        Rows.SelectedItems.Clear();
        var count = 0;
        foreach (var row in _visible.Where(predicate))
        {
            Rows.SelectedItems.Add(row);
            count++;
        }
        Say($"{count} selecionado(s).");
    }

    private void OnDelete()
    {
        var chosen = Selected();
        var removable = chosen.Where(r => r.Usage == 0).ToList();
        if (removable.Count == 0)
        {
            Say("Selecione materiais \"nao usados\". Os em uso nunca sao excluidos.");
            return;
        }
        var skipped = chosen.Count - removable.Count;
        var question = $"Excluir {removable.Count} material(is) nao usado(s) e as aparencias que ninguem mais usa?";
        if (skipped > 0) question += $"\n{skipped} selecionado(s) em uso serao mantidos.";
        question += "\n\nVoce pode desfazer com Ctrl+Z.";
        if (!Dialogs.Alert(question, WindowTitle, ask: true)) return;

        DeleteResult result;
        try
        {
            result = Hygiene.DeleteUnused(_doc, removable);
        }
        catch (Exception error)
        {
            Dialogs.Alert($"Nada foi excluido.\n\n{error.Message}", WindowTitle);
            return;
        }
        Load();
        var message = $"{result.Deleted.Count} material(is) e {result.AssetsDeleted} aparencia(s) excluidos.";
        if (result.Failures.Count > 0)
            message += $" {result.Failures.Count} mantido(s): {result.Failures[0]}";
        Say(message);
    }

    private RenameRules BuildRules() => new()
    {
        Find = TxtFind.Text,
        Replace = TxtReplace.Text,
        UseRegex = ChkRegex.IsChecked == true,
        Prefix = TxtPrefix.Text,
        Suffix = TxtSuffix.Text,
        Case = (CaseMode)Math.Max(0, CmbCase.SelectedIndex),
    };

    private static string StateText(RenameStatus status) => status switch
    {
        RenameStatus.Ok => "ok",
        RenameStatus.Unchanged => "igual",
        RenameStatus.Empty => "vazio",
        _ => "conflito",
    };

    private List<RenameStep> Plan()
    {
        var targets = Selected();
        if (targets.Count == 0) targets = _visible.ToList();
        if (targets.Count == 0) return [];
        var steps = Renamer.Plan(targets.Select(t => (t.Key, t.Name)).ToList(), BuildRules(), _rows.Select(r => r.Name));
        var byKey = targets.ToDictionary(t => t.Key);
        foreach (var step in steps)
        {
            var row = byKey[step.Key];
            row.NewName = step.Status == RenameStatus.Unchanged ? "" : step.New;
            row.State = StateText(step.Status);
        }
        return steps;
    }

    private void OnPreview()
    {
        List<RenameStep> steps;
        try { steps = Plan(); }
        catch (ArgumentException error) { Dialogs.Alert(error.Message, WindowTitle); return; }
        var ok = steps.Count(s => s.Status == RenameStatus.Ok);
        var conflicts = steps.Count(s => s.Status == RenameStatus.Conflict);
        Say($"{ok} para renomear, {conflicts} em conflito (nome ja existe), {steps.Count - ok - conflicts} sem mudanca.");
    }

    private void OnApplyRename()
    {
        List<RenameStep> steps;
        try { steps = Plan(); }
        catch (ArgumentException error) { Dialogs.Alert(error.Message, WindowTitle); return; }
        var ready = steps.Where(s => s.Status == RenameStatus.Ok).ToList();
        if (ready.Count == 0)
        {
            Say("Nada para renomear. Confira a pre-visualizacao.");
            return;
        }
        var conflicts = steps.Count(s => s.Status == RenameStatus.Conflict);
        var question = $"Renomear {ready.Count} material(is)?";
        if (conflicts > 0) question += $"\n{conflicts} em conflito serao ignorados.";
        question += "\n\nVoce pode desfazer com Ctrl+Z.";
        if (!Dialogs.Alert(question, WindowTitle, ask: true)) return;

        int renamed;
        try
        {
            renamed = Hygiene.Rename(_doc, steps, _rows);
        }
        catch (Exception error)
        {
            Dialogs.Alert($"Nada foi renomeado.\n\n{error.Message}", WindowTitle);
            return;
        }
        Load();
        Say($"{renamed} material(is) renomeado(s).");
    }
}
