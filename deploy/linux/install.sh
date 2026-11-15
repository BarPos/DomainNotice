#!/usr/bin/env bash
# DomainNotice server installer for Ubuntu 24.04 LTS and newer (plan 11.5). No Docker.
#
#   sudo ./install.sh                                   interactive: asks every question
#   sudo ./install.sh --unattended --package FILE ...   unattended, for automated installs
#   sudo ./install.sh --repair | --upgrade              on an installed server (re-running offers both)
#   sudo ./install.sh --uninstall                       removes program and units; keeps config, data, database
#   sudo ./install.sh --purge                           removes everything (asks for confirmation)
#
# Package source:
#   --package FILE        a DomainNotice.Server-<version>-linux-<arch>.tar.gz from build/build-server.sh or a release
#                         (checked against SHA256SUMS.txt next to it when present)
#   --version X.Y.Z       download that release from GitHub (default: latest). Works only once GITHUB_REPOSITORY
#                         below is set; while it is blank, only --package works.
# Database:
#   --db=local|existing   local: install PostgreSQL here (role and database "domainnotice", localhost only)
#   --db-host H --db-port P --db-name N --db-user U --db-sslmode disable|allow|prefer|require|verify-ca|verify-full
# Console:
#   --console-port N (443)   --server-name NAME (DNS name agents and browsers use; default: hostname -f)
#   --cert FILE --cert-key FILE   existing PEM certificate and key; without them a self-signed one is made
#   --admin-user NAME        first local account
# Other:
#   --open-firewall=yes|no   open the Console port and 5150 when ufw is active (unattended default: no)
#   --yes                    confirm --purge in unattended mode
#
# Secrets never come from command-line flags (other users can see the process list). Sources, in order:
#   --secrets-file FILE   KEY=VALUE lines, the file must be mode 600 (or 400)
#   --secrets-stdin       the same KEY=VALUE lines on standard input
#   environment           DOMAINNOTICE_DB_PASSWORD, DOMAINNOTICE_ADMIN_PASSWORD
#   prompt                hidden input (interactive mode)
# Keys: DB_PASSWORD (existing database), ADMIN_PASSWORD (first local account, at least 8 characters).
set -Eeuo pipefail

# Set to owner/name once the project is published on GitHub (plan 11.4). Blank = no network access to GitHub.
GITHUB_REPOSITORY=""

INSTALL_ROOT=/opt/domainnotice
SERVICE_USER=domainnotice
AGENT_PORT=5150
UNIT_DIR=/etc/systemd/system
UNITS=(domainnotice.service domainnotice-update.path domainnotice-update.service)
CONFIG_FILE="$INSTALL_ROOT/config/appsettings.Production.json"
UPDATE_ENV="$INSTALL_ROOT/config/update.env"
STATE_FILE="$INSTALL_ROOT/config/install.state"
USER_AGENT="DomainNotice-Installer"

MODE="" UNATTENDED=0 ASSUME_YES=0
PACKAGE="" VERSION_ARG=""
DB_MODE="" DB_HOST="" DB_PORT="" DB_NAME="" DB_USER="" DB_SSLMODE=""
CONSOLE_PORT="" SERVER_NAME="" CERT_FILE="" CERT_KEY_FILE="" ADMIN_USER="" OPEN_FIREWALL=""
SECRETS_FILE="" SECRETS_STDIN=0
DB_PASSWORD="" ADMIN_PASSWORD=""
ARCH="" PKG_FILE="" PKG_VERSION="" PG_BIN="" WORK=""

# ------------------------------------------------------------------------------------------------ helpers

info() { printf '==> %s\n' "$*"; }
warn() { printf 'WARNING: %s\n' "$*" >&2; }
die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
usage() { sed -n '2,35p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

cleanup() { if [[ -n "$WORK" && -d "$WORK" ]]; then rm -rf -- "$WORK"; fi; }
trap cleanup EXIT
trap 'die "install.sh failed at line $LINENO"' ERR

tty_read() { # tty_read VAR PROMPT [DEFAULT]
    local __var="$1" __prompt="$2" __def="${3-}" __ans
    if [[ -n "$__def" ]]; then __prompt="$__prompt [$__def]"; fi
    read -r -p "$__prompt: " __ans </dev/tty || die "no answer"
    printf -v "$__var" '%s' "${__ans:-$__def}"
}

tty_secret() { # tty_secret VAR PROMPT [confirm]
    local __var="$1" __prompt="$2" __a __b
    while true; do
        read -r -s -p "$__prompt: " __a </dev/tty || die "no answer"; echo >/dev/tty
        if [[ "${3-}" == confirm ]]; then
            read -r -s -p "Repeat: " __b </dev/tty || die "no answer"; echo >/dev/tty
            [[ "$__a" == "$__b" ]] || { echo "The entries differ, try again." >/dev/tty; continue; }
        fi
        [[ -n "$__a" ]] && break
    done
    printf -v "$__var" '%s' "$__a"
}

ask() { # ask VAR PROMPT DEFAULT: keeps a value given by flag; unattended takes the default
    local __var="$1"
    [[ -n "${!__var}" ]] && return 0
    if [[ $UNATTENDED -eq 1 ]]; then printf -v "$__var" '%s' "${3-}"; return 0; fi
    tty_read "$__var" "$2" "${3-}"
}

ask_yes_no() { # ask_yes_no PROMPT DEFAULT(y|n) -> status
    local ans
    if [[ $UNATTENDED -eq 1 ]]; then [[ "$2" == y ]]; return; fi
    read -r -p "$1 [$([[ "$2" == y ]] && echo Y/n || echo y/N)]: " ans </dev/tty || return 1
    ans="${ans:-$2}"
    [[ "$ans" == [yY]* ]]
}

json_escape() { # prints the JSON string body for $1
    local s="$1" out="" c i
    for ((i = 0; i < ${#s}; i++)); do
        c="${s:i:1}"
        case "$c" in
            '"') out+='\"' ;;
            '\') out+='\\' ;;
            $'\n') out+='\n' ;;
            $'\r') out+='\r' ;;
            $'\t') out+='\t' ;;
            *) if [[ "$c" < ' ' ]]; then printf -v c '\\u%04x' "'$c"; fi; out+="$c" ;;
        esac
    done
    printf '%s' "$out"
}

