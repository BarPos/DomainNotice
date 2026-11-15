#!/usr/bin/env bash
# DEV/TEST ONLY. Exercises install.sh and update.sh in a throw-away ubuntu:24.04 Docker container.
# (Production never uses Docker; this only borrows a clean Ubuntu userland.)
#
# Containers have no systemd, so systemctl is replaced by a stub: PostgreSQL is started with pg_ctlcluster,
# "start domainnotice-update.service" runs /opt/domainnotice/bin/update.sh, and domainnotice.service is not run.
# The server's own steps that need the other areas (first local account, real health endpoint) are skipped.
#
# What it covers: apt package names, the system user, layout owners/modes, local PostgreSQL with role/database,
# appsettings.Production.json and update.env, the self-signed certificate, --migrate-only as the service user,
# units and update.sh installed root-owned, and update.sh end to end: rollback (health check fails, database
# restored, symlink switched back, result file) and success (health check passes, old releases pruned).
#
# Usage (on the host): deploy/linux/tests/container-smoke.sh ARTIFACTS_DIR
#   ARTIFACTS_DIR holds DomainNotice.Server-<version>-linux-<arch>.tar.gz for the Docker host's architecture.
set -Eeuo pipefail

if [[ "${1-}" != --inside ]]; then
    here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
    artifacts="$(cd "${1:?usage: $0 ARTIFACTS_DIR}" && pwd)"
    exec docker run --rm -v "$here:/src:ro" -v "$artifacts:/pkg:ro" ubuntu:24.04 bash /src/tests/container-smoke.sh --inside
fi

# ------------------------------------------------------------------------------------------------ inside
fails=0
check() { local d="$1"; shift; if "$@"; then echo "ok   - $d"; else echo "FAIL - $d"; fails=$((fails + 1)); fi; }
mode_owner() { [[ "$(stat -c '%a %U:%G' "$1")" == "$2" ]] || { echo "     $1 is $(stat -c '%a %U:%G' "$1"), expected $2"; return 1; }; }

case "$(uname -m)" in x86_64) arch=x64 ;; aarch64) arch=arm64 ;; esac
pkg="$(ls /pkg/DomainNotice.Server-*-linux-$arch.tar.gz | head -n1)"
version="$(basename "$pkg" | sed -E 's/^DomainNotice\.Server-([0-9.]+)-linux-.*/\1/')"
echo "# package $pkg ($version)"

cat >/usr/local/bin/systemctl <<'STUB'
#!/bin/bash
echo "systemctl $*" >>/tmp/systemctl.log
start_pg() { local v; v="$(ls /etc/postgresql | sort -V | tail -n1)"; pg_ctlcluster --skip-systemctl-redirect "$v" main start 2>/dev/null || true; }
case "$*" in
    *postgresql*) [[ "$1" == enable || "$1" == start || "$1" == restart ]] && start_pg ;;
    "start domainnotice-update.service") /opt/domainnotice/bin/update.sh ;;
esac
exit 0
STUB
chmod 755 /usr/local/bin/systemctl
# Debian's policy layer keeps services from starting during apt in containers; pg_ctlcluster starts it.

# --- install (unattended, local database) with the steps that need other areas stubbed out
# shellcheck source=../install.sh
source /src/install.sh
create_admin_account() { info "(smoke) skipping the first local account"; }
start_services() { info "(smoke) not starting domainnotice.service"; }
# shellcheck disable=SC2034 # read by the sourced install.sh functions
ADMIN_PASSWORD=smoke-test-pw UNATTENDED=1 MODE=install PACKAGE="$pkg" DB_MODE=local SERVER_NAME=dn.example.test CONSOLE_PORT=443 OPEN_FIREWALL=no
check_system
do_install >/tmp/install.log 2>&1 || { cat /tmp/install.log; echo "FAIL - install"; exit 1; }
tail -n 12 /tmp/install.log
set +e
trap - ERR

R=/opt/domainnotice
check "current -> releases/$version" [ "$(readlink $R/current)" == "$R/releases/$version" ]
check "release owned by root"        mode_owner "$R/releases/$version" "755 root:root"
check "no file in release not root"  [ -z "$(find $R/releases/$version ! -user root -print -quit)" ]
check "server executable"            [ -x "$R/releases/$version/DomainNotice.Server" ]
check "config dir"                   mode_owner "$R/config" "750 root:domainnotice"
check "appsettings 640"              mode_owner "$R/config/appsettings.Production.json" "640 root:domainnotice"
check "console.pem 640"              mode_owner "$R/config/console.pem" "640 root:domainnotice"
check "console.key 640"              mode_owner "$R/config/console.key" "640 root:domainnotice"
check "update.env 600 root"          mode_owner "$R/config/update.env" "600 root:root"
check "data 700"                     mode_owner "$R/data" "700 domainnotice:domainnotice"
check "data/updates 700"             mode_owner "$R/data/updates" "700 domainnotice:domainnotice"
check "data/keys/dataprotection"     mode_owner "$R/data/keys/dataprotection" "700 domainnotice:domainnotice"
check "backups 700"                  mode_owner "$R/backups" "700 domainnotice:domainnotice"
check "bin/update.sh root 755"       mode_owner "$R/bin/update.sh" "755 root:root"
for u in domainnotice.service domainnotice-update.path domainnotice-update.service; do
    check "unit $u installed"        mode_owner "/etc/systemd/system/$u" "644 root:root"
