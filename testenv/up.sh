#!/usr/bin/env bash
# DEV/TEST ONLY. Builds (if needed) and starts the DomainNotice test environment, installs the server with
# install.sh inside the Ubuntu container, wires it to the Samba AD DC and prints what the Windows agent needs.
#
#   testenv/up.sh [--host-ip IP] [--rebuild]
#
# --host-ip   address the Windows PC uses to reach this machine (default: this Mac's en0 address)
# --rebuild   rebuild the server tarball (build/build-server.sh) even if one exists
set -Eeuo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"
ROOT="$(cd .. && pwd)"
HOST_IP="$(ipconfig getifaddr en0 2>/dev/null || hostname -I 2>/dev/null | awk '{print $1}' || true)"
REBUILD=0
while (($#)); do
    case "$1" in
        --host-ip) HOST_IP="$2"; shift 2 ;;
        --rebuild) REBUILD=1; shift ;;
        *) echo "unknown option $1" >&2; exit 2 ;;
    esac
done
[[ -n "$HOST_IP" ]] || { echo "Cannot find this machine's LAN address; pass --host-ip" >&2; exit 2; }
step() { printf '\n==> %s\n' "$*"; }
rand() { openssl rand -base64 24 | tr -d '/+=' | cut -c1-20; }

# ---------------------------------------------------------------- secrets (kept in testenv/.env, git-ignored)
if [[ ! -f .env ]]; then
    umask 077
    cat >.env <<ENV
DC_ADMIN_PASSWORD=Dc-$(rand)
USER_PASSWORD=User-$(rand)
BIND_PASSWORD=Bind-$(rand)
CONSOLE_ADMIN_PASSWORD=Admin-$(rand)
INSTALL_PASSWORD=Enrol-$(rand)
ENV
fi
set -a; source .env; set +a

# ---------------------------------------------------------------- server package
case "$(uname -m)" in arm64|aarch64) ARCH=arm64 ;; *) ARCH=x64 ;; esac
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -n1)"
PKG="DomainNotice.Server-$VERSION-linux-$ARCH.tar.gz"
if ((REBUILD)) || [[ ! -f "$ROOT/artifacts/$PKG" ]]; then
    step "Building $PKG"
    "$ROOT/build/build-server.sh" --arch "$ARCH"
fi

# ---------------------------------------------------------------- Console certificate (SAN: names + host IP)
mkdir -p certs
if [[ ! -f certs/console.pem ]] || ! openssl x509 -in certs/console.pem -noout -ext subjectAltName 2>/dev/null | grep -q "IP Address:$HOST_IP"; then
    step "Creating the Console certificate for server.corp.test and $HOST_IP"
    openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -days 825 \
        -keyout certs/console.key -out certs/console.pem -subj "/CN=server.corp.test" \
        -addext "subjectAltName=DNS:server.corp.test,DNS:server,DNS:localhost,IP:$HOST_IP,IP:127.0.0.1" \
        -addext "extendedKeyUsage=serverAuth" 2>/dev/null
    chmod 644 certs/console.pem certs/console.key
fi

# ---------------------------------------------------------------- containers
step "Starting the containers"
docker compose up -d

step "Waiting for the domain controller"
for i in $(seq 1 90); do
    if docker exec dn-dc ldapsearch -LLL -x -H ldaps://localhost -o tls_reqcert=never \
        -D "svc-domainnotice@corp.test" -w "$BIND_PASSWORD" -b "DC=corp,DC=test" "(sAMAccountName=alice)" dn >/dev/null 2>&1; then
        echo "domain controller ready"; break
    fi
    ((i == 90)) && { docker logs --tail 40 dn-dc; echo "DC did not come up" >&2; exit 1; }
    sleep 2
done

step "Waiting for systemd in the server container"
for i in $(seq 1 30); do
    state="$(docker exec dn-server systemctl is-system-running 2>/dev/null || true)"
    [[ "$state" == running || "$state" == degraded ]] && break
    sleep 1
done
echo "systemd: $state"

# ---------------------------------------------------------------- install (real install.sh, unattended)
if docker exec dn-server test -f /opt/domainnotice/config/install.state; then
    step "DomainNotice is already installed (use ./reset.sh for a clean install)"
else
    step "Installing DomainNotice with install.sh (local PostgreSQL)"
    docker exec -e DOMAINNOTICE_ADMIN_PASSWORD="$CONSOLE_ADMIN_PASSWORD" dn-server bash -c "
        set -e
        mkdir -p /root/pkg && cp /pkg/$PKG /root/pkg/
        [ -f /pkg/SHA256SUMS.txt ] && cp /pkg/SHA256SUMS.txt /root/pkg/ || true
        tar -xzf /root/pkg/$PKG -C /root/pkg ./deploy/linux/install.sh
        bash /root/pkg/deploy/linux/install.sh --unattended --db=local --package /root/pkg/$PKG \
            --server-name server.corp.test --console-port 443 --cert /certs/console.pem --cert-key /certs/console.key \
            --admin-user admin --open-firewall=no
    " | tee /tmp/dn-install.log | tail -n 25
