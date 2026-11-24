using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Gestaobim.Familias.Core.Server;

/// <summary>Enderecos e identificadores publicos do servidor. Nada aqui e segredo: o app e publico (PKCE, sem chave).</summary>
public sealed class ServerConfig
{
    public string Issuer { get; init; } = "https://auth.gestaobim.com";
    public string ApiBase { get; init; } = "https://gbf.valbermotta.org";
    public string RegisterUrl { get; init; } = "https://cadastro.gestaobim.com";
    public string ClientId { get; init; } = "400731524095344643";
    public string ProjectId { get; init; } = "400731521125711875";
    public int[] RedirectPorts { get; init; } = [53682, 53683, 53684];
    /// <summary>Chave publica Ed25519 (base64) que assina o manifesto do catalogo.</summary>
    public string CatalogPublicKey { get; init; } = "QjzbuV+qjwLBP9ZFk1bvwbScfIrSISNjM7NjgMpQf7E=";

    public string Scope =>
        $"openid profile offline_access urn:zitadel:iam:org:project:id:{ProjectId}:aud urn:zitadel:iam:org:projects:roles";
}

public sealed class AuthRequiredException(string message) : Exception(message);

public sealed record TokenSet(string AccessToken, string? RefreshToken, DateTime ExpiresAtUtc)
{
    public bool IsFresh(DateTime nowUtc, TimeSpan margin) => ExpiresAtUtc - nowUtc > margin;
}

public static class Pkce
{
    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>43 a 128 caracteres (RFC 7636). Aqui, 64.</summary>
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(48));

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(24));
}

/// <summary>Protege os tokens em disco (DPAPI no Windows). Interface para os testes nao dependerem do sistema.</summary>
public interface ITokenProtector
{
    byte[] Protect(byte[] plain);
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>So para testes e sistemas sem protecao de usuario: NAO protege nada.</summary>
public sealed class NoProtection : ITokenProtector
{
    public byte[] Protect(byte[] plain) => plain;
    public byte[] Unprotect(byte[] protectedData) => protectedData;
}

public sealed class TokenStore(string path, ITokenProtector protector)
{
    public TokenSet? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = Encoding.UTF8.GetString(protector.Unprotect(File.ReadAllBytes(path)));
            var node = JsonNode.Parse(json) as JsonObject;
            var access = node?["access_token"]?.GetValue<string>();
            if (string.IsNullOrEmpty(access)) return null;
            return new TokenSet(access, node?["refresh_token"]?.GetValue<string>(),
                DateTime.Parse(node!["expires_at"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime());
        }
        catch (Exception error) when (error is IOException or CryptographicException or JsonException or FormatException
                                          or InvalidOperationException or NullReferenceException or UnauthorizedAccessException)
        {
            return null; // arquivo ilegivel (outro usuario, corrompido): pede login de novo
        }
    }

    public void Save(TokenSet tokens)
    {
        var json = new JsonObject
        {
            ["access_token"] = tokens.AccessToken,
            ["refresh_token"] = tokens.RefreshToken,
            ["expires_at"] = tokens.ExpiresAtUtc.ToString("O"),
        }.ToJsonString();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temp, protector.Protect(Encoding.UTF8.GetBytes(json)));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Resultado da pagina de retorno do navegador.</summary>
public sealed record CallbackResult(string? Code, string? State, string? Error);

/// <summary>
/// Servidor minimo em 127.0.0.1 que recebe o retorno do login (GET /callback?code=...&amp;state=...). Usa TcpListener
/// (sem HttpListener) para nao depender de reserva de URL nem de administrador.
/// </summary>
public sealed class LoopbackListener : IDisposable
{
    private readonly TcpListener _listener;
    public int Port { get; }

    private LoopbackListener(TcpListener listener, int port)
    {
        _listener = listener;
        Port = port;
    }

    /// <summary>Tenta cada porta da lista (as registradas no provedor OIDC) e usa a primeira livre.</summary>
    public static LoopbackListener Start(IEnumerable<int> ports)
    {
        foreach (var port in ports)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
                listener.Start();
                return new LoopbackListener(listener, port);
            }
            catch (SocketException)
            {
            }
        }
        throw new IOException("Nao ha porta local livre para receber o login (as portas 53682 a 53684 estao ocupadas).");
    }

