#!/usr/bin/env bash
# DEV/TEST ONLY. Installs a real server tarball with install.sh in a throw-away ubuntu:24.04 Docker container and
# runs the published server in Production, the way domainnotice.service would (production never uses Docker).
#
# Unlike container-smoke.sh nothing of install.sh is stubbed except systemctl (containers have no systemd):
# "restart domainnotice.service" starts the server as the domainnotice user with the unit's environment.
# Port 8443 is used because the container cannot grant CAP_NET_BIND_SERVICE for 443.
#
# Checks: first local account via --create-local-account, migrations, the fingerprint in the summary, /healthz,
# the sign-in page and Blazor/MudBlazor static assets over HTTPS with the generated certificate, a real local sign-in,
# Console pages, the agent port, and key file owners.
#
# Usage (on the host): deploy/linux/tests/container-run-smoke.sh ARTIFACTS_DIR
set -Eeuo pipefail

if [[ "${1-}" != --inside ]]; then
    here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
    artifacts="$(cd "${1:?usage: $0 ARTIFACTS_DIR}" && pwd)"
    exec docker run --rm -v "$here:/src:ro" -v "$artifacts:/pkg:ro" ubuntu:24.04 bash /src/tests/container-run-smoke.sh --inside
fi

fails=0
check() { local d="$1"; shift; if "$@"; then echo "ok   - $d"; else echo "FAIL - $d"; fails=$((fails + 1)); fi; }
owner() { [[ "$(stat -c '%a %U' "$1")" == "$2" ]] || { echo "     $1 is $(stat -c '%a %U' "$1"), expected $2"; return 1; }; }

case "$(uname -m)" in x86_64) arch=x64 ;; aarch64) arch=arm64 ;; esac
pkg="$(ls /pkg/DomainNotice.Server-*-linux-$arch.tar.gz | head -n1)"
echo "# package $pkg"

cat >/usr/local/bin/systemctl <<'STUB'
#!/bin/bash
echo "systemctl $*" >>/tmp/systemctl.log
start_pg() { local v; v="$(ls /etc/postgresql | sort -V | tail -n1)"; pg_ctlcluster --skip-systemctl-redirect "$v" main start 2>/dev/null || true; }
case "$*" in
    *postgresql*) [[ "$1" == enable || "$1" == start || "$1" == restart ]] && start_pg ;;
    "restart domainnotice.service"|"start domainnotice.service")
        pkill -f /opt/domainnotice/current/DomainNotice.Server || true
        cd /opt/domainnotice/current && setsid runuser -u domainnotice -- env -i \
            PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin HOME=/nonexistent LANG=C.UTF-8 \
            DOMAINNOTICE_CONFIG=/opt/domainnotice/config/appsettings.Production.json ASPNETCORE_ENVIRONMENT=Production \
            DOTNET_NOLOGO=1 /opt/domainnotice/current/DomainNotice.Server >/tmp/server.log 2>&1 </dev/null &
        ;;
esac
exit 0
STUB
chmod 755 /usr/local/bin/systemctl

export ADMIN_PASSWORD='Smoke-Test-Passw0rd' DOMAINNOTICE_ADMIN_PASSWORD='Smoke-Test-Passw0rd'
bash /src/install.sh --unattended --db=local --package "$pkg" --admin-user admin --console-port 8443 \
    --server-name dn.example.test --open-firewall no >/tmp/install.log 2>&1 || { tail -n 40 /tmp/install.log; echo "FAIL - install"; exit 1; }
tail -n 14 /tmp/install.log
set +e
trap - ERR

B=https://127.0.0.1:8443
for _ in $(seq 1 40); do curl -fsk --max-time 3 $B/healthz >/dev/null 2>&1 && break; sleep 2; done
code() { curl -sk -o /dev/null -w '%{http_code}' "$@"; }

check "fingerprint in summary"     grep -Eq '[0-9a-f]{64}|([0-9A-F]{2}:){31}[0-9A-F]{2}' /tmp/install.log
check "healthz ok"                 bash -c "curl -fsk $B/healthz | grep -q '\"status\":\"ok\"'"
check "sign-in page 200"           [ "$(code $B/account/login)" == 200 ]
check "anonymous root redirects"   [ "$(code $B/)" == 302 ]
check "first-run setup closed"     [ "$(code $B/account/setup)" == 404 ]
check "blazor.web.js served"       [ "$(code $B/_framework/blazor.web.js)" == 200 ]
check "MudBlazor css served"       [ "$(code $B/_content/MudBlazor/MudBlazor.min.css)" == 200 ]
check "cert is the generated one"  bash -c "echo | openssl s_client -connect 127.0.0.1:8443 2>/dev/null | openssl x509 -noout -subject | grep -q dn.example.test"

jar=/tmp/jar
tok="$(curl -sk -c $jar $B/account/login | grep -o 'name="__RequestVerificationToken" value="[^"]*"' | sed 's/.*value="//;s/"$//')"
loc="$(curl -sk -b $jar -c $jar -o /dev/null -w '%{redirect_url}' --data-urlencode "__RequestVerificationToken=$tok" \
    --data-urlencode "Source=Local" --data-urlencode "UserName=admin" --data-urlencode "Password=$ADMIN_PASSWORD" $B/account/login)"
check "local sign-in works"        [ "$loc" == "$B/" ]
for p in / /computers /announcements /confirmations /answers /history /agent-updates /directory /settings /audit; do
    check "page $p 200"            [ "$(code -b $jar $B$p)" == 200 ]
done
check "audit row for sign-in"      bash -c "[ \"\$(runuser -u postgres -- psql -tA domainnotice -c \"select count(*) from \\\"AuditLog\\\" where \\\"Action\\\"='sign-in'\")\" -ge 1 ]"

check "agent port 5150 open"       bash -c "exec 3<>/dev/tcp/127.0.0.1/5150"
check "agent key owned by service" owner /opt/domainnotice/data/keys/agent-link.pfx "600 domainnotice"
check "dataprotection keys exist"  bash -c "ls /opt/domainnotice/data/keys/dataprotection/key-*.xml >/dev/null 2>&1"
check "server runs as domainnotice" bash -c "ps -o user= -C DomainNotice.Server | grep -qx domainnotice"
check "no errors in server log"    bash -c "! grep -E '^(fail|crit):' /tmp/server.log"

if ((fails)); then echo "--- server log"; tail -n 60 /tmp/server.log; fi
echo "# $fails failure(s)"
exit $((fails > 0))
