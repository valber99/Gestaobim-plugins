using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Gestaobim.Familias.Core.Server;

/// <summary>
/// Quais familias ja estao na pasta de downloads. Uma unica varredura da pasta (em vez de um acesso a disco por
/// familia), refeita depois de cada download e quando a janela volta ao foco. Vale o nome relativo e o tamanho.
/// </summary>
public sealed class DownloadedIndex
{
    private Dictionary<string, long> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public string? Root { get; private set; }
    public int Count { get { lock (_lock) return _files.Count; } }

    private static string Key(string relativePath) => relativePath.Replace('/', '\\').Trim('\\');

    /// <summary>Varre a pasta. Pasta inexistente ou ilegivel = nada baixado (nunca lanca).</summary>
    public void Refresh(string? root)
    {
        var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
        {
            try
            {
                var full = Path.GetFullPath(root);
                var prefix = full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
                foreach (var file in new DirectoryInfo(full).EnumerateFiles("*.rfa", SearchOption.AllDirectories))
                    files[Key(file.FullName[prefix.Length..])] = file.Length;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // varredura incompleta: o que ja foi lido vale
            }
        }
        lock (_lock)
        {
            Root = root;
            _files = files;
        }
    }

    /// <summary>Existe no disco com o tamanho do catalogo? (O hash so e conferido ao inserir.)</summary>
    public bool IsDownloaded(FamilyRecord record)
    {
        lock (_lock)
            return _files.TryGetValue(Key(record.RelPath), out var length) && (record.SizeBytes <= 0 || length == record.SizeBytes);
    }

    public void MarkDownloaded(FamilyRecord record, long length)
    {
        lock (_lock) _files[Key(record.RelPath)] = length;
    }
}

/// <summary>Mensagens em portugues para os erros do servidor (a interface so mostra o texto).</summary>
public static class ServerMessages
{
    public static string Describe(Exception error) => error switch
    {
        AuthRequiredException e => e.Message,
        NotAuthorizedException e => e.Message,
        FamilyNotOnServerException => "Esta família não está no servidor. Peça ao administrador para publicar o catálogo de novo.",
        RateLimitedException e => $"O servidor limitou os downloads: {e.Message} Tente de novo em instantes.",
        IntegrityException e => e.Message,
        CatalogRejectedException e => e.Message,
        ServerUnavailableException e => $"Sem conexão com o servidor. {e.Message}\n\nAs famílias já baixadas continuam disponíveis.",
        OperationCanceledException => "Cancelado.",
        InvalidDataException e => $"O servidor enviou um arquivo inválido: {e.Message}",
        IOException e => $"Erro ao gravar na pasta de downloads: {e.Message}",
        UnauthorizedAccessException e => $"Sem permissão para gravar na pasta de downloads: {e.Message}",
        _ => error.Message,
    };

    /// <summary>Esse erro pede que o usuario entre de novo (e vale tentar depois do login)?</summary>
    public static bool NeedsLogin(Exception error) => error is AuthRequiredException;
}
