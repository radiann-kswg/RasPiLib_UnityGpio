// 公開 API の入口。接続の管理と、デーモンからのイベントのメインスレッドへの配送。
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("RasPiLib.UnityGpio.Tests")]

namespace RasPiLib.UnityGpio
{
    public enum Bias { None, PullUp, PullDown }
    public enum Edge { None, Rising, Falling, Both }

    public static class Gpio
    {
        /// <summary>GPIO が使えるか。初回アクセスでデーモンへ接続を試みる。無くてもゲームは動くこと。</summary>
        public static bool IsAvailable { get { EnsureConnected(); return _transport != null; } }
        /// <summary>"pi4" / "pi5" / "mock"。未接続なら null。</summary>
        public static string Board { get; private set; }
        /// <summary>今使えるハードウェア PWM ピン（有効化されていなければ空）。</summary>
        public static IReadOnlyCollection<int> PwmPins => _pwmPins;
        public static IReadOnlyCollection<int> I2cBuses => _i2cBuses;
        public static IReadOnlyCollection<int> ReservedPins => _reserved;

        /// <summary>送るだけの操作の失敗（デーモンから非同期に届く）。メインスレッドで発火。</summary>
        public static event Action<string, int, string> Error;   // (code, pin or -1, message)
        /// <summary>デーモンとの接続が切れた。v1 は自動再接続しない。</summary>
        public static event Action Disconnected;

        /// <summary>Linux プレイヤーでの接続先。環境変数 UNITYGPIO_SOCKET / UNITYGPIO_TCP (host:port) で上書きできる。</summary>
        public const string DefaultSocketPath = "/run/unitygpio/unitygpio.sock";
        const int Proto = 1;

        static ITransport _transport;
        static bool _triedConnect;
        static Thread _reader;
        static int _nextId;
        static readonly Dictionary<int, Action<Msg>> _pending = new Dictionary<int, Action<Msg>>();
        static readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();
        static readonly Dictionary<int, DigitalInput> _inputs = new Dictionary<int, DigitalInput>();
        static int[] _pwmPins = Array.Empty<int>(), _i2cBuses = Array.Empty<int>(), _reserved = Array.Empty<int>();

        // ---------------------------------------------------------------- 公開: ハンドルを開く
        public static DigitalInput OpenInput(int pin, Bias bias = Bias.None, Edge edge = Edge.Both, int debounceMs = 0)
        {
            var h = new DigitalInput(pin);
            if (!IsAvailable) return Unavailable(h, pin);
            _inputs[pin] = h;
            Request(m => { if (m.ok) h.Set(m.value != 0, raise: false); else Fail(h, m); },
                    ("op", "claim_in"), ("pin", pin), ("bias", Name(bias)), ("edge", Name(edge)), ("debounce_ms", debounceMs));
            return h;
        }

        public static DigitalOutput OpenOutput(int pin, bool initialValue = false)
        {
            var h = new DigitalOutput(pin, initialValue);
            if (!IsAvailable) return Unavailable(h, pin);
            Request(m => { if (!m.ok) Fail(h, m); }, ("op", "claim_out"), ("pin", pin), ("value", initialValue ? 1 : 0));
            return h;
        }

        public static PwmOutput OpenPwm(int pin, float frequencyHz)
        {
            var h = new PwmOutput(pin, frequencyHz);
            if (!IsAvailable) return Unavailable(h, pin);
            Request(m => { if (m.ok) h.Push(); else Fail(h, m); }, ("op", "pwm_claim"), ("pin", pin));
            return h;
        }

        public static RgbLed OpenRgbLed(int r, int g, int b, bool activeLow = false) => new RgbLed(r, g, b, activeLow);

        public static I2cDevice OpenI2c(int bus, int address) => new I2cDevice(bus, address);

        // ---------------------------------------------------------------- 内部: ハンドルから呼ばれる
        internal static void Send(string json) { if (_transport != null) TrySend(json); }

