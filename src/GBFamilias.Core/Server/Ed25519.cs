using System;
using System.Numerics;
using System.Security.Cryptography;

namespace Gestaobim.Familias.Core.Server;

/// <summary>
/// Ed25519 (RFC 8032) em codigo gerenciado: o .NET nao traz a assinatura. Usado so para conferir o manifesto do
/// catalogo (uma verificacao por sincronizacao), entao a clareza vale mais que a velocidade. <see cref="Sign"/>
/// existe para os testes e para ferramentas do administrador.
/// </summary>
public static class Ed25519
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Mod(-121665 * ModInverse(121666));
    private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);
    private static readonly (BigInteger X, BigInteger Y) B = MakeBase();

    private static BigInteger Mod(BigInteger a)
    {
        var r = a % P;
        return r.Sign < 0 ? r + P : r;
    }

    private static BigInteger ModInverse(BigInteger a) => BigInteger.ModPow(a < 0 ? a + P : a, P - 2, P);

    private static (BigInteger, BigInteger) MakeBase()
    {
        var y = Mod(4 * ModInverse(5));
        var x = RecoverX(y, 0) ?? throw new InvalidOperationException("ponto base invalido");
        return (x, y);
    }

    private static BigInteger? RecoverX(BigInteger y, int sign)
    {
        if (y >= P) return null;
        var y2 = Mod(y * y);
        var x2 = Mod((y2 - 1) * ModInverse(D * y2 + 1));
        if (x2.IsZero) return sign == 1 ? null : BigInteger.Zero;
        var x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (Mod(x * x - x2) != 0) x = Mod(x * SqrtM1);
        if (Mod(x * x - x2) != 0) return null;
        if ((int)(x & 1) != sign) x = P - x;
        return x;
    }

    private static (BigInteger X, BigInteger Y) Add((BigInteger X, BigInteger Y) p, (BigInteger X, BigInteger Y) q)
    {
        var dxy = Mod(D * p.X * q.X * p.Y * q.Y);
        var x = Mod((p.X * q.Y + q.X * p.Y) * ModInverse(1 + dxy));
        var y = Mod((p.Y * q.Y + p.X * q.X) * ModInverse(1 - dxy));
        return (x, y);
    }

    private static (BigInteger X, BigInteger Y) Multiply(BigInteger scalar, (BigInteger X, BigInteger Y) point)
    {
        var result = (X: BigInteger.Zero, Y: BigInteger.One);
        var addend = point;
        while (!scalar.IsZero)
        {
            if (!scalar.IsEven) result = Add(result, addend);
            addend = Add(addend, addend);
            scalar >>= 1;
        }
        return result;
    }

    private static byte[] Encode((BigInteger X, BigInteger Y) p)
    {
        var bytes = new byte[32];
        var y = p.Y.ToByteArray(isUnsigned: true, isBigEndian: false);
        Array.Copy(y, bytes, Math.Min(32, y.Length));
        if (!p.X.IsEven) bytes[31] |= 0x80;
        return bytes;
    }

    private static (BigInteger X, BigInteger Y)? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length != 32) return null;
        var copy = data.ToArray();
        var sign = (copy[31] >> 7) & 1;
        copy[31] &= 0x7F;
        var y = new BigInteger(copy, isUnsigned: true, isBigEndian: false);
        var x = RecoverX(y, sign);
        return x is null ? null : (x.Value, y);
    }

    private static BigInteger HashToScalar(params byte[][] parts)
    {
        using var sha = SHA512.Create();
        foreach (var part in parts) sha.TransformBlock(part, 0, part.Length, null, 0);
        sha.TransformFinalBlock([], 0, 0);
        return new BigInteger(sha.Hash!, isUnsigned: true, isBigEndian: false) % L;
    }

    private static BigInteger Scalar(byte[] a) => new(a, isUnsigned: true, isBigEndian: false);

    private static byte[] Le32(BigInteger value)
    {
        var raw = value.ToByteArray(isUnsigned: true, isBigEndian: false);
        var bytes = new byte[32];
        Array.Copy(raw, bytes, Math.Min(32, raw.Length));
        return bytes;
    }

    private static (BigInteger A, byte[] Prefix) Expand(byte[] seed)
    {
        var h = SHA512.HashData(seed);
        var a = new byte[32];
        Array.Copy(h, a, 32);
        a[0] &= 248;
        a[31] &= 127;
        a[31] |= 64;
        var prefix = new byte[32];
        Array.Copy(h, 32, prefix, 0, 32);
        return (Scalar(a), prefix);
    }

    public static byte[] PublicKeyFromSeed(byte[] seed)
    {
        if (seed.Length != 32) throw new ArgumentException("a semente deve ter 32 bytes");
        return Encode(Multiply(Expand(seed).A, B));
    }

    public static byte[] Sign(byte[] seed, byte[] message)
    {
        if (seed.Length != 32) throw new ArgumentException("a semente deve ter 32 bytes");
        var (a, prefix) = Expand(seed);
        var publicKey = Encode(Multiply(a, B));
        var r = HashToScalar(prefix, message);
        var rEncoded = Encode(Multiply(r, B));
        var h = HashToScalar(rEncoded, publicKey, message);
        var s = (r + h * a) % L;
        var signature = new byte[64];
        Array.Copy(rEncoded, signature, 32);
        Array.Copy(Le32(s), 0, signature, 32, 32);
        return signature;
    }

    /// <summary>Confere a assinatura. Nunca lanca: qualquer entrada ruim vira false.</summary>
    public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        try
        {
            if (publicKey.Length != 32 || signature.Length != 64) return false;
            var a = Decode(publicKey);
            var r = Decode(signature.AsSpan(0, 32));
            if (a is null || r is null) return false;
            var s = Scalar(signature.AsSpan(32, 32).ToArray());
            if (s >= L) return false;
            var h = HashToScalar(signature.AsSpan(0, 32).ToArray(), publicKey, message);
            var left = Multiply(s, B);
            var right = Add(r.Value, Multiply(h, a.Value));
            return left.X == right.X && left.Y == right.Y;
        }
        catch (Exception error) when (error is ArgumentException or OverflowException or InvalidOperationException)
        {
            return false;
        }
    }
}
