#!/bin/sh
set -e

if command -v setcap >/dev/null 2>&1 && [ -x /usr/bin/obxodka ]; then
  setcap cap_net_admin,cap_net_raw+ep /usr/bin/obxodka || true
fi

if command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ]; then
  systemctl daemon-reload || true
fi
