#!/usr/bin/env bash
# DEV/TEST ONLY. Removes the test containers AND their volumes (domain, database, DomainNotice data).
# testenv/.env (passwords) is kept; delete it too for new passwords.
cd "$(dirname "${BASH_SOURCE[0]}")" && docker compose down -v --remove-orphans
