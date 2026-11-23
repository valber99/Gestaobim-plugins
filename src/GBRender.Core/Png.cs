using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Gestaobim.Render.Core;

public enum PngKind { Gray8, Gray16, Rgb8 }

/// <summary>Uma imagem decodificada: pixels em linhas, de cima para baixo. Rgb8 usa 3 bytes, Gray8 1 e Gray16 2 (big-endian) por pixel.</summary>
public sealed record RawImage(int Width, int Height, PngKind Kind, byte[] Data)
{
    public int BytesPerPixel => Kind switch { PngKind.Gray8 => 1, PngKind.Gray16 => 2, _ => 3 };

    public (byte R, byte G, byte B) Rgb(int x, int y)
    {
        var i = (y * Width + x) * BytesPerPixel;
        return Kind switch
        {
            PngKind.Rgb8 => (Data[i], Data[i + 1], Data[i + 2]),
            PngKind.Gray8 => (Data[i], Data[i], Data[i]),
            _ => (Data[i], Data[i], Data[i]),
        };
    }

    public ushort Gray16(int x, int y)
    {
        var i = (y * Width + x) * BytesPerPixel;
        return Kind == PngKind.Gray16 ? (ushort)((Data[i] << 8) | Data[i + 1]) : (ushort)(Data[i] * 257);
    }
}

/// <summary>PNG minimo, sem dependencias: grava e le sem entrelacamento (8 bits RGB/cinza, 16 bits cinza; RGBA e lido e vira RGB).</summary>
public static class Png
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    public static byte[] Encode(RawImage image)
    {
        var bpp = image.BytesPerPixel;
        if (image.Data.Length != image.Width * image.Height * bpp) throw new ArgumentException("Tamanho dos pixels nao confere com a imagem.");
        var raw = new byte[image.Height * (1 + image.Width * bpp)];
        var stride = image.Width * bpp;
        for (var y = 0; y < image.Height; y++)
        {
            raw[y * (stride + 1)] = 0;   // sem filtro
            Buffer.BlockCopy(image.Data, y * stride, raw, y * (stride + 1) + 1, stride);
        }
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw, 0, raw.Length);

        using var output = new MemoryStream();
        output.Write(Signature);
        var header = new byte[13];
        WriteBe(header, 0, (uint)image.Width);
        WriteBe(header, 4, (uint)image.Height);
        header[8] = (byte)(image.Kind == PngKind.Gray16 ? 16 : 8);
        header[9] = (byte)(image.Kind == PngKind.Rgb8 ? 2 : 0);
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    public static RawImage Decode(byte[] png)
    {
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(Signature)) throw new InvalidDataException("Nao e um PNG.");
        var pos = 8;
        int width = 0, height = 0, depth = 0, colorType = -1, interlace = 0;
        using var idat = new MemoryStream();
        var ended = false;
        while (pos + 12 <= png.Length && !ended)
        {
            var length = (int)ReadBe(png, pos);
            if (length < 0 || pos + 12 + length > png.Length) throw new InvalidDataException("PNG truncado.");
            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            var data = png.AsSpan(pos + 8, length).ToArray();
            var expected = ReadBe(png, pos + 8 + length);
            if (Crc(png.AsSpan(pos + 4, 4).ToArray(), data) != expected) throw new InvalidDataException($"CRC invalido no bloco {type}.");
            switch (type)
            {
                case "IHDR":
                    width = (int)ReadBe(data, 0);
                    height = (int)ReadBe(data, 4);
                    depth = data[8];
                    colorType = data[9];
                    interlace = data[12];
                    break;
                case "IDAT":
                    idat.Write(data);
                    break;
                case "IEND":
                    ended = true;
                    break;
            }
            pos += 12 + length;
        }
        if (width <= 0 || height <= 0 || width > 32768 || height > 32768) throw new InvalidDataException("Dimensoes de PNG invalidas.");
        if (interlace != 0) throw new NotSupportedException("PNG entrelacado nao suportado.");

        var (kind, channels, bytesPerSample) = (colorType, depth) switch
        {
            (0, 8) => (PngKind.Gray8, 1, 1),
            (0, 16) => (PngKind.Gray16, 1, 2),
            (2, 8) => (PngKind.Rgb8, 3, 1),
            (6, 8) => (PngKind.Rgb8, 4, 1),
            _ => throw new NotSupportedException($"PNG tipo {colorType} com {depth} bits nao suportado."),
        };
        var stride = width * channels * bytesPerSample;
        var bpp = channels * bytesPerSample;
        idat.Position = 0;
        var raw = new byte[height * (stride + 1)];
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            var read = 0;
            while (read < raw.Length)
            {
                var n = z.Read(raw, read, raw.Length - read);
                if (n == 0) throw new InvalidDataException("Dados do PNG incompletos.");
                read += n;
            }
        }

        var pixels = new byte[height * stride];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var src = y * (stride + 1) + 1;
            var dst = y * stride;
            for (var i = 0; i < stride; i++)
            {
                int a = i >= bpp ? pixels[dst + i - bpp] : 0;
                int b = y > 0 ? pixels[dst - stride + i] : 0;
                int c = i >= bpp && y > 0 ? pixels[dst - stride + i - bpp] : 0;
                var x = raw[src + i];
                pixels[dst + i] = filter switch
                {
                    0 => x,
                    1 => (byte)(x + a),
                    2 => (byte)(x + b),
                    3 => (byte)(x + ((a + b) >> 1)),
                    4 => (byte)(x + Paeth(a, b, c)),
                    _ => throw new InvalidDataException($"Filtro de PNG desconhecido: {filter}."),
                };
            }
        }
        if (channels == 4)
        {
            var rgb = new byte[width * height * 3];
            for (var p = 0; p < width * height; p++)
            {
                rgb[p * 3] = pixels[p * 4];
                rgb[p * 3 + 1] = pixels[p * 4 + 1];
                rgb[p * 3 + 2] = pixels[p * 4 + 2];
            }
            pixels = rgb;
        }
        return new RawImage(width, height, kind, pixels);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        var length = new byte[4];
        WriteBe(length, 0, (uint)data.Length);
        output.Write(length);
        output.Write(typeBytes);
        output.Write(data);
        var crc = new byte[4];
        WriteBe(crc, 0, Crc(typeBytes, data));
        output.Write(crc);
    }

    private static void WriteBe(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static uint ReadBe(byte[] source, int offset) =>
        ((uint)source[offset] << 24) | ((uint)source[offset + 1] << 16) | ((uint)source[offset + 2] << 8) | source[offset + 3];
}
