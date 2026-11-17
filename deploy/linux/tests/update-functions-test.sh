#!/usr/bin/env bash
# Tests the validation functions of deploy/linux/update.sh in isolation (no root, no systemd, no database).
#
# Usage: deploy/linux/tests/update-functions-test.sh [FIXTURES_DIR]
# Without FIXTURES_DIR the malicious tarballs are generated with python3 (make-fixtures.py). Pass a folder made
# earlier to run where python3 is missing (e.g. in an ubuntu:24.04 container, to cover GNU tar).
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../update.sh
source "$HERE/../update.sh"
set +e

TMP="$(mktemp -d "${TMPDIR:-/tmp}/dn-update-test.XXXXXX")"
trap 'chmod -R u+w "$TMP" 2>/dev/null; rm -rf "$TMP"' EXIT
FIX="${1-}"
if [[ -z "$FIX" ]]; then
    FIX="$TMP/fixtures"
    python3 -I "$HERE/make-fixtures.py" "$FIX" || { echo "cannot generate fixtures (python3 missing?)"; exit 2; }
fi

PASS=0
FAIL=0
ok() { PASS=$((PASS + 1)); echo "ok   - $*"; }
bad() { FAIL=$((FAIL + 1)); echo "FAIL - $*"; }
expect_true() { local d="$1"; shift; if "$@"; then ok "$d"; else bad "$d (${DN_ERROR-})"; fi; }
expect_false() { local d="$1"; shift; if "$@"; then bad "$d (was accepted)"; else ok "$d (${DN_ERROR-})"; fi; }

echo "# tar: $(tar --version | head -n1)"
echo "# bash: $BASH_VERSION"

# ---- versions
expect_true  "plain version"              dn_is_plain_version 1.2.3
expect_false "prerelease suffix refused"  dn_is_plain_version 1.2.3-rc1
expect_false "two parts refused"          dn_is_plain_version 1.2
expect_false "leading v refused"          dn_is_plain_version v1.2.3
expect_false "newline refused"            dn_is_plain_version $'1.2.3\n4.5.6'
expect_true  "1.10.0 > 1.9.9"             dn_version_gt 1.10.0 1.9.9
expect_false "same version not newer"     dn_version_gt 1.0.0 1.0.0
expect_false "older not newer"            dn_version_gt 0.9.9 1.0.0
expect_true  "leading zeros compare as numbers" dn_version_gt 1.0.10 1.0.09

# ---- request parsing
write_request() {
    local file="$TMP/request.conf"
    printf '%s\n' "# comment" "format=1" "run_id=0f8fad5b-d9cb-469f-a165-70867728950e" "version=${1:-1.1.0}" \
        "from_version=1.0.0" "arch=${2:-x64}" "file=${3:-DomainNotice.Server-1.1.0-linux-x64.tar.gz}" \
        "sha256=${4:-$(printf 'ab%.0s' {1..32})}" >"$file"
    echo "$file"
}
expect_true "valid request parses" dn_parse_request "$(write_request)"
[[ "$REQ_VERSION" == 1.1.0 && "$REQ_ARCH" == x64 && "$REQ_RUN_ID" == 0f8fad5b-d9cb-469f-a165-70867728950e ]] \
    && ok "request values" || bad "request values"
expect_false "version 1.1.0-beta refused" dn_parse_request "$(write_request 1.1.0-beta x64 DomainNotice.Server-1.1.0-beta-linux-x64.tar.gz)"
expect_false "path in version refused"    dn_parse_request "$(write_request '../../etc' x64 'DomainNotice.Server-../../etc-linux-x64.tar.gz')"
expect_false "file name must match version/arch" dn_parse_request "$(write_request 1.1.0 x64 ../../../etc/shadow)"
expect_false "file for other arch refused" dn_parse_request "$(write_request 1.1.0 x64 DomainNotice.Server-1.1.0-linux-arm64.tar.gz)"
expect_false "bad arch refused"           dn_parse_request "$(write_request 1.1.0 x86 DomainNotice.Server-1.1.0-linux-x86.tar.gz)"
expect_false "uppercase sha refused"      dn_parse_request "$(write_request 1.1.0 x64 DomainNotice.Server-1.1.0-linux-x64.tar.gz "$(printf 'AB%.0s' {1..32})")"
printf 'format=1\nversion=$(touch %s/pwned)\n' "$TMP" >"$TMP/evil.conf"
expect_false "command substitution not evaluated" dn_parse_request "$TMP/evil.conf"
[[ ! -e "$TMP/pwned" ]] && ok "nothing executed" || bad "request content was executed"

