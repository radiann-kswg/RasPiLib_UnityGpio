// プロトコル (docs/protocol.md) のメッセージと伝送路。1 行 = 1 JSON。
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace RasPiLib.UnityGpio
{
    /// <summary>デーモンからの応答・イベントを 1 つの平たい型で受ける (JsonUtility 用)。無いフィールドは既定値のまま。</summary>
    [Serializable]
    internal sealed class Msg
    {
        public int id;           // 0 = 無し
        public bool ok;
        public int proto;
        public string board;
        public int[] i2c;
        public int[] pwm;
        public int[] reserved;
        public string code;
        public string message;
        public string ev;        // "edge" / "error"。無ければ応答
        public int pin = -1;
        public int value;
        public long t;
        public string data;
        public bool IsEvent => !string.IsNullOrEmpty(ev);
    }

    /// <summary>要求 JSON を組み立てる最小のライタ。値は int / long / bool / string のみ。</summary>
    internal static class Json
    {
        public static string Obj(params (string key, object value)[] fields)
        {
            var sb = new StringBuilder(64).Append('{');
            bool first = true;
            foreach (var (key, value) in fields)
            {
                if (value == null) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(key).Append("\":");
                switch (value)
                {
                    case bool b: sb.Append(b ? "true" : "false"); break;
                    case string s: sb.Append('"').Append(s.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"'); break;
                    case int[] a: sb.Append('[').Append(string.Join(",", a)).Append(']'); break;
                    default: sb.Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)); break;
                }
            }
            return sb.Append('}').ToString();
        }
    }

    internal interface ITransport : IDisposable
    {
        void Send(string line);
        /// <summary>1 行読む。切断で null。</summary>
        string ReadLine();
    }

    /// <summary>Unix ソケット (Pi 上) または TCP (遠隔モード / ループバック代替)。</summary>
    internal sealed class SocketTransport : ITransport
    {
        readonly Socket _sock;
        readonly NetworkStream _stream;
        readonly StreamReader _reader;
        readonly object _writeLock = new object();

        public SocketTransport(string unixPath)
        {
            _sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _sock.Connect(new UnixDomainSocketEndPoint(unixPath));
            _stream = new NetworkStream(_sock, ownsSocket: true);
            _reader = new StreamReader(_stream, new UTF8Encoding(false));
        }

        public SocketTransport(string host, int port)
        {
            _sock = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            _sock.Connect(host, port);
            _stream = new NetworkStream(_sock, ownsSocket: true);
            _reader = new StreamReader(_stream, new UTF8Encoding(false));
        }

        public void Send(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            lock (_writeLock) _stream.Write(bytes, 0, bytes.Length);
        }

        public string ReadLine()
        {
            try { return _reader.ReadLine(); }
            catch (IOException) { return null; }
            catch (ObjectDisposedException) { return null; }
        }

        public void Dispose()
        {
            try { _sock.Shutdown(SocketShutdown.Both); } catch (SocketException) { }
            _stream.Dispose();
        }
    }

    /// <summary>模擬モード。Editor 内だけでピンの状態を持つ。入力は <see cref="Gpio.Mock"/> から変えられる。</summary>
    internal sealed class MockTransport : ITransport
    {
        [Serializable]
        sealed class Req
        {
            public int id; public string op; public int proto; public int pin = -1; public int value;
            public string edge; public int bus = 1; public int addr; public string write; public int read;
        }

        readonly BlockingCollection<string> _inbox = new BlockingCollection<string>();

        public MockTransport() { Gpio.Mock.Attach(this); }

        internal void Push(string line) => _inbox.Add(line);

        public void Send(string line)
        {
            var r = JsonUtility.FromJson<Req>(line);
            switch (r.op)
            {
                case "hello":
                    Push(Json.Obj(("id", r.id), ("ok", true), ("proto", 1), ("board", "mock"),
                                  ("i2c", new[] { 1 }), ("pwm", new[] { 12, 13 }), ("reserved", Array.Empty<int>())));
                    return;
                case "claim_in":
                    int cur = Gpio.Mock.Inputs.TryGetValue(r.pin, out var v) ? v.value : 1;
                    Gpio.Mock.Inputs[r.pin] = (cur, r.edge);
                    Reply(r.id, ("value", cur));
                    return;
                case "i2c":
                    var data = Gpio.Mock.I2cHandler?.Invoke(r.addr, Hex.Parse(r.write), r.read) ?? new byte[r.read];
                    Reply(r.id, ("data", Hex.Format(data)));
                    return;
                case "release":
                    Gpio.Mock.Inputs.Remove(r.pin);
                    break;
            }
            Reply(r.id);
        }

        void Reply(int id, params (string key, object value)[] extra)
        {
            if (id == 0) return;
            var fields = new List<(string, object)> { ("id", id), ("ok", true) };
            fields.AddRange(extra);
            Push(Json.Obj(fields.ToArray()));
        }

        public string ReadLine()
        {
            try { return _inbox.Take(); }
            catch (InvalidOperationException) { return null; }   // CompleteAdding → 切断扱い
        }

        public void Dispose() { _inbox.CompleteAdding(); Gpio.Mock.Detach(this); }
    }

    internal static class Hex
    {
        public static string Format(byte[] b) => b == null ? "" : BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();

        public static byte[] Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return Array.Empty<byte>();
            var b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return b;
        }
    }
}
