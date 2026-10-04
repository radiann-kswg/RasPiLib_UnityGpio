#!/usr/bin/env python3
"""unitygpiod — RasPiLib_UnityGpio のデーモン。GPIO / ハードウェア PWM / I2C を 1 本のソケット越しに提供する。

プロトコルの正本は docs/protocol.md。依存は Python 3 標準ライブラリ + python3-libgpiod (v2) のみ。
"""
import argparse
import configparser
import ctypes
import fcntl
import glob
import grp
import json
import logging
import os
import selectors
import socket
import sys
import time
from datetime import timedelta

PROTO = 1
PWM_CHANNELS = {12: 0, 13: 1}          # BCM → pwmchip のチャンネル（pwm-2chan overlay, pin=12,func=4,pin2=13,func2=4）
I2C_BUS_PINS = {1: (2, 3)}             # I2C1 が占有するピン
DEFAULT_SOCK = "/run/unitygpio/unitygpio.sock"
DEFAULT_CONF = "/etc/unitygpio.conf"
CHIP_LABELS = {"pinctrl-rp1": "pi5", "pinctrl-bcm2711": "pi4", "pinctrl-bcm2835": "pi3"}
PWM_SYSFS_HINTS = ("fe20c000.pwm", "1f00098000.pwm")   # Pi 4B の pwm0 / Pi 5 の rp1 pwm0
log = logging.getLogger("unitygpiod")


class GpioError(Exception):
    def __init__(self, code, message, pin=None):
        super().__init__(message)
        self.code, self.message, self.pin = code, message, pin