cs_value() { # Npgsql connection-string value; quoted when needed, embedded quotes doubled
    local v="$1"
    if [[ "$v" == *[\;\"\'=]* || "$v" != "${v#[[:space:]]}" || "$v" != "${v%[[:space:]]}" ]]; then
        printf '"%s"' "${v//\"/\"\"}"
    else
        printf '%s' "$v"
    fi
}

random_alnum() { # random_alnum LENGTH
    local out=""
    while [[ ${#out} -lt $1 ]]; do out+="$(openssl rand -base64 48 | tr -dc 'A-Za-z0-9')"; done
    printf '%s' "${out:0:$1}"
}

new_uuid() { cat /proc/sys/kernel/random/uuid; }

npgsql_sslmode() {
    case "$1" in
        disable) echo Disable ;; allow) echo Allow ;; prefer) echo Prefer ;; require) echo Require ;;
        verify-ca) echo VerifyCA ;; verify-full) echo VerifyFull ;; *) return 1 ;;
    esac
}

apt_has_candidate() {
    local c
    c="$(apt-cache policy "$1" 2>/dev/null | awk '/Candidate:/ {print $2; exit}')"
    [[ -n "$c" && "$c" != "(none)" ]]
}

run_server() { # run_server RELEASE_DIR ARGS...: the server CLI as the service user (never as root)
    local release="$1"
    shift
    (cd "$release" && runuser -u "$SERVICE_USER" -- env -i \
        PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin HOME=/nonexistent LANG=C.UTF-8 \
        ASPNETCORE_ENVIRONMENT=Production DOMAINNOTICE_CONFIG="$CONFIG_FILE" \
        DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 \
        "$release/DomainNotice.Server" "$@")
}

installed_version() {
    local t
    t="$(readlink "$INSTALL_ROOT/current" 2>/dev/null)" || return 1
    basename "$t"
}

read_state() { # read_state KEY
    [[ -f "$STATE_FILE" ]] || return 1
    local line
    while IFS= read -r line; do
        [[ "$line" == "$1="* ]] && { printf '%s' "${line#*=}"; return 0; }
    done <"$STATE_FILE"
    return 1
}

# ------------------------------------------------------------------------------------------------ arguments

parse_args() {
    local a v
    while [[ $# -gt 0 ]]; do
        a="$1"
        v=""
        if [[ "$a" == --*=* ]]; then v="${a#*=}"; a="${a%%=*}"; fi
        case "$a" in
            -h|--help) usage; exit 0 ;;
            --unattended) UNATTENDED=1 ;;
            --yes|-y) ASSUME_YES=1 ;;
            --repair) MODE=repair ;;
            --upgrade) MODE=upgrade ;;
            --uninstall) MODE=uninstall ;;
            --purge) MODE=purge ;;
            --secrets-stdin) SECRETS_STDIN=1 ;;
            --package|--version|--db|--db-host|--db-port|--db-name|--db-user|--db-sslmode|--console-port|--server-name|\
            --cert|--cert-key|--admin-user|--open-firewall|--secrets-file)
                local val
                if [[ -n "$v" ]]; then val="$v"; else [[ $# -ge 2 ]] || die "$a needs a value"; val="$2"; shift; fi
                case "$a" in
                    --package) PACKAGE="$val" ;; --version) VERSION_ARG="$val" ;; --db) DB_MODE="$val" ;;
                    --db-host) DB_HOST="$val" ;; --db-port) DB_PORT="$val" ;; --db-name) DB_NAME="$val" ;;
                    --db-user) DB_USER="$val" ;; --db-sslmode) DB_SSLMODE="$val" ;; --console-port) CONSOLE_PORT="$val" ;;
                    --server-name) SERVER_NAME="$val" ;; --cert) CERT_FILE="$val" ;; --cert-key) CERT_KEY_FILE="$val" ;;
                    --admin-user) ADMIN_USER="$val" ;; --open-firewall) OPEN_FIREWALL="$val" ;; --secrets-file) SECRETS_FILE="$val" ;;
                esac ;;
            --db-password|--admin-password|--password*)
                die "$a: passwords are never accepted as flags; use --secrets-file, --secrets-stdin or the environment" ;;
            *) die "unknown argument: $1 (see --help)" ;;
        esac
        shift
    done
}

load_secrets() {
    local line key value
    parse_secret_line() {
        line="${1%$'\r'}"
        [[ -z "$line" || "$line" == \#* || "$line" != *=* ]] && return 0
        key="${line%%=*}"
        value="${line#*=}"
        case "$key" in
            DB_PASSWORD) [[ -n "$DB_PASSWORD" ]] || DB_PASSWORD="$value" ;;
            ADMIN_PASSWORD) [[ -n "$ADMIN_PASSWORD" ]] || ADMIN_PASSWORD="$value" ;;
            *) warn "ignoring unknown secret key '$key'" ;;
        esac
    }
    if [[ -n "$SECRETS_FILE" ]]; then
        [[ -f "$SECRETS_FILE" && ! -L "$SECRETS_FILE" ]] || die "secrets file $SECRETS_FILE not found"
        local mode owner
        mode="$(stat -c %a -- "$SECRETS_FILE")"
        owner="$(stat -c %u -- "$SECRETS_FILE")"
        [[ "$mode" == 600 || "$mode" == 400 ]] || die "secrets file $SECRETS_FILE must be mode 600 (is $mode)"
        [[ "$owner" == 0 ]] || die "secrets file $SECRETS_FILE must be owned by root"
        while IFS= read -r line || [[ -n "$line" ]]; do parse_secret_line "$line"; done <"$SECRETS_FILE"
    fi
    if [[ $SECRETS_STDIN -eq 1 ]]; then
        while IFS= read -r line || [[ -n "$line" ]]; do parse_secret_line "$line"; done
    fi
    [[ -n "$DB_PASSWORD" ]] || DB_PASSWORD="${DOMAINNOTICE_DB_PASSWORD-}"
    [[ -n "$ADMIN_PASSWORD" ]] || ADMIN_PASSWORD="${DOMAINNOTICE_ADMIN_PASSWORD-}"
    unset DOMAINNOTICE_DB_PASSWORD DOMAINNOTICE_ADMIN_PASSWORD
    unset -f parse_secret_line
}

