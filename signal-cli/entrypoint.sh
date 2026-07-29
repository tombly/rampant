#!/bin/sh
set -e

# No args -> normal operation: start the JSON-RPC daemon. Any args -> pass through as a one-off
# signal-cli subcommand against the same account (e.g. one-time registration):
#   docker compose run --rm signal-cli register --voice
#   docker compose run --rm signal-cli verify 123456
if [ "$#" -eq 0 ]; then
    exec signal-cli -a "$SIGNAL_PHONE_NUMBER" daemon --tcp 0.0.0.0:7583 --receive-mode=on-start
else
    exec signal-cli -a "$SIGNAL_PHONE_NUMBER" "$@"
fi
