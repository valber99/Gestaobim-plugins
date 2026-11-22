using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Gestaobim.Familias.Core;
using Gestaobim.Familias.Revit;

namespace Gestaobim.Familias.UI;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Miniaturas lidas do banco sob demanda e guardadas num cache limitado.</summary>
public sealed class ThumbnailCache
{
    private const int Capacity = 800;
    private readonly Dictionary<long, BitmapSource> _cache = [];
    private readonly Queue<long> _order = new();

    public bool TryGet(long id, out BitmapSource? image) => _cache.TryGetValue(id, out image);

    public void Put(long id, BitmapSource image)
    {
        if (_cache.TryAdd(id, image)) _order.Enqueue(id);
        while (_cache.Count > Capacity && _order.TryDequeue(out var old)) _cache.Remove(old);
    }

    public static BitmapSource? Decode(byte[]? png)
    {
        if (png is null || png.Length == 0) return null;
        try
        {
            using var stream = new MemoryStream(png);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is NotSupportedException or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>Um cartao da grade de resultados.</summary>
public sealed class CardItem : Observable
{
    private readonly ThumbnailCache _cache;
    private readonly LibraryDb _db;
    private ImageSource? _thumbnail;
    private bool _loading;
    private bool _favorite;

    private readonly Func<FamilyRecord, bool>? _downloaded;

    /// <param name="downloaded">So no modo servidor: diz se a familia ja esta na pasta de downloads (null = sem marca).</param>
    public CardItem(FamilyRecord record, ThumbnailCache cache, LibraryDb db, bool favorite, Func<FamilyRecord, bool>? downloaded = null)
    {
        _downloaded = downloaded;
        Record = record;
        _cache = cache;
        _db = db;
        _favorite = favorite;
    }

    public FamilyRecord Record { get; }
    public string Title => Record.Title;
    public string Subtitle => string.Join(" › ", new[] { Record.Category, Record.Subcategory }.Where(s => !string.IsNullOrEmpty(s)));
    public string VersionBadge => Record.RevitVersion is { Length: > 0 } v ? v.TrimStart('~') : "";
    public bool NoPreview => !Record.HasPreview;
    /// <summary>Mostra a marca verde "baixada" (so no modo servidor).</summary>
    public bool ShowDownloaded => _downloaded?.Invoke(Record) == true;
    public void RefreshDownloaded() => Raise(nameof(ShowDownloaded));

    public bool IsFavorite
    {
        get => _favorite;
        set
        {
            if (_favorite == value) return;
            _favorite = value;
            Raise();
            Raise(nameof(StarText));
        }
    }

    public string StarText => _favorite ? "★" : "☆";

    /// <summary>Carrega no primeiro acesso: devolve null agora e avisa a interface quando a imagem chega.</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbnail is not null) return _thumbnail;
            if (_cache.TryGet(Record.Id, out var cached)) return _thumbnail = cached;
            if (!_loading && Record.HasPreview)
            {
                _loading = true;
                var id = Record.Id;
                _ = Task.Run(() => _db.GetPreview(id)).ContinueWith(task =>
                {
                    var image = ThumbnailCache.Decode(task.Status == TaskStatus.RanToCompletion ? task.Result : null);
                    if (image is not null) _cache.Put(id, image);
                    _thumbnail = image;
                    Raise(nameof(Thumbnail));
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            return null;
        }
    }
}

/// <summary>Uma opcao de filtro (material, cor, ambiente...) com a contagem de resultados.</summary>
public sealed class FacetItem : Observable
{
    private bool _checked;
    private int _count;

    public FacetItem(Facet facet, string key, string label, int count, bool isChecked)
    {
        Facet = facet;
        Key = key;
        Label = label;
        _count = count;
        _checked = isChecked;
    }

    public Facet Facet { get; }
    public string Key { get; }
    public string Label { get; }
    public int Count { get => _count; set { _count = value; Raise(); Raise(nameof(Text)); } }
    public string Text => $"{Label} ({_count})";
    public bool IsChecked { get => _checked; set { if (_checked != value) { _checked = value; Raise(); } } }
}

/// <summary>No da arvore de categorias (a subcategoria e filha da categoria).</summary>
public sealed class CategoryNode : Observable
{
    private bool _checked;

    public CategoryNode(string key, string label, int count, bool isChecked, bool isSubcategory)
    {
        Key = key;
        Label = label;
        Count = count;
        _checked = isChecked;
        IsSubcategory = isSubcategory;
    }

    public string Key { get; }
    public string Label { get; }
    public int Count { get; }
    public bool IsSubcategory { get; }
    public string Text => $"{Label} ({Count})";
    public List<CategoryNode> Children { get; } = [];
    public bool IsChecked { get => _checked; set { if (_checked != value) { _checked = value; Raise(); } } }

    private bool _expanded;
    /// <summary>A arvore e reconstruida a cada filtro; a janela guarda quais categorias estavam abertas.</summary>
    public bool IsExpanded { get => _expanded; set { if (_expanded != value) { _expanded = value; Raise(); } } }
}

/// <summary>Uma linha da lista de colecoes (todas, favoritas, recentes ou uma colecao do usuario).</summary>
public sealed class CollectionItem(string kind, string name, int count)
{
    public const string All = "all";
    public const string Favorites = "fav";
    public const string Recents = "recent";
    public const string Custom = "custom";

    public string Kind { get; } = kind;
    public string Name { get; } = name;
    public int Count { get; } = count;
    public string Text => Kind == All ? Name : $"{Name} ({Count})";
}