# ------------------------------------------------------------------------------------------------ step 1: checks

check_system() {
    [[ $EUID -eq 0 ]] || die "run as root: sudo $0"
    [[ -r /etc/os-release ]] || die "/etc/os-release not found"
    local id="" version=""
    while IFS='=' read -r k v; do
        v="${v%\"}"; v="${v#\"}"
        case "$k" in ID) id="$v" ;; VERSION_ID) version="$v" ;; esac
    done </etc/os-release
    [[ "$id" == ubuntu ]] || die "Ubuntu 24.04 LTS or newer is required (found '$id')"
    local major="${version%%.*}" minor="${version#*.}"
    [[ "$major" =~ ^[0-9]+$ && "$minor" =~ ^[0-9]+$ ]] || die "cannot read the Ubuntu version '$version'"
    if ((10#$major < 24 || (10#$major == 24 && 10#$minor < 4))); then die "Ubuntu 24.04 LTS or newer is required (found $version)"; fi
    case "$(uname -m)" in
        x86_64) ARCH=x64 ;;
        aarch64) ARCH=arm64 ;;
        *) die "unsupported CPU architecture $(uname -m) (x64 or arm64)" ;;
    esac
    command -v systemctl >/dev/null || die "systemd is required"
    if [[ $UNATTENDED -eq 0 && ! -r /dev/tty ]]; then die "no terminal: use --unattended"; fi
}

# ------------------------------------------------------------------------------------------------ step 2: packages

install_packages() {
    info "Installing packages"
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -q
    local pkgs=(ca-certificates curl tar openssl tzdata postgresql-client)
    local icu
    icu="$(apt-cache pkgnames libicu 2>/dev/null | grep -E '^libicu[0-9]+$' | sort -V | tail -n1 || true)"
    [[ -n "$icu" ]] || die "no ICU library package (libicuNN) found"
    pkgs+=("$icu")
    local ssl=""
    for p in libssl3t64 libssl3; do if apt_has_candidate "$p"; then ssl="$p"; break; fi; done
    [[ -n "$ssl" ]] || die "no OpenSSL 3 library package found"
    pkgs+=("$ssl")
    # OpenLDAP client library for System.DirectoryServices.Protocols.
    # TODO(build step 7): confirm the exact package; libldap2 on Ubuntu 24.04+, libldap-2.5-0 on older series.
    local ldap=""
    for p in libldap2 libldap-2.5-0; do if apt_has_candidate "$p"; then ldap="$p"; break; fi; done
    [[ -n "$ldap" ]] || die "no OpenLDAP client library package (libldap2 / libldap-2.5-0) found"
    pkgs+=("$ldap")
    apt-get install -y -q --no-install-recommends "${pkgs[@]}"
}

# ------------------------------------------------------------------------------------------------ step 3: package source

sha256_of() { sha256sum -- "$1" | cut -d' ' -f1; }

verify_against_sums() { # verify_against_sums FILE SUMSFILE (before anything from the package is used)
    local name expected actual
    name="$(basename "$1")"
    expected="$(awk -v n="$name" '{ f = $2; sub(/^\*/, "", f); sub(/^\.\//, "", f)
        if (f == n && length($1) == 64 && $1 ~ /^[0-9a-fA-F]+$/) { print tolower($1); exit } }' "$2")"
    [[ -n "$expected" ]] || die "$(basename "$2") has no entry for $name"
    actual="$(sha256_of "$1")"
    [[ "$actual" == "$expected" ]] || die "SHA-256 mismatch for $name"
    info "SHA-256 of $name verified"
}

github_download() { # github_download URL DEST
    curl -fsSL --proto '=https' --tlsv1.2 -A "$USER_AGENT" -o "$2" -- "$1" || die "download failed: $1"
}

