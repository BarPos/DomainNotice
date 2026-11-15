#!/usr/bin/env bash
# DomainNotice backup and restore (Ubuntu 24.04+, installs made by install.sh). Run as root.
#
#   backup.sh create [--output PATH]   one archive with the database, the configuration, the keys and data, and the
#                                      running program. PATH: a folder or a file name ending in .tar.gz
#                                      (default folder: /opt/domainnotice/backups).
#   backup.sh restore FILE [--yes]     puts a backup back on this server: database, configuration, keys, data and
#                                      program (the backup's version becomes the running one). A backup of the current
#                                      state is made first. DomainNotice must be installed (install.sh) on this
#                                      server: its database connection settings are kept, so a backup can also be
#                                      restored onto a new server.
#   backup.sh list                     backups in /opt/domainnotice/backups
#
# The archive contains secrets (the agent-link key, the database password, encrypted settings): it is written with
# mode 600 and owned by root. Keep copies somewhere safe, off this server.
set -Eeuo pipefail

DN_ROOT="${DN_ROOT:-/opt/domainnotice}"
SERVICE_USER=domainnotice
UNIT_DIR=/etc/systemd/system
UNITS=(domainnotice.service domainnotice-update.path domainnotice-update.service)
FORMAT=1
CONFIG="$DN_ROOT/config/appsettings.Production.json"
UPDATE_ENV="$DN_ROOT/config/update.env"
STATE="$DN_ROOT/config/install.state"
WORK=""

info() { printf '==> %s\n' "$*"; }
warn() { printf 'WARNING: %s\n' "$*" >&2; }
die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
usage() { sed -n '2,18p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }
cleanup() { if [[ -n "$WORK" && -d "$WORK" ]]; then rm -rf -- "$WORK"; fi; }
trap cleanup EXIT

require_install() {
    [[ $EUID -eq 0 ]] || die "run as root: sudo $0"
    [[ -f "$UPDATE_ENV" && -f "$CONFIG" && -L "$DN_ROOT/current" ]] ||
        die "DomainNotice is not installed here ($DN_ROOT). Install it with install.sh first."
}

# update.env holds KEY=VALUE lines written by install.sh (values are taken literally, never evaluated).
load_db_env() {
    local key value
    while IFS= read -r line || [[ -n "$line" ]]; do
        [[ "$line" =~ ^(PGHOST|PGPORT|PGDATABASE|PGUSER|PGPASSWORD|PGSSLMODE|PG_BIN|HEALTH_URL)=(.*)$ ]] || continue
        key="${BASH_REMATCH[1]}"; value="${BASH_REMATCH[2]}"
        printf -v "$key" '%s' "$value"
        export "${key?}"
    done <"$UPDATE_ENV"
    [[ -n "${PGDATABASE-}" && -n "${PGUSER-}" ]] || die "$UPDATE_ENV has no database settings"
}

pg() { local tool="$1"; shift; if [[ -n "${PG_BIN-}" && -x "$PG_BIN/$tool" ]]; then "$PG_BIN/$tool" "$@"; else "$tool" "$@"; fi; }

current_version() { basename "$(readlink -f "$DN_ROOT/current")"; }

state_value() { # state_value FILE KEY
    sed -n "s/^$2=//p" "$1" 2>/dev/null | head -n1
}

# ------------------------------------------------------------------------------------------------ create

do_create() {
    local output="$DN_ROOT/backups"
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --output) [[ $# -ge 2 ]] || die "--output needs a path"; output="$2"; shift ;;
            --output=*) output="${1#*=}" ;;
            *) die "unknown argument: $1" ;;
        esac
        shift
    done
    require_install
    load_db_env

    local version stamp file
    version="$(current_version)"
    stamp="$(date -u +%Y%m%dT%H%M%SZ)"
    if [[ "$output" == *.tar.gz ]]; then
        file="$output"
        mkdir -p -- "$(dirname -- "$file")"
    else
        mkdir -p -- "$output"
        file="$output/domainnotice-backup-$version-$(hostname -s)-$stamp.tar.gz"
    fi
    [[ ! -e "$file" ]] || die "$file already exists"

    WORK="$(mktemp -d /var/tmp/domainnotice-backup.XXXXXX)"
    chmod 700 "$WORK"
    mkdir "$WORK/backup"

    info "Dumping the database $PGDATABASE"
    pg pg_dump --format=custom --no-password --file="$WORK/backup/database.dump" ||
        die "pg_dump failed (is the client as new as the server? see PG_BIN in $UPDATE_ENV)"

    info "Collecting configuration, keys, data and the program ($version)"
    local -a paths=("${DN_ROOT#/}/config" "${DN_ROOT#/}/data" "${DN_ROOT#/}/releases/$version")
    [[ -d "$DN_ROOT/bin" ]] && paths+=("${DN_ROOT#/}/bin")
    local u
    for u in "${UNITS[@]}"; do [[ -f "$UNIT_DIR/$u" ]] && paths+=("${UNIT_DIR#/}/$u"); done
    # Staged self-update downloads are not worth keeping; everything else in data/ is (keys, packages).
    tar --create --numeric-owner --file="$WORK/backup/files.tar" -C / \
        --exclude="${DN_ROOT#/}/data/updates" "${paths[@]}"

    printf '%s\n' "format=$FORMAT" "version=$version" "created=$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
        "host=$(hostname -f 2>/dev/null || hostname)" "database=$PGDATABASE" >"$WORK/backup/manifest.txt"

    (umask 077; tar --create --gzip --file="$file.partial" -C "$WORK/backup" manifest.txt database.dump files.tar)
    chown root:root "$file.partial"
    chmod 600 "$file.partial"
    mv -T -- "$file.partial" "$file"
    info "Backup written: $file ($(du -h "$file" | cut -f1))"
    echo "It contains secrets (keys, database password): keep a copy somewhere safe, off this server."
}

