#!/usr/bin/env bash
# DEV/TEST ONLY. In a throw-away ubuntu:24.04 Docker container (production never uses Docker), with the real server
# tarball: install with --console-mode=http, sign in over plain HTTP, then backup, restore, --reset-password and a
# full --uninstall with a backup first. systemctl is stubbed (no systemd in containers): starting
# domainnotice.service runs the server as the domainnotice user with the unit's environment.
#
# Usage (on the host): deploy/linux/tests/container-maint-smoke.sh ARTIFACTS_DIR
set -Eeuo pipefail

if [[ "${1-}" != --inside ]]; then
    here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
    artifacts="$(cd "${1:?usage: $0 ARTIFACTS_DIR}" && pwd)"
    exec docker run --rm -v "$here:/src:ro" -v "$artifacts:/pkg:ro" ubuntu:24.04 bash /src/tests/container-maint-smoke.sh --inside
fi

fails=0
check() { local d="$1"; shift; if "$@"; then echo "ok   - $d"; else echo "FAIL - $d"; fails=$((fails + 1)); fi; }

case "$(uname -m)" in x86_64) arch=x64 ;; aarch64) arch=arm64 ;; esac
pkg="$(ls /pkg/DomainNotice.Server-*-linux-$arch.tar.gz | sort -V | tail -n1)"
echo "# package $pkg"

cat >/usr/local/bin/systemctl <<'STUB'
#!/bin/bash
echo "systemctl $*" >>/tmp/systemctl.log
verb="$1"; shift
start_pg() { local v; v="$(ls /etc/postgresql 2>/dev/null | sort -V | tail -n1)"; [[ -n "$v" ]] && pg_ctlcluster --skip-systemctl-redirect "$v" main start 2>/dev/null || true; }
start_dn() {
    pkill -f /opt/domainnotice/current/DomainNotice.Server || true
    sleep 1
    [[ -x /opt/domainnotice/current/DomainNotice.Server ]] || return 0
    cd /opt/domainnotice/current && setsid runuser -u domainnotice -- env -i \
        PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin HOME=/nonexistent LANG=C.UTF-8 \
        DOMAINNOTICE_CONFIG=/opt/domainnotice/config/appsettings.Production.json ASPNETCORE_ENVIRONMENT=Production \
        DOTNET_NOLOGO=1 /opt/domainnotice/current/DomainNotice.Server >>/tmp/server.log 2>&1 </dev/null &
}
for unit in "$@"; do
    case "$unit" in
        --now) [[ "$verb" == disable ]] && pkill -f /opt/domainnotice/current/DomainNotice.Server || true ;;
        *postgresql*) [[ "$verb" =~ ^(enable|start|restart)$ ]] && start_pg ;;
        domainnotice.service)
            case "$verb" in start|restart) start_dn ;; stop) pkill -f /opt/domainnotice/current/DomainNotice.Server || true ;; esac ;;
    esac
done
exit 0
STUB
chmod 755 /usr/local/bin/systemctl

export DOMAINNOTICE_ADMIN_PASSWORD='First-Passw0rd-123'
bash /src/install.sh --unattended --db=local --package "$pkg" --console-mode=http --console-port 8080 \
    --server-name dn.example.test --admin-user admin --open-firewall=no >/tmp/install.log 2>&1 ||
    { tail -n 40 /tmp/install.log; echo "FAIL - install"; exit 1; }