obtain_package() {
    WORK="$(mktemp -d /tmp/domainnotice-install.XXXXXX)"
    chmod 700 "$WORK"
    if [[ -z "$PACKAGE" && -z "$VERSION_ARG" && -z "$GITHUB_REPOSITORY" && $UNATTENDED -eq 0 ]]; then
        tty_read PACKAGE "Path to DomainNotice.Server-<version>-linux-$ARCH.tar.gz"
    fi
    if [[ -n "$PACKAGE" ]]; then
        [[ -f "$PACKAGE" ]] || die "package $PACKAGE not found"
        local base
        base="$(basename "$PACKAGE")"
        [[ "$base" =~ ^DomainNotice\.Server-([0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5})-linux-(x64|arm64)\.tar\.gz$ ]] ||
            die "the package must be named DomainNotice.Server-<x.y.z>-linux-<x64|arm64>.tar.gz"
        PKG_VERSION="${BASH_REMATCH[1]}"
        [[ "${BASH_REMATCH[2]}" == "$ARCH" ]] || die "the package is for ${BASH_REMATCH[2]}, this machine is $ARCH"
        PKG_FILE="$WORK/$base"
        cp -- "$PACKAGE" "$PKG_FILE"
        local sums
        sums="$(dirname "$PACKAGE")/SHA256SUMS.txt"
        if [[ -f "$sums" ]]; then
            verify_against_sums "$PKG_FILE" "$sums"
        else
            warn "no SHA256SUMS.txt next to the package; its hash is not checked"
        fi
        bootstrap_functions "$PKG_FILE"
        return 0
    fi

    [[ -n "$GITHUB_REPOSITORY" ]] || die "updates from GitHub are not configured yet: use --package FILE (a tarball from build/build-server.sh)"
    local version="$VERSION_ARG"
    if [[ -z "$version" ]]; then
        local json
        json="$(curl -fsSL --proto '=https' -A "$USER_AGENT" -H 'Accept: application/vnd.github+json' \
            "https://api.github.com/repos/$GITHUB_REPOSITORY/releases/latest")" || die "cannot read the latest release"
        version="$(printf '%s' "$json" | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"v\{0,1\}\([^"]*\)".*/\1/p' | head -n1)"
    fi
    [[ "$version" =~ ^[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}$ ]] || die "version '$version' is not a plain x.y.z"
    PKG_VERSION="$version"
    local name="DomainNotice.Server-$version-linux-$ARCH.tar.gz" base="https://github.com/$GITHUB_REPOSITORY/releases/download/v$version"
    info "Downloading $name"
    github_download "$base/$name" "$WORK/$name"
    github_download "$base/SHA256SUMS.txt" "$WORK/SHA256SUMS.txt"
    PKG_FILE="$WORK/$name"
    verify_against_sums "$PKG_FILE" "$WORK/SHA256SUMS.txt"
    bootstrap_functions "$PKG_FILE"
}

# The validation and unpack functions live in update.sh (one implementation). Take them from the package.
bootstrap_functions() {
    local pkg="$1" dir="$WORK/bootstrap" m
    mkdir -p "$dir"
    for m in ./deploy/linux/update.sh deploy/linux/update.sh; do
        if tar -xzf "$pkg" -C "$dir" --no-same-owner "$m" 2>/dev/null; then break; fi
    done
    local f="$dir/deploy/linux/update.sh"
    [[ -f "$f" && ! -L "$f" ]] || die "the package has no deploy/linux/update.sh"
    # shellcheck source=update.sh
    source "$f"
    set -Eeuo pipefail
    trap 'die "install.sh failed at line $LINENO"' ERR
}

# ------------------------------------------------------------------------------------------------ steps 5, 6: layout and user

create_user() {
    if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
        info "Creating system user $SERVICE_USER"
        useradd --system --user-group --home-dir /nonexistent --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"
    fi
}

create_layout() {
    info "Preparing $INSTALL_ROOT"
    install -d -o root -g root -m 755 "$INSTALL_ROOT" "$INSTALL_ROOT/releases" "$INSTALL_ROOT/bin"
    install -d -o root -g "$SERVICE_USER" -m 750 "$INSTALL_ROOT/config"
    install -d -o "$SERVICE_USER" -g "$SERVICE_USER" -m 700 "$INSTALL_ROOT/data" "$INSTALL_ROOT/data/keys" \
        "$INSTALL_ROOT/data/keys/dataprotection" "$INSTALL_ROOT/data/updates" "$INSTALL_ROOT/data/packages" \
        "$INSTALL_ROOT/data/packages/agent" "$INSTALL_ROOT/backups"
}

fix_permissions() { # repair: owners and modes as in plan 11.5, without touching contents
    create_layout
    chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_ROOT/data"
    chmod 700 "$INSTALL_ROOT/data"
    local f
    for f in "$CONFIG_FILE" "$INSTALL_ROOT/config/console.pem" "$INSTALL_ROOT/config/console.key"; do
        [[ -f "$f" ]] && { chown root:"$SERVICE_USER" "$f"; chmod 640 "$f"; }
    done
    for f in "$UPDATE_ENV" "$STATE_FILE"; do [[ -f "$f" ]] && { chown root:root "$f"; chmod 600 "$f"; }; done
    find "$INSTALL_ROOT/releases" -mindepth 1 -maxdepth 1 -type d -exec chown -hR root:root {} + -exec chmod -R go-w {} +
    return 0
}

unpack_release() {
    local release="$INSTALL_ROOT/releases/$PKG_VERSION" staging="$INSTALL_ROOT/releases/.staging-$PKG_VERSION"
    info "Unpacking $PKG_VERSION into $release"
    rm -rf -- "$staging"
    DN_ERROR=""
    dn_safe_unpack "$PKG_FILE" "$staging" || { rm -rf -- "$staging"; die "unsafe or broken package: $DN_ERROR"; }
    [[ -x "$staging/DomainNotice.Server" ]] || { rm -rf -- "$staging"; die "the package has no DomainNotice.Server"; }
    rm -rf -- "$release"
    mv -T -- "$staging" "$release"
}

install_deploy_files() { # install_deploy_files RELEASE_DIR: root-owned update.sh and the systemd units
    local src="$1/deploy/linux" u
    install -o root -g root -m 755 "$src/update.sh" "$INSTALL_ROOT/bin/update.sh"
    for u in "${UNITS[@]}"; do install -o root -g root -m 644 "$src/$u" "$UNIT_DIR/$u"; done
    systemctl daemon-reload
}

# ------------------------------------------------------------------------------------------------ step 4: database

pg_query() { # pg_query SQL: against the configured database with the configured role
    PGPASSWORD="$DB_PASSWORD" PGSSLMODE="$DB_SSLMODE" PGCONNECT_TIMEOUT=10 psql -X -At -v ON_ERROR_STOP=1 \
        -h "$DB_HOST" -p "$DB_PORT" -U "$DB_USER" -d "$DB_NAME" -c "$1" 2>&1
}

pg_test_connection() { # sets PG_SERVER_NUM; returns 1 with a message on failure
    local out
    if ! out="$(pg_query "SELECT current_setting('server_version_num') || '|' || (SELECT pg_catalog.pg_get_userbyid(datdba) = current_user FROM pg_database WHERE datname = current_database())::text")"; then
        printf 'Connection failed: %s\n' "$out" >&2
        return 1
    fi
    PG_SERVER_NUM="${out%%|*}"
    PG_IS_OWNER="${out#*|}"
    return 0
}

ensure_pg_client() { # ensure_pg_client MAJOR: pg_dump must not be older than the server
    local major="$1" have=0
    if [[ -x "/usr/lib/postgresql/$major/bin/pg_dump" ]]; then have=1; fi
    if [[ $have -eq 0 ]]; then
        info "PostgreSQL $major server: adding the PostgreSQL project's apt repository for a matching client"
        apt-get install -y -q --no-install-recommends postgresql-common
        /usr/share/postgresql-common/pgdg/apt.postgresql.org.sh -y
        apt-get install -y -q --no-install-recommends "postgresql-client-$major"
    fi
    PG_BIN="/usr/lib/postgresql/$major/bin"
    [[ -x "$PG_BIN/pg_dump" ]] || die "pg_dump for PostgreSQL $major not found"
}

setup_database() {
    if [[ -z "$DB_MODE" ]]; then
        if [[ $UNATTENDED -eq 1 ]]; then die "missing --db=local|existing"; fi
        echo "Database: 1) install PostgreSQL on this machine  2) use an existing PostgreSQL server"
        local choice
        tty_read choice "Choose 1 or 2" 1
        case "$choice" in 1) DB_MODE=local ;; 2) DB_MODE=existing ;; *) die "invalid choice" ;; esac
    fi
    case "$DB_MODE" in
        local) setup_local_database ;;
        existing) setup_existing_database ;;
        *) die "--db must be local or existing" ;;
    esac
}

