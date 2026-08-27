#!/usr/bin/env bash
# Per-boot startup for the SQL Account Sync Cloud Agent environment.
# Brings up the local Firebird 3.0 server and ensures the SYSDBA password matches
# the one the app expects. Idempotent and safe to run on every boot.
set -euo pipefail

FB_PASSWORD="masterkey"
SYSDBA_FILE="/etc/firebird/3.0/SYSDBA.password"

echo "==> Ensuring Firebird runtime directory exists"
sudo mkdir -p /run/firebird3.0
sudo chown firebird:firebird /run/firebird3.0

if ! pgrep -x fbguard >/dev/null 2>&1; then
  echo "==> Starting Firebird 3.0 server"
  sudo -u firebird /usr/sbin/fbguard -daemon -forever >/dev/null 2>&1 &
else
  echo "==> Firebird already running"
fi

echo "==> Waiting for Firebird to listen on port 3050"
port_open() { awk '$4=="0A"{print $2}' /proc/net/tcp /proc/net/tcp6 2>/dev/null | grep -qi ':0BEA'; }
for _ in $(seq 1 30); do
  if port_open; then break; fi
  sleep 1
done
if ! port_open; then
  echo "!! Firebird did not open port 3050 in time" >&2
  exit 1
fi

# gsec -display returns 0 only when the given SYSDBA password is correct.
password_ok() { sudo -u firebird gsec -user SYSDBA -password "$1" -display >/dev/null 2>&1; }

if password_ok "$FB_PASSWORD"; then
  echo "==> SYSDBA password already set to the expected value"
else
  echo "==> Resetting SYSDBA password to the expected development value"
  CURRENT="$(sed -n 's/^ISC_PASSWORD="\(.*\)"/\1/p' "$SYSDBA_FILE" 2>/dev/null | head -1)"
  if [ -n "$CURRENT" ] && sudo -u firebird gsec -user SYSDBA -password "$CURRENT" -modify SYSDBA -pw "$FB_PASSWORD" >/dev/null 2>&1; then
    sudo sed -i "s/^ISC_PASSWORD=.*/ISC_PASSWORD=\"${FB_PASSWORD}\"/" "$SYSDBA_FILE" || true
    echo "==> SYSDBA password updated"
  else
    echo "!! Could not reset SYSDBA password automatically" >&2
    exit 1
  fi
fi

echo "==> Firebird is ready on 127.0.0.1:3050"
