#!/usr/bin/env bash
# DomainNotice self-update (plan 11.2, steps 3-5).
#
# Started as root by domainnotice-update.service when the server writes
# /opt/domainnotice/data/updates/request.conf (watched by domainnotice-update.path).
# install.sh installs this script root-owned as /opt/domainnotice/bin/update.sh. It is never run from a package
# being installed, and nothing from the package runs as root: migrations run as the domainnotice user.
#
# Request (written by the server, key=value, parsed without eval; every value is validated):
#   format=1  run_id=<guid>  version=<x.y.z>  from_version=<x.y.z>  arch=x64|arm64
#   file=DomainNotice.Server-<version>-linux-<arch>.tar.gz  sha256=<64 hex>
# The package and SHA256SUMS.txt are in /opt/domainnotice/data/updates/<version>/.
# Result (read by the server on start): /opt/domainnotice/data/updates/result.conf, written by the service user
# (root never opens a path in the service user's folders for writing)
#   format=1  run_id  version  from_version  state=Succeeded|RolledBack|Failed  message  finished_at
# Database dump before the update: /opt/domainnotice/backups/pre-update/ (root:root 700, only root can write it)
#
# Needs /opt/domainnotice/config/update.env (root:root 600, written by install.sh), key=value lines:
#   PGHOST PGPORT PGDATABASE PGUSER PGPASSWORD PGSSLMODE  (for pg_dump / pg_restore)
#   HEALTH_URL (e.g. https://127.0.0.1:443/healthz), optional PG_BIN (folder of pg_dump matching the server)
#
# Usage: update.sh            apply a pending request (what the systemd unit runs)
#        update.sh --self-test   run the built-in checks of the validation functions (no root needed)
set -Eeuo pipefail

DN_ROOT="${DN_ROOT:-/opt/domainnotice}"
DN_USER="${DN_USER:-domainnotice}"
DN_SERVICE="domainnotice.service"
HEALTH_TIMEOUT_SECONDS=120
KEEP_RELEASES=3
MAX_REQUEST_BYTES=4096

# ------------------------------------------------------------------------------------------------ functions
# All functions below are side-effect free unless noted, so tests can source this file (main runs only when the
# file is executed, not sourced).

dn_log() { printf 'update.sh: %s\n' "$*" >&2; }

# Plain x.y.z only (the same rule as AppVersion.IsPlainTriplet).
dn_is_plain_version() { [[ "${1-}" =~ ^[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}$ ]]; }

# dn_version_cmp A B -> prints -1, 0 or 1. Both must be plain versions.
dn_version_cmp() {
    local a="$1" b="$2" i
    dn_is_plain_version "$a" && dn_is_plain_version "$b" || return 2
    local -a pa pb
    IFS=. read -r -a pa <<<"$a"
    IFS=. read -r -a pb <<<"$b"
    for i in 0 1 2; do
        if ((10#${pa[i]} > 10#${pb[i]})); then echo 1; return 0; fi
        if ((10#${pa[i]} < 10#${pb[i]})); then echo -1; return 0; fi
    done
    echo 0
}

# True when A is newer than B.
dn_version_gt() { local c; c="$(dn_version_cmp "$1" "$2")" || return 1; [[ "$c" == 1 ]]; }

dn_is_guid() { [[ "${1-}" =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]]; }
dn_is_sha256() { [[ "${1-}" =~ ^[0-9a-f]{64}$ ]]; }
dn_is_arch() { [[ "${1-}" == x64 || "${1-}" == arm64 ]]; }

dn_machine_arch() {
    case "$(uname -m)" in
        x86_64|amd64) echo x64 ;;
        aarch64|arm64) echo arm64 ;;
        *) return 1 ;;
    esac
}

dn_tarball_name() { printf 'DomainNotice.Server-%s-linux-%s.tar.gz' "$1" "$2"; }

# dn_parse_request FILE: sets REQ_FORMAT REQ_RUN_ID REQ_VERSION REQ_FROM REQ_ARCH REQ_FILE REQ_SHA256.
# Unknown keys are ignored; nothing is evaluated. Returns 1 when a value is missing or invalid.
dn_parse_request() {
    local file="$1" key value
    REQ_FORMAT="" REQ_RUN_ID="" REQ_VERSION="" REQ_FROM="" REQ_ARCH="" REQ_FILE="" REQ_SHA256=""
    while IFS= read -r line || [[ -n "$line" ]]; do
        line="${line%$'\r'}"
        [[ -z "$line" || "$line" == \#* ]] && continue
        [[ "$line" == *=* ]] || continue
        key="${line%%=*}"
        value="${line#*=}"
        case "$key" in
            format) REQ_FORMAT="$value" ;;
            run_id) REQ_RUN_ID="$value" ;;
            version) REQ_VERSION="$value" ;;
            from_version) REQ_FROM="$value" ;;
            arch) REQ_ARCH="$value" ;;
            file) REQ_FILE="$value" ;;
            sha256) REQ_SHA256="$value" ;;
        esac
    done <"$file"
    [[ "$REQ_FORMAT" == 1 ]] || { DN_ERROR="unsupported request format"; return 1; }
    dn_is_guid "$REQ_RUN_ID" || { REQ_RUN_ID=""; DN_ERROR="invalid run_id"; return 1; }
    dn_is_plain_version "$REQ_VERSION" || { REQ_VERSION=""; DN_ERROR="version is not a plain x.y.z"; return 1; }
    [[ -z "$REQ_FROM" ]] || dn_is_plain_version "$REQ_FROM" || { REQ_FROM=""; DN_ERROR="invalid from_version"; return 1; }
    dn_is_arch "$REQ_ARCH" || { DN_ERROR="invalid arch"; return 1; }
    dn_is_sha256 "$REQ_SHA256" || { DN_ERROR="invalid sha256"; return 1; }
    # The file name is derived from version and arch; the request must agree with it.
    [[ "$REQ_FILE" == "$(dn_tarball_name "$REQ_VERSION" "$REQ_ARCH")" ]] || { DN_ERROR="unexpected file name"; return 1; }
    return 0
}

