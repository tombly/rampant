#!/bin/sh
# Prints Rampant's interleaved log view (see Rampant.Cli). Requires the `rampant-pi` SSH alias
# (~/.ssh/config, local to this machine - never committed) and the `rampant` CLI already
# installed on the Pi at /usr/local/bin/rampant.
ssh rampant-pi "cd ~/rampant && rampant log"
