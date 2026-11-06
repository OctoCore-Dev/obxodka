namespace obxodka.Maui.Theming.Controls;

[SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "visual particle physics and rendering")]
public sealed partial class ThemeParticlesOverlay
{
    //тип движения частиц
    private enum MovementType
    {
        Falling,
        Rising,
        Floating
    }

    //состояние отдельной частицы фона
    private sealed class Particle
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float SpeedX { get; set; }
        public float SpeedY { get; set; }
        public float Rotation { get; set; }
        public float RotationSpeed { get; set; }
        public float Size { get; set; }
        public float Depth { get; set; } = 1.0f;
        public float BaseAlpha { get; set; }
        public float CurrentAlpha { get; set; }
        public float SwayPhase { get; set; }
        public float SwaySpeed { get; set; }
        public float TwinklePhase { get; set; }
        public float TwinkleSpeed { get; set; }
        public int SpriteIndex { get; set; }
    }

    //частица интерактивного взрыва при касании
    private sealed class BurstParticle
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Vx { get; set; }
        public float Vy { get; set; }
        public float Size { get; set; }
        public float Alpha { get; set; }
        public float Life { get; set; }
        public float MaxLife { get; set; }
        public SKColor Color { get; set; }
    }

    //обновление координат курсора
    public void SetPointerPosition(float x, float y, bool active)
    {
        _pointerX = x;
        _pointerY = y;
        _pointerActive = active;
        if (active)
        {
            _lastPointerMoveTicks = Environment.TickCount64;
        }
    }

    //запуск интерактивного всплеска частиц в точке клика
    public void TriggerPointerBurst(float x, float y)
    {
        if (!_interactiveParticles)
        {
            return;
        }

        var count = Math.Min(12, 36 - _burstParticles.Count);
        for (var i = 0; i < count; i++)
        {
            var angle = (float)(_random.NextDouble() * Math.PI * 2);
            var speed = (float)(1.8 + (_random.NextDouble() * 3.6));
            var life = (float)(0.35 + (_random.NextDouble() * 0.45));
            var color = _random.Next(2) == 0 ? _accentSkColor : _primarySkColor;

            _burstParticles.Add(new BurstParticle
            {
                X = x,
                Y = y,
                Vx = MathF.Cos(angle) * speed,
                Vy = MathF.Sin(angle) * speed,
                Size = (float)(3.0 + (_random.NextDouble() * 4.5)),
                Alpha = 1.0f,
                Life = 0f,
                MaxLife = life,
                Color = color
            });
        }

        if (!IsVisible && HasActiveVisuals())
        {
            IsVisible = true;
            StartAnimation();
        }
    }

    //динамическая реакция скорости частиц на сетевой трафик vpn
    public void SetTrafficSpeedMbps(double totalMbps)
    {
        if (!_trafficReactive)
        {
            _targetTrafficMultiplier = 1.0f;
            return;
        }

        var safeMbps = Math.Max(0.0, totalMbps);
        var scaled = 1.0f + (float)(Math.Log10(1.0 + safeMbps) * 0.95);
        _targetTrafficMultiplier = Math.Clamp(scaled, 1.0f, 2.8f);
    }

    //эффект ударной волны при смене состояния соединения
    public void TriggerVpnStateEffect(AppVpnState state)
    {
        var previous = _vpnState;
        _vpnState = state;

        if (state == AppVpnState.Connected && previous != AppVpnState.Connected)
        {
            _shockwaveActive = true;
            _shockwaveProgress = 0f;

            if (!IsVisible && HasActiveVisuals())
            {
                IsVisible = true;
            }

            if (IsVisible)
            {
                StartAnimation();
            }
        }
    }

    //проверка наличия активных визуальных эффектов для анимации
    private bool HasActiveVisuals()
    {
        var activeOverlay = GetActiveOverlayName();
        var hasOverlay = !string.IsNullOrWhiteSpace(activeOverlay) &&
            !string.Equals(activeOverlay, "none", StringComparison.OrdinalIgnoreCase);
        var hasParticles = _initialized &&
            _particles.Count > 0 &&
            !string.Equals(_particleTypeName, "none", StringComparison.OrdinalIgnoreCase);

        return hasParticles || hasOverlay || _shockwaveActive || _burstParticles.Count > 0;
    }

    //получение активного имени оверлея с учетом переопределений
    private string GetActiveOverlayName() =>
        _vpnState == AppVpnState.Connected &&
        !string.IsNullOrWhiteSpace(_activeOverrideOverlay) &&
        !string.Equals(_activeOverrideOverlay, "none", StringComparison.OrdinalIgnoreCase)
            ? _activeOverrideOverlay
            : _screenOverlay;

    //очистка загруженных растровых спрайтов
    private void ClearSprites()
    {
        lock (_spriteBitmaps)
        {
            foreach (var bmp in _spriteBitmaps)
            {
                bmp.Dispose();
            }

            _spriteBitmaps.Clear();
            _bitmapPaint.ColorFilter = null;
        }
    }

    //инициализация пула частиц под выбранный тип эффекта
    private void InitParticles(int count, float width, float height)
    {
        _particles.Clear();

        int spriteCount;
        lock (_spriteBitmaps)
        {
            spriteCount = _spriteBitmaps.Count;
        }

        for (var i = 0; i < count; i++)
        {
            var baseSize = CalculateParticleBaseSize();
            var speedX = CalculateParticleSpeedX();
            var speedY = CalculateParticleSpeedY();
            var rotSpeed = CalculateParticleRotationSpeed();
            var swaySpeed = CalculateParticleSwaySpeed();
            var twinkleSpeed = CalculateParticleTwinkleSpeed();

            var depth = (float)(0.40 + (_random.NextDouble() * 1.10));
            var baseAlpha = (float)(0.30 + (_random.NextDouble() * 0.45)) * (0.65f + (depth * 0.35f));

            _particles.Add(new Particle
            {
                X = (float)(_random.NextDouble() * width),
                Y = (float)(_random.NextDouble() * height),
                SpeedX = speedX * _speedMultiplier * depth,
                SpeedY = speedY * _speedMultiplier * depth,
                Rotation = rotSpeed == 0f ? (_particleTypeName == "rain" ? 12f : 0f) : (float)(_random.NextDouble() * 360),
                RotationSpeed = rotSpeed,
                Size = baseSize * _sizeMultiplier * depth,
                Depth = depth,
                BaseAlpha = baseAlpha,
                CurrentAlpha = baseAlpha,
                SwayPhase = (float)(_random.NextDouble() * Math.PI * 2),
                SwaySpeed = swaySpeed,
                TwinklePhase = (float)(_random.NextDouble() * Math.PI * 2),
                TwinkleSpeed = twinkleSpeed,
                SpriteIndex = spriteCount > 0 ? i % spriteCount : 0
            });
        }

        _initialized = true;
    }

    //расчет базового размера частицы
    private float CalculateParticleBaseSize()
    {
        if (_customMinSize.HasValue || _customMaxSize.HasValue)
        {
            var minS = _customMinSize ?? (_movementType == MovementType.Floating ? 4f : 8f);
            var maxS = Math.Max(minS + 1f, _customMaxSize ?? (minS + 14f));
            return (float)(minS + (_random.NextDouble() * (maxS - minS)));
        }

        return _particleTypeName == "rain"
            ? (float)(12 + (_random.NextDouble() * 16))
            : _movementType switch
            {
                MovementType.Floating => (float)(4 + (_random.NextDouble() * 12)),
                MovementType.Rising => (float)(5 + (_random.NextDouble() * 14)),
                MovementType.Falling => (float)(9 + (_random.NextDouble() * 15)),
                _ => (float)(9 + (_random.NextDouble() * 15))
            };
    }

    //расчет горизонтальной скорости частицы
    private float CalculateParticleSpeedX()
    {
        return _customSpeedX.HasValue
            ? (float)(_customSpeedX.Value * (0.8 + (_random.NextDouble() * 0.4)))
            : _particleTypeName == "matrix"
            ? 0f
            : _particleTypeName == "rain"
            ? (float)(0.8 + (_random.NextDouble() * 0.8))
            : _movementType switch
            {
                MovementType.Floating => (float)((_random.NextDouble() - 0.5) * 0.3),
                MovementType.Rising => (float)((_random.NextDouble() - 0.5) * 0.8),
                MovementType.Falling => (float)(0.3 + (_random.NextDouble() * 0.9)),
                _ => (float)(0.3 + (_random.NextDouble() * 0.9))
            };
    }

    //расчет вертикальной скорости частицы
    private float CalculateParticleSpeedY()
    {
        return _customSpeedY.HasValue
            ? (float)(_customSpeedY.Value * (0.8 + (_random.NextDouble() * 0.4)))
            : _particleTypeName == "rain"
            ? (float)(4.5 + (_random.NextDouble() * 3.5))
            : _particleTypeName == "matrix"
            ? (float)(2.4 + (_random.NextDouble() * 2.8))
            : _movementType switch
            {
                MovementType.Floating => (float)((_random.NextDouble() - 0.5) * 0.3),
                MovementType.Rising => (float)(-0.8 - (_random.NextDouble() * 1.5)),
                MovementType.Falling => (float)(1.0 + (_random.NextDouble() * 1.8)),
                _ => (float)(1.0 + (_random.NextDouble() * 1.8))
            };
    }

    //расчет угловой скорости вращения частицы
    private float CalculateParticleRotationSpeed()
    {
        return _customRotationSpeed.HasValue
            ? (float)((_random.NextDouble() - 0.5) * _customRotationSpeed.Value * 2)
            : _particleTypeName is "rain" or "matrix" ? 0f : (float)((_random.NextDouble() - 0.5) * 3.0);
    }

    //расчет скорости горизонтального покачивания частицы
    private float CalculateParticleSwaySpeed()
    {
        return _customSwaySpeed.HasValue
            ? (float)(_customSwaySpeed.Value * (0.8 + (_random.NextDouble() * 0.4)))
            : _particleTypeName is "matrix" ? 0f : _particleTypeName is "rain" ? 0.01f : (float)(0.02 + (_random.NextDouble() * 0.04));
    }

    //расчет скорости мерцания частицы
    private float CalculateParticleTwinkleSpeed()
    {
        return _customTwinkleSpeed.HasValue
            ? (float)(_customTwinkleSpeed.Value * (0.8 + (_random.NextDouble() * 0.4)))
            : (float)(0.03 + (_random.NextDouble() * 0.06));
    }

    //физическое обновление координат частиц импульсов и волн
    private void UpdatePhysics(float width, float height, float dt)
    {
        var w = width > 1f ? width : (_lastWidth > 1f ? _lastWidth : 800f);
        var h = height > 1f ? height : (_lastHeight > 1f ? _lastHeight : 600f);
        var step = dt * 36f;
        _shaderTime += dt;

        for (var i = _burstParticles.Count - 1; i >= 0; i--)
        {
            var bp = _burstParticles[i];
            bp.Life += dt;
            if (bp.Life >= bp.MaxLife)
            {
                _burstParticles.RemoveAt(i);
                continue;
            }

            var progress = bp.Life / bp.MaxLife;
            bp.Alpha = 1.0f - (progress * progress);
            bp.X += bp.Vx * step;
            bp.Y += bp.Vy * step;
            bp.Vx *= 0.94f;
            bp.Vy = (bp.Vy * 0.94f) + (0.06f * step);
        }

        if (_shockwaveActive)
        {
            _shockwaveProgress += dt * 1.15f;
            if (_shockwaveProgress >= 1.0f)
            {
                _shockwaveActive = false;
                _shockwaveProgress = 0f;
            }
        }

        if (_pointerActive && Environment.TickCount64 - _lastPointerMoveTicks > 2500)
        {
            _pointerActive = false;
        }

        var targetMult = _trafficReactive ? _targetTrafficMultiplier : 1.0f;
        if (_vpnState == AppVpnState.Connected)
        {
            targetMult = Math.Max(targetMult, _activeSpeedOverride);
        }

        _currentTrafficMultiplier += (targetMult - _currentTrafficMultiplier) * Math.Clamp(dt * 4.0f, 0.01f, 0.35f);
        if (!_initialized || _particles.Count == 0)
        {
            return;
        }

        var speedScale = _currentTrafficMultiplier * step;
        var centerX = w * 0.5f;
        var centerY = h * 0.5f;

        foreach (var p in _particles)
        {
            p.SwayPhase += p.SwaySpeed * step;
            var sway = p.SwaySpeed <= 0f
                ? 0f
                : (float)Math.Sin(p.SwayPhase) * step;

            p.TwinklePhase += p.TwinkleSpeed * step;
            var twinkleAmp = p.TwinkleSpeed > 0.05f ? 0.38f : 0.25f;
            var twinkle = (float)Math.Sin(p.TwinklePhase) * twinkleAmp;
            p.CurrentAlpha = Math.Clamp(p.BaseAlpha + twinkle, 0.08f, 1.0f);

            p.X += (p.SpeedX * speedScale) + sway;
            p.Y += p.SpeedY * speedScale;
            p.Rotation += p.RotationSpeed * step;

            if (_vpnState == AppVpnState.Connecting)
            {
                var cdx = centerX - p.X;
                var cdy = centerY - p.Y;
                var cDist = MathF.Sqrt((cdx * cdx) + (cdy * cdy));
                if (cDist > 18f && cDist < Math.Max(w, h) * 0.65f)
                {
                    var pull = (1.0f - (cDist / (Math.Max(w, h) * 0.65f))) * 1.35f * step;
                    var nx = cdx / cDist;
                    var ny = cdy / cDist;
                    p.X += (nx * pull) - (ny * pull * 0.6f);
                    p.Y += (ny * pull) + (nx * pull * 0.6f);
                }
            }

            if (_shockwaveActive)
            {
                var maxR = MathF.Sqrt((w * w) + (h * h)) * 0.65f;
                var easedR = 1f - MathF.Pow(1f - _shockwaveProgress, 3f);
                var waveR = 24f + (maxR * easedR);
                var cdx = p.X - centerX;
                var cdy = p.Y - centerY;
                var cDist = MathF.Sqrt((cdx * cdx) + (cdy * cdy));
                var delta = MathF.Abs(cDist - waveR);

                if (delta < 42f && cDist > 10f)
                {
                    var pushWave = (1f - (delta / 42f)) * 14f * (1f - _shockwaveProgress) * p.Depth;
                    var nx = cdx / cDist;
                    var ny = cdy / cDist;
                    p.X += nx * pushWave;
                    p.Y += ny * pushWave;
                    p.CurrentAlpha = Math.Min(1.0f, p.CurrentAlpha + 0.45f);
                }
            }

            if (_interactiveParticles && _pointerActive)
            {
                var dx = p.X - _pointerX;
                var dy = p.Y - _pointerY;
                var distSq = (dx * dx) + (dy * dy);
                const float repulsionRadius = 150f;

                if (distSq is > 0.01f and < repulsionRadius * repulsionRadius)
                {
                    var dist = MathF.Sqrt(distSq);
                    var factor = 1.0f - (dist / repulsionRadius);
                    var push = factor * factor * 5.2f * step * p.Depth;
                    var nx = dx / dist;
                    var ny = dy / dist;
                    p.X += (nx * push) - (ny * push * 0.45f * step);
                    p.Y += (ny * push) + (nx * push * 0.45f * step);
                    p.Rotation += factor * 3.5f * step;
                }
            }

            switch (_movementType)
            {
                case MovementType.Falling:
                    if (p.Y > h + 30 || p.X > w + 50 || p.X < -50)
                    {
                        p.Y = -25;
                        p.X = (float)((_random.NextDouble() * (w + 100)) - 50);
                        if (p.RotationSpeed != 0f)
                        {
                            p.Rotation = (float)(_random.NextDouble() * 360);
                        }
                    }

                    break;

                case MovementType.Rising:
                    if (p.Y < -30 || p.X > w + 30 || p.X < -30)
                    {
                        p.Y = h + 25;
                        p.X = (float)(_random.NextDouble() * w);
                        p.Rotation = (float)(_random.NextDouble() * 360);
                    }

                    break;

                case MovementType.Floating:
                    if (p.X > w + 30)
                    {
                        p.X = -20;
                    }

                    if (p.X < -30)
                    {
                        p.X = w + 20;
                    }

                    if (p.Y > h + 30)
                    {
                        p.Y = -20;
                    }

                    if (p.Y < -30)
                    {
                        p.Y = h + 20;
                    }

                    break;

                default:
                    break;
            }
        }
    }
}