# ---------------------------------------------------------------- ハードウェア
class Hw:
    """実機バックエンド。gpiod は実機にしか無いので import はここで行う。"""

    def __init__(self, pwmchip_override=None):
        import gpiod  # noqa: F401  python3-libgpiod (v2)
        self.gpiod = gpiod
        self.chip, self.board = self._find_chip()
        self.pwmchip = pwmchip_override or self._find_pwmchip()
        self.pwm_pins = dict(PWM_CHANNELS) if self.pwmchip else {}
        self.i2c_buses = [b for b in I2C_BUS_PINS if os.path.exists(f"/dev/i2c-{b}")]
        log.info("board=%s chip=%s pwmchip=%s i2c=%s", self.board, self.chip.path, self.pwmchip, self.i2c_buses)

    def _find_chip(self):
        for path in sorted(glob.glob("/dev/gpiochip*")):
            chip = self.gpiod.Chip(path)
            label = chip.get_info().label
            if label in CHIP_LABELS:
                return chip, CHIP_LABELS[label]
            chip.close()
        raise SystemExit("ヘッダの GPIO チップ (pinctrl-rp1 / pinctrl-bcm2711) が見つからない")

    @staticmethod
    def _find_pwmchip():
        for path in sorted(glob.glob("/sys/class/pwm/pwmchip*")):
            real = os.path.realpath(path)
            if any(h in real for h in PWM_SYSFS_HINTS):
                return path
        return None

    # --- デジタル
    def claim_out(self, pin, value):
        g = self.gpiod
        from gpiod.line import Direction, Value
        s = g.LineSettings(direction=Direction.OUTPUT, output_value=Value.ACTIVE if value else Value.INACTIVE)
        return self._request(pin, s)

    def claim_in(self, pin, bias, edge, debounce_ms):
        g = self.gpiod
        from gpiod.line import Direction, Bias, Edge, Value
        s = g.LineSettings(
            direction=Direction.INPUT,
            bias={"pull_up": Bias.PULL_UP, "pull_down": Bias.PULL_DOWN}.get(bias, Bias.DISABLED),
            edge_detection={"rising": Edge.RISING, "falling": Edge.FALLING, "both": Edge.BOTH}.get(edge, Edge.NONE),
            debounce_period=timedelta(milliseconds=debounce_ms or 0),
        )
        req = self._request(pin, s)
        return req, int(req.get_value(pin) == Value.ACTIVE)

    def _request(self, pin, settings):
        try:
            return self.chip.request_lines(consumer="unitygpiod", config={pin: settings})
        except OSError as e:
            raise GpioError("BUSY" if e.errno == 16 else "BAD_PIN", f"GPIO{pin}: {e.strerror}", pin)

    def write(self, req, pin, value):
        from gpiod.line import Value
        req.set_value(pin, Value.ACTIVE if value else Value.INACTIVE)

    def to_input(self, req, pin):
        from gpiod.line import Direction, Bias
        req.reconfigure_lines({pin: self.gpiod.LineSettings(direction=Direction.INPUT, bias=Bias.DISABLED)})

    def release(self, req):
        req.release()

    def edge_fd(self, req):
        return req.fd

    def read_edges(self, req):
        from gpiod.edge_event import EdgeEvent
        return [(e.line_offset, int(e.event_type == EdgeEvent.Type.RISING_EDGE), e.timestamp_ns)
                for e in req.read_edge_events()]

    # --- PWM (sysfs)
    def pwm_claim(self, pin):
        ch = self.pwm_pins[pin]
        d = f"{self.pwmchip}/pwm{ch}"
        if not os.path.isdir(d):
            self._sysfs(f"{self.pwmchip}/export", ch)
            for _ in range(50):              # udev が権限を付け終わるまで少し待つ
                if os.access(f"{d}/enable", os.W_OK):
                    break
                time.sleep(0.02)
        return d

    def pwm_set(self, d, period_ns, duty_ns, enabled):
        try:
            cur = int(open(f"{d}/period").read() or 0)
            if period_ns != cur:
                self._sysfs(f"{d}/duty_cycle", 0)   # duty > period はカーネルが拒むので順序に注意
                self._sysfs(f"{d}/period", period_ns)
            self._sysfs(f"{d}/duty_cycle", min(duty_ns, period_ns))
            self._sysfs(f"{d}/enable", 1 if enabled else 0)
        except OSError as e:
            raise GpioError("IO", f"PWM {d}: {e.strerror}")

    def pwm_release(self, d):
        try:
            self.pwm_set(d, int(open(f"{d}/period").read() or 1000000), 0, False)
            self._sysfs(f"{self.pwmchip}/unexport", os.path.basename(d)[3:])
        except OSError:
            pass

    @staticmethod
    def _sysfs(path, value):
        with open(path, "w") as f:
            f.write(str(value))

    # --- I2C (ioctl I2C_RDWR: 書き→読みをリピーテッドスタートで)
    def i2c_xfer(self, bus, addr, wbytes, rlen):
        class Msg(ctypes.Structure):
            _fields_ = [("addr", ctypes.c_uint16), ("flags", ctypes.c_uint16),
                        ("len", ctypes.c_uint16), ("buf", ctypes.POINTER(ctypes.c_uint8))]

        class Data(ctypes.Structure):
            _fields_ = [("msgs", ctypes.POINTER(Msg)), ("nmsgs", ctypes.c_uint32)]

        msgs, bufs = [], []
        if wbytes:
            b = (ctypes.c_uint8 * len(wbytes))(*wbytes)
            bufs.append(b)
            msgs.append(Msg(addr, 0, len(wbytes), b))
        rbuf = None
        if rlen:
            rbuf = (ctypes.c_uint8 * rlen)()
            msgs.append(Msg(addr, 0x0001, rlen, rbuf))   # I2C_M_RD
        if not msgs:
            return b""
        arr = (Msg * len(msgs))(*msgs)
        data = Data(arr, len(msgs))
        try:
            fd = os.open(f"/dev/i2c-{bus}", os.O_RDWR)
        except OSError as e:
            raise GpioError("IO", f"/dev/i2c-{bus}: {e.strerror}")
        try:
            fcntl.ioctl(fd, 0x0707, data)                 # I2C_RDWR
        except OSError as e:
            raise GpioError("IO", f"I2C 0x{addr:02x}: {e.strerror}")
        finally:
            os.close(fd)
        return bytes(rbuf) if rbuf is not None else b""