# ------------------------------------------------------------------------------------------------ restore

do_restore() {
    local file="" yes=0 safety=1
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --yes|-y) yes=1 ;;
            --no-safety-backup) safety=0 ;;
            -*) die "unknown argument: $1" ;;
            *) [[ -z "$file" ]] || die "only one backup file"; file="$1" ;;
        esac
        shift
    done
    [[ -n "$file" ]] || die "usage: $0 restore FILE [--yes]"
    [[ -f "$file" ]] || die "$file not found"
    require_install

    WORK="$(mktemp -d /var/tmp/domainnotice-restore.XXXXXX)"
    chmod 700 "$WORK"
    tar --extract --gzip --file="$file" -C "$WORK" manifest.txt database.dump files.tar ||
        die "$file is not a DomainNotice backup"
    [[ "$(state_value "$WORK/manifest.txt" format)" == "$FORMAT" ]] || die "unsupported backup format"
    local bversion
    bversion="$(state_value "$WORK/manifest.txt" version)"
    [[ "$bversion" =~ ^[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}$ ]] || die "the backup has no valid version"
    # Refuse anything that would land outside the expected paths.
    local entry
    while IFS= read -r entry; do
        case "$entry" in
            "${DN_ROOT#/}/config"|"${DN_ROOT#/}/config/"*|"${DN_ROOT#/}/data"|"${DN_ROOT#/}/data/"*) ;;
            "${DN_ROOT#/}/releases/$bversion"|"${DN_ROOT#/}/releases/$bversion/"*|"${DN_ROOT#/}/bin"|"${DN_ROOT#/}/bin/"*) ;;
            "${UNIT_DIR#/}/domainnotice.service"|"${UNIT_DIR#/}/domainnotice-update.path"|"${UNIT_DIR#/}/domainnotice-update.service") ;;
            *) die "unexpected path in the backup: $entry" ;;
        esac
        [[ "$entry" != *..* ]] || die "unexpected path in the backup: $entry"
    done < <(tar --list --file="$WORK/files.tar")

    echo "Backup:  $(basename "$file")"
    sed 's/^/         /' "$WORK/manifest.txt"
    echo "This server: DomainNotice $(current_version)"
    echo
    echo "Restoring REPLACES this server's database, configuration, keys, data and program with the backup's."
    echo "The database connection settings of this server are kept."
    if [[ $yes -eq 0 ]]; then
        local answer
        read -r -p "Type 'restore' to continue: " answer </dev/tty || die "not confirmed"
        [[ "$answer" == restore ]] || die "not confirmed"
    fi

    if [[ $safety -eq 1 ]]; then
        info "Backing up the current state first"
        bash "${BASH_SOURCE[0]}" create --output "$DN_ROOT/backups" || die "the safety backup failed; nothing was changed"
    fi

    load_db_env
    local keep_cs keep_env keep_state
    keep_cs="$(grep -m1 '"ConnectionStrings"' "$CONFIG" || true)"
    keep_env="$(cat "$UPDATE_ENV")"
    keep_state="$(cat "$STATE" 2>/dev/null || true)"

    info "Stopping DomainNotice"
    systemctl stop domainnotice-update.path domainnotice.service 2>/dev/null || true

    info "Restoring files"
    mkdir "$WORK/root"
    tar --extract --numeric-owner --preserve-permissions --file="$WORK/files.tar" -C "$WORK/root"
    local src="$WORK/root${DN_ROOT}"
    rm -rf -- "${DN_ROOT:?}/config" "${DN_ROOT:?}/data"
    cp -a -- "$src/config" "$DN_ROOT/config"
    cp -a -- "$src/data" "$DN_ROOT/data"
    mkdir -p "$DN_ROOT/data/updates"
    rm -rf -- "${DN_ROOT:?}/releases/$bversion"
    mkdir -p "$DN_ROOT/releases"
    cp -a -- "$src/releases/$bversion" "$DN_ROOT/releases/$bversion"
    if [[ -d "$src/bin" ]]; then rm -rf -- "${DN_ROOT:?}/bin"; cp -a -- "$src/bin" "$DN_ROOT/bin"; fi
    local u
    for u in "${UNITS[@]}"; do
        [[ -f "$WORK/root${UNIT_DIR}/$u" ]] && install -o root -g root -m 644 "$WORK/root${UNIT_DIR}/$u" "$UNIT_DIR/$u"
    done
    ln -sfn "releases/$bversion" "$DN_ROOT/.current.new" && mv -T -- "$DN_ROOT/.current.new" "$DN_ROOT/current"

    # Keep this server's database settings (a backup from another server has another password).
    printf '%s\n' "$keep_env" >"$UPDATE_ENV"
    if [[ -n "$keep_cs" ]]; then
        awk -v line="$keep_cs" '/"ConnectionStrings"/ && !done { print line; done = 1; next } { print }' "$CONFIG" >"$CONFIG.new"
        mv -T -- "$CONFIG.new" "$CONFIG"
    fi
    if [[ -n "$keep_state" && -f "$STATE" ]]; then
        local k v
        for k in DB_MODE DB_NAME DB_USER; do
            v="$(printf '%s\n' "$keep_state" | sed -n "s/^$k=//p" | head -n1)"
            [[ -n "$v" ]] || continue
            if grep -q "^$k=" "$STATE"; then sed -i "s|^$k=.*|$k=$v|" "$STATE"; else echo "$k=$v" >>"$STATE"; fi
        done
    fi
    # The health check follows the restored Console mode and port.
    local mode port scheme
    mode="$(state_value "$STATE" CONSOLE_MODE)"; port="$(state_value "$STATE" CONSOLE_PORT)"
    if [[ -n "$port" ]]; then
        scheme=https; [[ "$mode" == http ]] && scheme=http
        sed -i "s|^HEALTH_URL=.*|HEALTH_URL=$scheme://127.0.0.1:$port/healthz|" "$UPDATE_ENV"
    fi

    # Owners and modes as install.sh sets them.
    chown -R root:"$SERVICE_USER" "$DN_ROOT/config"; chmod 750 "$DN_ROOT/config"
    find "$DN_ROOT/config" -type f -exec chmod 640 {} +
    for f in "$UPDATE_ENV" "$STATE"; do [[ -f "$f" ]] && { chown root:root "$f"; chmod 600 "$f"; }; done
    chown -R "$SERVICE_USER:$SERVICE_USER" "$DN_ROOT/data"; chmod 700 "$DN_ROOT/data"
    chown -hR root:root "$DN_ROOT/releases/$bversion"; chmod -R go-w "$DN_ROOT/releases/$bversion"
    [[ -d "$DN_ROOT/bin" ]] && { chown -R root:root "$DN_ROOT/bin"; chmod 755 "$DN_ROOT/bin"/*.sh 2>/dev/null || true; }

    info "Restoring the database $PGDATABASE"
    load_db_env
    pg psql --no-password -X -v ON_ERROR_STOP=1 -q -c 'DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;' ||
        die "could not reset the database schema (the role must own the database)"
    pg pg_restore --no-password --no-owner --no-privileges --exit-on-error -d "$PGDATABASE" "$WORK/database.dump" ||
        die "pg_restore failed; the safety backup is in $DN_ROOT/backups"

    info "Starting DomainNotice $bversion"
    systemctl daemon-reload
    systemctl enable domainnotice.service domainnotice-update.path >/dev/null 2>&1 || true
    systemctl start domainnotice.service domainnotice-update.path
    load_db_env
    local i
    for ((i = 0; i < 40; i++)); do
        if curl -fsk --max-time 5 "$HEALTH_URL" >/dev/null 2>&1; then
            info "Restored. DomainNotice $bversion is running."
            return 0
        fi
        sleep 3
    done
    warn "the server did not answer on $HEALTH_URL yet: journalctl -u domainnotice"
}

do_list() {
    ls -lh "$DN_ROOT"/backups/*.tar.gz 2>/dev/null || echo "No backups in $DN_ROOT/backups."
}

case "${1-}" in
    create) shift; do_create "$@" ;;
    restore) shift; do_restore "$@" ;;
    list) do_list ;;
    -h|--help|"") usage ;;
    *) die "unknown command: $1 (see --help)" ;;
esac
