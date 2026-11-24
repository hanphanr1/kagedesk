using KageDesk.Rendering.Canvas;

namespace KageDesk.Rendering.Sprites;

public enum ParticleKind : byte
{
    Spark,
    Confetti,
    Fire,
    Smoke,
    Poof,
    Steam,
    Rain,
    Bubble
}

public struct Particle
{
    public bool Active;
    public ParticleKind Kind;
    public float X;
    public float Y;
    public float Vx;
    public float Vy;
    public float Age;
    public float MaxAge;
    public int Color;
    public byte Size;
}

public sealed class ParticleSystem
{
    public const int MaxParticles = 128;

    private readonly Particle[] _pool = new Particle[MaxParticles];
    private int _cursor;
    private uint _rngState = 0x5EED1234u;

    private static readonly int[] ConfettiColors =
    {
        ColorUtil.FromRgb(250, 204, 21),
        ColorUtil.FromRgb(52, 211, 153),
        ColorUtil.FromRgb(56, 189, 248),
        ColorUtil.FromRgb(244, 114, 182),
        ColorUtil.FromRgb(251, 146, 60),
        ColorUtil.FromRgb(167, 139, 250)
    };

    public int ActiveCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < MaxParticles; i++)
            {
                if (_pool[i].Active) count++;
            }
            return count;
        }
    }

    public void Reset(uint seed = 0x5EED1234u)
    {
        Array.Clear(_pool, 0, MaxParticles);
        _cursor = 0;
        _rngState = seed == 0 ? 0x5EED1234u : seed;
    }

    public void Emit(
        ParticleKind kind,
        float x,
        float y,
        float vx,
        float vy,
        float maxAge,
        int color,
        byte size = 1)
    {
        int slot = -1;
        for (int i = 0; i < MaxParticles; i++)
        {
            int idx = (_cursor + i) & (MaxParticles - 1);
            if (!_pool[idx].Active)
            {
                slot = idx;
                _cursor = (idx + 1) & (MaxParticles - 1);
                break;
            }
        }

        if (slot < 0)
        {
            slot = _cursor;
            _cursor = (_cursor + 1) & (MaxParticles - 1);
        }

        _pool[slot] = new Particle
        {
            Active = true,
            Kind = kind,
            X = x,
            Y = y,
            Vx = vx,
            Vy = vy,
            Age = 0f,
            MaxAge = maxAge > 0.05f ? maxAge : 0.05f,
            Color = color,
            Size = size
        };
    }

    public void EmitSparks(float x, float y, int count = 3)
    {
        for (int i = 0; i < count; i++)
        {
            float vx = (NextFloat() - 0.5f) * 34f;
            float vy = -12f - NextFloat() * 20f;
            int color = (i & 1) == 0
                ? ColorUtil.FromRgb(254, 240, 138)
                : ColorUtil.FromRgb(56, 189, 248);
            Emit(ParticleKind.Spark, x + (NextFloat() - 0.5f) * 8f, y, vx, vy, 0.35f + NextFloat() * 0.25f, color, 1);
        }
    }

    public void EmitConfetti(float x, float y, int count = 6)
    {
        for (int i = 0; i < count; i++)
        {
            float vx = (NextFloat() - 0.5f) * 44f;
            float vy = -24f - NextFloat() * 24f;
            int color = ConfettiColors[NextInt(ConfettiColors.Length)];
            byte sz = (byte)(NextInt(2) + 1);
            Emit(ParticleKind.Confetti, x + (NextFloat() - 0.5f) * 14f, y, vx, vy, 0.9f + NextFloat() * 0.6f, color, sz);
        }
    }

    public void EmitFireAndSmoke(float x, float y)
    {
        int fireColor = NextInt(3) switch
        {
            0 => ColorUtil.FromRgb(254, 240, 138),
            1 => ColorUtil.FromRgb(249, 115, 22),
            _ => ColorUtil.FromRgb(239, 68, 68)
        };
        Emit(
            ParticleKind.Fire,
            x + (NextFloat() - 0.5f) * 7f,
            y,
            (NextFloat() - 0.5f) * 8f,
            -14f - NextFloat() * 14f,
            0.35f + NextFloat() * 0.25f,
            fireColor,
            (byte)(NextInt(2) + 1));

        if (NextFloat() < 0.6f)
        {
            int smokeColor = ColorUtil.FromRgb(100, 116, 139);
            Emit(
                ParticleKind.Smoke,
                x + (NextFloat() - 0.5f) * 6f,
                y - 4f,
                (NextFloat() - 0.5f) * 6f,
                -10f - NextFloat() * 8f,
                0.75f + NextFloat() * 0.45f,
                smokeColor,
                2);
        }
    }

    public void EmitNinjaPoof(float x, float y, int count = 12)
    {
        for (int i = 0; i < count; i++)
        {
            float angle = (i / (float)count) * 6.2831853f + NextFloat() * 0.4f;
            float speed = 10f + NextFloat() * 20f;
            int color = (i & 1) == 0
                ? ColorUtil.FromRgb(241, 245, 249)
                : ColorUtil.FromRgb(203, 213, 225);
            Emit(
                ParticleKind.Poof,
                x + MathF.Cos(angle) * 4f,
                y + MathF.Sin(angle) * 3f,
                MathF.Cos(angle) * speed,
                MathF.Sin(angle) * speed - 6f,
                0.55f + NextFloat() * 0.35f,
                color,
                (byte)(NextInt(2) + 2));
        }
    }

    public void EmitCoffeeSteam(float x, float y)
    {
        Emit(
            ParticleKind.Steam,
            x + (NextFloat() - 0.5f) * 2f,
            y,
            (NextFloat() - 0.5f) * 3f,
            -7f - NextFloat() * 5f,
            0.8f + NextFloat() * 0.4f,
            ColorUtil.FromRgb(226, 232, 240),
            1);
    }

    public void EmitChakraBubble(float x, float y)
    {
        int color = NextInt(2) == 0
            ? ColorUtil.FromRgb(134, 239, 172)
            : ColorUtil.FromRgb(34, 197, 94);
        Emit(
            ParticleKind.Bubble,
            x + (NextFloat() - 0.5f) * 4f,
            y,
            (NextFloat() - 0.5f) * 4f,
            -9f - NextFloat() * 7f,
            0.55f + NextFloat() * 0.35f,
            color,
            1);
    }

    public void EmitRain(float x, float y, float vx = -18f, float vy = 68f)
    {
        Emit(
            ParticleKind.Rain,
            x,
            y,
            vx,
            vy,
            0.45f,
            ColorUtil.FromRgb(147, 197, 253),
            1);
    }

    public void Update(float dtSeconds)
    {
        if (dtSeconds <= 0f) return;
        if (dtSeconds > 0.25f) dtSeconds = 0.25f;

        for (int i = 0; i < MaxParticles; i++)
        {
            ref Particle p = ref _pool[i];
            if (!p.Active) continue;

            p.Age += dtSeconds;
            if (p.Age >= p.MaxAge)
            {
                p.Active = false;
                continue;
            }

            switch (p.Kind)
            {
                case ParticleKind.Confetti:
                    p.Vy += 38f * dtSeconds;
                    p.Vx *= (1f - 1.2f * dtSeconds);
                    break;
                case ParticleKind.Spark:
                    p.Vy += 42f * dtSeconds;
                    break;
                case ParticleKind.Poof:
                    p.Vx *= (1f - 2.6f * dtSeconds);
                    p.Vy *= (1f - 2.2f * dtSeconds);
                    break;
                case ParticleKind.Smoke:
                case ParticleKind.Steam:
                    p.Vx += MathF.Sin(p.Age * 9f + i) * 6f * dtSeconds;
                    break;
                case ParticleKind.Bubble:
                    p.Vx += MathF.Sin(p.Age * 12f + i) * 8f * dtSeconds;
                    break;
            }

            p.X += p.Vx * dtSeconds;
            p.Y += p.Vy * dtSeconds;
        }
    }

    public void Render(PixelBuffer buf)
    {
        for (int i = 0; i < MaxParticles; i++)
        {
            ref readonly Particle p = ref _pool[i];
            if (!p.Active) continue;

            float life = 1f - (p.Age / p.MaxAge);
            if (life <= 0f) continue;

            int baseAlpha = p.Kind switch
            {
                ParticleKind.Steam => 135,
                ParticleKind.Smoke => 155,
                ParticleKind.Poof => 210,
                ParticleKind.Rain => 150,
                _ => 245
            };

            int alpha = (int)(baseAlpha * life);
            if (alpha <= 8) continue;

            int px = (int)p.X;
            int py = (int)p.Y;

            if (p.Kind == ParticleKind.Rain)
            {
                int col = ColorUtil.WithAlpha(p.Color, alpha);
                buf.BlendPixel(px, py, col);
                buf.BlendPixel(px - 1, py + 2, col);
            }
            else if (p.Kind == ParticleKind.Poof)
            {
                int r = p.Size;
                buf.DrawCircle(px, py, r, ColorUtil.WithAlpha(p.Color, alpha), filled: true);
            }
            else
            {
                buf.BlendRect(px, py, p.Size, p.Size, p.Color, alpha);
            }
        }
    }

    private uint NextUInt()
    {
        uint x = _rngState;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _rngState = x;
        return x;
    }

    private float NextFloat() => (NextUInt() & 0x00FFFFFFu) / 16777216f;
    private int NextInt(int maxExclusive) => (int)(NextUInt() % (uint)maxExclusive);
}
