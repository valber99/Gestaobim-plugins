using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Gestaobim.Ai.Revit;

/// <summary>Uma conexao pronta com o gateway: o cliente, as configuracoes e os modelos que ele oferece.</summary>
public sealed class AiConnection : IDisposable
{
    public AiConnection(AiClient client, AiSettings settings, IReadOnlyList<string> models)
    {
        Client = client;
        Settings = settings;
        Models = models;
    }

    public AiClient Client { get; }
    public AiSettings Settings { get; }
    public IReadOnlyList<string> Models { get; }

    /// <summary>O modelo que planeja: o escolhido pelo usuario se o gateway o oferece, senao o primeiro adequado.</summary>
    public string PlanModel => Resolve(Settings.PlanModel, image: false);

    public string ImageModel => Resolve(Settings.ImageModel, image: true);

    private string Resolve(string chosen, bool image)
    {
        if (!string.IsNullOrWhiteSpace(chosen) && Models.Contains(chosen))
        {
            // Se o usuario deixou um modelo de texto no campo de render mas ha um modelo de imagem disponivel, usa o de imagem.
            if (image && !AiUi.LooksLikeImageModel(chosen))
            {
                var autoImg = AiSettings.PickModel(Models, image: true);
                if (!string.IsNullOrWhiteSpace(autoImg) && AiUi.LooksLikeImageModel(autoImg)) return autoImg;
            }
            return chosen;
        }
        return AiSettings.PickModel(Models, image);
    }

    public void Dispose() => Client.Dispose();
}

public static class AiService
{
    /// <summary>Sobe o gateway (se ainda nao estiver de pe) e lista os modelos. Lanca <see cref="GatewayException"/> ou <see cref="AiException"/> com mensagem para o usuario.</summary>
    public static async Task<AiConnection> ConnectAsync(CancellationToken cancel = default)
    {
        var settings = AiSettings.Load();
        var endpoint = await GatewayHost.EnsureStartedAsync(settings.ProviderKeys(), cancel).ConfigureAwait(false);
        var client = new AiClient(endpoint);
        try
        {
            var models = await client.ListModelsAsync(cancel).ConfigureAwait(false);
            return new AiConnection(client, settings, models);
        }
        catch (Exception)
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Mensagem curta para quando nao ha conta nem chave.</summary>
    public const string NoModelsMessage =
        "O gateway de IA esta de pe, mas nao ha nenhum modelo disponivel. Abra \"Configurar IA\" e conecte uma conta ou informe uma chave de API.";
}
