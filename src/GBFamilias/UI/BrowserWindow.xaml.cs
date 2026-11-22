using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Gestaobim.Familias.Core;
using Gestaobim.Familias.Core.Server;
using Gestaobim.Familias.Revit;

namespace Gestaobim.Familias.UI;

public partial class BrowserWindow : Window
{
    private const int PageSize = 120;

    public static BrowserWindow? Instance { get; private set; }

    private readonly LibraryService _lib;
    private readonly FilterSpec _spec = new();
    private readonly ThumbnailCache _thumbs = new();
    private readonly ObservableCollection<CardItem> _cards = [];
    private readonly Dictionary<Facet, FacetPanel> _panels = [];
    private readonly HashSet<string> _expanded = [];
    private readonly DispatcherTimer _debounce = new();

    private List<FamilyRecord> _results = [];
    private int _shown;
    private HashSet<string> _favorites = [];
    private byte[]? _imageQuery;
    private int _version;
    private CancellationTokenSource? _semanticCts;
    private CardItem? _selected;
    private bool _ready;
    private bool _suppress;

    private sealed class FacetPanel
    {
        public required Facet Facet { get; init; }
        public required string Title { get; init; }
        public required Expander Expander { get; init; }
        public required TextBox Filter { get; init; }
        public ObservableCollection<FacetItem> Items { get; } = [];
    }

    // ------------------------------------------------------------------ abrir

    /// <summary>Abre a janela (ou traz a que ja esta aberta). Pede as configuracoes se a biblioteca nao for encontrada.</summary>
    public static void ShowOrActivate()
    {
        if (Instance is not null)
        {
            Instance.Show();
            if (Instance.WindowState == WindowState.Minimized) Instance.WindowState = WindowState.Normal;
            Instance.Activate();
            return;
        }

        var settings = FamiliasSettings.Load();
        if (settings.Problems().Count > 0)
        {
            var intro = settings.Mode == LibraryMode.Server
                ? "Antes de começar, escolha a pasta onde as famílias baixadas serão guardadas."
                : "A biblioteca ainda nao foi encontrada. Informe onde estao o banco e a pasta das familias.";
            if (!SettingsWindow.Edit(null, intro)) return;
            settings = FamiliasSettings.Load();
        }

        LibraryService lib;
        try
        {
            lib = LibraryService.Current is { } current && SameSettings(current.Settings, settings) ? current : OpenLibrary(settings);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception error)
        {
            Dialogs.Alert($"Nao foi possivel abrir a biblioteca.\n\n{ServerMessages.Describe(error)}");
            return;
        }

        RevitDispatcher.Ensure();
        Instance = new BrowserWindow(lib);
        Dialogs.Attach(Instance);
        Instance.Show();
    }

    /// <summary>Abre a biblioteca; no primeiro uso do modo servidor, entra e baixa o catalogo antes.</summary>
    private static LibraryService OpenLibrary(FamiliasSettings settings)
    {
        try
        {
            return LibraryService.Open(settings);
        }
        catch (CatalogMissingException) when (settings.CatalogSync)
        {
            // So com a atualizacao pelo servidor ligada; senao o catalogo vem no instalador e a mensagem orienta a reinstalar.
            if (!ServerFlows.EnsureCatalog(null, settings)) throw new OperationCanceledException();
            return LibraryService.Open(settings);
        }
    }

    private static bool SameSettings(FamiliasSettings a, FamiliasSettings b) =>
        a.Mode == b.Mode && a.EffectiveDb == b.EffectiveDb && a.EffectiveRoot == b.EffectiveRoot && a.GbfamExe == b.GbfamExe
        && a.SemanticEnabled == b.SemanticEnabled && a.CacheLimitGb == b.CacheLimitGb;

