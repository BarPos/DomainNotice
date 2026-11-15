#!/usr/bin/env bash
# DEV/TEST ONLY. Stops the test environment and keeps its data (./up.sh starts it again).
cd "$(dirname "${BASH_SOURCE[0]}")" && docker compose stop
