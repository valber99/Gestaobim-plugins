using System.Net.Http;
using Gestaobim.Familias.Core;
using Gestaobim.Familias.Core.Server;

namespace GBFamilias.Core.Tests;

public sealed class DownloadedIndexTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gbfam-idx").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private static FamilyRecord Rec(string rel, long size) => new() { Id = 1, Sha = "s", Name = "n", RelPath = rel, SizeBytes = size };

    private void Write(string rel, int bytes)
    {
        var path = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    [Fact]
    public void ReconheceArquivosPeloCaminhoRelativoEPeloTamanho()
    {
        Write("#PORTAS\\Porta 1.rfa", 100);
        Write("#PORTAS\\sub\\Porta 2.RFA", 50);
        Write("#PORTAS\\notas.txt", 10);
        var index = new DownloadedIndex();
        index.Refresh(_root);
        Assert.Equal(2, index.Count);
        Assert.True(index.IsDownloaded(Rec("#PORTAS\\Porta 1.rfa", 100)));
        Assert.True(index.IsDownloaded(Rec("#portas/porta 1.rfa", 100)), "barra e caixa nao importam");
        Assert.True(index.IsDownloaded(Rec("#PORTAS\\sub\\Porta 2.rfa", 50)));
        Assert.False(index.IsDownloaded(Rec("#PORTAS\\Porta 1.rfa", 999)), "tamanho diferente = arquivo incompleto ou de outra versao");
        Assert.False(index.IsDownloaded(Rec("#PORTAS\\Outra.rfa", 100)));
        Assert.True(index.IsDownloaded(Rec("#PORTAS\\Porta 1.rfa", 0)), "tamanho desconhecido: basta existir");
    }

    [Fact]
    public void PastaInexistenteOuVaziaNaoLancaENaoTemNada()
    {
        var index = new DownloadedIndex();
        index.Refresh(Path.Combine(_root, "nao-existe"));
        Assert.Equal(0, index.Count);
        index.Refresh(null);
        index.Refresh("  ");
        Assert.False(index.IsDownloaded(Rec("a.rfa", 1)));
    }

    [Fact]
    public void MarcarBaixadaEAtualizarDeNovo()
    {
        var index = new DownloadedIndex();
        index.Refresh(_root);
        var rec = Rec("x\\a.rfa", 10);
        Assert.False(index.IsDownloaded(rec));
        index.MarkDownloaded(rec, 10);
        Assert.True(index.IsDownloaded(rec));
        index.Refresh(_root); // a varredura reflete o disco: o arquivo nao existe
        Assert.False(index.IsDownloaded(rec));
    }

    [Fact]
    public void FiltroExtraRestringeResultadosEContagens()
    {
        var index = TestSupport.SampleIndex();
        var downloaded = new HashSet<long> { 1, 3, 6 };
        var spec = new FilterSpec { Extra = r => downloaded.Contains(r.Id) };
        Assert.False(spec.IsEmpty);
        Assert.Equal([1L, 3L, 6L], index.Query(spec, SortMode.Name).Select(r => r.Id).Order());
        // as contagens de uma faceta consideram o filtro extra
        var counts = index.Counts(spec, Facet.Category);
        Assert.Equal(3, counts.Sum(c => c.Count));
        var clone = spec.Clone();
        Assert.NotNull(clone.Extra);
        spec.Clear();
        Assert.Null(spec.Extra);
        Assert.True(spec.IsEmpty);
        Assert.Equal(8, index.Query(spec).Count);
    }
}

public class ServerMessagesTests
{
    [Fact]
    public void CadaErroVirraUmaMensagemEmPortugues()
    {
        Assert.Contains("Entre", ServerMessages.Describe(new AuthRequiredException("Entre com a sua conta.")));
        Assert.Contains("administrador", ServerMessages.Describe(new FamilyNotOnServerException("x")));
        Assert.Contains("limitou", ServerMessages.Describe(new RateLimitedException(5, "Limite de downloads.")));
        Assert.Contains("já baixadas continuam", ServerMessages.Describe(new ServerUnavailableException("dns")));
        Assert.Equal("Cancelado.", ServerMessages.Describe(new OperationCanceledException()));
        Assert.Contains("gravar", ServerMessages.Describe(new IOException("disco cheio")));
        Assert.Contains("permissão", ServerMessages.Describe(new UnauthorizedAccessException("negado")));
        Assert.Equal("qualquer", ServerMessages.Describe(new InvalidOperationException("qualquer")));
    }

    [Fact]
    public void SoOErroDeLoginPedeLogin()
    {
        Assert.True(ServerMessages.NeedsLogin(new AuthRequiredException("x")));
        Assert.False(ServerMessages.NeedsLogin(new NotAuthorizedException("x")));
        Assert.False(ServerMessages.NeedsLogin(new ServerUnavailableException("x")));
    }

    [Fact]
    public void ClienteHttpSempreSeIdentificaParaACloudflareNaoBloquear()
    {
        using var http = new HttpClient();
        ServerApi.ConfigureClient(http, "0.2.0");
        ServerApi.ConfigureClient(http, "0.2.0"); // repetir nao duplica
        Assert.Equal("GBFamilias/0.2.0", http.DefaultRequestHeaders.UserAgent.ToString());
    }
}