        internal static void Request(Action<Msg> onReply, params (string key, object value)[] fields)
        {
            if (_transport == null) return;
            int id = Interlocked.Increment(ref _nextId);
            lock (_pending) _pending[id] = onReply;
            var all = new (string, object)[fields.Length + 1];
            all[0] = ("id", id);
            fields.CopyTo(all, 1);
            TrySend(Json.Obj(all));
        }

        internal static void Release(int pin, Action after = null)
        {
            _inputs.Remove(pin);
            if (_transport == null) { after?.Invoke(); return; }
            Request(_ => after?.Invoke(), ("op", "release"), ("pin", pin));
        }

        internal static void Warn(string text) => Debug.LogWarning("[UnityGpio] " + text);

        static T Unavailable<T>(T h, int pin) where T : GpioHandle
        {
            Warn($"GPIO{pin}: デーモンに接続できないため操作は無視される");
            h.Dead = true;
            return h;
        }

        static void Fail(GpioHandle h, Msg m)
        {
            h.Dead = true;
            Debug.LogError($"[UnityGpio] GPIO{h.Pin}: {m.code} {m.message}");
            Error?.Invoke(m.code, h.Pin, m.message);
        }

        static string Name(Bias b) => b == Bias.PullUp ? "pull_up" : b == Bias.PullDown ? "pull_down" : "none";
        static string Name(Edge e) => e.ToString().ToLowerInvariant();

        static void TrySend(string json)
        {
            try { _transport.Send(json); }
            catch (Exception e) { Warn("送信失敗: " + e.Message); OnClosed(); }
        }

        // ---------------------------------------------------------------- 接続
        static void EnsureConnected()
        {
            if (_triedConnect) return;
            _triedConnect = true;
            try
            {
                _transport = CreateTransport();
                _transport.Send(Json.Obj(("id", 1), ("op", "hello"), ("proto", Proto), ("token", Token())));
                _nextId = 1;
                var reply = JsonUtility.FromJson<Msg>(_transport.ReadLine() ?? throw new Exception("hello に応答が無い"));
                if (!reply.ok) throw new Exception($"{reply.code}: {reply.message}");
                Board = reply.board;
                _pwmPins = reply.pwm ?? Array.Empty<int>();
                _i2cBuses = reply.i2c ?? Array.Empty<int>();
                _reserved = reply.reserved ?? Array.Empty<int>();
                Debug.Log($"[UnityGpio] 接続 board={Board} pwm=[{string.Join(",", _pwmPins)}] i2c=[{string.Join(",", _i2cBuses)}] reserved=[{string.Join(",", _reserved)}]");
                _reader = new Thread(ReadLoop) { IsBackground = true, Name = "UnityGpio reader" };
                _reader.Start();
                Application.quitting += Shutdown;
            }
            catch (Exception e)
            {
                Warn("GPIO は使えない: " + e.Message);
                _transport?.Dispose();
                _transport = null;
            }
        }

        static ITransport CreateTransport()
        {
#if UNITY_EDITOR
            var s = UnityGpioSettings.instance;
            if (s.mode == UnityGpioSettings.Mode.Remote) return new SocketTransport(s.host, s.port);
            return new MockTransport();
#else
            var tcp = Environment.GetEnvironmentVariable("UNITYGPIO_TCP");
            if (!string.IsNullOrEmpty(tcp))
            {
                int colon = tcp.LastIndexOf(':');
                return new SocketTransport(tcp.Substring(0, colon), int.Parse(tcp.Substring(colon + 1)));
            }
            return new SocketTransport(Environment.GetEnvironmentVariable("UNITYGPIO_SOCKET") ?? DefaultSocketPath);
#endif
        }

        static string Token()
        {
#if UNITY_EDITOR
            return string.IsNullOrEmpty(UnityGpioSettings.instance.token) ? null : UnityGpioSettings.instance.token;
#else
            return null;
#endif
        }

