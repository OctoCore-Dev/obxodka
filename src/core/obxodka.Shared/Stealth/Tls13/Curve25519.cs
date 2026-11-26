namespace obxodka.Shared.Stealth.Tls13;

//реализация эллиптической кривой curve25519 (rfc 7748 x25519) в постоянном времени без внешних зависимостей и аллокаций в куче
public static class Curve25519
{
    public const int KeySize = 32;

    //базовая точка u = 9 кривой curve25519
    private static ReadOnlySpan<byte> BasePoint =>
    [
        9, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0
    ];

    //константа 121665 = (486662 - 2) / 4 в представлении 16-битных конечностей
    private static ReadOnlySpan<long> Constant121665 =>
    [
        0xDB41, 1, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0
    ];

    //генерация ключевой пары x25519 (приватный скаляр 32б + публичный ключ 32б)
    public static void GenerateKeyPair(Span<byte> privateKey, Span<byte> publicKey)
    {
        if (privateKey.Length < KeySize || publicKey.Length < KeySize)
        {
            throw new ArgumentException("X25519 key buffer must be at least 32 bytes.");
        }

        RandomNumberGenerator.Fill(privateKey[..KeySize]);
        ClampScalar(privateKey);
        GetPublicKey(privateKey[..KeySize], publicKey[..KeySize]);
    }

    //вычисление публичного ключа из 32-байтного приватного скаляра
    public static void GetPublicKey(ReadOnlySpan<byte> privateKey, Span<byte> publicKey) =>
        ScalarMult(publicKey, privateKey, BasePoint);

    //вычисление общего секрета ecdh x25519 (возвращает false при низкопорядковой нулевой точке)
    public static bool ComputeSharedSecret(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> peerPublicKey, Span<byte> sharedSecret)
    {
        ScalarMult(sharedSecret, privateKey, peerPublicKey);
        var acc = 0;
        for (var i = 0; i < KeySize; i++)
        {
            acc |= sharedSecret[i];
        }

        return acc != 0;
    }

    //клемпинг скаляра согласно rfc 7748 раздел 5
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ClampScalar(Span<byte> scalar)
    {
        scalar[0] &= 248;
        scalar[31] &= 127;
        scalar[31] |= 64;
    }

