#!/usr/bin/env python3
"""Session の拒否判定と後始末順序の自己テスト。実機不要: python3 daemon/test_unitygpiod.py"""
import unitygpiod as d


class FakeHw:
    board, i2c_buses = "pi5", [1]
    pwm_pins = {12: 0, 13: 1}

    def __init__(self):
        self.log = []

    def claim_out(self, pin, value):      self.log.append(("out", pin, value)); return f"o{pin}"
    def claim_in(self, pin, b, e, dms):   self.log.append(("in", pin, b, e, dms)); return f"i{pin}", 1
    def write(self, h, pin, value):       self.log.append(("write", pin, value))
    def to_input(self, h, pin):           self.log.append(("to_input", pin))
    def release(self, h):                 self.log.append(("release", h))
    def pwm_claim(self, pin):             self.log.append(("pwm", pin)); return f"p{pin}"
    def pwm_set(self, h, p, dc, en):      self.log.append(("pwm_set", h, p, dc, en))
    def pwm_release(self, h):             self.log.append(("pwm_release", h))
    def i2c_xfer(self, bus, addr, w, r):  self.log.append(("i2c", bus, addr, w, r)); return bytes(r)


def expect(code, fn, *a, **k):
    try:
        fn(*a, **k)
    except d.GpioError as e:
        assert e.code == code, f"{e.code} != {code}"
        return
    raise AssertionError(f"{code} が出なかった")


hw = FakeHw()
s = d.Session(hw, reserved=[21])
assert s.hello() == {"proto": 1, "board": "pi5", "i2c": [1], "pwm": [12, 13], "reserved": [21]}

expect("RESERVED", s.claim_out, 21, 0)         # 予約ピン
expect("BUSY", s.claim_out, 2, 0)              # 有効な I2C のピン
expect("BUSY", s.claim_in, 12)                 # 有効な PWM のピンをデジタルで
expect("BAD_PIN", s.pwm_claim, 17)             # PWM でないピン
expect("BAD_PIN", s.claim_out, 28, 0)
expect("BAD_PIN", s.write, 17, 1)              # 未確保への write

s.claim_out(17, 1); s.write(17, 0)
expect("BUSY", s.claim_out, 17, 0)             # 二重確保
assert s.claim_in(27, "pull_up", "both", 10) == {"value": 1}
s.pwm_claim(12); s.pwm_set(12, 20_000_000, 1_500_000)
expect("BAD_PIN", s.pwm_set, 12, 0, 0)
assert s.i2c(1, 0x76, "d0", 1) == {"data": "00"}
expect("DISABLED", s.i2c, 0, 0x76)
expect("BAD_PIN", s.i2c, 1, 0x80)

hw.log.clear()
s.release_all()                                 # 切断時の後始末 (D9)
assert ("to_input", 17) in hw.log and hw.log.index(("to_input", 17)) < hw.log.index(("release", "o17")), hw.log
assert ("release", "i27") in hw.log and ("pwm_release", "p12") in hw.log
assert s.owned == {}

hw.pwm_pins = {}                                # PWM 無効なボード
expect("DISABLED", d.Session(hw, []).pwm_claim, 12)
assert d.Session(hw, []).claim_out(12, 0) == {}   # 無効なら 12 はデジタルに使える

print("ok")