setup_local_database() {
    info "Installing PostgreSQL on this machine"
    apt-get install -y -q --no-install-recommends postgresql
    systemctl enable --now postgresql
    DB_HOST=127.0.0.1 DB_PORT=5432 DB_NAME=domainnotice DB_USER=domainnotice DB_SSLMODE=prefer
    local num
    num="$(runuser -u postgres -- psql -X -At -c 'SHOW server_version_num')"
    ((num >= 160000)) || die "PostgreSQL 16 or newer is required (found $num)"
    local listen
    listen="$(runuser -u postgres -- psql -X -At -c 'SHOW listen_addresses')"
    [[ "$listen" == localhost || "$listen" == 127.0.0.1 || "$listen" == "127.0.0.1,::1" ]] ||
        warn "PostgreSQL listens on '$listen', not only on localhost"
    DB_PASSWORD="$(random_alnum 32)"
    # SQL on stdin, never on the command line. The password is alphanumeric, so the literal needs no escaping.
    runuser -u postgres -- psql -X -q -v ON_ERROR_STOP=1 -d postgres <<SQL
DO \$\$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'domainnotice') THEN
    ALTER ROLE domainnotice WITH LOGIN PASSWORD '$DB_PASSWORD';
  ELSE
    CREATE ROLE domainnotice WITH LOGIN PASSWORD '$DB_PASSWORD';
  END IF;
END
\$\$;
SQL
    if [[ "$(runuser -u postgres -- psql -X -At -c "SELECT 1 FROM pg_database WHERE datname = 'domainnotice'")" == 1 ]]; then
        runuser -u postgres -- psql -X -q -v ON_ERROR_STOP=1 -c 'ALTER DATABASE domainnotice OWNER TO domainnotice'
    else
        runuser -u postgres -- psql -X -q -v ON_ERROR_STOP=1 -c 'CREATE DATABASE domainnotice OWNER domainnotice'
    fi
    pg_test_connection || die "cannot connect to the local database as domainnotice"
    ensure_pg_client "$((PG_SERVER_NUM / 10000))"
}

setup_existing_database() {
    while true; do
        ask DB_HOST "PostgreSQL host" ""
        ask DB_PORT "Port" 5432
        ask DB_NAME "Database" domainnotice
        ask DB_USER "User" domainnotice
        ask DB_SSLMODE "SSL mode (disable/allow/prefer/require/verify-ca/verify-full)" require
        [[ $UNATTENDED -eq 0 || -n "$DB_HOST" ]] || die "missing --db-host"
        npgsql_sslmode "$DB_SSLMODE" >/dev/null || die "invalid SSL mode '$DB_SSLMODE'"
        [[ "$DB_PORT" =~ ^[0-9]+$ ]] || die "invalid port '$DB_PORT'"
        if [[ -z "$DB_PASSWORD" ]]; then
            [[ $UNATTENDED -eq 0 ]] || die "missing DB_PASSWORD (secrets file, stdin or DOMAINNOTICE_DB_PASSWORD)"
            tty_secret DB_PASSWORD "Password for $DB_USER"
        fi
        info "Testing the connection to $DB_HOST:$DB_PORT/$DB_NAME"
        if pg_test_connection; then break; fi
        [[ $UNATTENDED -eq 0 ]] || die "cannot connect to the database"
        ask_yes_no "Try again with other values?" y || die "cannot connect to the database"
        DB_HOST="" DB_PORT="" DB_NAME="" DB_USER="" DB_SSLMODE="" DB_PASSWORD=""
    done
    [[ "$PG_SERVER_NUM" =~ ^[0-9]+$ ]] || die "unexpected server version '$PG_SERVER_NUM'"
    ((PG_SERVER_NUM >= 160000)) || die "PostgreSQL 16 or newer is required (server is $PG_SERVER_NUM)"
    [[ "$PG_IS_OWNER" == true ]] || die "the role $DB_USER must own the database $DB_NAME (the updater drops and restores it on rollback)"
    ensure_pg_client "$((PG_SERVER_NUM / 10000))"
}

# ------------------------------------------------------------------------------------------------ config, step 7 HTTPS