# ---------------------------------------------------------------- セッション（純ロジック・テスト対象）
class Session:
    """1 接続分の確保状態。ピンの拒否判定と後始末の順序はここに集約する。"""

    def __init__(self, hw, reserved):
        self.hw, self.reserved = hw, set(reserved)
        self.owned = {}          # pin → (kind, handle)

    def hello(self):
        return {"proto": PROTO, "board": self.hw.board, "i2c": list(self.hw.i2c_buses),
                "pwm": sorted(self.hw.pwm_pins), "reserved": sorted(self.reserved)}

    def _check_pin(self, pin, for_pwm=False):
        if not isinstance(pin, int) or not 0 <= pin <= 27:
            raise GpioError("BAD_PIN", f"GPIO{pin} は存在しない", pin)
        if pin in self.reserved:
            raise GpioError("RESERVED", f"GPIO{pin} は OS が予約している", pin)
        if pin in self.owned:
            raise GpioError("BUSY", f"GPIO{pin} はこの接続が既に確保している", pin)
        for bus in self.hw.i2c_buses:
            if pin in I2C_BUS_PINS[bus]:
                raise GpioError("BUSY", f"GPIO{pin} は有効な I2C{bus} が使っている", pin)
        if for_pwm:
            if pin not in PWM_CHANNELS:
                raise GpioError("BAD_PIN", f"GPIO{pin} はハードウェア PWM ピンではない (12 / 13)", pin)
            if pin not in self.hw.pwm_pins:
                raise GpioError("DISABLED", "PWM が無効。`sudo unitygpio-config pwm on` の後に再起動", pin)
        elif pin in self.hw.pwm_pins:
            raise GpioError("BUSY", f"GPIO{pin} は有効な PWM が使っている (pwm_claim を使う)", pin)

    def _owned(self, pin, kind):
        got = self.owned.get(pin)
        if not got or got[0] != kind:
            raise GpioError("BAD_PIN", f"GPIO{pin} は {kind} として確保されていない", pin)
        return got[1]

    def claim_out(self, pin, value):
        self._check_pin(pin)
        self.owned[pin] = ("out", self.hw.claim_out(pin, int(bool(value))))
        return {}

    def claim_in(self, pin, bias="none", edge="none", debounce_ms=0):
        self._check_pin(pin)
        h, v = self.hw.claim_in(pin, bias, edge, int(debounce_ms or 0))
        self.owned[pin] = ("in", h)
        return {"value": v}

    def write(self, pin, value):
        self.hw.write(self._owned(pin, "out"), pin, int(bool(value)))

    def pwm_claim(self, pin):
        self._check_pin(pin, for_pwm=True)
        self.owned[pin] = ("pwm", self.hw.pwm_claim(pin))
        return {}

    def pwm_set(self, pin, period_ns, duty_ns, enabled=True):
        if period_ns <= 0 or duty_ns < 0:
            raise GpioError("BAD_PIN", "period_ns は正、duty_ns は 0 以上", pin)
        self.hw.pwm_set(self._owned(pin, "pwm"), int(period_ns), int(duty_ns), bool(enabled))

    def release(self, pin):
        if pin not in self.owned:
            raise GpioError("BAD_PIN", f"GPIO{pin} は確保されていない", pin)
        kind, h = self.owned.pop(pin)
        if kind == "out":
            self.hw.to_input(h, pin)        # 解放前に入力へ戻す (D9)
            self.hw.release(h)
        elif kind == "in":
            self.hw.release(h)
        else:
            self.hw.pwm_release(h)          # duty 0 ・無効にしてから解放
        return {}

    def release_all(self):
        for pin in list(self.owned):
            try:
                self.release(pin)
            except Exception as e:           # 1 本の失敗で他の後始末を止めない
                log.warning("release GPIO%s: %s", pin, e)

    def i2c(self, bus, addr, write="", read=0):
        if bus not in self.hw.i2c_buses:
            raise GpioError("DISABLED", f"I2C{bus} が無効。`sudo unitygpio-config i2c on` の後に再起動")
        if not 0x03 <= addr <= 0x77:
            raise GpioError("BAD_PIN", f"I2C アドレス 0x{addr:02x} は範囲外")
        data = self.hw.i2c_xfer(bus, addr, bytes.fromhex(write or ""), int(read or 0))
        return {"data": data.hex()}

    def edge_handles(self):
        return [(pin, h) for pin, (kind, h) in self.owned.items() if kind == "in"]


