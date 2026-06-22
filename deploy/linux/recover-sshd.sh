#!/bin/bash
# Запускать из консоли FirstVDS (VNC/serial), когда SSH зависает на «banner exchange».
# Не требует SSH — только root/sudo на сервере.

set -euo pipefail

echo "==> sshd status"
systemctl status ssh --no-pager || systemctl status sshd --no-pager || true

echo "==> fail2ban sshd jail"
if command -v fail2ban-client >/dev/null 2>&1; then
  fail2ban-client status sshd 2>/dev/null || fail2ban-client status 2>/dev/null || true
  echo "Unban all (if needed): fail2ban-client unban --all"
fi

echo "==> connections on :22"
ss -tn sport = :22 | head -20 || netstat -tn | grep ':22 ' | head -20 || true

echo "==> fix sshd_config (UseDNS, GSSAPI)"
SSHD_CFG="/etc/ssh/sshd_config"
for key in UseDNS GSSAPIAuthentication; do
  if grep -q "^${key}" "$SSHD_CFG" 2>/dev/null; then
    sed -i "s/^${key}.*/${key} no/" "$SSHD_CFG"
  else
    echo "${key} no" >> "$SSHD_CFG"
  fi
done

if ! grep -q "^MaxStartups" "$SSHD_CFG" 2>/dev/null; then
  echo "MaxStartups 30:60:100" >> "$SSHD_CFG"
fi

echo "==> restart ssh"
systemctl restart ssh 2>/dev/null || systemctl restart sshd

sleep 2
systemctl is-active ssh 2>/dev/null || systemctl is-active sshd

echo "==> done — try: ssh deploy@$(hostname -I | awk '{print $1}')"
