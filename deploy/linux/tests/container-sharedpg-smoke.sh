#!/usr/bin/env bash
# DEV/TEST ONLY. install.sh --db=local on a machine whose PostgreSQL already serves another application, in a
# throw-away ubuntu:24.04 Docker container (production never uses Docker). systemctl is stubbed as in
# container-run-smoke.sh.
#
# Before the install the machine has: PostgreSQL 16 (cluster "main" on 5432 and a second cluster on 5433), another
# application's database "otherapp" (role "otherapp"), and a database AND role named "domainnotice" that belong to that
# other application. DomainNotice must add its own role and database (domainnotice_2) and never install, upgrade,
# restart or reconfigure PostgreSQL, nor change anything of the other application, through install, backup, restore
# and uninstall. An explicit --db-name that is taken is refused, and a failed install removes only what it created.
#
# Usage (on the host): deploy/linux/tests/container-sharedpg-smoke.sh ARTIFACTS_DIR
set -Eeuo pipefail

if [[ "${1-}" != --inside ]]; then
    here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
    artifacts="$(cd "${1:?usage: $0 ARTIFACTS_DIR}" && pwd)"
    exec docker run --rm -v "$here:/src:ro" -v "$artifacts:/pkg:ro" ubuntu:24.04 bash /src/tests/container-sharedpg-smoke.sh --inside
fi

fails=0
check() { local d="$1"; shift; if "$@"; then echo "ok   - $d"; else echo "FAIL - $d"; fails=$((fails + 1)); fi; }

case "$(uname -m)" in x86_64) arch=x64 ;; aarch64) arch=arm64 ;; esac
pkg="$(ls /pkg/DomainNotice.Server-*-linux-$arch.tar.gz | sort -V | tail -n1)"
echo "# package $pkg"

# ---- the machine before DomainNotice: PostgreSQL in use by another application
export DEBIAN_FRONTEND=noninteractive
apt-get update -q >/dev/null
apt-get install -y -q --no-install-recommends postgresql curl ca-certificates iproute2 >/dev/null
v="$(ls /etc/postgresql | sort -V | tail -n1)"
pg_ctlcluster "$v" main start
pg_createcluster "$v" second --port 5433 --start >/dev/null
psql_pg() { runuser -u postgres -- psql -X -At -q -v ON_ERROR_STOP=1 "$@"; }
psql_pg -p 5432 -d postgres <<'SQL'
CREATE ROLE otherapp LOGIN PASSWORD 'OtherAppPass1';
CREATE DATABASE otherapp OWNER otherapp;
CREATE ROLE domainnotice LOGIN PASSWORD 'TheirOwnPass1';
CREATE DATABASE domainnotice OWNER domainnotice;
SQL
psql_pg -p 5432 -d otherapp -c "CREATE TABLE things(x int); INSERT INTO things VALUES (42); ALTER TABLE things OWNER TO otherapp;"
psql_pg -p 5432 -d domainnotice -c "CREATE TABLE theirs(x int); INSERT INTO theirs VALUES (7); ALTER TABLE theirs OWNER TO domainnotice;"
psql_pg -p 5433 -d postgres -c "CREATE DATABASE secondapp;"
dpkg-query -W 'postgresql*' >/tmp/pkgs.before
pg_lsclusters --no-header | awk '{print $1, $2, $3, $4}' >/tmp/clusters.before