    public async Task<CallbackResult> WaitAsync(CancellationToken cancel)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancel).ConfigureAwait(false);
            client.ReceiveTimeout = 5000;
            using var stream = client.GetStream();
            var buffer = new byte[8192];
            var read = 0;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }
            if (read == 0) continue;
            var request = Encoding.ASCII.GetString(buffer, 0, read);
            var line = request.Split('\n', 2)[0].Trim();
            var parts = line.Split(' ');
            if (parts.Length < 2 || parts[0] != "GET" || !parts[1].StartsWith("/callback", StringComparison.Ordinal))
            {
                await TryRespond(stream, 404, "Nao encontrado.").ConfigureAwait(false);
                continue; // ex.: /favicon.ico
            }
            var query = parts[1].Contains('?') ? parts[1][(parts[1].IndexOf('?') + 1)..] : "";
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var segment in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = segment.Split('=', 2);
                var key = TryUnescape(kv[0]);
                var val = kv.Length > 1 ? TryUnescape(kv[1].Replace('+', ' ')) : "";
                values.TryAdd(key, val);
            }
            values.TryGetValue("code", out var code);
            values.TryGetValue("state", out var state);
            values.TryGetValue("error", out var error);
            var ok = !string.IsNullOrEmpty(code) && string.IsNullOrEmpty(error);
            await TryRespond(stream, 200, ok
                ? "Login concluido. Pode fechar esta aba e voltar para o Revit."
                : "O login nao foi concluido. Volte para o Revit e tente de novo.").ConfigureAwait(false);
            return new CallbackResult(code, state, error);
        }
    }

    private static string TryUnescape(string value)
    {
        try { return Uri.UnescapeDataString(value); }
        catch (UriFormatException) { return value; }
    }

    private static async Task TryRespond(NetworkStream stream, int status, string message)
    {
        try
        {
            await Respond(stream, status, message).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
    }

    private static async Task Respond(NetworkStream stream, int status, string message)
    {
        var html = $"<!doctype html><html lang=\"pt-BR\"><meta charset=\"utf-8\"><title>GB Famílias</title>" +
                   $"<body style=\"font-family:Segoe UI,sans-serif;text-align:center;margin-top:15vh\"><h2>GB Famílias</h2><p>{WebUtility.HtmlEncode(message)}</p></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    public void Dispose() => _listener.Stop();
}

/// <summary>OAuth 2.0 Authorization Code + PKCE contra o provedor OIDC, pelo navegador do sistema.</summary>
public sealed class AuthClient
{
    private readonly ServerConfig _config;
    private readonly HttpClient _http;
    private readonly TokenStore _store;
    private readonly Func<DateTime> _now;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private TokenSet? _tokens;

    public AuthClient(ServerConfig config, HttpClient http, TokenStore store, Func<DateTime>? now = null)
    {
        _config = config;
        _http = http;
        _store = store;
        _now = now ?? (() => DateTime.UtcNow);
        _tokens = store.Load();
    }

    public bool HasSession => _tokens is not null;

    public string BuildAuthorizeUrl(string redirectUri, string state, string challenge) =>
        $"{_config.Issuer}/oauth/v2/authorize?client_id={Uri.EscapeDataString(_config.ClientId)}" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}&response_type=code&scope={Uri.EscapeDataString(_config.Scope)}" +
        $"&state={Uri.EscapeDataString(state)}&code_challenge={Uri.EscapeDataString(challenge)}&code_challenge_method=S256";

    /// <summary>Abre o navegador (via <paramref name="openBrowser"/>), espera o retorno e troca o codigo pelos tokens.</summary>
    public async Task LoginAsync(Action<string> openBrowser, CancellationToken cancel, TimeSpan? timeout = null)
    {
        using var listener = LoopbackListener.Start(_config.RedirectPorts);
        var redirect = $"http://127.0.0.1:{listener.Port}/callback";
        var verifier = Pkce.NewVerifier();
        var state = Pkce.NewState();
        openBrowser(BuildAuthorizeUrl(redirect, state, Pkce.Challenge(verifier)));

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
        CallbackResult result;
        try
        {
            result = await listener.WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new AuthRequiredException("O login nao foi concluido a tempo. Tente de novo.");
        }

        if (!string.Equals(result.State, state, StringComparison.Ordinal))
            throw new AuthRequiredException("Resposta de login invalida (state diferente). Tente de novo.");
        if (!string.IsNullOrEmpty(result.Error) || string.IsNullOrEmpty(result.Code))
            throw new AuthRequiredException($"O login foi recusado ({result.Error ?? "sem codigo"}).");

        var tokens = await RequestTokensAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = result.Code,
            ["redirect_uri"] = redirect,
            ["client_id"] = _config.ClientId,
            ["code_verifier"] = verifier,
        }, cancel).ConfigureAwait(false);
        Remember(tokens);
    }

    private void Remember(TokenSet tokens)
    {
        _tokens = tokens;
        _store.Save(tokens);
    }

    public void Logout()
    {
        _tokens = null;
        _store.Clear();
    }

    /// <summary>Um token valido: o guardado, ou um novo pela renovacao. Lanca <see cref="AuthRequiredException"/> se precisar de login.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancel, bool forceRefresh = false, string? failedToken = null)
    {
        var current = _tokens;
        if (current is null)
            throw new AuthRequiredException($"Entre com a sua conta para usar a biblioteca.\n\nAinda não tem conta? Cadastre-se em {_config.RegisterUrl}");
        if (!forceRefresh && current.IsFresh(_now(), TimeSpan.FromSeconds(60))) return current.AccessToken;

        await _refreshLock.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            current = _tokens;
            if (current is null)
                throw new AuthRequiredException($"Entre com a sua conta para usar a biblioteca.\n\nAinda não tem conta? Cadastre-se em {_config.RegisterUrl}");
            // outra chamada pode ter renovado enquanto esperavamos (mesmo em forceRefresh com token antigo diferente)
            if ((!forceRefresh || (failedToken is not null && !string.Equals(current.AccessToken, failedToken, StringComparison.Ordinal)))
                && current.IsFresh(_now(), TimeSpan.FromSeconds(60)))
                return current.AccessToken;
            if (string.IsNullOrEmpty(current.RefreshToken))
            {
                Logout();
                throw new AuthRequiredException("A sessao expirou. Entre de novo.");
            }
            try
            {
                var renewed = await RequestTokensAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = current.RefreshToken,
                    ["client_id"] = _config.ClientId,
                }, cancel).ConfigureAwait(false);
                Remember(renewed with { RefreshToken = renewed.RefreshToken ?? current.RefreshToken });
                return _tokens!.AccessToken;
            }
            catch (AuthRequiredException)
            {
                Logout();
                throw;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<TokenSet> RequestTokensAsync(Dictionary<string, string> form, CancellationToken cancel)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync($"{_config.Issuer}/oauth/v2/token", new FormUrlEncodedContent(form), cancel).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            throw new ServerUnavailableException($"Nao foi possivel falar com o servidor de login: {error.Message}");
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var reason = TryError(text);
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new AuthRequiredException(reason == "invalid_grant" ? "A sessao expirou. Entre de novo." : $"O login foi recusado ({reason ?? (int)response.StatusCode + ""}).");
                throw new ServerUnavailableException($"O servidor de login respondeu {(int)response.StatusCode}.");
            }
            var node = JsonNode.Parse(text) as JsonObject ?? throw new ServerUnavailableException("Resposta de login ilegivel.");
            var access = node["access_token"]?.GetValue<string>();
            if (string.IsNullOrEmpty(access)) throw new ServerUnavailableException("A resposta de login veio sem token.");
            var seconds = node["expires_in"] is JsonValue v && v.TryGetValue<long>(out var s) ? s : 300;
            return new TokenSet(access, node["refresh_token"]?.GetValue<string>(), _now().AddSeconds(seconds));
        }
    }

    private static string? TryError(string text)
    {
        try
        {
            return (JsonNode.Parse(text) as JsonObject)?["error"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class ServerUnavailableException(string message) : Exception(message);
