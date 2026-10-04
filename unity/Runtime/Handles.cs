// ピンのハンドル。Dispose でデーモン側の確保も解放する。
using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace RasPiLib.UnityGpio
{
    public abstract class GpioHandle : IDisposable
    {
        public int Pin { get; }
        /// <summary>デーモンが無い・確保に失敗した。以後の操作は無視される。</summary>
        public bool Dead { get; internal set; }
        public bool IsDisposed { get; private set; }

        protected GpioHandle(int pin) { Pin = pin; }

        protected bool Live => !Dead && !IsDisposed;

        public virtual void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            if (!Dead) Gpio.Release(Pin);
        }
    }

    public sealed class DigitalInput : GpioHandle
    {
        /// <summary>保持している最新値（true = High）。デーモンからの edge で更新される。</summary>
        public bool Value { get; private set; }
        /// <summary>値が変わった。メインスレッド (Update の前) で発火。</summary>
        public event Action<bool> Changed;

        internal DigitalInput(int pin) : base(pin) { }

        internal void Set(bool v, bool raise)
        {
            Value = v;
            if (raise) Changed?.Invoke(v);
        }
    }

    public sealed class DigitalOutput : GpioHandle
    {
        bool _value;
        /// <summary>書き込みは送るだけ（ブロックしない）。</summary>
        public bool Value
        {
            get => _value;
            set { _value = value; if (Live) Gpio.Send(Json.Obj(("op", "write"), ("pin", Pin), ("value", value ? 1 : 0))); }
        }

        internal DigitalOutput(int pin, bool initial) : base(pin) { _value = initial; }

        public override void Dispose() { if (Live) Value = false; base.Dispose(); }
    }

    /// <summary>ハードウェア PWM (GPIO12 / GPIO13)。周波数とデューティ比で指定する。</summary>
    public sealed class PwmOutput : GpioHandle
    {
        float _frequencyHz, _duty;
        bool _enabled = true;

        public float FrequencyHz { get => _frequencyHz; set { _frequencyHz = Mathf.Max(value, 0.01f); Push(); } }
        /// <summary>0〜1。</summary>
        public float Duty { get => _duty; set { _duty = Mathf.Clamp01(value); Push(); } }
        public bool Enabled { get => _enabled; set { _enabled = value; Push(); } }

        internal PwmOutput(int pin, float frequencyHz) : base(pin) { _frequencyHz = Mathf.Max(frequencyHz, 0.01f); }

        /// <summary>パルス幅 (µs) で指定する。サーボ用。</summary>
        public void SetPulseWidthUs(float us) => Duty = us * 1e-6f * _frequencyHz;

        internal void Push()
        {
            if (!Live) return;
            long period = (long)(1e9 / _frequencyHz);
            Gpio.Send(Json.Obj(("op", "pwm_set"), ("pin", Pin), ("period_ns", period), ("duty_ns", (long)(period * _duty)), ("enabled", _enabled)));
        }
    }

    /// <summary>角度 → パルス幅の変換。個体差は min/max/maxAngle で補正する。</summary>
    public sealed class Servo : IDisposable
    {
        public PwmOutput Pwm { get; }
        public float MinPulseUs { get; set; }
        public float MaxPulseUs { get; set; }
        public float MaxAngle { get; set; }
        float _angle;

        public Servo(PwmOutput pwm, float minPulseUs = 500f, float maxPulseUs = 2500f, float maxAngle = 180f)
        {
            Pwm = pwm; MinPulseUs = minPulseUs; MaxPulseUs = maxPulseUs; MaxAngle = maxAngle;
        }

        public float Angle
        {
            get => _angle;
            set { _angle = Mathf.Clamp(value, 0f, MaxAngle); Pwm.SetPulseWidthUs(Mathf.Lerp(MinPulseUs, MaxPulseUs, _angle / MaxAngle)); }
        }

        public void Dispose() => Pwm.Dispose();
    }

    /// <summary>3 ピンの RGB LED。PWM ピンに当たったチャンネルは明るさ可変、ほかは 0.5 を閾値にオン/オフ。</summary>
    public sealed class RgbLed : IDisposable
    {
        readonly GpioHandle[] _ch = new GpioHandle[3];
        readonly bool _activeLow;
        Color _color;

        internal RgbLed(int r, int g, int b, bool activeLow)
        {
            _activeLow = activeLow;
            int[] pins = { r, g, b };
            bool available = Gpio.IsAvailable;
            for (int i = 0; i < 3; i++)
                _ch[i] = available && Gpio.PwmPins.Contains(pins[i]) ? Gpio.OpenPwm(pins[i], 1000f) : Gpio.OpenOutput(pins[i], activeLow);
            Color = Color.black;
        }

        public Color Color
        {
            get => _color;
            set
            {
                _color = value;
                float[] level = { value.r, value.g, value.b };
                for (int i = 0; i < 3; i++)
                {
                    float v = Mathf.Clamp01(level[i]);
                    if (_ch[i] is PwmOutput pwm) pwm.Duty = _activeLow ? 1f - v : v;
                    else ((DigitalOutput)_ch[i]).Value = (v > 0.5f) != _activeLow;
                }
            }
        }

        public void Dispose() { foreach (var c in _ch) c.Dispose(); }
    }

    /// <summary>I2C デバイス 1 つ（バスとアドレス）。書いて続けて読む (リピーテッドスタート)。失敗は IOException。</summary>
    public sealed class I2cDevice
    {
        public int Bus { get; }
        public int Address { get; }

        internal I2cDevice(int bus, int address) { Bus = bus; Address = address; }

        public Awaitable<byte[]> WriteReadAsync(byte[] write, int readLength)
        {
            var src = new AwaitableCompletionSource<byte[]>();
            if (!Gpio.IsAvailable)
            {
                Gpio.Warn($"I2C 0x{Address:x2}: デーモンに接続できないため空の応答を返す");
                src.SetResult(Array.Empty<byte>());
                return src.Awaitable;
            }
            Gpio.Request(m =>
                {
                    if (m.ok) src.SetResult(Hex.Parse(m.data));
                    else src.SetException(new IOException($"I2C{Bus} 0x{Address:x2}: {m.code} {m.message}"));
                },
                ("op", "i2c"), ("bus", Bus), ("addr", Address), ("write", Hex.Format(write)), ("read", readLength));
            return src.Awaitable;
        }

        public Awaitable<byte[]> ReadAsync(int length) => WriteReadAsync(null, length);
        public Awaitable<byte[]> WriteAsync(byte[] data) => WriteReadAsync(data, 0);
    }
}
