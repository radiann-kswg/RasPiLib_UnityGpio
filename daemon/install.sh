#!/bin/sh
# install.sh — unitygpiod を Raspberry Pi OS (trixie, arm64) へ導入する。
# 使い方: sudo daemon/install.sh [--no-start]   （pi-gen の on_chroot 内では --no-start）
set -eu
HERE="$(cd "$(dirname "$0")" && pwd)"
START=1
[ "${1:-}" = "--no-start" ] && START=0

if ! python3 -c 'import gpiod' 2>/dev/null; then
  apt-get install -y --no-install-recommends python3-libgpiod
fi

getent group gpio >/dev/null || groupadd --system gpio
getent group i2c  >/dev/null || groupadd --system i2c
getent passwd unitygpio >/dev/null || useradd --system --no-create-home --shell /usr/sbin/nologin unitygpio

install -d -m 0755 /usr/local/lib/unitygpio
install -m 0755 "$HERE/unitygpiod.py" /usr/local/lib/unitygpio/unitygpiod.py
install -m 0755 "$HERE/unitygpio-config" /usr/local/sbin/unitygpio-config
install -m 0644 "$HERE/unitygpiod.service" /etc/systemd/system/unitygpiod.service
[ -f /etc/unitygpio.conf ] || install -m 0644 "$HERE/unitygpio.conf" /etc/unitygpio.conf

systemctl enable unitygpiod.service
if [ "$START" = 1 ]; then
  systemctl daemon-reload
  systemctl restart unitygpiod.service
  echo "unitygpiod を起動した。状態: systemctl status unitygpiod"
else
  echo "unitygpiod を導入した（起動は次回ブート時）"
fi
echo "予約ピンは /etc/unitygpio.conf の reserved に、I2C/PWM は unitygpio-config で有効化する"