intact() { # the other application's data, roles and passwords, PostgreSQL packages and clusters are unchanged
    [[ "$(psql_pg -p 5432 -d otherapp -c 'SELECT x FROM things')" == 42 ]] || { echo "     otherapp.things changed"; return 1; }
    [[ "$(psql_pg -p 5432 -d domainnotice -c 'SELECT x FROM theirs')" == 7 ]] || { echo "     domainnotice.theirs changed"; return 1; }
    [[ "$(psql_pg -p 5432 -d postgres -c "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = 'domainnotice'")" == domainnotice ]] ||
        { echo "     owner of database domainnotice changed"; return 1; }
    [[ "$(PGPASSWORD=TheirOwnPass1 psql -X -At -h 127.0.0.1 -p 5432 -U domainnotice -d domainnotice -c 'SELECT 1')" == 1 ]] ||
        { echo "     the password of role domainnotice changed"; return 1; }
    [[ "$(PGPASSWORD=OtherAppPass1 psql -X -At -h 127.0.0.1 -p 5432 -U otherapp -d otherapp -c 'SELECT 1')" == 1 ]] ||
        { echo "     role otherapp cannot sign in"; return 1; }
    [[ "$(psql_pg -p 5433 -d postgres -c "SELECT count(*) FROM pg_database WHERE datname = 'secondapp'")" == 1 ]] ||
        { echo "     second cluster changed"; return 1; }
    dpkg-query -W "postgresql*" | cmp -s - /tmp/pkgs.before || { echo "     PostgreSQL packages changed:"; dpkg-query -W "postgresql*" | diff /tmp/pkgs.before - | sed "s/^/       /"; return 1; }
    pg_lsclusters --no-header | awk '{print $1, $2, $3, $4}' | cmp -s - /tmp/clusters.before || { echo "     clusters changed"; return 1; }
}
check "before: other application set up" intact

cat >/usr/local/bin/systemctl <<'STUB'
#!/bin/bash
echo "systemctl $*" >>/tmp/systemctl.log
case "$*" in
    "restart domainnotice.service"|"start domainnotice.service")
        pkill -f /opt/domainnotice/current/DomainNotice.Server || true
        cd /opt/domainnotice/current && setsid runuser -u domainnotice -- env -i \
            PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin HOME=/nonexistent LANG=C.UTF-8 \
            DOMAINNOTICE_CONFIG=/opt/domainnotice/config/appsettings.Production.json ASPNETCORE_ENVIRONMENT=Production \
            DOTNET_NOLOGO=1 /opt/domainnotice/current/DomainNotice.Server >/tmp/server.log 2>&1 </dev/null &
        ;;
    "stop domainnotice"*|*"stop domainnotice.service"*) pkill -f /opt/domainnotice/current/DomainNotice.Server || true ;;
esac
exit 0
STUB
chmod 755 /usr/local/bin/systemctl

export DOMAINNOTICE_ADMIN_PASSWORD='Smoke-Test-Passw0rd'
common=(--unattended --db=local --package "$pkg" --admin-user admin --console-port 8443 --server-name dn.example.test --open-firewall no)
set +e
trap - ERR

# ---- an explicit name that is taken is refused, and nothing is left behind
bash /src/install.sh "${common[@]}" --db-name domainnotice >/tmp/install-taken.log 2>&1
check "taken --db-name refused"          [ $? -ne 0 ]
check "  with a clear message"           grep -q "already exists" /tmp/install-taken.log
check "  nothing installed"              [ ! -e /opt/domainnotice/config/install.state ]
check "  no role or database created"    [ "$(psql_pg -p 5432 -d postgres -c "SELECT count(*) FROM pg_database WHERE datname LIKE 'domainnotice_%'")" == 0 ]
check "  other application intact"       intact
rm -rf /opt/domainnotice; userdel domainnotice 2>/dev/null

# ---- the install
: >/tmp/systemctl.log
bash /src/install.sh "${common[@]}" >/tmp/install.log 2>&1
rc=$?
check "install succeeded"                [ $rc -eq 0 ]
((rc == 0)) || tail -n 30 /tmp/install.log
check "existing PostgreSQL was shared"   grep -q "PostgreSQL is already installed" /tmp/install.log
check "PostgreSQL never (re)started"     bash -c "! grep -q postgresql /tmp/systemctl.log"
check "own name domainnotice_2"          grep -qx "DB_NAME=domainnotice_2" /opt/domainnotice/config/install.state
check "role domainnotice_2"              grep -qx "DB_USER=domainnotice_2" /opt/domainnotice/config/install.state
check "cluster on 5432 chosen"           grep -qx "DB_PORT=5432" /opt/domainnotice/config/install.state
check "role is not privileged"           [ "$(psql_pg -p 5432 -d postgres -c "SELECT rolsuper OR rolcreatedb OR rolcreaterole FROM pg_roles WHERE rolname = 'domainnotice_2'")" == f ]
check "others cannot connect to it"      bash -c "! PGPASSWORD=OtherAppPass1 psql -X -At -h 127.0.0.1 -p 5432 -U otherapp -d domainnotice_2 -c 'SELECT 1' >/dev/null 2>&1"
check "other application intact"         intact