# ---------------------------------------------------------------- サーバ
class Server:
    def __init__(self, hw, conf):
        self.hw, self.conf = hw, conf
        self.sel = selectors.DefaultSelector()
        self.client = None          # (sock, Session, buffer, needs_token)
        self.hello_done = False
        self.listeners = []

    def listen(self):
        path = self.conf["socket"]
        if os.path.exists(path):
            os.unlink(path)
        us = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        us.bind(path)
        try:
            os.chown(path, -1, grp.getgrnam("gpio").gr_gid)
        except (KeyError, PermissionError) as e:
            log.warning("ソケットのグループを gpio にできない: %s", e)
        os.chmod(path, 0o660)
        self._add_listener(us)
        tcp = self.conf.get("tcp_listen")
        if tcp:
            host, _, port = tcp.rpartition(":")
            loopback = host in ("127.0.0.1", "::1", "localhost")
            if not loopback and not self.conf.get("tcp_token"):
                log.error("tcp_listen=%s はループバック以外なのに tcp_token が無い。TCP は開かない", tcp)
            else:
                ts = socket.socket(socket.AF_INET6 if ":" in host else socket.AF_INET, socket.SOCK_STREAM)
                ts.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                ts.bind((host, int(port)))
                self._add_listener(ts)
                log.info("TCP 待受 %s (token %s)", tcp, "必須" if not loopback else "不要")

    def _add_listener(self, s):
        s.listen(2)
        s.setblocking(False)
        self.listeners.append(s)
        self.sel.register(s, selectors.EVENT_READ, self._accept)

    def _accept(self, ls, _mask):
        s, addr = ls.accept()
        if self.client:
            self._send_to(s, {"ok": False, "code": "SESSION_TAKEN", "message": "ほかの接続が使用中"})
            s.close()
            return
        s.setblocking(False)
        needs_token = ls.family != socket.AF_UNIX and not (isinstance(addr, tuple) and addr[0] in ("127.0.0.1", "::1", "::ffff:127.0.0.1"))
        self.client = [s, Session(self.hw, self.conf["reserved"]), b"", needs_token]
        self.hello_done = False
        self.sel.register(s, selectors.EVENT_READ, self._on_client)
        log.info("接続 %s", addr or "unix")

    def _drop_client(self):
        if not self.client:
            return
        s, sess, _, _ = self.client
        for _pin, h in sess.edge_handles():
            try:
                self.sel.unregister(self.hw.edge_fd(h))
            except (KeyError, ValueError):
                pass
        sess.release_all()
        try:
            self.sel.unregister(s)
        except (KeyError, ValueError):
            pass
        s.close()
        self.client = None
        log.info("切断・後始末完了")

    def _on_client(self, s, _mask):
        try:
            data = s.recv(65536)
        except OSError:
            data = b""
        if not data:
            self._drop_client()
            return
        self.client[2] += data
        while self.client and b"\n" in self.client[2]:
            line, _, self.client[2] = self.client[2].partition(b"\n")
            if line.strip():
                self._handle_line(line)

    def _on_edge(self, fd, _mask, pin=None, handle=None):
        for p, value, t in self.hw.read_edges(handle):
            self._send({"ev": "edge", "pin": p, "value": value, "t": t})

    def _handle_line(self, line):
        s, sess, _, needs_token = self.client
        try:
            req = json.loads(line)
            if not isinstance(req, dict) or "op" not in req:
                raise GpioError("PROTO", "op が無い")
        except (ValueError, GpioError) as e:
            self._send({"ok": False, "code": "PROTO", "message": str(e)})
            self._drop_client()
            return
        rid, op = req.get("id"), req["op"]
        try:
            if op == "hello":
                if req.get("proto") != PROTO:
                    raise GpioError("PROTO", f"proto {req.get('proto')} は非対応 (サーバは {PROTO})")
                if needs_token and req.get("token") != self.conf.get("tcp_token"):
                    raise GpioError("AUTH", "token が違う")
                self.hello_done = True
                result = sess.hello()
            elif not self.hello_done:
                raise GpioError("PROTO", "先に hello を送る")
            elif op == "claim_out":
                result = sess.claim_out(req["pin"], req.get("value", 0))
            elif op == "claim_in":
                result = sess.claim_in(req["pin"], req.get("bias", "none"), req.get("edge", "none"), req.get("debounce_ms", 0))
                if req.get("edge", "none") != "none":
                    h = sess.owned[req["pin"]][1]
                    self.sel.register(self.hw.edge_fd(h), selectors.EVENT_READ,
                                      lambda fd, m, h=h: self._on_edge(fd, m, handle=h))
            elif op == "write":
                result = sess.write(req["pin"], req["value"])
            elif op == "pwm_claim":
                result = sess.pwm_claim(req["pin"])
            elif op == "pwm_set":
                result = sess.pwm_set(req["pin"], req["period_ns"], req["duty_ns"], req.get("enabled", True))
            elif op == "release":
                pin = req["pin"]
                got = sess.owned.get(pin)
                if got and got[0] == "in":
                    try:
                        self.sel.unregister(self.hw.edge_fd(got[1]))
                    except (KeyError, ValueError):
                        pass
                result = sess.release(pin)
            elif op == "i2c":
                result = sess.i2c(req.get("bus", 1), req["addr"], req.get("write", ""), req.get("read", 0))
            else:
                raise GpioError("PROTO", f"未知の op: {op}")
        except GpioError as e:
            self._fail(rid, e.code, e.message, e.pin)
            if e.code in ("PROTO", "AUTH"):
                self._drop_client()
            return
        except (KeyError, TypeError, ValueError) as e:
            self._fail(rid, "PROTO", f"引数の誤り: {e!r}")
            return
        if rid is not None:
            self._send({"id": rid, "ok": True, **(result or {})})

    def _fail(self, rid, code, message, pin=None):
        msg = {"code": code, "message": message}
        if pin is not None:
            msg["pin"] = pin
        if rid is None:
            self._send({"ev": "error", **msg})
        else:
            self._send({"id": rid, "ok": False, **msg})

    def _send(self, obj):
        if self.client:
            self._send_to(self.client[0], obj)

    @staticmethod
    def _send_to(s, obj):
        try:
            s.sendall((json.dumps(obj, separators=(",", ":")) + "\n").encode())
        except OSError:
            pass

    def serve_forever(self):
        while True:
            for key, mask in self.sel.select():
                key.data(key.fileobj, mask)


# ---------------------------------------------------------------- 設定・起動
def load_conf(path):
    cp = configparser.ConfigParser()
    cp.read_dict({"unitygpio": {"socket": DEFAULT_SOCK, "reserved": "", "tcp_listen": "", "tcp_token": "", "pwmchip": ""}})
    cp.read(path)
    c = cp["unitygpio"]
    return {
        "socket": c["socket"],
        "reserved": [int(p) for p in c["reserved"].replace(" ", "").split(",") if p],
        "tcp_listen": c["tcp_listen"] or None,
        "tcp_token": c["tcp_token"] or None,
        "pwmchip": c["pwmchip"] or None,
    }


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("-c", "--conf", default=DEFAULT_CONF)
    ap.add_argument("-v", "--verbose", action="store_true")
    a = ap.parse_args(argv)
    logging.basicConfig(level=logging.DEBUG if a.verbose else logging.INFO, format="%(levelname)s %(message)s", stream=sys.stderr)
    conf = load_conf(a.conf)
    log.info("conf=%s reserved=%s", a.conf, conf["reserved"])
    srv = Server(Hw(conf["pwmchip"]), conf)
    srv.listen()
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        srv._drop_client()


if __name__ == "__main__":
    main()