# dn_read_env_file FILE: reads key=value lines of update.env into variables (known keys only, no eval).
dn_read_env_file() {
    local file="$1" key value
    while IFS= read -r line || [[ -n "$line" ]]; do
        line="${line%$'\r'}"
        [[ -z "$line" || "$line" == \#* || "$line" != *=* ]] && continue
        key="${line%%=*}"
        value="${line#*=}"
        case "$key" in
            PGHOST|PGPORT|PGDATABASE|PGUSER|PGPASSWORD|PGSSLMODE) export "$key=$value" ;;
            HEALTH_URL) HEALTH_URL="$value" ;;
            PG_BIN) PG_BIN="$value" ;;
        esac
    done <"$file"
}

# dn_sha256 FILE -> lowercase hex
dn_sha256() {
    if command -v sha256sum >/dev/null 2>&1; then sha256sum -- "$1" | cut -d' ' -f1
    else shasum -a 256 -- "$1" | cut -d' ' -f1; fi
}

# dn_sums_lookup SUMSFILE NAME -> hash of NAME in a sha256sum-style file ("<hex>  name" or "<hex> *name").
dn_sums_lookup() {
    local sums="$1" name="$2" hash rest
    while IFS= read -r line || [[ -n "$line" ]]; do
        line="${line%$'\r'}"
        [[ "$line" =~ ^([0-9a-fA-F]{64})\ [\ \*](.+)$ ]] || continue
        hash="${BASH_REMATCH[1]}"
        rest="${BASH_REMATCH[2]}"
        rest="${rest#./}"
        if [[ "$rest" == "$name" ]]; then printf '%s\n' "$hash" | tr 'A-F' 'a-f'; return 0; fi
    done <"$sums"
    return 1
}

