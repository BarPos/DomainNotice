#!/usr/bin/env bash
# DEV/TEST ONLY. Adds your Windows test PC and/or user to the test domain so AD targets match.
#   testenv/add-ad-object.sh computer WINPC-NAME     → CN=WINPC-NAME in OU=Workstations (name-fallback AD link)
#   testenv/add-ad-object.sh user     USERNAME       → user in OU=Sales,OU=Staff, password USER_PASSWORD from .env
#                                                       (the agent reports MACHINE\user; matching uses the user part)
#   testenv/add-ad-object.sh member   GROUP NAME     → add a user (or 'PC$' for a computer) to a group
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"; set -a; source .env; set +a
case "${1-}" in
    computer) docker exec dn-dc samba-tool computer create "$2" --computerou="OU=Workstations" ;;
    user)     docker exec dn-dc samba-tool user create "$2" "$USER_PASSWORD" --userou="OU=Sales,OU=Staff"
              docker exec dn-dc samba-tool user setexpiry "$2" --noexpiry ;;
    member)   docker exec dn-dc samba-tool group addmembers "$2" "$3" ;;
    *) sed -n '2,6p' "$0"; exit 2 ;;
esac
echo "Done. DomainNotice picks it up at the next incremental sync (1 min) or Directory → Sync now."
