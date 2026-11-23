using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gestaobim.Cotas.Core;

/// <summary>
/// O "padrao do escritorio" como dado, nao como prompt: distancias em milimetros DE PAPEL (o solver multiplica pela escala da vista),
/// quais lados recebem as tres cadeias e como tratar vaos curtos. O padrao segue a pratica da NBR 6492 (cotas fora do desenho,
/// linhas paralelas, texto acima da linha).
/// </summary>
public sealed class RuleProfile
{
    public string Name { get; set; } = "ABNT NBR 6492 (padrao)";

    /// <summary>Da face da construcao ate a primeira linha de cota.</summary>
    public double FirstOffsetPaperMm { get; set; } = 10;

    /// <summary>Entre uma linha de cota e a proxima.</summary>
    public double LineSpacingPaperMm { get; set; } = 7;

    public double TextHeightPaperMm { get; set; } = 2.5;

    /// <summary>Entre a linha e a base do texto.</summary>
    public double TextGapPaperMm { get; set; } = 1;

    /// <summary>Folga em volta da faixa de uma cadeia na deteccao de colisao.</summary>
    public double CollisionMarginPaperMm { get; set; } = 0.5;

    /// <summary>Quanto a linha pode ser empurrada para fora ate desistir (em passos de meio espacamento).</summary>
    public int MaxPushSteps { get; set; } = 40;

    /// <summary>Lados que recebem as tres cadeias (interna, intermediaria e externa). Os demais recebem so a externa.</summary>
    public List<Side> FullSides { get; set; } = [Side.Top, Side.Left];

    public List<Side> ExternalOnlySides { get; set; } = [Side.Bottom, Side.Right];

    /// <summary>Usar o nucleo da parede em vez do acabamento nas referencias de parede (executivo estrutural).</summary>
    public bool UseWallCore { get; set; }

    /// <summary>Margem, em mm do modelo, para dizer que uma referencia pertence a uma fachada (espessura de parede e algo mais).</summary>
    public double FacadeBandMm { get; set; } = 700;

    public int MaxChains { get; set; } = 24;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static RuleProfile FromJson(string json)
    {
        var profile = JsonSerializer.Deserialize<RuleProfile>(json, Options) ?? throw new JsonException("Perfil vazio.");
        profile.Validate();
        return profile;
    }

    public void Validate()
    {
        void Positive(double value, string name)
        {
            if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException($"{name} deve ser maior que zero.");
        }
        Positive(FirstOffsetPaperMm, nameof(FirstOffsetPaperMm));
        Positive(LineSpacingPaperMm, nameof(LineSpacingPaperMm));
        Positive(TextHeightPaperMm, nameof(TextHeightPaperMm));
        Positive(FacadeBandMm, nameof(FacadeBandMm));
        if (TextGapPaperMm < 0 || CollisionMarginPaperMm < 0) throw new ArgumentException("Folgas nao podem ser negativas.");
        if (MaxPushSteps is < 1 or > 500) throw new ArgumentException("MaxPushSteps fora do intervalo 1..500.");
        if (MaxChains is < 1 or > 200) throw new ArgumentException("MaxChains fora do intervalo 1..200.");
    }

    /// <summary>Largura estimada do texto de uma cota (em mm de papel): numero de caracteres x altura x 0,75.</summary>
    public double TextWidthPaperMm(double valueMm) => Math.Max(1, Math.Round(valueMm).ToString("0", System.Globalization.CultureInfo.InvariantCulture).Length) * TextHeightPaperMm * 0.75;
}