    //лестница монтгомери x25519 в 16 конечностях int64 (исключает переполнение разрядной сетки)
    public static void ScalarMult(Span<byte> q, ReadOnlySpan<byte> n, ReadOnlySpan<byte> p)
    {
        if (q.Length < KeySize || n.Length < KeySize || p.Length < KeySize)
        {
            throw new ArgumentException("Buffers for X25519 ScalarMult must be 32 bytes.");
        }

        Span<byte> z = stackalloc byte[32];
        n[..32].CopyTo(z);
        ClampScalar(z);

        //очищаем старший бит координаты u согласно rfc 7748
        Span<byte> uBytes = stackalloc byte[32];
        p[..32].CopyTo(uBytes);
        uBytes[31] &= 0x7F;

        Span<long> x = stackalloc long[16];
        Span<long> a = stackalloc long[16];
        Span<long> b = stackalloc long[16];
        Span<long> c = stackalloc long[16];
        Span<long> d = stackalloc long[16];
        Span<long> e = stackalloc long[16];
        Span<long> f = stackalloc long[16];
        Span<long> c121665 = stackalloc long[16];
        Constant121665.CopyTo(c121665);

        Unpack25519(x, uBytes);
        a.Clear();
        b.Clear();
        c.Clear();
        d.Clear();
        a[0] = 1;
        d[0] = 1;
        x.CopyTo(b);

        for (var i = 254; i >= 0; --i)
        {
            var r = (z[i >> 3] >> (i & 7)) & 1;
            Swap25519(a, b, r);
            Swap25519(c, d, r);

            Add(e, a, c);
            Sub(a, a, c);
            Add(c, b, d);
            Sub(b, b, d);

            Square(d, e);
            Square(f, a);
            Mul(a, c, a);
            Mul(c, b, e);

            Add(e, a, c);
            Sub(a, a, c);
            Square(b, a);
            Sub(c, d, f);

            Mul(a, c, c121665);
            Add(a, a, d);
            Mul(c, c, a);
            Mul(a, d, f);
            Mul(d, b, x);
            Square(b, e);

            Swap25519(a, b, r);
            Swap25519(c, d, r);
        }

        Invert(c, c);
        Mul(a, a, c);
        Pack25519(q, a);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Unpack25519(Span<long> o, ReadOnlySpan<byte> n)
    {
        for (var i = 0; i < 16; i++)
        {
            o[i] = n[2 * i] + ((long)n[2 * i + 1] << 8);
        }

        o[15] &= 0x7FFF;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Carry25519(Span<long> o)
    {
        for (var i = 0; i < 16; i++)
        {
            var c = o[i] >> 16;
            o[i] -= c << 16;
            if (i < 15)
            {
                o[i + 1] += c;
            }
            else
            {
                o[0] += 38 * c;
            }
        }
    }

    private static void Pack25519(Span<byte> o, ReadOnlySpan<long> n)
    {
        Span<long> m = stackalloc long[16];
        Span<long> t = stackalloc long[16];
        n.CopyTo(t);
        Carry25519(t);
        Carry25519(t);
        Carry25519(t);

        for (var j = 0; j < 2; j++)
        {
            m[0] = t[0] - 0xFFED;
            for (var i = 1; i < 15; i++)
            {
                m[i] = t[i] - 0xFFFF - ((m[i - 1] >> 16) & 1);
                m[i - 1] &= 0xFFFF;
            }

            m[15] = t[15] - 0x7FFF - ((m[14] >> 16) & 1);
            var b = (int)((m[15] >> 16) & 1);
            m[14] &= 0xFFFF;
            Swap25519(t, m, 1 - b);
        }

        for (var i = 0; i < 16; i++)
        {
            o[2 * i] = (byte)(t[i] & 0xFF);
            o[2 * i + 1] = (byte)(t[i] >> 8);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Swap25519(Span<long> p, Span<long> q, int b)
    {
        var c = ~(long)(b - 1);
        for (var i = 0; i < 16; i++)
        {
            var t = c & (p[i] ^ q[i]);
            p[i] ^= t;
            q[i] ^= t;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Add(Span<long> o, ReadOnlySpan<long> a, ReadOnlySpan<long> b)
    {
        for (var i = 0; i < 16; i++)
        {
            o[i] = a[i] + b[i];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Sub(Span<long> o, ReadOnlySpan<long> a, ReadOnlySpan<long> b)
    {
        for (var i = 0; i < 16; i++)
        {
            o[i] = a[i] - b[i];
        }
    }

    private static void Mul(Span<long> o, ReadOnlySpan<long> a, ReadOnlySpan<long> b)
    {
        Span<long> t = stackalloc long[31];
        t.Clear();

        for (var i = 0; i < 16; i++)
        {
            var ai = a[i];
            for (var j = 0; j < 16; j++)
            {
                t[i + j] += ai * b[j];
            }
        }

        for (var i = 0; i < 15; i++)
        {
            t[i] += 38 * t[i + 16];
        }

        for (var i = 0; i < 16; i++)
        {
            o[i] = t[i];
        }

        Carry25519(o);
        Carry25519(o);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Square(Span<long> o, ReadOnlySpan<long> a) => Mul(o, a, a);

    private static void Invert(Span<long> o, ReadOnlySpan<long> input)
    {
        Span<long> c = stackalloc long[16];
        input.CopyTo(c);
        for (var a = 253; a >= 0; a--)
        {
            Square(c, c);
            if (a is not (2 or 4))
            {
                Mul(c, c, input);
            }
        }

        c.CopyTo(o);
    }
}