fi

# ---------------------------------------------------------------- test configuration: directory + install password
step "Configuring the directory (Samba AD) and the install password"
docker exec dn-dc cat /var/lib/samba/private/tls/ca.pem >/tmp/dn-dc-ca.pem
docker cp /tmp/dn-dc-ca.pem dn-server:/opt/domainnotice/config/dc-ca.pem
ENROL_HASH="$(python3 - "$INSTALL_PASSWORD" <<'PY'
import base64, hashlib, os, sys
salt = os.urandom(16); it = 210000
h = hashlib.pbkdf2_hmac("sha256", sys.argv[1].encode(), salt, it, 32)
print(f"pbkdf2-sha256${it}${base64.b64encode(salt).decode()}${base64.b64encode(h).decode()}")
PY
)"
CFG_TMP="$(mktemp)"
docker cp dn-server:/opt/domainnotice/config/appsettings.Production.json "$CFG_TMP"
python3 - "$CFG_TMP" "$ENROL_HASH" <<'PY'
import json, sys
p = sys.argv[1]
c = json.load(open(p))
c.setdefault("Enrolment", {}).update({"RequirePassword": True, "PasswordHash": sys.argv[2]})
c["Directory"] = {
    "Hosts": ["dc.corp.test"], "Port": 636, "Security": "Ldaps",
    "TrustedCertificate": "/opt/domainnotice/config/dc-ca.pem", "AcceptInvalidCertificate": False,
    "BaseDn": "DC=corp,DC=test", "BindUser": "svc-domainnotice@corp.test",
    "BindPasswordSource": "Environment:DOMAINNOTICE_BIND_PASSWORD",
    "AdminGroups": ["DomainNotice Admins"], "ViewerGroups": ["DomainNotice Viewers"],
    "FullSyncCron": "0 2 * * *", "IncrementalSyncMinutes": 1,
    "KerberosServicePrincipal": "DomainNotice/server.corp.test",
}
json.dump(c, open(p, "w"), indent=2)
PY
docker cp "$CFG_TMP" dn-server:/opt/domainnotice/config/appsettings.Production.json
rm -f "$CFG_TMP"
docker exec dn-server bash -c "
    chown root:domainnotice /opt/domainnotice/config/dc-ca.pem /opt/domainnotice/config/appsettings.Production.json
    chmod 640 /opt/domainnotice/config/dc-ca.pem /opt/domainnotice/config/appsettings.Production.json
    mkdir -p /etc/systemd/system/domainnotice.service.d
    umask 077
    printf '[Service]\nEnvironment=DOMAINNOTICE_BIND_PASSWORD=%s\n' '$BIND_PASSWORD' > /etc/systemd/system/domainnotice.service.d/testenv.conf
    systemctl daemon-reload && systemctl restart domainnotice.service"

step "Waiting for the server"
for i in $(seq 1 60); do
    docker exec dn-server curl -fsk --max-time 3 https://127.0.0.1/healthz >/dev/null 2>&1 && break
    ((i == 60)) && { docker exec dn-server journalctl -u domainnotice --no-pager -n 60; exit 1; }
    sleep 2
done
FP="$(docker exec dn-server bash -c "cd /opt/domainnotice/current && runuser -u domainnotice -- env DOMAINNOTICE_CONFIG=/opt/domainnotice/config/appsettings.Production.json ASPNETCORE_ENVIRONMENT=Production ./DomainNotice.Server --print-fingerprint 2>/dev/null" | grep -E '^[0-9a-f]{64}$' | tail -n1)"

sleep 3
SYNC="$(docker exec dn-server journalctl -u domainnotice --no-pager -o cat | grep -E 'Directory (Full|Incremental) sync|sync failed' | tail -n 2 || true)"

cat <<SUMMARY | tee connection.txt

================================================================================ DomainNotice test environment
 Console      https://$HOST_IP:8443        (self-signed certificate: accept the warning)
              local account:   admin / $CONSOLE_ADMIN_PASSWORD
              domain accounts: dnadmin (Admin, via nested group), dnviewer (Viewer) / $USER_PASSWORD
 Domain       CORP.TEST (Samba AD DC dc.corp.test), users alice bob carol dave / $USER_PASSWORD
 Agent port   $HOST_IP:5150
 Fingerprint  $FP

 Windows agent (as Administrator, after building the MSI with build\\build-agent-msi.ps1):
   msiexec /i DomainNotice.Agent-$VERSION-x64.msi /qn /l*v install.log SERVERHOST=$HOST_IP SERVERPORT=5150 SERVERFINGERPRINT=$FP INSTALLPASSWORD=$INSTALL_PASSWORD

 Secrets are in testenv/.env.  Logs: docker exec dn-server journalctl -u domainnotice -f
 Directory sync: ${SYNC:-see the Directory page in the Console}
================================================================================
SUMMARY