# dn_check_member_names: reads archive member names (tar -t output) on stdin.
# Rejects absolute paths, any ".." component, and empty names. A name with an embedded newline shows up as two
# lines; each line is checked, so such tricks fail safe.
dn_check_member_names() {
    local name n=0
    while IFS= read -r name || [[ -n "$name" ]]; do
        n=$((n + 1))
        if [[ -z "$name" ]]; then DN_ERROR="empty member name"; return 1; fi
        if [[ "$name" == /* ]]; then DN_ERROR="absolute path in package: $name"; return 1; fi
        case "/$name/" in
            */../*) DN_ERROR="'..' in package path: $name"; return 1 ;;
        esac
        if [[ "$name" == *\\* ]]; then DN_ERROR="backslash in package path: $name"; return 1; fi
    done
    [[ $n -gt 0 ]] || { DN_ERROR="empty package"; return 1; }
}

# dn_check_member_types: reads "tar -tv" output on stdin. Only regular files, folders and symbolic links
# (checked after unpacking) are allowed: no hard links, devices, FIFOs or sockets.
dn_check_member_types() {
    local line n=0
    while IFS= read -r line || [[ -n "$line" ]]; do
        n=$((n + 1))
        case "${line:0:1}" in
            -|d|l) ;;
            *) DN_ERROR="unsupported entry type in package: ${line:0:1}"; return 1 ;;
        esac
    done
    [[ $n -gt 0 ]] || { DN_ERROR="empty package"; return 1; }
}

# dn_check_member_modes: reads "tar -tv" output on stdin. Refuses setuid and setgid entries (a backup never needs
# them, and root would put them back as they are).
dn_check_member_modes() {
    local line perm
    while IFS= read -r line || [[ -n "$line" ]]; do
        perm="${line%% *}"
        case "${perm:3:1}${perm:6:1}" in
            *s*|*S*) DN_ERROR="setuid or setgid entry: ${line##* }"; return 1 ;;
        esac
    done
    return 0
}

# dn_normalize_relpath PATH: lexically resolves "." and ".." of a relative path. Prints the result (may be
# empty for the root itself). Fails when the path climbs above its root.
dn_normalize_relpath() {
    local path="$1" part out=() parts=()
    IFS=/ read -r -a parts <<<"$path"
    for part in "${parts[@]}"; do
        case "$part" in
            ''|.) ;;
            ..) [[ ${#out[@]} -gt 0 ]] || return 1; unset 'out[${#out[@]}-1]' ;;
            *) out+=("$part") ;;
        esac
    done
    local IFS=/
    printf '%s' "${out[*]}"
}

# dn_check_tree DIR: after unpacking. Only files, folders and symlinks; no hard-linked files; every symlink is
# relative and stays inside DIR: checked on the text of the target, and again with "realpath -m" against the real
# path of DIR where GNU realpath is available (defence in depth; macOS has no -m).
dn_check_tree() {
    local dir="$1" link rel target linkdir real="" resolved
    if [[ -n "$(find "$dir" ! -type f ! -type d ! -type l -print -quit)" ]]; then
        DN_ERROR="special files in package"; return 1
    fi
    if [[ -n "$(find "$dir" -type f -links +1 -print -quit)" ]]; then
        DN_ERROR="hard links in package"; return 1
    fi
    if realpath -m / >/dev/null 2>&1; then
        real="$(realpath -e -- "$dir")" || { DN_ERROR="cannot resolve $dir"; return 1; }
    fi
    while IFS= read -r -d '' link; do
        target="$(readlink "$link")"
        rel="${link#"$dir"/}"
        if [[ -z "$target" || "$target" == /* ]]; then DN_ERROR="link with absolute target: $rel"; return 1; fi
        linkdir="$(dirname "$rel")"
        [[ "$linkdir" == . ]] && linkdir=""
        if ! dn_normalize_relpath "${linkdir:+$linkdir/}$target" >/dev/null; then
            DN_ERROR="link pointing outside the package: $rel"; return 1
        fi
        if [[ -n "$real" ]]; then
            resolved="$(realpath -m -- "$link" 2>/dev/null)" || { DN_ERROR="cannot resolve link: $rel"; return 1; }
            [[ "$resolved" == "$real" || "$resolved" == "$real"/* ]] || { DN_ERROR="link pointing outside the package: $rel"; return 1; }
        fi
    done < <(find "$dir" -type l -print0)
    return 0
}

# dn_safe_unpack TARBALL DEST: validates and unpacks into DEST (must not exist). As root, files become root-owned.
dn_safe_unpack() {
    local tarball="$1" dest="$2" listing
    [[ ! -e "$dest" && ! -L "$dest" ]] || { DN_ERROR="$dest already exists"; return 1; }
    listing="$(tar -tzf "$tarball")" || { DN_ERROR="cannot read package"; return 1; }
    dn_check_member_names <<<"$listing" || return 1
    listing="$(tar -tvzf "$tarball")" || { DN_ERROR="cannot read package"; return 1; }
    dn_check_member_types <<<"$listing" || return 1
    mkdir -m 700 -- "$dest" || { DN_ERROR="cannot create $dest"; return 1; }
    if ! tar --no-same-owner --no-same-permissions -xzf "$tarball" -C "$dest"; then
        DN_ERROR="unpacking failed"; return 1
    fi
    dn_check_tree "$dest" || return 1
    if [[ $EUID -eq 0 ]]; then
        chown -hR root:root -- "$dest" || { DN_ERROR="chown failed"; return 1; }
    fi
    # Symlinks were checked above; chmod -R does not follow them below the top folder.
    chmod -R ug-s,u+rwX,go+rX,go-w "$dest" || { DN_ERROR="chmod failed"; return 1; }
    chmod 755 "$dest"
}

# dn_releases_to_prune RELEASES_DIR KEEP CURRENT [EXTRA...]: prints release folders to delete. Keeps the KEEP
# newest versions plus CURRENT and EXTRA versions in any case. Non-version folders are left alone.
dn_releases_to_prune() {
    local dir="$1" keep="$2"
    shift 2
    local -a protect=("$@") versions=()
    local v p skip
    for v in "$dir"/*; do
        [[ -d "$v" && ! -L "$v" ]] || continue
        v="$(basename "$v")"
        dn_is_plain_version "$v" && versions+=("$v")
    done
    [[ ${#versions[@]} -gt 0 ]] || return 0
    local sorted
    sorted="$(printf '%s\n' "${versions[@]}" | sort -t. -k1,1nr -k2,2nr -k3,3nr)"
    local i=0
    while IFS= read -r v; do
        i=$((i + 1))
        [[ $i -le $keep ]] && continue
        skip=0
        for p in "${protect[@]}"; do [[ "$p" == "$v" ]] && skip=1; done
        [[ $skip -eq 1 ]] || printf '%s\n' "$v"
    done <<<"$sorted"
}

dn_one_line() { printf '%s' "$*" | tr '\r\n\t' '   ' | cut -c1-1500; }

# dn_trusted_dir DIR: sets DN_TRUSTED_DIR to the real path of DIR. Fails unless DIR is a folder (not a symbolic
# link) that only root can change: DIR and every folder above it owned by root and not writable by group or others
# (a sticky folder such as /tmp is accepted above DIR, never as DIR). Root can then create files in it without
# races. Needs GNU stat.
# shellcheck disable=SC2034 # DN_TRUSTED_DIR is read by the callers (backup.sh)
dn_trusted_dir() {
    local dir="$1" path cur uid mode first=1
    DN_TRUSTED_DIR=""
    while [[ "$dir" == */ && "$dir" != / ]]; do dir="${dir%/}"; done
    [[ -d "$dir" && ! -L "$dir" ]] || { DN_ERROR="$dir is not a folder (or is a symbolic link)"; return 1; }
    path="$(realpath -e -- "$dir")" || { DN_ERROR="cannot resolve $dir"; return 1; }
    cur="$path"
    while true; do
        read -r uid mode < <(stat -c '%u %a' -- "$cur") || { DN_ERROR="cannot read the owner of $cur"; return 1; }
        [[ "$uid" == 0 ]] || { DN_ERROR="$cur is not owned by root"; return 1; }
        if (((8#$mode & 8#022) != 0)) && { ((first)) || (((8#$mode & 8#1000) == 0)); }; then
            DN_ERROR="$cur is writable by other users than root"; return 1
        fi
        [[ "$cur" == / ]] && break
        cur="$(dirname -- "$cur")"
        first=0
    done
    DN_TRUSTED_DIR="$path"
}

# dn_root_dir DIR [replace-link]: makes DIR a folder owned by root:root with mode 700 (created when missing). Its
# parent must already be a folder only root can change. A symbolic link at DIR is refused, or with replace-link
# removed and replaced by a folder. Only DIR itself is changed; links are never followed.
dn_root_dir() {
    local dir="$1"
    if [[ -L "$dir" ]]; then
        [[ "${2-}" == replace-link ]] || { DN_ERROR="$dir is a symbolic link; make it a real folder"; return 1; }
        rm -f -- "$dir" || { DN_ERROR="cannot remove the link $dir"; return 1; }
    fi
    if [[ ! -e "$dir" ]]; then mkdir -m 700 -- "$dir" || { DN_ERROR="cannot create $dir"; return 1; }; fi
    [[ -d "$dir" && ! -L "$dir" ]] || { DN_ERROR="$dir is not a folder"; return 1; }
    { chown -h root:root -- "$dir" && chmod 700 -- "$dir"; } || { DN_ERROR="cannot set the owner of $dir"; return 1; }
    [[ ! -L "$dir" && "$(stat -c '%u %a' -- "$dir")" == "0 700" ]] || { DN_ERROR="$dir is not root:root 700"; return 1; }
}

# ------------------------------------------------------------------------------------------------ main (root)

dn_write_result() {
    # data/updates belongs to the service user, so the service user writes the file itself (temp file + rename):
    # root never opens a path there for writing, and a planted link or FIFO reaches only what that user can reach.
    local state="$1" message="$2" dir="$DN_ROOT/data/updates"
    local now
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    # shellcheck disable=SC2016 # $1 and $t are expanded by the inner shell
    if ! printf 'format=1\nrun_id=%s\nversion=%s\nfrom_version=%s\nstate=%s\nmessage=%s\nfinished_at=%s\n' \
            "${REQ_RUN_ID-}" "${REQ_VERSION-}" "${CURRENT_VERSION:-${REQ_FROM-}}" "$state" "$(dn_one_line "$message")" "$now" |
        runuser -u "$DN_USER" -- env -i PATH=/usr/sbin:/usr/bin:/sbin:/bin LANG=C.UTF-8 /bin/sh -c \
            'umask 077; t="$(mktemp "$1/.result.XXXXXX")" || exit 1; if cat >"$t" && mv -fT -- "$t" "$1/result.conf"; then exit 0; fi; rm -f -- "$t"; exit 1' \
            sh "$dir"; then
        dn_log "cannot write $dir/result.conf"
        return 0
    fi
    dn_log "result: $state - $message"
}

dn_health_ok() {
    # Healthy = /healthz answers and reports the expected version.
    local expected="$1" body
    body="$(curl -fsk --max-time 5 -- "$HEALTH_URL" 2>/dev/null)" || return 1
    [[ "$body" == *"\"version\":\"$expected\""* ]]
}

dn_wait_healthy() {
    local expected="$1" timeout="$2" deadline
    deadline=$((SECONDS + timeout))
    while ((SECONDS < deadline)); do
        if dn_health_ok "$expected"; then return 0; fi
        sleep 3
    done
    return 1
}

dn_pg() { local tool="$1"; shift; if [[ -n "${PG_BIN-}" && -x "$PG_BIN/$tool" ]]; then "$PG_BIN/$tool" "$@"; else "$tool" "$@"; fi; }

# dn_prepare_backup_dir: backups/ and backups/pre-update/ as root:root 700, so only root can write there and the
# dump is created without races. Before 1.0.5 backups/ belonged to the service user: it is taken over here (owner
# and mode of the folders only; install.sh also fixes what is inside). A link at backups/ is refused (only root can
# put one there); one at pre-update/ is replaced (the service user could have planted it).
dn_prepare_backup_dir() {
    local top="$DN_ROOT/backups"
    BACKUP_DIR="$top/pre-update"
    dn_root_dir "$top" || return 1
    dn_root_dir "$BACKUP_DIR" replace-link
}

dn_backup_db() {
    # Into the root-only folder from dn_prepare_backup_dir; the name must not exist yet, not even as a link.
    local stamp name
    stamp="$(date -u +%Y%m%dT%H%M%SZ)"
    name="domainnotice-${CURRENT_VERSION}-to-${REQ_VERSION}-$stamp.dump"
    BACKUP_FILE="$BACKUP_DIR/$name"
    [[ -n "$BACKUP_DIR" && -d "$BACKUP_DIR" && ! -L "$BACKUP_DIR" && "$(stat -c %u -- "$BACKUP_DIR")" == 0 ]] || return 1
    [[ ! -e "$BACKUP_FILE" && ! -L "$BACKUP_FILE" ]] || return 1
    ( set -o noclobber; umask 077; dn_pg pg_dump --format=custom --no-password >"$BACKUP_FILE" )
}

dn_restore_db() {
    [[ -n "${BACKUP_FILE-}" && -f "$BACKUP_FILE" && ! -L "$BACKUP_FILE" ]] || return 1
    # The role owns the database (install.sh checks it), so it can drop and recreate the public schema.
    dn_pg psql --no-password -v ON_ERROR_STOP=1 -q -c 'DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;' &&
        dn_pg pg_restore --no-password --no-owner --no-privileges --exit-on-error -d "$PGDATABASE" "$BACKUP_FILE"
}

dn_switch_current() {
    local target="$1" tmp="$DN_ROOT/.current.$$"
    rm -f -- "$tmp"
    ln -s -- "$target" "$tmp"
    mv -Tf -- "$tmp" "$DN_ROOT/current"
}

dn_run_as_service_user() {
    # Runs a release's server binary as the service user, from the release folder, with the production config.
    local release="$1"
    shift
    (cd "$release" && runuser -u "$DN_USER" -- env -i \
        PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
        HOME=/nonexistent LANG=C.UTF-8 \
        ASPNETCORE_ENVIRONMENT=Production \
        DOMAINNOTICE_CONFIG="$DN_ROOT/config/appsettings.Production.json" \
        DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 \
        "$release/DomainNotice.Server" "$@")
}

dn_rollback() {
    local why="$1" state=RolledBack note=""
    dn_log "rolling back: $why"
    systemctl stop "$DN_SERVICE" || true
    if [[ "$PHASE" == migrated || "$PHASE" == switched ]]; then
        if dn_restore_db; then note="database restored from $(basename "$BACKUP_FILE")"
        else state=Failed; note="DATABASE RESTORE FAILED, backup: ${BACKUP_FILE-none}"; fi
    fi
    if [[ -n "${PREVIOUS_TARGET-}" ]]; then dn_switch_current "$PREVIOUS_TARGET"; fi
    # Write the result before the old version starts, so it finds the result on start.
    dn_write_result "$state" "$why; rolled back to $CURRENT_VERSION. $note"
    systemctl start --no-block "$DN_SERVICE" || true
    if ! dn_wait_healthy "$CURRENT_VERSION" 60; then dn_log "the old version $CURRENT_VERSION is not healthy either"; fi
    PHASE="done"
}

dn_fail() {
    # Before the service was stopped: nothing changed, just report. After: roll back, best effort.
    local why="$1"
    trap - ERR
    set +e
    if [[ "$PHASE" == stopped || "$PHASE" == migrated || "$PHASE" == switched ]]; then
        dn_rollback "$why"
    elif [[ "$PHASE" != "done" ]]; then
        dn_write_result Failed "$why"
        if [[ "$PHASE" == prepared ]]; then systemctl start --no-block "$DN_SERVICE" 2>/dev/null || true; fi
        PHASE="done"
    fi
    exit 1
}

dn_on_error() { local line="$1"; trap - ERR; dn_fail "unexpected error in update.sh at line $line"; }

dn_main() {
    PHASE=init
    REQ_RUN_ID="" REQ_VERSION="" REQ_FROM="" CURRENT_VERSION="" PREVIOUS_TARGET="" BACKUP_FILE="" BACKUP_DIR=""
    [[ $EUID -eq 0 ]] || { dn_log "must run as root"; exit 1; }
    umask 022

    local updates="$DN_ROOT/data/updates" request="$DN_ROOT/data/updates/request.conf"
    exec 9>/run/domainnotice-update.lock
    flock -n 9 || { dn_log "another update is running"; exit 0; }
    if [[ ! -e "$request" && ! -L "$request" ]]; then dn_log "no update request"; exit 0; fi

    # Work folder on disk in the root-owned releases folder (/run is a small tmpfs; the package is ~50 MB).
    [[ -d "$DN_ROOT/releases" && ! -L "$DN_ROOT/releases" ]] || { dn_log "$DN_ROOT/releases is missing"; exit 1; }
    rm -rf -- "$DN_ROOT"/releases/.work.*
    WORK="$(mktemp -d "$DN_ROOT/releases/.work.XXXXXX")"
    trap 'rm -rf -- "$WORK"' EXIT
    trap 'dn_on_error $LINENO' ERR

    # Take the request away first: the path unit fires again as long as request.conf exists.
    local size
    # A link at data/updates is removed itself (unlink does not follow it), never anything behind it.
    if [[ -L "$updates" || ! -d "$updates" ]]; then [[ ! -L "$updates" ]] || rm -f -- "$updates"; dn_fail "$updates is not a folder"; fi
    if [[ -L "$request" || ! -f "$request" ]]; then rm -f -- "$request"; dn_fail "the request is not a regular file"; fi
    size="$(stat -c %s -- "$request")"
    if ((size > MAX_REQUEST_BYTES)); then rm -f -- "$request"; dn_fail "the request file is too large"; fi
    cp -- "$request" "$WORK/request.conf"
    rm -f -- "$request"

    DN_ERROR=""
    dn_parse_request "$WORK/request.conf" || dn_fail "invalid request: $DN_ERROR"

    local env_file="$DN_ROOT/config/update.env"
    [[ -f "$env_file" && ! -L "$env_file" ]] || dn_fail "$env_file is missing (re-run install.sh to repair)"
    [[ "$(stat -c %u -- "$env_file")" == 0 ]] || dn_fail "$env_file is not owned by root"
    HEALTH_URL="" PG_BIN=""
    dn_read_env_file "$env_file"
    [[ -n "$HEALTH_URL" && -n "${PGDATABASE-}" ]] || dn_fail "$env_file lacks HEALTH_URL or PGDATABASE"

    # Versions: plain x.y.z, and newer than the running release only.
    PREVIOUS_TARGET="$(readlink -- "$DN_ROOT/current")" || dn_fail "$DN_ROOT/current is not a symlink"
    CURRENT_VERSION="$(basename -- "$PREVIOUS_TARGET")"
    dn_is_plain_version "$CURRENT_VERSION" || dn_fail "cannot tell the running version from $DN_ROOT/current"
    dn_version_gt "$REQ_VERSION" "$CURRENT_VERSION" || dn_fail "version $REQ_VERSION is not newer than $CURRENT_VERSION"

    local arch
    arch="$(dn_machine_arch)" || dn_fail "unsupported CPU architecture $(uname -m)"
    [[ "$arch" == "$REQ_ARCH" ]] || dn_fail "package is for $REQ_ARCH, this machine is $arch"

    # Copy package and sums out of the service user's folder before checking them (no link games, no swaps).
    local stage_dir="$updates/$REQ_VERSION" name
    name="$(dn_tarball_name "$REQ_VERSION" "$arch")"
    [[ -d "$stage_dir" && ! -L "$stage_dir" ]] || dn_fail "$stage_dir is missing"
    [[ -f "$stage_dir/$name" && ! -L "$stage_dir/$name" ]] || dn_fail "the package $name is missing"
    [[ -f "$stage_dir/SHA256SUMS.txt" && ! -L "$stage_dir/SHA256SUMS.txt" ]] || dn_fail "SHA256SUMS.txt is missing"
    cp -- "$stage_dir/$name" "$WORK/$name"
    cp -- "$stage_dir/SHA256SUMS.txt" "$WORK/SHA256SUMS.txt"

    # Check the hash again (catches corruption; the sums file sits in a folder the service user can write).
    local actual expected
    actual="$(dn_sha256 "$WORK/$name")"
    expected="$(dn_sums_lookup "$WORK/SHA256SUMS.txt" "$name")" || dn_fail "SHA256SUMS.txt has no entry for $name"
    [[ "$actual" == "$expected" && "$actual" == "$REQ_SHA256" ]] || dn_fail "SHA-256 mismatch for $name"

    # Unpack safely into releases/<version>.
    local releases="$DN_ROOT/releases" release="$DN_ROOT/releases/$REQ_VERSION" staging="$DN_ROOT/releases/.staging-$REQ_VERSION"
    rm -rf -- "$staging"
    DN_ERROR=""
    dn_safe_unpack "$WORK/$name" "$staging" || { rm -rf -- "$staging"; dn_fail "unsafe or broken package: $DN_ERROR"; }
    [[ -f "$staging/DomainNotice.Server" && -x "$staging/DomainNotice.Server" ]] || { rm -rf -- "$staging"; dn_fail "package has no DomainNotice.Server"; }
    rm -rf -- "$release"
    mv -T -- "$staging" "$release"
    dn_log "unpacked $REQ_VERSION into $release"

    # Stop, back up, migrate as the service user, switch, start. The backup folder is checked before the stop.
    DN_ERROR=""
    dn_prepare_backup_dir || dn_fail "backup folder: $DN_ERROR (run install.sh --repair)"
    PHASE=prepared
    systemctl stop "$DN_SERVICE"
    PHASE=stopped
    dn_backup_db || dn_fail "pg_dump failed"
    dn_log "database backup: $BACKUP_FILE"
    PHASE=migrated # from here on the database may have changed
    dn_run_as_service_user "$release" --migrate-only || dn_fail "migrations of $REQ_VERSION failed"
    dn_switch_current "$release"
    PHASE=switched
    # --no-block: with Type=notify a blocking start waits up to TimeoutStartSec; the 2-minute health check decides.
    systemctl start --no-block "$DN_SERVICE" || dn_fail "$DN_SERVICE did not start"
    dn_wait_healthy "$REQ_VERSION" "$HEALTH_TIMEOUT_SECONDS" || dn_fail "$REQ_VERSION was not healthy within $HEALTH_TIMEOUT_SECONDS seconds"
    PHASE="done"
    trap - ERR

    dn_write_result Succeeded "Updated from $CURRENT_VERSION to $REQ_VERSION. Backup: $(basename "$BACKUP_FILE")"

    # Keep the last releases for rollback (never the running one or the one before).
    local old
    while IFS= read -r old; do
        [[ -n "$old" ]] || continue
        dn_log "removing old release $old"
        rm -rf -- "${releases:?}/$old"
    done < <(dn_releases_to_prune "$releases" "$KEEP_RELEASES" "$REQ_VERSION" "$CURRENT_VERSION")
    rm -rf -- "$releases"/.staging-*
    return 0
}

# ------------------------------------------------------------------------------------------------ self-test

dn_self_test() {
    local fails=0
    t() { if "$@" >/dev/null; then :; else echo "FAIL: $*"; fails=$((fails + 1)); fi; }
    f() { if "$@" >/dev/null 2>&1; then echo "FAIL (expected false): $*"; fails=$((fails + 1)); fi; }
    t dn_is_plain_version 1.2.3
    t dn_is_plain_version 10.20.30
    f dn_is_plain_version 1.2
    f dn_is_plain_version 1.2.3.4
    f dn_is_plain_version 1.2.3-beta
    f dn_is_plain_version v1.2.3
    f dn_is_plain_version "1.2.3 "
    f dn_is_plain_version "1.2.3;rm"
    f dn_is_plain_version ""
    t dn_version_gt 1.2.10 1.2.9
    t dn_version_gt 2.0.0 1.99.99
    f dn_version_gt 1.2.3 1.2.3
    f dn_version_gt 1.2.2 1.2.3
    f dn_version_gt 1.2.3-rc 1.0.0
    t dn_is_guid 0f8fad5b-d9cb-469f-a165-70867728950e
    f dn_is_guid ../../etc
    t dn_is_sha256 "$(printf 'a%.0s' {1..64})"
    f dn_is_sha256 "$(printf 'A%.0s' {1..64})"
    t dn_check_member_names <<<$'./\n./DomainNotice.Server\n./wwwroot/app.css'
    f dn_check_member_names <<<$'./ok\n/etc/passwd'
    f dn_check_member_names <<<$'./ok\n../evil'
    f dn_check_member_names <<<$'./a/../../evil'
    f dn_check_member_names <<<$'a/..'
    t dn_check_member_names <<<$'./a..b/c..'
    t dn_check_member_types <<<$'drwxr-xr-x root/root 0 x ./\n-rw-r--r-- root/root 1 x ./a\nlrwxrwxrwx root/root 0 x ./b -> a'
    f dn_check_member_types <<<$'-rw-r--r-- root/root 1 x ./a\nhrw-r--r-- root/root 0 x ./b link to ./a'
    f dn_check_member_types <<<$'crw-r--r-- root/root 1,3 x ./null'
    t dn_check_member_modes <<<$'drwxr-xr-x root/root 0 x ./\n-rwxr-xr-x root/root 1 x ./a\ndrwxrwxrwt root/root 0 x ./tmp'
    f dn_check_member_modes <<<$'-rwsr-xr-x root/root 1 x ./a'
    f dn_check_member_modes <<<$'-rwxr-Sr-x root/root 1 x ./a'
    f dn_check_member_modes <<<$'drwxr-sr-x root/root 0 x ./d'
    t dn_normalize_relpath a/b/../c
    f dn_normalize_relpath a/../../b
    f dn_normalize_relpath ..
    [[ "$(dn_normalize_relpath ./a/./b/../c)" == a/c ]] || { echo "FAIL: normalize ./a/./b/../c"; fails=$((fails + 1)); }
    if [[ $fails -eq 0 ]]; then echo "self-test passed"; return 0; fi
    echo "self-test: $fails failure(s)"
    return 1
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    case "${1-}" in
        --self-test) dn_self_test ;;
        "") dn_main ;;
        -h|--help) sed -n '2,24p' "$0" | sed 's/^# \{0,1\}//' ;;
        *) dn_log "unknown argument: $1"; exit 2 ;;
    esac
fi
