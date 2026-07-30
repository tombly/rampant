#!/bin/sh
# Starts Rampant - routine lifecycle only (docker compose up -d, no rebuild). Deploying new code
# is still the wipe-and-reseed procedure documented in the local working notes, not this.
ssh rampant-pi "cd ~/rampant && rampant start"
