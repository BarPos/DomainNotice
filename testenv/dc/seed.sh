#!/bin/bash
# Test objects for DomainNotice (runs once, at provisioning). Passwords come from the environment.
set -euo pipefail
BASE="DC=corp,DC=test"
USER_PASSWORD="${USER_PASSWORD:?}"
BIND_PASSWORD="${BIND_PASSWORD:?}"
st() { samba-tool "$@" >/dev/null; }

for ou in "OU=Workstations" "OU=Servers" "OU=Staff" "OU=Sales,OU=Staff" "OU=IT,OU=Staff" "OU=Service Accounts"; do
    st ou create "$ou,$BASE"
done

# Bind account for DomainNotice (LDAP sync + Kerberos SPN for the computer proof).
st user create svc-domainnotice "$BIND_PASSWORD" --userou="OU=Service Accounts" --description="DomainNotice bind account"
st user setexpiry svc-domainnotice --noexpiry
st spn add DomainNotice/server.corp.test svc-domainnotice
# AES tickets for the SPN (plan 3.4 note).
ldbmodify -H /var/lib/samba/private/sam.ldb <<LDIF >/dev/null
dn: CN=svc-domainnotice,OU=Service Accounts,$BASE
changetype: modify
replace: msDS-SupportedEncryptionTypes
msDS-SupportedEncryptionTypes: 24
LDIF

user() { # user NAME OU GIVEN SURNAME
    st user create "$1" "$USER_PASSWORD" --userou="$2" --given-name="$3" --surname="$4" --mail-address="$1@corp.test"
    st user setexpiry "$1" --noexpiry
}
user alice   "OU=Sales,OU=Staff" Alice Anders
user bob     "OU=Sales,OU=Staff" Bob Brown
user carol   "OU=IT,OU=Staff"    Carol Clark
user dave    "OU=IT,OU=Staff"    Dave Doe
user dnadmin "OU=IT,OU=Staff"    DomainNotice Admin
user dnviewer "OU=IT,OU=Staff"   DomainNotice Viewer

group() { st group add "$1" --groupou="$2" --description="$3"; }
group "IT Admins"            "OU=IT,OU=Staff" "Nested into DomainNotice Admins"
group "DomainNotice Admins"  "OU=IT,OU=Staff" "Console administrators (Directory:AdminGroups)"
group "DomainNotice Viewers" "OU=IT,OU=Staff" "Console read-only (Directory:ViewerGroups)"
group "Sales Team"           "OU=Sales,OU=Staff" "Sales users"
group "All Staff"            "OU=Staff" "Sales Team + IT users (nested)"
group "Pilot Workstations"   "OU=Workstations" "Computer group target"

st group addmembers "IT Admins" dnadmin
st group addmembers "DomainNotice Admins" "IT Admins"     # nested: dnadmin is an admin via IT Admins
st group addmembers "DomainNotice Viewers" dnviewer
st group addmembers "Sales Team" alice,bob
st group addmembers "All Staff" "Sales Team",carol,dave

for pc in PC-01 PC-02 PC-03; do
    st computer create "$pc" --computerou="OU=Workstations"
done
st group addmembers "Pilot Workstations" 'PC-01$'

echo "==> seeded test users, groups, OUs and computers"