    private BrowserWindow(LibraryService lib)
    {
        _lib = lib;
        InitializeComponent();
        Title = $"GB Famílias — {lib.Index.All.Count} famílias";
        ResultsList.ItemsSource = _cards;
        AiCheck.IsChecked = lib.Settings.SemanticEnabled;
        AiCheck.IsEnabled = lib.Settings.SemanticEnabled;
        _favorites = lib.User.Favorites();

        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };
        BuildFacetPanels();
        RebuildCollections();
        var server = lib.Session is not null;
        OnlyDownloadedCheck.Visibility = server ? Visibility.Visible : Visibility.Collapsed;
        SyncButton.Visibility = server && lib.Settings.CatalogSync ? Visibility.Visible : Visibility.Collapsed;
        AccountButton.Visibility = server ? Visibility.Visible : Visibility.Collapsed;
        UpdateAccountUi();
        _ready = true;

        Loaded += (_, _) =>
        {
            Refresh();
            SearchBox.Focus();
            if (lib.Settings.SemanticEnabled) _ = StartAiAsync();
            if (server && lib.Settings.CatalogSync) _ = CheckCatalogAsync(force: false);
        };
        // ao voltar do Explorer ou de outro computador da rede, a marca "baixada" reflete o disco
        Activated += (_, _) => RefreshDownloadedMarks();
        Closed += (_, _) =>
        {
            _debounce.Stop();
            _semanticCts?.Cancel();
            Instance = null;
        };
    }

    // ------------------------------------------------------------------ busca e filtros

    private void QueueRefresh(int milliseconds = 30)
    {
        if (!_ready) return;
        _debounce.Stop();
        _debounce.Interval = TimeSpan.FromMilliseconds(milliseconds);
        _debounce.Start();
    }

    private SortMode CurrentSort => SortBox.SelectedIndex switch { 1 => SortMode.Name, 2 => SortMode.Category, _ => SortMode.Relevance };

    private void Refresh()
    {
        if (!_ready) return;
        _spec.Text = SearchBox.Text.Trim();
        _spec.OnlyWithPreview = OnlyPreviewCheck.IsChecked == true;
        _spec.Extra = OnlyDownloadedCheck.IsChecked == true && _lib.Session is not null ? _lib.IsAvailable : null;
        ApplyCollectionToSpec();

        var version = ++_version;
        _semanticCts?.Cancel();
        var sort = CurrentSort;
        var aiOn = AiCheck.IsChecked == true && _lib.Settings.SemanticEnabled;
        var wantsSemantic = aiOn && (_spec.Text.Length > 0 || _imageQuery is not null);

        if (_imageQuery is null)
        {
            ShowResults(_lib.Index.Query(_spec, sort)); // o texto aparece na hora; a IA refina logo depois
        }
        else if (!aiOn)
        {
            ShowResults([]);
            StatusText.Text = "A busca por imagem precisa da busca por IA ligada.";
        }
        else
        {
            ShowResults([]);
            StatusText.Text = "Buscando por imagem…";
        }

        RebuildSidePanels();
        if (wantsSemantic) _ = RunSemanticAsync(version, sort);
        UpdateAiStatus();
    }

    private async Task RunSemanticAsync(int version, SortMode sort)
    {
        try
        {
            if (_lib.SemanticState is SemanticState.Off or SemanticState.Starting)
            {
                AiStatusText.Text = "IA iniciando…";
                await _lib.EnsureSemanticAsync();
                UpdateAiStatus();
            }
            if (version != _version) return;
            if (_lib.Semantic is not { IsAlive: true } client)
            {
                if (_imageQuery is not null) StatusText.Text = _lib.SemanticDetail;
                return;
            }

            var cts = _semanticCts = new CancellationTokenSource();
            var noText = _spec.Clone();
            noText.Text = "";
            var allowed = _lib.Index.Query(noText).Select(r => r.Id).ToList();
            var ids = await client.SearchAsync(_spec.Text, _imageQuery, allowed, 150, cts.Token);
            if (version != _version) return;
            ShowResults(_lib.Index.Query(_spec, sort, ids));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            if (version == _version) StatusText.Text = $"A busca por IA falhou: {error.Message}";
        }
    }

    private void ApplyCollectionToSpec()
    {
        if (CollectionsList.SelectedItem is not CollectionItem item || item.Kind == CollectionItem.All)
        {
            _spec.OnlyShas = null;
            return;
        }
        _spec.OnlyShas = item.Kind switch
        {
            CollectionItem.Favorites => [.. _favorites],
            CollectionItem.Recents => _lib.User.Recents().Select(r => r.Sha).ToHashSet(),
            _ => _lib.User.Collection(item.Name),
        };
    }

    private void ShowResults(IReadOnlyList<FamilyRecord> list)
    {
        _results = [.. list];
        _cards.Clear();
        _shown = 0;
        AppendPage();
        EmptyText.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_cards.Count > 0) ResultsList.ScrollIntoView(_cards[0]);
        var total = _lib.Index.All.Count;
        StatusText.Text = _results.Count == total
            ? $"{total} famílias"
            : $"{_results.Count} de {total} famílias";
    }

    private void AppendPage()
    {
        foreach (var record in _results.Skip(_shown).Take(PageSize))
            _cards.Add(new CardItem(record, _thumbs, _lib.Db, _favorites.Contains(record.Sha), _lib.Session is null ? null : _lib.IsAvailable));
        _shown = _cards.Count;
        var remaining = _results.Count - _shown;
        ShowMoreButton.Visibility = remaining > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowMoreButton.Content = $"Mostrar mais ({remaining})";
    }

    // ------------------------------------------------------------------ painel lateral

    private void BuildFacetPanels()
    {
        foreach (var (facet, title) in new[]
                 {
                     (Facet.Folder, "Pasta de origem"), (Facet.Environment, "Ambiente"), (Facet.Material, "Material"),
                     (Facet.Color, "Cor"), (Facet.Style, "Estilo"), (Facet.RevitVersion, "Versão do Revit"),
                     (Facet.RevitCategory, "Categoria no Revit"),
                 })
        {
            var filter = new TextBox { Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(3), ToolTip = "Filtrar esta lista" };
            var panel = new FacetPanel
            {
                Facet = facet, Title = title, Filter = filter,
                Expander = new Expander { Style = (Style)FindResource("SideExpander"), Header = title },
            };
            var view = CollectionViewSource.GetDefaultView(panel.Items);
            view.Filter = o => filter.Text.Length == 0 || ((FacetItem)o).Label.Contains(filter.Text, StringComparison.CurrentCultureIgnoreCase)
                               || TextNorm.Fold(((FacetItem)o).Label).Contains(TextNorm.Fold(filter.Text));
            filter.TextChanged += (_, _) => view.Refresh();

            var list = new ItemsControl { ItemsSource = panel.Items, ItemTemplate = (DataTemplate)FindResource("FacetTemplate") };
            var scroll = new ScrollViewer { MaxHeight = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list };
            var stack = new StackPanel { Margin = new Thickness(4, 6, 0, 0) };
            stack.Children.Add(filter);
            stack.Children.Add(scroll);
            panel.Expander.Content = stack;
            FacetsHost.Children.Add(panel.Expander);
            _panels[facet] = panel;
        }
    }

    private void RebuildSidePanels()
    {
        _suppress = true;
        try
        {
            // Categorias: guarda quais estavam abertas antes de reconstruir.
            if (CategoryTree.ItemsSource is List<CategoryNode> old)
                foreach (var n in old) { if (n.IsExpanded) _expanded.Add(n.Key); else _expanded.Remove(n.Key); }

            var all = _lib.Index.CountsAll(_spec); // uma passada para todas as facetas
            var nodes = new List<CategoryNode>();
            foreach (var c in all[Facet.Category])
            {
                var node = new CategoryNode(c.Key, c.Label, c.Count, _spec.Categories.Contains(c.Key), false);
                foreach (var s in _lib.Index.Subcategories(_spec, c.Key))
                    node.Children.Add(new CategoryNode(s.Key, s.Label, s.Count, _spec.Subcategories.Contains(s.Key), true));
                node.IsExpanded = _expanded.Contains(c.Key) || node.Children.Any(ch => ch.IsChecked);
                nodes.Add(node);
            }
            CategoryTree.ItemsSource = nodes;

            foreach (var panel in _panels.Values)
            {
                var selected = _spec.Selection(panel.Facet);
                var counts = all[panel.Facet];
                panel.Items.Clear();
                foreach (var c in counts) panel.Items.Add(new FacetItem(panel.Facet, c.Key, c.Label, c.Count, selected.Contains(c.Key)));
                foreach (var key in selected.Where(k => counts.All(c => c.Key != k)))
                    panel.Items.Add(new FacetItem(panel.Facet, key, _lib.Index.Label(panel.Facet, key), 0, true));
                panel.Filter.Visibility = panel.Items.Count > 10 ? Visibility.Visible : Visibility.Collapsed;
                panel.Expander.Header = selected.Count > 0 ? $"{panel.Title}  ●{selected.Count}" : panel.Title;
                if (selected.Count > 0) panel.Expander.IsExpanded = true;
            }
        }
        finally
        {
            _suppress = false;
        }
    }

    private void RebuildCollections()
    {
        var keepKind = (CollectionsList.SelectedItem as CollectionItem)?.Kind ?? CollectionItem.All;
        var keepName = (CollectionsList.SelectedItem as CollectionItem)?.Name;
        var items = new List<CollectionItem>
        {
            new(CollectionItem.All, "Todas as famílias", _lib.Index.All.Count),
            new(CollectionItem.Favorites, "★ Favoritas", _favorites.Count),
            new(CollectionItem.Recents, "Recentes", _lib.User.Recents().Count),
        };
        items.AddRange(_lib.User.CollectionNames().Select(n => new CollectionItem(CollectionItem.Custom, n, _lib.User.Collection(n).Count)));

        _suppress = true;
        CollectionsList.ItemsSource = items;
        CollectionsList.SelectedItem = items.FirstOrDefault(i => i.Kind == keepKind && (keepKind != CollectionItem.Custom || i.Name == keepName)) ?? items[0];
        _suppress = false;
        DeleteCollectionButton.IsEnabled = (CollectionsList.SelectedItem as CollectionItem)?.Kind == CollectionItem.Custom;
    }

    // ------------------------------------------------------------------ eventos de filtro

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueRefresh(250);
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _debounce.Stop(); Refresh(); e.Handled = true; }
        else if (e.Key == Key.Escape) { SearchBox.Clear(); e.Handled = true; }
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e) => QueueRefresh();
    private void OnOptionChanged(object sender, RoutedEventArgs e) => QueueRefresh();

    private void OnFacetToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || (sender as FrameworkElement)?.DataContext is not FacetItem item) return;
        var set = _spec.Selection(item.Facet);
        if (item.IsChecked) set.Add(item.Key); else set.Remove(item.Key);
        QueueRefresh();
    }

    private void OnCategoryToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || (sender as FrameworkElement)?.DataContext is not CategoryNode node) return;
        var set = node.IsSubcategory ? _spec.Subcategories : _spec.Categories;
        if (node.IsChecked) set.Add(node.Key); else set.Remove(node.Key);
        QueueRefresh();
    }

    private void OnCollectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        DeleteCollectionButton.IsEnabled = (CollectionsList.SelectedItem as CollectionItem)?.Kind == CollectionItem.Custom;
        QueueRefresh();
    }

    private void OnClearFilters(object sender, RoutedEventArgs e)
    {
        _spec.Clear();
        _imageQuery = null;
        ImageChip.Visibility = Visibility.Collapsed;
        OnlyPreviewCheck.IsChecked = false;
        OnlyDownloadedCheck.IsChecked = false;
        _suppress = true;
        SearchBox.Clear();
        CollectionsList.SelectedIndex = 0;
        _suppress = false;
        Refresh();
    }

    private void OnShowMore(object sender, RoutedEventArgs e) => AppendPage();

    // ------------------------------------------------------------------ IA

    private async Task StartAiAsync()
    {
        try
        {
            UpdateAiStatus();
            await _lib.EnsureSemanticAsync();
        }
        catch (Exception)
        {
            // EnsureSemanticAsync nao lanca; o estado ja descreve a falha.
        }
        UpdateAiStatus();
    }

    private void UpdateAiStatus()
    {
        if (AiCheck.IsChecked != true || !_lib.Settings.SemanticEnabled)
        {
            AiStatusText.Text = "";
            return;
        }
        AiStatusText.Text = _lib.SemanticState switch
        {
            SemanticState.Ready => "IA pronta",
            SemanticState.Starting => "IA iniciando…",
            SemanticState.Unavailable => "IA indisponível",
            _ => "",
        };
        AiStatusText.ToolTip = _lib.SemanticDetail;
    }

    private async void OnAiToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (AiCheck.IsChecked == true) await StartAiAsync();
        QueueRefresh();
    }

    private void OnPickImage(object sender, RoutedEventArgs e)
    {
        var path = Dialogs.PickFile("Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.webp|Todos os arquivos|*.*", "Escolha uma imagem para buscar");
        if (path is null) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 16 * 1024 * 1024) { Dialogs.Alert("A imagem e grande demais (maximo 16 MB)."); return; }
            SetImageQuery(File.ReadAllBytes(path), $"Imagem: {info.Name}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Dialogs.Alert($"Nao foi possivel ler a imagem.\n\n{error.Message}");
        }
    }

    private void SetImageQuery(byte[] bytes, string label)
    {
        _imageQuery = bytes;
        ImageChipText.Text = label;
        ImageChip.Visibility = Visibility.Visible;
        if (AiCheck.IsEnabled) AiCheck.IsChecked = true;
        Refresh();
    }

    private void OnClearImage(object sender, RoutedEventArgs e)
    {
        _imageQuery = null;
        ImageChip.Visibility = Visibility.Collapsed;
        Refresh();
    }

    private void OnSimilar(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var png = _lib.Db.GetPreview(_selected.Record.Id);
        if (png is null) { Dialogs.Alert("Esta familia nao tem preview para comparar."); return; }
        SearchBox.Clear();
        SetImageQuery(png, $"Parecidas com: {_selected.Title}");
    }

    // ------------------------------------------------------------------ selecao e detalhes

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetails(ResultsList.SelectedItem as CardItem);

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_selected is not null && FindParent<ListBoxItem>(e.OriginalSource as DependencyObject) is not null) InsertSelected();
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null && child is not T) child = VisualTreeHelper.GetParent(child);
        return child as T;
    }

    private void ShowDetails(CardItem? card)
    {
        if (_selected is not null) _selected.PropertyChanged -= OnSelectedCardChanged;
        _selected = card;
        if (card is null)
        {
            DetailsHint.Visibility = Visibility.Visible;
            DetailsBody.Visibility = Visibility.Collapsed;
            return;
        }
        card.PropertyChanged += OnSelectedCardChanged;
        var r = card.Record;
        DetailsHint.Visibility = Visibility.Collapsed;
        DetailsBody.Visibility = Visibility.Visible;
        DetailImage.Source = card.Thumbnail;
        DetailTitle.Text = r.Title;
        DetailCategory.Text = card.Subtitle;
        DetailCategory.Visibility = card.Subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailDescription.Text = r.Description ?? "Ainda sem descrição gerada pela IA.";

        TagsPanel.Children.Clear();
        foreach (var tag in r.Tags.Take(20))
        {
            var chip = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFA)), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 4), Cursor = Cursors.Hand,
                ToolTip = "Buscar por esta palavra-chave",
                Child = new TextBlock { Text = tag, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x20, 0x4E, 0x80)) },
            };
            chip.MouseLeftButtonUp += (_, _) => SearchBox.Text = tag;
            TagsPanel.Children.Add(chip);
        }

        var meta = new List<string>();
        if (r.Materials.Count > 0) meta.Add($"Material: {string.Join(", ", r.Materials)}");
        if (r.Colors.Count > 0) meta.Add($"Cores: {string.Join(", ", r.Colors)}");
        if (r.Environments.Count > 0) meta.Add($"Ambientes: {string.Join(", ", r.Environments)}");
        if (!string.IsNullOrEmpty(r.Style)) meta.Add($"Estilo: {r.Style}");
        if (!string.IsNullOrEmpty(r.RevitVersion)) meta.Add($"Salva no Revit: {_lib.Index.Label(Facet.RevitVersion, r.RevitVersion)}");
        if (!string.IsNullOrEmpty(r.RevitCategory)) meta.Add($"Categoria no Revit: {r.RevitCategory}");
        if (r.Copies > 0) meta.Add($"Há {r.Copies} cópia(s) idêntica(s) na pasta de origem");
        DetailMeta.Text = string.Join("\n", meta);

        var note = FamilyCompat.Note(r.RevitVersion, RevitContext.RevitYear);
        CompatBox.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        CompatText.Text = note ?? "";
        DetailPath.Text = r.RelPath;
        if (_lib.Session is not null)
        {
            var have = _lib.IsAvailable(r);
            var size = r.SizeBytes > 0 ? $" ({r.SizeBytes / 1048576.0:0.#} MB)" : "";
            DetailPath.Text = $"{r.RelPath}\n{(have ? "Já baixada: funciona sem internet." : "Será baixada do servidor ao inserir" + size + ".")}";
            InsertButton.Content = have ? "Inserir no projeto" : "Baixar e inserir";
        }
        FavoriteButton.Content = card.IsFavorite ? "★ Favorita" : "☆ Favoritar";
        SimilarButton.IsEnabled = r.HasPreview && _lib.Settings.SemanticEnabled;
    }

    private void OnSelectedCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CardItem card || card != _selected) return;
        if (e.PropertyName == nameof(CardItem.Thumbnail)) DetailImage.Source = card.Thumbnail;
        if (e.PropertyName == nameof(CardItem.IsFavorite)) FavoriteButton.Content = card.IsFavorite ? "★ Favorita" : "☆ Favoritar";
    }

    // ------------------------------------------------------------------ favoritos e colecoes

    private void ToggleFavorite(CardItem card)
    {
        var now = _lib.User.ToggleFavorite(card.Record.Sha);
        if (now) _favorites.Add(card.Record.Sha); else _favorites.Remove(card.Record.Sha);
        card.IsFavorite = now;
        RebuildCollections();
        if ((CollectionsList.SelectedItem as CollectionItem)?.Kind == CollectionItem.Favorites) QueueRefresh();
    }

    private void OnStarClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CardItem card) ToggleFavorite(card);
        e.Handled = true;
    }

    private void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (_selected is not null) ToggleFavorite(_selected);
    }

    private void OnNewCollection(object sender, RoutedEventArgs e)
    {
        var name = Dialogs.Prompt("Nome da nova coleção:", "Nova coleção");
        if (name is null) return;
        _lib.User.CreateCollection(name);
        RebuildCollections();
    }

    private void OnDeleteCollection(object sender, RoutedEventArgs e)
    {
        if (CollectionsList.SelectedItem is not CollectionItem { Kind: CollectionItem.Custom } item) return;
        if (!Dialogs.Alert($"Excluir a coleção \"{item.Name}\"? As famílias continuam na biblioteca.", ask: true)) return;
        _lib.User.DeleteCollection(item.Name);
        CollectionsList.SelectedIndex = 0;
        RebuildCollections();
        QueueRefresh();
    }

    private void OnCollectionMenu(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var sha = _selected.Record.Sha;
        var members = _lib.User.CollectionNames().ToDictionary(n => n, n => _lib.User.Collection(n).Contains(sha));
        var menu = new ContextMenu { PlacementTarget = CollectionButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (name, isMember) in members)
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = isMember };
            item.Click += (_, _) =>
            {
                if (item.IsChecked) _lib.User.AddToCollection(name, sha); else _lib.User.RemoveFromCollection(name, sha);
                RebuildCollections();
            };
            menu.Items.Add(item);
        }
        if (members.Count > 0) menu.Items.Add(new Separator());
        var create = new MenuItem { Header = "Nova coleção…" };
        create.Click += (_, _) =>
        {
            var name = Dialogs.Prompt("Nome da nova coleção:", "Nova coleção");
            if (name is null) return;
            _lib.User.CreateCollection(name);
            _lib.User.AddToCollection(name, sha);
            RebuildCollections();
        };
        menu.Items.Add(create);
        menu.IsOpen = true;
    }

    // ------------------------------------------------------------------ inserir e abrir pasta

    private void OnInsert(object sender, RoutedEventArgs e) => InsertSelected();

    private CancellationTokenSource? _downloadCts;

    private void SetBusy(bool busy, string? status = null)
    {
        InsertButton.IsEnabled = !busy;
        CancelDownloadButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        DownloadBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        DownloadBar.IsIndeterminate = true;
        if (status is not null) StatusText.Text = status;
    }

    private void OnCancelDownload(object sender, RoutedEventArgs e) => _downloadCts?.Cancel();

    private async void InsertSelected()
    {
        if (_selected is null || _downloadCts is not null) return;
        var record = _selected.Record;
        if (_lib.Settings.DownloadProblem() is { } folderProblem)
        {
            // Primeira insercao: a pasta e obrigatoria para baixar. A janela reabre com a configuracao nova.
            if (SettingsWindow.Edit(this, $"Para baixar e inserir famílias, escolha a pasta onde elas serão guardadas.\n{folderProblem}"))
            {
                Close();
                ShowOrActivate();
            }
            return;
        }
        if (_lib.FamilyPath(record) is null)
        {
            Dialogs.Alert("O caminho desta familia no banco e invalido (fora da pasta da biblioteca).");
            return;
        }
        var note = FamilyCompat.Note(record.RevitVersion, RevitContext.RevitYear);
        if (note is not null && note.Contains("nao consegue abri", StringComparison.Ordinal))
        {
            Dialogs.Alert(note);
            return;
        }

        string path;
        _downloadCts = new CancellationTokenSource();
        try
        {
            path = await EnsureFileAsync(record, _downloadCts.Token);
        }
        catch (Exception error)
        {
            SetBusy(false, error is OperationCanceledException ? "Download cancelado." : "Não foi possível baixar a família.");
            if (error is not OperationCanceledException) Dialogs.Alert(ServerMessages.Describe(error));
            return;
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
        SetBusy(false);
        RefreshDownloadedMarks(force: true);

        StatusText.Text = $"Carregando {record.Title}…";
        Hide(); // o posicionamento precisa do foco do Revit; a janela volta quando terminar
        RevitDispatcher.Post(app =>
        {
            InsertResult result;
            try
            {
                result = FamilyInserter.Run(app, path);
            }
            catch (Exception error)
            {
                result = new InsertResult(InsertOutcome.Failed, error.Message);
            }
            try
            {
                if (result.Outcome != InsertOutcome.Failed) _lib.User.AddRecent(record.Sha);
            }
            finally
            {
                Dispatcher.BeginInvoke(() =>
                {
                    Show();
                    Activate();
                    StatusText.Text = result.Message.Replace('\n', ' ');
                    RebuildCollections();
                    if (result.Outcome == InsertOutcome.Failed) Dialogs.Alert(result.Message);
                });
            }
        });
    }

    /// <summary>Garante o arquivo (baixa se preciso), mostrando o andamento; se a sessao expirou, entra e tenta de novo.</summary>
    private async Task<string> EnsureFileAsync(FamilyRecord record, CancellationToken cancel)
    {
        for (var attempt = 0; ; attempt++)
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (p.Phase == DownloadPhase.WaitingForSlot)
                {
                    DownloadBar.IsIndeterminate = true;
                    StatusText.Text = $"Aguardando a vez no servidor… ({p.WaitSeconds}s)";
                    return;
                }
                if (p.Total > 0)
                {
                    DownloadBar.IsIndeterminate = false;
                    DownloadBar.Maximum = p.Total;
                    DownloadBar.Value = p.Done;
                    StatusText.Text = $"Baixando {record.Title}… {p.Done * 100 / p.Total}% ({p.Done / 1048576.0:0.#} de {p.Total / 1048576.0:0.#} MB)";
                }
            });
            SetBusy(true, _lib.IsAvailable(record) ? $"Conferindo {record.Title}…" : $"Baixando {record.Title}…");
            try
            {
                return await _lib.EnsureFamilyFileAsync(record, progress, cancel);
            }
            catch (AuthRequiredException) when (attempt == 0)
            {
                SetBusy(false, "Entre com a sua conta para baixar a família.");
                if (!ServerFlows.SignIn(this)) throw new OperationCanceledException();
                UpdateAccountUi();
            }
        }
    }

    // ------------------------------------------------------------------ conta e catalogo (modo servidor)

    private void UpdateAccountUi()
    {
        if (_lib.Session is not { } session)
        {
            AccountText.Text = "";
            return;
        }
        var version = CatalogSync.LocalVersion(_lib.Settings.EffectiveDb);
        AccountText.Text = (session.Auth.HasSession ? "Conectado" : "Desconectado") + (version > 0 ? $" · catálogo {version}" : "");
        AccountButton.Content = session.Auth.HasSession ? "Sair" : "Entrar";
    }

    private void OnAccount(object sender, RoutedEventArgs e)
    {
        if (_lib.Session is not { } session) return;
        if (session.Auth.HasSession) session.Auth.Logout();
        else ServerFlows.SignIn(this);
        UpdateAccountUi();
    }

    private DateTime _lastScan = DateTime.MinValue;

    /// <summary>
    /// Revarre a pasta de downloads para atualizar as marcas "baixada". Ao voltar o foco para a janela acontece toda hora,
    /// entao so revarre se a ultima varredura tem mais de 20 s (uma pasta de rede grande custa caro); depois de um
    /// download, <paramref name="force"/>.
    /// </summary>
    private void RefreshDownloadedMarks(bool force = false)
    {
        if (_lib.Session is not { } session || _downloadCts is not null) return;
        if (!force && DateTime.UtcNow - _lastScan < TimeSpan.FromSeconds(20)) return;
        _lastScan = DateTime.UtcNow;
        _ = Task.Run(() => session.Downloaded.Refresh(_lib.Settings.EffectiveRoot)).ContinueWith(_ =>
        {
            foreach (var card in _cards) card.RefreshDownloaded();
            if (_selected is not null) ShowDetails(_selected);
            if (OnlyDownloadedCheck.IsChecked == true) QueueRefresh();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnSync(object sender, RoutedEventArgs e) => _ = CheckCatalogAsync(force: true);

    private bool _syncing;

    /// <summary>Procura catalogo novo em segundo plano. Nunca troca o banco com a janela aberta: avisa e oferece aplicar.</summary>
    private async Task CheckCatalogAsync(bool force)
    {
        if (_lib.Session is not { } session || _syncing) return;
        _syncing = true;
        try
        {
            if (force) SyncButton.IsEnabled = false;
            var result = await session.CheckForUpdatesAsync(_lib.Settings.EffectiveDb, _lib.Settings.SyncEveryHours, force, CancellationToken.None);
            switch (result.Outcome)
            {
                case SyncOutcome.Ready:
                    StatusText.Text = result.Message;
                    if (Dialogs.Alert($"{result.Message}\n\nAplicar agora? A janela da biblioteca será reaberta.", "GB Famílias", ask: true))
                    {
                        Close();
                        ShowOrActivate();
                    }
                    break;
                case SyncOutcome.UpToDate:
                    if (force) StatusText.Text = result.Message;
                    break;
                case SyncOutcome.NeedsLogin:
                    if (force && ServerFlows.SignIn(this)) _syncing = false;
                    if (force) UpdateAccountUi();
                    break;
                default:
                    if (force) Dialogs.Alert(result.Message);
                    else StatusText.Text = result.Message.Replace('\n', ' ');
                    break;
            }
        }
        finally
        {
            _syncing = false;
            SyncButton.IsEnabled = true;
            UpdateAccountUi();
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _lib.FamilyPath(_selected.Record) is not { } path) return;
        if (!File.Exists(path) && !Directory.Exists(Path.GetDirectoryName(path)))
        {
            Dialogs.Alert("Esta família ainda não foi baixada, então a pasta dela não existe no seu computador.");
            return;
        }
        try
        {
            var args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Dialogs.Alert($"Nao foi possivel abrir a pasta.\n\n{error.Message}");
        }
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        if (!SettingsWindow.Edit(this, null)) return;
        Close();
        ShowOrActivate(); // reabre com as configuracoes novas
    }
}