# ---- sums
h1="$(printf '1%.0s' {1..64})"
h2="$(printf 'F%.0s' {1..64})"
printf '%s  DomainNotice.Server-1.1.0-linux-x64.tar.gz\n%s *install.sh\n' "$h1" "$h2" >"$TMP/SHA256SUMS.txt"
[[ "$(dn_sums_lookup "$TMP/SHA256SUMS.txt" DomainNotice.Server-1.1.0-linux-x64.tar.gz)" == "$h1" ]] && ok "sums lookup" || bad "sums lookup"
[[ "$(dn_sums_lookup "$TMP/SHA256SUMS.txt" install.sh)" == "$(printf 'f%.0s' {1..64})" ]] && ok "sums lookup binary-mode line" || bad "sums lookup binary-mode line"
expect_false "sums lookup missing name" dn_sums_lookup "$TMP/SHA256SUMS.txt" DomainNotice.Server-1.1.0-linux-arm64.tar.gz
printf 'hello' >"$TMP/hello"
[[ "$(dn_sha256 "$TMP/hello")" == 2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824 ]] && ok "sha256" || bad "sha256"

# ---- safe unpack
outside="$TMP/outside"
mkdir -p "$outside"
unpack_ok() {
    local name="$1" dest="$TMP/unpack-$1"
    DN_ERROR=""
    expect_true "unpack $name" dn_safe_unpack "$FIX/$name.tar.gz" "$dest"
}
unpack_refused() {
    local name="$1" dest="$TMP/work/$1/release"
    mkdir -p "$TMP/work/$1"
    DN_ERROR=""
    expect_false "refuse $name" dn_safe_unpack "$FIX/$name.tar.gz" "$dest"
}
unpack_ok good
d="$TMP/unpack-good"
[[ -x "$d/DomainNotice.Server" && -L "$d/runtimes/libfoo.so" && -f "$d/libfoo.so" ]] && ok "good tree complete" || bad "good tree complete"
[[ ! -u "$d/deploy/update.sh" ]] && ok "setuid bit stripped" || bad "setuid bit kept"
[[ -z "$(find "$d" -perm -0002 ! -type l -print -quit)" ]] && ok "nothing world-writable" || bad "world-writable entries"
expect_false "unpack into existing folder refused" dn_safe_unpack "$FIX/good.tar.gz" "$d"

for name in absolute dotdot dotdot-inner link-outside link-absolute link-then-write hardlink fifo chardev; do
    unpack_refused "$name"
done
if realpath -m / >/dev/null 2>&1; then
    unpack_refused link-chain
else
    echo "# skipped link-chain: no GNU realpath -m here (the textual check alone accepts it)"
fi
if [[ -z "$(find "$outside" "$TMP/work" -name 'dn-evil-*' -print -quit)" && ! -e /tmp/dn-evil-absolute ]]; then
    ok "no file written outside the release folder"
else
    bad "a file escaped the release folder"
fi

# ---- path normalisation and pruning
[[ "$(dn_normalize_relpath 'a/./b/../c')" == a/c ]] && ok "normalize" || bad "normalize"
expect_false "climbing out refused" dn_normalize_relpath 'a/../../b'
expect_false "glob characters are not expanded" dn_normalize_relpath '*/../../x'
rel="$TMP/releases"
mkdir -p "$rel"/{0.9.0,1.0.0,1.1.0,1.2.0,1.10.0,.staging-2.0.0,notes}
pruned="$(dn_releases_to_prune "$rel" 3 1.10.0 1.2.0 | tr '\n' ' ')"
[[ "$pruned" == "1.0.0 0.9.0 " ]] && ok "prune keeps newest 3 ($pruned)" || bad "prune: got '$pruned'"
pruned="$(dn_releases_to_prune "$rel" 3 1.10.0 0.9.0 | tr '\n' ' ')"
[[ "$pruned" == "1.0.0 " ]] && ok "prune never removes protected versions" || bad "prune protected: got '$pruned'"

echo "# $PASS passed, $FAIL failed"
[[ $FAIL -eq 0 ]]
