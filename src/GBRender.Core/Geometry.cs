using System;

namespace Gestaobim.Render.Core;

/// <summary>Vetor 3D em metros (o Revit trabalha em pes: a camada do Revit converte antes de chegar aqui).</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    public double Dot(Vec3 o) => X * o.X + Y * o.Y + Z * o.Z;
    public Vec3 Cross(Vec3 o) => new(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);
    public double Length => Math.Sqrt(Dot(this));

    public Vec3 Normalized()
    {
        var l = Length;
        return l < 1e-12 ? new Vec3(0, 0, 0) : this * (1 / l);
    }
}

/// <summary>
/// A camera da vista 3D. <see cref="Right"/>, <see cref="Up"/> e <see cref="Forward"/> formam uma base ortonormal. A janela da imagem e
/// um retangulo (esquerda, direita, baixo, cima): em perspectiva em tangentes (x/z e y/z), em ortografica em metros. A janela pode ser
/// descentrada, como a regiao de corte de uma vista em perspectiva do Revit.
/// </summary>
public sealed class Camera
{
    public Vec3 Eye { get; }
    public Vec3 Forward { get; }
    public Vec3 Right { get; }
    public Vec3 Up { get; }
    public bool Perspective { get; }
    public double Left { get; }
    public double RightEdge { get; }
    public double Bottom { get; }
    public double Top { get; }
    public double Near { get; }

    private Camera(Vec3 eye, Vec3 forward, Vec3 up, bool perspective, double left, double right, double bottom, double top, double near)
    {
        if (!(right > left) || !(top > bottom)) throw new ArgumentOutOfRangeException(nameof(right), "A janela da camera precisa ter largura e altura positivas.");
        var f = forward.Normalized();
        if (f.Length < 0.5) throw new ArgumentException("Direcao da camera invalida.", nameof(forward));
        var r = f.Cross(up).Normalized();
        if (r.Length < 0.5) throw new ArgumentException("A direcao 'para cima' nao pode ser paralela a da camera.", nameof(up));
        Eye = eye;
        Forward = f;
        Right = r;
        Up = r.Cross(f).Normalized();
        Perspective = perspective;
        Left = left;
        RightEdge = right;
        Bottom = bottom;
        Top = top;
        Near = near;
    }

    /// <summary>Largura sobre altura da janela (a proporcao da imagem).</summary>
    public double Aspect => (RightEdge - Left) / (Top - Bottom);

    public double TanHalfH => (RightEdge - Left) / 2;
    public double TanHalfV => (Top - Bottom) / 2;

    public static Camera CreatePerspective(Vec3 eye, Vec3 forward, Vec3 up, double aspect, double verticalFovRadians, double near = 0.05)
    {
        if (aspect <= 0) throw new ArgumentOutOfRangeException(nameof(aspect));
        if (verticalFovRadians is <= 0.01 or >= 3.0) throw new ArgumentOutOfRangeException(nameof(verticalFovRadians));
        var t = Math.Tan(verticalFovRadians / 2);
        return new Camera(eye, forward, up, true, -t * aspect, t * aspect, -t, t, near);
    }

    /// <summary>Perspectiva com a janela dada em tangentes (pode ser descentrada).</summary>
    public static Camera CreatePerspectiveWindow(Vec3 eye, Vec3 forward, Vec3 up, double tanLeft, double tanRight, double tanBottom, double tanTop, double near = 0.05) =>
        new(eye, forward, up, true, tanLeft, tanRight, tanBottom, tanTop, near);

    public static Camera CreateOrthographic(Vec3 eye, Vec3 forward, Vec3 up, double aspect, double halfHeightMeters, double near = -1e9)
    {
        if (aspect <= 0) throw new ArgumentOutOfRangeException(nameof(aspect));
        if (halfHeightMeters <= 0) throw new ArgumentOutOfRangeException(nameof(halfHeightMeters));
        return new Camera(eye, forward, up, false, -halfHeightMeters * aspect, halfHeightMeters * aspect, -halfHeightMeters, halfHeightMeters, near);
    }

    /// <summary>Ortografica com a janela dada em metros, no plano da camera.</summary>
    public static Camera CreateOrthographicWindow(Vec3 eye, Vec3 forward, Vec3 up, double left, double right, double bottom, double top, double near = -1e9) =>
        new(eye, forward, up, false, left, right, bottom, top, near);

    /// <summary>Ponto do mundo para o espaco da camera: x para a direita, y para cima, z para a frente.</summary>
    public Vec3 ToCamera(Vec3 p)
    {
        var d = p - Eye;
        return new Vec3(d.Dot(Right), d.Dot(Up), d.Dot(Forward));
    }

    /// <summary>Do espaco da camera para o pixel (x para a direita, y para baixo). Devolve false se estiver atras da camera.</summary>
    public bool ToPixel(Vec3 cam, int width, int height, out double px, out double py)
    {
        px = py = 0;
        double nx, ny;
        if (Perspective)
        {
            if (cam.Z <= 1e-9) return false;
            nx = cam.X / cam.Z;
            ny = cam.Y / cam.Z;
        }
        else
        {
            nx = cam.X;
            ny = cam.Y;
        }
        px = (nx - Left) / (RightEdge - Left) * width;
        py = (Top - ny) / (Top - Bottom) * height;
        return true;
    }

    /// <summary>Distancia focal equivalente em milimetros num sensor de 36 mm de largura (null em vista ortografica).</summary>
    public double? LensMm => Perspective ? 18.0 / TanHalfH : null;
}
