using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gestaobim.Ai;

namespace Gestaobim.Render.Core;

/// <summary>Os arquivos de um render (pacote da fase 2) e a conferencia deles.</summary>
public static class RenderBundle
{
    public const string ClayName = "clay.png";
    public const string ColorIdName = "color_id.png";
    public const string DepthName = "depth.png";
    public const string BackgroundName = "background.png";
    public const string ManifestName = "scene_manifest.json";

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static void Write(Stream output, RenderLayers layers, ManifestResult manifest)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Add(zip, ClayName, Png.Encode(layers.Clay));
        Add(zip, ColorIdName, Png.Encode(layers.ColorId));
        Add(zip, DepthName, Png.Encode(layers.Depth));
        Add(zip, BackgroundName, Png.Encode(layers.Background));
        Add(zip, ManifestName, Encoding.UTF8.GetBytes(manifest.Manifest.ToJsonString(Pretty)));
        foreach (var swatch in manifest.Swatches) Add(zip, $"swatch_{swatch.ImageIndex}.png", swatch.Png);
    }

    private static void Add(ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content);
    }

    /// <summary>Abre o pacote, decodifica as quatro imagens e confere tudo (e o que o teste de aceite roda).</summary>
    public static VerificationResult Verify(Stream bundle)
    {
        using var zip = new ZipArchive(bundle, ZipArchiveMode.Read, leaveOpen: true);
        byte[] Read(string name)
        {
            var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"Falta {name} no pacote.");
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        try
        {
            var manifest = JsonNode.Parse(Read(ManifestName))!;
            return ManifestVerifier.Verify(manifest, Png.Decode(Read(ClayName)), Png.Decode(Read(ColorIdName)), Png.Decode(Read(DepthName)), Png.Decode(Read(BackgroundName)));
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or JsonException)
        {
            return new VerificationResult([error.Message], 0, 0);
        }
    }

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}

/// <summary>Relatorio de uma geracao: hashes de entrada e saida, modelo e tempos. Nunca leva chave, token nem imagem.</summary>
public sealed class RenderReport
{
    public string Kind { get; set; } = "render";
    public string Model { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public Dictionary<string, string> InputSha256 { get; set; } = [];
    public string? OutputSha256 { get; set; }
    public long OutputBytes { get; set; }
    public string? OutputMime { get; set; }
    public double DurationSeconds { get; set; }
    public int ObjectCount { get; set; }
    public string? Note { get; set; }
    public string? EditedObject { get; set; }
    public double? ChangedInsideMask { get; set; }
    public double? ChangedOutsideMask { get; set; }
    public string StartedUtc { get; set; } = DateTime.UtcNow.ToString("o");

    public string ToJson(params string?[] secrets)
    {
        var text = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        return Redactor.Scrub(text, secrets);
    }
}