write_config() {
    [[ ! -e "$CONFIG_FILE" ]] || { info "Keeping the existing $CONFIG_FILE"; return 0; }
    info "Writing $CONFIG_FILE"
    local cs
    cs="Host=$(cs_value "$DB_HOST");Port=$DB_PORT;Database=$(cs_value "$DB_NAME");Username=$(cs_value "$DB_USER");Password=$(cs_value "$DB_PASSWORD");SSL Mode=$(npgsql_sslmode "$DB_SSLMODE")"
    local tmp
    tmp="$(mktemp "$INSTALL_ROOT/config/.appsettings.XXXXXX")"
    (umask 077; cat >"$tmp" <<JSON
{
  "ConnectionStrings": { "Default": "$(json_escape "$cs")" },
  "Console":   { "Urls": "https://0.0.0.0:$CONSOLE_PORT", "Certificate": "$INSTALL_ROOT/config/console.pem", "CertificateKey": "$INSTALL_ROOT/config/console.key" },
  "Paths":     { "Data": "$INSTALL_ROOT/data", "Backups": "$INSTALL_ROOT/backups" },
  "Agents":    { "Port": $AGENT_PORT, "OfflineAfterSeconds": 90, "MaxMessageBytes": 1048576,
                 "UpdateTimeoutMinutes": 30, "MaxParallelUpdates": 50, "UpdateBandwidthMbps": 0, "AutoUpdate": false },
  "AnonymousAnswers": { "FlushMinutes": 10 },
  "LocalAccounts": { "Enabled": true },
  "Updates":   { "GitHubRepository": "$(json_escape "$GITHUB_REPOSITORY")", "Channel": "stable", "CheckIntervalHours": 24, "GitHubToken": "" },
  "Enrolment": { "RequirePassword": true, "PasswordHash": "" },
  "History":   { "RetentionDays": 30 },
  "Directory": {
    "Hosts": [], "Port": 636, "Security": "Ldaps",
    "TrustedCertificate": "", "BaseDn": "",
    "BindUser": "", "BindPasswordSource": "Environment:DOMAINNOTICE_BIND_PASSWORD",
    "AdminGroups": [ "DomainNotice Admins" ], "FullSyncCron": "0 2 * * *", "IncrementalSyncMinutes": 5,
    "KerberosServicePrincipal": ""
  }
}
JSON
    )
    chown root:"$SERVICE_USER" "$tmp"
    chmod 640 "$tmp"
    mv -T -- "$tmp" "$CONFIG_FILE"
}

write_update_env() { # root-only copy of the database settings for update.sh (pg_dump / pg_restore)
    if [[ -e "$UPDATE_ENV" ]]; then info "Keeping the existing $UPDATE_ENV"; return 0; fi
    local tmp
    tmp="$(mktemp "$INSTALL_ROOT/config/.update-env.XXXXXX")"
    (umask 077; printf '%s\n' "# Used by $INSTALL_ROOT/bin/update.sh. root only." \
        "PGHOST=$DB_HOST" "PGPORT=$DB_PORT" "PGDATABASE=$DB_NAME" "PGUSER=$DB_USER" "PGPASSWORD=$DB_PASSWORD" \
        "PGSSLMODE=$DB_SSLMODE" "PG_BIN=$PG_BIN" "HEALTH_URL=https://127.0.0.1:$CONSOLE_PORT/healthz" >"$tmp")
    chown root:root "$tmp"
    chmod 600 "$tmp"
    mv -T -- "$tmp" "$UPDATE_ENV"
}

write_state() {
    local tmp
    tmp="$(mktemp "$INSTALL_ROOT/config/.state.XXXXXX")"
    printf '%s\n' "DB_MODE=$DB_MODE" "DB_NAME=$DB_NAME" "DB_USER=$DB_USER" "SERVER_NAME=$SERVER_NAME" \
        "CONSOLE_PORT=$CONSOLE_PORT" "INSTALLED_AT=$(date -u +%Y-%m-%dT%H:%M:%SZ)" >"$tmp"
    chown root:root "$tmp"
    chmod 600 "$tmp"
    mv -T -- "$tmp" "$STATE_FILE"
}

setup_certificate() {
    local pem="$INSTALL_ROOT/config/console.pem" key="$INSTALL_ROOT/config/console.key"
    if [[ -f "$pem" && -f "$key" ]]; then info "Keeping the existing Console certificate"; return 0; fi
    if [[ -z "$CERT_FILE" && $UNATTENDED -eq 0 ]]; then
        if ask_yes_no "Use an existing certificate for the Console (otherwise a self-signed one is made)?" n; then
            tty_read CERT_FILE "Certificate file (PEM)"
            tty_read CERT_KEY_FILE "Private key file (PEM)"
        fi
    fi
    if [[ -n "$CERT_FILE" ]]; then
        [[ -f "$CERT_FILE" && -f "$CERT_KEY_FILE" ]] || die "certificate or key file not found"
        openssl x509 -in "$CERT_FILE" -noout 2>/dev/null || die "$CERT_FILE is not a PEM certificate"
        openssl pkey -in "$CERT_KEY_FILE" -noout 2>/dev/null || die "$CERT_KEY_FILE is not an unencrypted PEM private key"
        [[ "$(openssl x509 -in "$CERT_FILE" -noout -pubkey)" == "$(openssl pkey -in "$CERT_KEY_FILE" -pubout)" ]] ||
            die "the key does not belong to the certificate"
        install -o root -g "$SERVICE_USER" -m 640 "$CERT_FILE" "$pem"
        install -o root -g "$SERVICE_USER" -m 640 "$CERT_KEY_FILE" "$key"
        info "Installed the Console certificate"
        return 0
    fi
    info "Generating a self-signed certificate for $SERVER_NAME"
    local san="DNS:$SERVER_NAME,DNS:localhost,IP:127.0.0.1" ip
    for ip in $(hostname -I 2>/dev/null || true); do
        [[ "$ip" =~ ^[0-9.]+$ || "$ip" =~ ^[0-9a-fA-F:]+$ ]] && san+=",IP:$ip"
    done
    (umask 077; openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -days 825 \
        -subj "/CN=$SERVER_NAME" -addext "subjectAltName=$san" -addext "extendedKeyUsage=serverAuth" \
        -keyout "$key" -out "$pem" 2>/dev/null) || die "openssl could not create the certificate"
    chown root:"$SERVICE_USER" "$pem" "$key"
    chmod 640 "$pem" "$key"
}

# ------------------------------------------------------------------------------------------------ steps 8-12