done
check "install.state written last"   mode_owner "$R/config/install.state" "600 root:root"
check "service user has no shell"    [ "$(getent passwd domainnotice | cut -d: -f7)" == /usr/sbin/nologin ]
check "self-signed cert has SAN"     bash -c "openssl x509 -in $R/config/console.pem -noout -ext subjectAltName | grep -q 'DNS:dn.example.test'"
check "cert matches key"             [ "$(openssl x509 -in $R/config/console.pem -noout -pubkey)" == "$(openssl pkey -in $R/config/console.key -pubout)" ]
apt-get install -y -q jq >/dev/null 2>&1
check "appsettings is valid JSON"    jq -e '.ConnectionStrings.Default and .Paths.Data == "/opt/domainnotice/data" and .Updates.GitHubRepository == "" and .Agents.Port == 5150' "$R/config/appsettings.Production.json" >/dev/null
check "summary shows msiexec line"   grep -q 'msiexec /i DomainNotice.Agent-.*-x64.msi SERVERHOST=dn.example.test SERVERPORT=5150 SERVERFINGERPRINT=' /tmp/install.log
pw="$(sed -n 's/^PGPASSWORD=//p' $R/config/update.env)"
check "local db: role owns database" [ "$(PGPASSWORD="$pw" psql -X -At -h 127.0.0.1 -U domainnotice -d domainnotice -c "select pg_get_userbyid(datdba) from pg_database where datname='domainnotice'")" == domainnotice ]
check "migrate-only ran (EF history table)" [ "$(PGPASSWORD="$pw" psql -X -At -h 127.0.0.1 -U domainnotice -d domainnotice -c "select to_regclass('public.\"__EFMigrationsHistory\"') is not null")" == t ]
check "db password not in install log" bash -c '! grep -qF -- "$1" /tmp/install.log /tmp/systemctl.log' _ "$pw"

# --- update.sh: stage a newer package the way the server does
stage() { # stage VERSION
    local v="$1" name="DomainNotice.Server-$1-linux-$arch.tar.gz" d="$R/data/updates/$1"
    install -d -o domainnotice -g domainnotice -m 700 "$d"
    install -o domainnotice -g domainnotice -m 600 "$pkg" "$d/$name"
    local sha; sha="$(sha256sum "$pkg" | cut -d' ' -f1)"
    printf '%s  %s\n' "$sha" "$name" >"$d/SHA256SUMS.txt"; chown domainnotice: "$d/SHA256SUMS.txt"
    printf 'format=1\nrun_id=%s\nversion=%s\nfrom_version=%s\narch=%s\nfile=%s\nsha256=%s\n' \
        "$(cat /proc/sys/kernel/random/uuid)" "$v" "$version" "$arch" "$name" "$sha" >"$R/data/updates/request.conf"
    chown domainnotice: "$R/data/updates/request.conf"
}
result() { sed -n "s/^$1=//p" "$R/data/updates/result.conf"; }

# Marker row to see the restore.
PGPASSWORD="$pw" psql -X -q -h 127.0.0.1 -U domainnotice -d domainnotice -c "create table smoke_marker(v text); insert into smoke_marker values ('before')"

# 1) Rollback: nothing answers on /healthz in the container, so update.sh must roll back.
sed -i 's/^HEALTH_TIMEOUT_SECONDS=.*/HEALTH_TIMEOUT_SECONDS=6/' "$R/bin/update.sh"
stage 9.0.1
"$R/bin/update.sh" >/tmp/update1.log 2>&1
check "rollback: exit status non-zero"  [ $? -ne 0 ]
check "rollback: request consumed"      [ ! -e "$R/data/updates/request.conf" ]
check "rollback: result RolledBack"     [ "$(result state)" == RolledBack ]
check "rollback: current switched back" [ "$(readlink $R/current)" == "$R/releases/$version" ]
check "rollback: backup written"        [ -n "$(ls $R/backups/*.dump 2>/dev/null)" ]
check "rollback: backup root 600"       mode_owner "$(ls $R/backups/*.dump | head -n1)" "600 root:root"
check "rollback: database restored"     [ "$(PGPASSWORD="$pw" psql -X -At -h 127.0.0.1 -U domainnotice -d domainnotice -c 'select v from smoke_marker')" == before ]
check "rollback: result readable by service" mode_owner "$R/data/updates/result.conf" "600 domainnotice:domainnotice"
check "rollback: new release unpacked as root" mode_owner "$R/releases/9.0.1" "755 root:root"
grep -E 'rolling back|result' /tmp/update1.log | sed 's/^/     /'