B=https://127.0.0.1:8443
for _ in $(seq 1 40); do curl -fsk --max-time 3 $B/healthz >/dev/null 2>&1 && break; sleep 2; done
check "healthz ok"                       bash -c "curl -fsk $B/healthz | grep -q '\"status\":\"ok\"'"
check "migrated into domainnotice_2"     [ "$(psql_pg -p 5432 -d domainnotice_2 -c "SELECT count(*) FROM pg_tables WHERE tablename = 'AuditLog'")" == 1 ]
check "nothing written to theirs"        [ "$(psql_pg -p 5432 -d domainnotice -c "SELECT count(*) FROM pg_tables WHERE schemaname = 'public'")" == 1 ]

# ---- backup and restore use only domainnotice_2
bash /opt/domainnotice/bin/backup.sh create >/tmp/backup.log 2>&1
check "backup created"                   grep -q "Backup written" /tmp/backup.log
file="$(ls -t /opt/domainnotice/backups/*.tar.gz | head -n1)"
check "backup is of domainnotice_2"      bash -c "tar -xzOf '$file' manifest.txt | grep -qx database=domainnotice_2"
psql_pg -p 5432 -d otherapp -c "INSERT INTO things VALUES (43); DELETE FROM things WHERE x = 43;"
bash /src/install.sh --restore "$file" --unattended --yes >/tmp/restore.log 2>&1
check "restore succeeded"                [ $? -eq 0 ]
check "state still domainnotice_2"       grep -qx "DB_NAME=domainnotice_2" /opt/domainnotice/config/install.state
check "  and port and creation flag"     bash -c "grep -qx DB_PORT=5432 /opt/domainnotice/config/install.state && grep -qx DB_CREATED=1 /opt/domainnotice/config/install.state"
check "other application intact"         intact

# ---- uninstall drops only domainnotice_2
bash /src/install.sh --uninstall --unattended --yes >/tmp/uninstall.log 2>&1
check "uninstall succeeded"              [ $? -eq 0 ]
check "database domainnotice_2 dropped"  [ "$(psql_pg -p 5432 -d postgres -c "SELECT count(*) FROM pg_database WHERE datname = 'domainnotice_2'")" == 0 ]
check "role domainnotice_2 dropped"      [ "$(psql_pg -p 5432 -d postgres -c "SELECT count(*) FROM pg_roles WHERE rolname = 'domainnotice_2'")" == 0 ]
check "other application intact"         intact
check "PostgreSQL never (re)started"     bash -c "! grep -q postgresql /tmp/systemctl.log"

# ---- a failed install removes only what it created
: >/tmp/systemctl.log
# Fail after the database step: setup_certificate (after setup_database) refuses missing certificate files.
bash /src/install.sh "${common[@]}" --cert /nonexistent.pem --cert-key /nonexistent.key >/tmp/install-fail.log 2>&1
check "failing install failed"           [ $? -ne 0 ]
check "  after creating its database"    grep -q "Creating the role 'domainnotice_2'" /tmp/install-fail.log
check "  its new database was removed"   [ "$(psql_pg -p 5432 -d postgres -c "SELECT count(*) FROM pg_database WHERE datname LIKE 'domainnotice_%'")" == 0 ]
check "  its new role was removed"       [ "$(psql_pg -p 5432 -d postgres -c "SELECT count(*) FROM pg_roles WHERE rolname LIKE 'domainnotice_%'")" == 0 ]
check "  other application intact"       intact

if ((fails)); then echo "--- install log"; tail -n 30 /tmp/install.log; echo "--- uninstall log"; tail -n 20 /tmp/uninstall.log; fi
echo "# $fails failure(s)"
exit $((fails > 0))
