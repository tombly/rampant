#!/bin/sh
# Stops Rampant (docker compose stop) - containers are preserved, so starting again doesn't
# need a rebuild.
ssh rampant-pi "cd ~/rampant && rampant stop"