# 2) Refusals: not newer, and a symlinked request.
stage 0.0.1
"$R/bin/update.sh" >/tmp/update2.log 2>&1
check "older version: result Failed"    [ "$(result state)" == Failed ]
check "older version: reason"           grep -q 'not newer' /tmp/update2.log
check "older version: current unchanged" [ "$(readlink $R/current)" == "$R/releases/$version" ]
ln -s /etc/shadow "$R/data/updates/request.conf"
"$R/bin/update.sh" >/tmp/update3.log 2>&1
check "symlinked request refused"       grep -q 'not a regular file' /tmp/update3.log
check "symlinked request removed"       [ ! -L "$R/data/updates/request.conf" ]
check "/etc/shadow untouched"           mode_owner /etc/shadow "640 root:shadow"

# 3) Success: point the health check at a file that reports the new version.
sed -i 's|^HEALTH_URL=.*|HEALTH_URL=file:///tmp/healthz.json|' "$R/config/update.env"
printf '{"status":"ok","version":"9.0.2"}' >/tmp/healthz.json
for v in 9.0.0 8.0.0; do mkdir -p "$R/releases/$v"; done   # old releases to prune
stage 9.0.2
"$R/bin/update.sh" >/tmp/update4.log 2>&1
check "success: exit status 0"          [ $? -eq 0 ]
check "success: result Succeeded"       [ "$(result state)" == Succeeded ]
check "success: current -> 9.0.2"       [ "$(readlink $R/current)" == "$R/releases/9.0.2" ]
check "success: keeps 3 newest + previous" [ "$(ls $R/releases | sort -V | tr '\n' ' ')" == "$version 9.0.0 9.0.1 9.0.2 " ]
grep -E 'result|removing' /tmp/update4.log | sed 's/^/     /'
check "success: work folder removed"    [ -z "$(find "$R/releases" -maxdepth 1 -name '.work.*' -print -quit)" ]

# 4) install.sh --upgrade uses the same path (staging + update unit); a downgrade is refused.
out="$(bash /src/install.sh --unattended --upgrade --package "$pkg" 2>&1)"
check "install.sh --upgrade refuses a downgrade" grep -q 'is not newer' <<<"$out"

# 5) --repair keeps config and data, fixes modes, re-installs units and update.sh.
cfg_sum="$(sha256sum "$R/config/appsettings.Production.json")"
chmod 644 "$R/config/appsettings.Production.json"
chmod 755 "$R/data"
rm -f "$R/bin/update.sh"
bash /src/install.sh --unattended --repair >/tmp/repair.log 2>&1
check "repair: exit 0"                  [ $? -eq 0 ]
check "repair: config unchanged"        [ "$(sha256sum "$R/config/appsettings.Production.json")" == "$cfg_sum" ]
check "repair: config mode restored"    mode_owner "$R/config/appsettings.Production.json" "640 root:domainnotice"
check "repair: data mode restored"      mode_owner "$R/data" "700 domainnotice:domainnotice"
check "repair: update.sh restored"      mode_owner "$R/bin/update.sh" "755 root:root"

# 6) --uninstall keeps config, data and the database; --purge removes everything after confirmation.
bash /src/install.sh --uninstall >/tmp/uninstall.log 2>&1
check "uninstall: releases removed"     [ ! -e "$R/releases" ]
check "uninstall: current removed"      [ ! -L "$R/current" ]
check "uninstall: units removed"        [ ! -e /etc/systemd/system/domainnotice.service ]
check "uninstall: config kept"          [ -f "$R/config/appsettings.Production.json" ]
check "uninstall: data kept"            [ -d "$R/data/keys" ]
check "uninstall: database kept"        [ "$(runuser -u postgres -- psql -X -At -c "select count(*) from pg_database where datname='domainnotice'")" == 1 ]
bash /src/install.sh --purge --unattended >/tmp/purge1.log 2>&1
check "purge: unattended needs --yes"   [ -d "$R/config" ]
bash /src/install.sh --purge --unattended --yes >/tmp/purge2.log 2>&1
check "purge: folder removed"           [ ! -e "$R" ]
check "purge: user removed"             bash -c '! id -u domainnotice >/dev/null 2>&1'
check "purge: local database dropped"   [ "$(runuser -u postgres -- psql -X -At -c "select count(*) from pg_database where datname='domainnotice'")" == 0 ]

# 7) Unattended install with a missing input stops before changing the machine.
bash /src/install.sh --unattended --package "$pkg" >/tmp/missing.log 2>&1
check "missing --db: refused"           grep -q 'missing or invalid --db' /tmp/missing.log
check "missing --db: nothing created"   [ ! -e "$R" ]

echo "# $fails failure(s)"
[[ $fails -eq 0 ]]