set +e
trap - ERR
R=/opt/domainnotice
B=http://127.0.0.1:8080
wait_up() { for _ in $(seq 1 60); do curl -fs --max-time 3 "$B/healthz" >/dev/null 2>&1 && return 0; sleep 2; done; return 1; }
psql_dn() { runuser -u postgres -- psql -X -At domainnotice -c "$1"; }
sign_in() { # sign_in USER PASSWORD -> prints the redirect target
    local jar; jar="$(mktemp)"
    local tok
    tok="$(curl -s -c "$jar" "$B/account/login" | grep -o 'name="__RequestVerificationToken" value="[^"]*"' | sed 's/.*value="//;s/"$//')"
    curl -s -b "$jar" -c "$jar" -o /dev/null -w '%{redirect_url}' --data-urlencode "__RequestVerificationToken=$tok" \
        --data-urlencode "source=local" --data-urlencode "username=$1" --data-urlencode "password=$2" "$B/account/login"
}

# ---- HTTP mode
check "http: server up on plain HTTP"       wait_up
check "http: Console:Urls is http"          grep -q '"Urls": "http://0.0.0.0:8080"' $R/config/appsettings.Production.json
check "http: no certificate created"        [ ! -e $R/config/console.pem ]
check "http: health URL for updates is http" grep -q '^HEALTH_URL=http://127.0.0.1:8080/healthz$' $R/config/update.env
check "http: install.state has the mode"    grep -q '^CONSOLE_MODE=http$' $R/config/install.state
check "http: sign-in over plain HTTP works" [ "$(sign_in admin "$DOMAINNOTICE_ADMIN_PASSWORD")" == "$B/" ]
check "http: summary shows http URL"        grep -q 'Console: *http://dn.example.test:8080/' /tmp/install.log
check "backup.sh installed root-owned"      bash -c "[ \"\$(stat -c '%a %U' $R/bin/backup.sh)\" == '755 root' ]"
check "package has this backup.sh (rebuild it otherwise)" cmp -s /src/backup.sh $R/bin/backup.sh
check "package has this update.sh"          cmp -s /src/update.sh $R/bin/update.sh
check "backups folder is root-only"         bash -c "[ \"\$(stat -c '%a %U:%G' $R/backups)\" == '700 root:root' ]"

# ---- backup into the default folder after the service user planted links there (install from before 1.0.5,
#      when backups/ was the service user's): root must not write, chown or chmod through them.
echo victim >/etc/dn-victim; chmod 644 /etc/dn-victim
chown domainnotice:domainnotice $R/backups
now=$(date -u +%s)
for i in $(seq 0 90); do
    name="domainnotice-backup-$(basename "$(readlink -f $R/current)")-$(hostname -s)-$(date -u -d "@$((now + i))" +%Y%m%dT%H%M%SZ).tar.gz"
    runuser -u domainnotice -- ln -s /etc/dn-victim "$R/backups/$name.partial"
done
bash /src/install.sh --backup >/tmp/backup0.log 2>&1
check "planted links: backup written"       bash -c "ls $R/backups/domainnotice-backup-*.tar.gz >/dev/null 2>&1 && [ -f \"\$(ls $R/backups/domainnotice-backup-*.tar.gz | head -n1)\" ]"
check "planted links: target untouched"     bash -c "[ \"\$(cat /etc/dn-victim)\" == victim ] && [ \"\$(stat -c '%a %U' /etc/dn-victim)\" == '644 root' ]"
check "planted links: still links"          bash -c "[ -L \"\$(ls $R/backups/*.partial | head -n1)\" ]"
check "planted links: backups taken over"   bash -c "[ \"\$(stat -c '%a %U:%G' $R/backups)\" == '700 root:root' ]"
mkdir -p /srv/dn-shared && chown domainnotice /srv/dn-shared
check "backup refused into a folder another user owns" bash -c "! bash /src/install.sh --backup --output /srv/dn-shared >/dev/null 2>&1 && [ -z \"\$(ls -A /srv/dn-shared)\" ]"
check "backup refused into a world-writable folder" bash -c "! bash /src/install.sh --backup --output /tmp >/dev/null 2>&1"
check "backup refused onto a link"           bash -c "ln -sfn /etc /root/dn-link && ! bash /src/install.sh --backup --output /root/dn-link >/dev/null 2>&1"

# ---- backup
psql_dn "insert into \"AuditLog\" (\"AtUtc\",\"Actor\",\"Action\") values (now(),'smoke','marker-before-backup')" >/dev/null
fp_before="$(sha256sum $R/data/keys/agent-link.pfx | cut -c1-64)"
bash /src/install.sh --backup --output /root/backups >/tmp/backup.log 2>&1
file="$(ls /root/backups/domainnotice-backup-*.tar.gz 2>/dev/null | head -n1)"
check "backup: archive written"             [ -n "$file" ]
[ -n "$file" ] || sed 's/^/     /' /tmp/backup.log
grep -h 'changed while\|ERROR' /tmp/backup0.log /tmp/backup.log | sed 's/^/     /'
check "backup: mode 600, owned by root"     bash -c "[ \"\$(stat -c '%a %U' '$file')\" == '600 root' ]"
check "backup: has manifest, db dump, files" bash -c "tar -tzf '$file' | sort | tr '\n' ' ' | grep -q 'database.dump files.tar manifest.txt'"
check "backup: files include key and program" bash -c "tar -xzOf '$file' files.tar | tar -t | grep -q 'opt/domainnotice/data/keys/agent-link.pfx' && tar -xzOf '$file' files.tar | tar -t | grep -q 'opt/domainnotice/releases/.*/DomainNotice.Server$'"

# ---- damage, then restore
psql_dn "delete from \"AuditLog\" where \"Action\"='marker-before-backup'" >/dev/null
rm -f $R/data/keys/agent-link.pfx
sed -i 's/"RetentionDays": 30/"RetentionDays": 99/' $R/config/appsettings.Production.json
bash /src/install.sh --restore "$file" --unattended --yes >/tmp/restore.log 2>&1
check "restore: finished"                   grep -q 'Restored' /tmp/restore.log
check "restore: database row is back"       [ "$(psql_dn "select count(*) from \"AuditLog\" where \"Action\"='marker-before-backup'")" == 1 ]
check "restore: agent-link key is back"     [ "$(sha256sum $R/data/keys/agent-link.pfx 2>/dev/null | cut -c1-64)" == "$fp_before" ]
check "restore: configuration is back"      grep -q '"RetentionDays": 30' $R/config/appsettings.Production.json
check "restore: key owned by service user"  bash -c "[ \"\$(stat -c '%a %U' $R/data/keys/agent-link.pfx)\" == '600 domainnotice' ]"
check "restore: safety backup was made"     bash -c "ls $R/backups/domainnotice-backup-*.tar.gz >/dev/null 2>&1"
check "restore: server answers again"       wait_up
check "restore: unattended needs --yes"     bash -c "! bash /src/install.sh --restore '$file' --unattended >/dev/null 2>&1"

# ---- tampered archives: bin/ and units from a backup are never installed; setuid entries are refused
tamper() { # tamper OUT MEMBER_DIR: appends the files and links under MEMBER_DIR to files.tar of $file
    local t; t="$(mktemp -d)"
    tar -xzf "$file" -C "$t"
    (cd "$2" && find . ! -type d | sed 's|^\./||') >"$t/list"
    tar --append --file="$t/files.tar" -C "$2" -T "$t/list"
    tar -czf "$1" -C "$t" manifest.txt database.dump files.tar
    rm -rf "$t"
}
bin_sum="$(sha256sum $R/bin/update.sh | cut -c1-64)"
unit_sum="$(sha256sum /etc/systemd/system/domainnotice-update.service | cut -c1-64)"
x="$(mktemp -d)"
mkdir -p "$x/opt/domainnotice/bin" "$x/etc/systemd/system"
printf '#!/bin/sh\ntouch /tmp/dn-pwned\n' >"$x/opt/domainnotice/bin/update.sh"; chmod 755 "$x/opt/domainnotice/bin/update.sh"
printf '[Service]\nExecStart=/tmp/dn-evil\n' >"$x/etc/systemd/system/domainnotice-update.service"
tamper /root/tampered-bin.tar.gz "$x"
bash /src/install.sh --restore /root/tampered-bin.tar.gz --unattended --yes >/tmp/restore-bin.log 2>&1
check "tampered bin/: restore finished"     grep -q 'Restored' /tmp/restore-bin.log
check "tampered bin/: update.sh kept"       [ "$(sha256sum $R/bin/update.sh | cut -c1-64)" == "$bin_sum" ]
check "tampered bin/: unit kept"            [ "$(sha256sum /etc/systemd/system/domainnotice-update.service | cut -c1-64)" == "$unit_sum" ]
check "tampered bin/: server answers"       wait_up
x="$(mktemp -d)"
mkdir -p "$x/opt/domainnotice/data"
install -m 4755 /bin/true "$x/opt/domainnotice/data/dn-suid"
tamper /root/tampered-suid.tar.gz "$x"
bash /src/install.sh --restore /root/tampered-suid.tar.gz --unattended --yes >/tmp/restore-suid.log 2>&1
check "setuid entry: restore refused"       bash -c "grep -q 'setuid' /tmp/restore-suid.log && [ ! -e $R/data/dn-suid ]"
x="$(mktemp -d)"
mkdir -p "$x/opt/domainnotice/config"
ln -s appsettings.Production.json "$x/opt/domainnotice/config/install.state.new"
tamper /root/tampered-link.tar.gz "$x"
bash /src/install.sh --restore /root/tampered-link.tar.gz --unattended --yes >/tmp/restore-link.log 2>&1
check "link in config/: restore refused"    bash -c "grep -q 'links or special files in config/' /tmp/restore-link.log && [ ! -e $R/config/install.state.new ]"
x="$(mktemp -d)"
mkdir -p "$x/opt/domainnotice/data"
ln -s /etc/dn-victim "$x/opt/domainnotice/data/escape"
tamper /root/tampered-abs.tar.gz "$x"
bash /src/install.sh --restore /root/tampered-abs.tar.gz --unattended --yes >/tmp/restore-abs.log 2>&1
check "absolute link in data/: restore refused" bash -c "grep -q 'absolute target' /tmp/restore-abs.log && [ ! -L $R/data/escape ]"
check "server still answers after refusals" wait_up

# ---- reset password
DOMAINNOTICE_ADMIN_PASSWORD='Second-Passw0rd-456' bash /src/install.sh --reset-password admin --unattended >/tmp/reset.log 2>&1
check "reset-password: reported"            grep -q "password of 'admin' was reset" /tmp/reset.log
check "reset-password: new password works"  [ "$(sign_in admin 'Second-Passw0rd-456')" == "$B/" ]
check "reset-password: old password refused" [ "$(sign_in admin "$DOMAINNOTICE_ADMIN_PASSWORD")" != "$B/" ]
check "reset-password: unknown user fails"  bash -c "! DOMAINNOTICE_ADMIN_PASSWORD=xxxxxxxxxx bash /src/install.sh --reset-password nobody --unattended >/dev/null 2>&1"

# ---- uninstall (everything, with a backup first)
check "uninstall: unattended needs --yes"   bash -c "! bash /src/install.sh --uninstall --unattended >/dev/null 2>&1 && [ -d $R ]"
bash /src/install.sh --uninstall --unattended --yes --backup-to /root/final-backup.tar.gz >/tmp/uninstall.log 2>&1
check "uninstall: warns about the database"  grep -q 'DATABASE' /tmp/uninstall.log
check "uninstall: backup written first"     [ -s /root/final-backup.tar.gz ]
check "uninstall: $R removed"               [ ! -e $R ]
check "uninstall: database dropped"         [ "$(runuser -u postgres -- psql -X -At -c "select count(*) from pg_database where datname='domainnotice'")" == 0 ]
check "uninstall: service user removed"     bash -c '! id -u domainnotice >/dev/null 2>&1'
check "uninstall: backup inside /opt refused" bash -c "grep -q 'outside' <(bash /src/install.sh --uninstall --unattended --yes --backup-to /opt/domainnotice/x.tar.gz 2>&1) || true"

if ((fails)); then echo "--- server log"; tail -n 40 /tmp/server.log; echo "--- restore log"; tail -n 20 /tmp/restore.log; fi
echo "# $fails failure(s)"
exit $((fails > 0))