        static void ReadLoop()
        {
            var t = _transport;
            while (t != null && t == _transport)
            {
                string line = t.ReadLine();
                if (line == null) break;
                Msg m;
                try { m = JsonUtility.FromJson<Msg>(line); }
                catch (Exception e) { Warn("受信の解釈に失敗: " + e.Message + " / " + line); continue; }
                _mainThread.Enqueue(() => Dispatch(m));
            }
            _mainThread.Enqueue(OnClosed);
        }

        static void Dispatch(Msg m)
        {
            if (m.IsEvent)
            {
                if (m.ev == "edge") { if (_inputs.TryGetValue(m.pin, out var input)) input.Set(m.value != 0, raise: true); }
                else if (m.ev == "error") { Debug.LogError($"[UnityGpio] {m.code} pin={m.pin} {m.message}"); Error?.Invoke(m.code, m.pin, m.message); }
                return;
            }
            Action<Msg> cb;
            lock (_pending) { if (_pending.TryGetValue(m.id, out cb)) _pending.Remove(m.id); }
            cb?.Invoke(m);
        }

        static void OnClosed()
        {
            if (_transport == null) return;
            _transport.Dispose();
            _transport = null;
            lock (_pending) _pending.Clear();
            Warn("デーモンとの接続が切れた");
            Disconnected?.Invoke();
        }

        internal static void Shutdown()
        {
            Application.quitting -= Shutdown;
            var t = _transport;
            _transport = null;          // 切断でデーモン側が後始末する (D9)
            t?.Dispose();
            _triedConnect = false;
            _inputs.Clear();
            Mock.Inputs.Clear();
            lock (_pending) _pending.Clear();
            Board = null;
        }

        // ---------------------------------------------------------------- メインスレッドへの配送 (Update の前)
        struct UnityGpioPump { }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void InstallPump()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                if (loop.subSystemList[i].type != typeof(Update)) continue;
                var subs = new List<PlayerLoopSystem>(loop.subSystemList[i].subSystemList ?? Array.Empty<PlayerLoopSystem>());
                if (subs.Exists(s => s.type == typeof(UnityGpioPump))) return;
                subs.Insert(0, new PlayerLoopSystem { type = typeof(UnityGpioPump), updateDelegate = Pump });
                loop.subSystemList[i].subSystemList = subs.ToArray();
                PlayerLoop.SetPlayerLoop(loop);
                return;
            }
        }

        internal static void Pump()
        {
            while (_mainThread.TryDequeue(out var a))
            {
                try { a(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        // ---------------------------------------------------------------- 模擬モード
        /// <summary>模擬モード (Editor) の入力と I2C 応答を差し込む。</summary>
        public static class Mock
        {
            internal static readonly Dictionary<int, (int value, string edge)> Inputs = new Dictionary<int, (int, string)>();
            static MockTransport _mock;
            /// <summary>(address, writtenBytes, readLength) → 読み出すバイト列。未設定なら 0 埋め。</summary>
            public static Func<int, byte[], int, byte[]> I2cHandler;

            public static bool IsActive => _mock != null;

            /// <summary>模擬の入力ピンの値を変える。確保済みでエッジ監視中なら Changed が発火する。</summary>
            public static void SetInput(int pin, bool value)
            {
                int v = value ? 1 : 0;
                if (!Inputs.TryGetValue(pin, out var cur)) { Inputs[pin] = (v, "none"); return; }
                if (cur.value == v) return;
                Inputs[pin] = (v, cur.edge);
                bool fire = cur.edge == "both" || (cur.edge == "rising" && v == 1) || (cur.edge == "falling" && v == 0);
                if (fire) _mock?.Push(Json.Obj(("ev", "edge"), ("pin", pin), ("value", v), ("t", DateTime.UtcNow.Ticks * 100L)));
            }

            internal static void Attach(MockTransport m) => _mock = m;
            internal static void Detach(MockTransport m) { if (_mock == m) _mock = null; }
        }
    }
}