create_admin_account() {
    ask ADMIN_USER "First local account (user name)" admin
    [[ -n "$ADMIN_USER" && "$ADMIN_USER" != -* && "$ADMIN_USER" =~ ^[A-Za-z0-9._@-]{1,64}$ ]] ||
        die "invalid user name '$ADMIN_USER' (letters, digits, . _ @ -)"
    if [[ -z "$ADMIN_PASSWORD" ]]; then
        [[ $UNATTENDED -eq 0 ]] || die "missing ADMIN_PASSWORD (secrets file, stdin or DOMAINNOTICE_ADMIN_PASSWORD)"
        while true; do
            tty_secret ADMIN_PASSWORD "Password for $ADMIN_USER (at least 8 characters)" confirm
            [[ ${#ADMIN_PASSWORD} -ge 8 ]] && break
            echo "Too short." >/dev/tty
        done
    fi
    [[ ${#ADMIN_PASSWORD} -ge 8 ]] || die "ADMIN_PASSWORD must have at least 8 characters"
    [[ "$ADMIN_PASSWORD" != *$'\n'* ]] || die "ADMIN_PASSWORD must be one line"
    info "Creating the local account $ADMIN_USER"
    # The password goes through stdin, never the command line.
    printf '%s\n' "$ADMIN_PASSWORD" | run_server "$INSTALL_ROOT/current" --create-local-account "$ADMIN_USER" ||
        die "creating the local account failed"
}

migrate() {
    info "Applying database migrations"
    run_server "$1" --migrate-only || die "--migrate-only failed"
}

start_services() {
    info "Starting the services"
    systemctl enable domainnotice.service domainnotice-update.path
    systemctl restart domainnotice.service
    systemctl start domainnotice-update.path
    local i
    for ((i = 0; i < 40; i++)); do
        if curl -fsk --max-time 5 "https://127.0.0.1:$CONSOLE_PORT/healthz" >/dev/null 2>&1; then return 0; fi
        sleep 3
    done
    warn "the server did not answer on https://127.0.0.1:$CONSOLE_PORT/healthz yet (journalctl -u domainnotice)"
}

configure_firewall() {
    command -v ufw >/dev/null 2>&1 || return 0
    ufw status 2>/dev/null | head -n1 | grep -q 'Status: active' || return 0
    local open=no
    case "$OPEN_FIREWALL" in
        yes) open=yes ;;
        no) open=no ;;
        "") if [[ $UNATTENDED -eq 0 ]] && ask_yes_no "ufw is active. Open ports $CONSOLE_PORT (Console) and $AGENT_PORT (agents)?" y; then open=yes; fi ;;
        *) die "--open-firewall must be yes or no" ;;
    esac
    if [[ "$open" == yes ]]; then
        ufw allow "$CONSOLE_PORT/tcp" comment 'DomainNotice Console'
        ufw allow "$AGENT_PORT/tcp" comment 'DomainNotice agents'
    else
        warn "ufw is active: open TCP $CONSOLE_PORT and $AGENT_PORT yourself if needed"
    fi
}

print_summary() {
    local version fp url
    version="$(installed_version)"
    fp="$(run_server "$INSTALL_ROOT/current" --print-fingerprint 2>/dev/null | grep -E '^[0-9a-f]{64}$' | tail -n1 || true)"
    [[ -n "$fp" ]] || fp="<see Console: Settings>"
    url="https://$SERVER_NAME/"
    [[ "$CONSOLE_PORT" == 443 ]] || url="https://$SERVER_NAME:$CONSOLE_PORT/"
    cat <<EOF

DomainNotice $version is installed.

  Console:             $url
  Server fingerprint:  $fp
  Logs:                journalctl -u domainnotice

Install the agent on Windows PCs (as administrator), after setting the install password in the Console
(Settings, Enrolment):

  msiexec /i DomainNotice.Agent-$version-x64.msi SERVERHOST=$SERVER_NAME SERVERPORT=$AGENT_PORT SERVERFINGERPRINT=$fp INSTALLPASSWORD=<install password>

EOF
}

# ------------------------------------------------------------------------------------------------ modes

do_install() {
    ask SERVER_NAME "Server DNS name (used by browsers and agents)" "$(hostname -f 2>/dev/null || hostname)"
    [[ "$SERVER_NAME" =~ ^[A-Za-z0-9.-]{1,253}$ ]] || die "invalid server name '$SERVER_NAME'"
    ask CONSOLE_PORT "Console HTTPS port" 443
    [[ "$CONSOLE_PORT" =~ ^[0-9]{1,5}$ ]] && ((CONSOLE_PORT >= 1 && CONSOLE_PORT <= 65535)) || die "invalid port '$CONSOLE_PORT'"
    ((CONSOLE_PORT != AGENT_PORT)) || die "the Console port must differ from the agent port $AGENT_PORT"

    install_packages
    obtain_package
    create_user
    create_layout
    unpack_release
    dn_switch_current "$INSTALL_ROOT/releases/$PKG_VERSION"
    setup_database
    write_config
    write_update_env
    write_state
    setup_certificate
    install_deploy_files "$INSTALL_ROOT/current"
    migrate "$INSTALL_ROOT/current"
    create_admin_account
    start_services
    configure_firewall
    print_summary
}

do_repair() {
    local version
    version="$(installed_version)" || die "no current release to repair"
    info "Repairing DomainNotice $version (config and data are kept)"
    SERVER_NAME="$(read_state SERVER_NAME || hostname -f)"
    CONSOLE_PORT="$(read_state CONSOLE_PORT || echo 443)"
    install_packages
    create_user
    fix_permissions
    install_deploy_files "$INSTALL_ROOT/current"
    [[ -f "$UPDATE_ENV" ]] || warn "$UPDATE_ENV is missing: self-update will fail until it exists"
    [[ -f "$CONFIG_FILE" ]] || die "$CONFIG_FILE is missing; restore it or run --purge and install again"
    migrate "$INSTALL_ROOT/current"
    start_services
    print_summary
}

do_upgrade() {
    local current
    current="$(installed_version)" || die "no current release to upgrade"
    SERVER_NAME="$(read_state SERVER_NAME || hostname -f)"
    CONSOLE_PORT="$(read_state CONSOLE_PORT || echo 443)"
    install_packages
    obtain_package
    dn_version_gt "$PKG_VERSION" "$current" || die "$PKG_VERSION is not newer than the installed $current (downgrades are not offered)"
    [[ -f "$UPDATE_ENV" ]] || die "$UPDATE_ENV is missing; run --repair first"
    info "Upgrading $current to $PKG_VERSION with update.sh (backup, migrations, health check, rollback)"
    fix_permissions

    # New updater and units from the package (chosen by the admin running this script).
    local pkgdir="$WORK/pkg"
    DN_ERROR=""
    dn_safe_unpack "$PKG_FILE" "$pkgdir" || die "unsafe or broken package: $DN_ERROR"
    install_deploy_files "$pkgdir"

    # Stage the package like the server does, then run the update unit and wait for it.
    local stage="$INSTALL_ROOT/data/updates/$PKG_VERSION" name sha run_id
    name="$(basename "$PKG_FILE")"
    rm -rf -- "$stage"
    install -d -o "$SERVICE_USER" -g "$SERVICE_USER" -m 700 "$stage"
    install -o "$SERVICE_USER" -g "$SERVICE_USER" -m 600 "$PKG_FILE" "$stage/$name"
    sha="$(sha256_of "$PKG_FILE")"
    printf '%s  %s\n' "$sha" "$name" >"$WORK/SHA256SUMS.staged"
    install -o "$SERVICE_USER" -g "$SERVICE_USER" -m 600 "$WORK/SHA256SUMS.staged" "$stage/SHA256SUMS.txt"
    run_id="$(new_uuid)"
    printf '%s\n' "# Written by install.sh --upgrade" "format=1" "run_id=$run_id" "version=$PKG_VERSION" \
        "from_version=$current" "arch=$ARCH" "file=$name" "sha256=$sha" >"$WORK/request.conf"
    install -o "$SERVICE_USER" -g "$SERVICE_USER" -m 600 "$WORK/request.conf" "$INSTALL_ROOT/data/updates/.request.conf.tmp"
    mv -T -- "$INSTALL_ROOT/data/updates/.request.conf.tmp" "$INSTALL_ROOT/data/updates/request.conf"
    systemctl start domainnotice-update.service || true

    local now_version result="$INSTALL_ROOT/data/updates/result.conf"
    now_version="$(installed_version || true)"
    if [[ -f "$result" ]]; then grep -E '^(state|message)=' "$result" || true; fi
    [[ "$now_version" == "$PKG_VERSION" ]] || die "the upgrade did not complete; see journalctl -u domainnotice-update"
    print_summary
}

do_uninstall() {
    [[ $EUID -eq 0 ]] || die "run as root: sudo $0"
    info "Removing the DomainNotice program and units (config, data, backups and the database are kept)"
    systemctl disable --now domainnotice-update.path domainnotice.service 2>/dev/null || true
    systemctl stop domainnotice-update.service 2>/dev/null || true
    local u
    for u in "${UNITS[@]}"; do rm -f -- "$UNIT_DIR/$u"; done
    systemctl daemon-reload
    rm -rf -- "$INSTALL_ROOT/releases" "$INSTALL_ROOT/bin"
    rm -f -- "$INSTALL_ROOT/current"
    info "Kept: $INSTALL_ROOT/config, $INSTALL_ROOT/data, $INSTALL_ROOT/backups, the database and the $SERVICE_USER user"
}

do_purge() {
    [[ $EUID -eq 0 ]] || die "run as root: sudo $0"
    local db_mode db_name db_user
    db_mode="$(read_state DB_MODE || true)"
    db_name="$(read_state DB_NAME || echo domainnotice)"
    db_user="$(read_state DB_USER || echo domainnotice)"
    echo "This removes $INSTALL_ROOT completely (config, keys, data, backups), the $SERVICE_USER user"
    if [[ "$db_mode" == local ]]; then echo "and the local database '$db_name' with its role '$db_user'."; fi
    if [[ $UNATTENDED -eq 1 ]]; then
        [[ $ASSUME_YES -eq 1 ]] || die "--purge in unattended mode needs --yes"
    else
        local answer
        read -r -p "Type 'purge' to confirm: " answer </dev/tty || die "not confirmed"
        [[ "$answer" == purge ]] || die "not confirmed"
    fi
    do_uninstall
    if [[ "$db_mode" == local ]] && command -v psql >/dev/null 2>&1; then
        [[ "$db_name" =~ ^[A-Za-z0-9_]+$ && "$db_user" =~ ^[A-Za-z0-9_]+$ ]] || die "unexpected database name in $STATE_FILE"
        runuser -u postgres -- psql -X -q -c "DROP DATABASE IF EXISTS \"$db_name\"" || warn "could not drop the database"
        runuser -u postgres -- psql -X -q -c "DROP ROLE IF EXISTS \"$db_user\"" || warn "could not drop the role"
    elif [[ "$db_mode" == existing ]]; then
        warn "the external database was not touched"
    fi
    rm -rf -- "$INSTALL_ROOT"
    if id -u "$SERVICE_USER" >/dev/null 2>&1; then userdel "$SERVICE_USER" || warn "could not remove the user $SERVICE_USER"; fi
    info "DomainNotice was removed. PostgreSQL packages and firewall rules were left as they are."
}

main() {
    parse_args "$@"
    case "$MODE" in
        uninstall) do_uninstall; return ;;
        purge) do_purge; return ;;
    esac
    check_system
    load_secrets
    local installed=0
    if [[ -L "$INSTALL_ROOT/current" || -f "$CONFIG_FILE" ]]; then installed=1; fi
    if [[ $installed -eq 1 ]]; then
        if [[ -z "$MODE" ]]; then
            [[ $UNATTENDED -eq 0 ]] || die "DomainNotice is already installed: use --repair or --upgrade"
            echo "DomainNotice $(installed_version || echo '?') is already installed. Config and data are never overwritten."
            local choice
            tty_read choice "1) repair  2) upgrade  3) cancel" 1
            case "$choice" in 1) MODE=repair ;; 2) MODE=upgrade ;; *) exit 0 ;; esac
        fi
    else
        [[ -z "$MODE" ]] || die "DomainNotice is not installed; run without --$MODE"
        MODE=install
    fi
    case "$MODE" in
        install) do_install ;;
        repair) do_repair ;;
        upgrade) do_upgrade ;;
    esac
}

# Run only when executed (tests may source this file to call single steps).
if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then main "$@"; fi
