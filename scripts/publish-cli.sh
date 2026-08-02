#!/bin/sh
# Rebuilds the `rampant` CLI and installs it on the Pi.
#
# Needed because that binary is the one piece of this system nothing else deploys: it runs natively
# on the Pi host rather than in a container, so `docker compose up --build` does not touch it and a
# `git pull` on the Pi does not replace it. A change to Rampant.Cli/ leaves the installed binary
# stale until this is run.
#
# It really does have to be self-contained: there is no .NET runtime on the Pi host at all, only
# inside the container. Hence a ~78MB single-file linux-arm64 publish, cross-compiled from the
# laptop.
#
# The CLI warns about its own staleness on every run (see Rampant.Cli/StalenessCheck.cs), so the
# usual prompt to run this is the tool itself saying so.
set -e

cd "$(dirname "$0")/.."

out=$(mktemp -d)
trap 'rm -rf "$out"' EXIT

echo "publishing linux-arm64..."
dotnet publish Rampant.Cli/Rampant.Cli.csproj \
    -c Release \
    -r linux-arm64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -o "$out" \
    --nologo -v q

echo "copying to the Pi..."
scp -q "$out/rampant" rampant-pi:/tmp/rampant-new

# install(1) rather than cp: sets the mode in one step, and replaces the file rather than writing
# through it, so a running invocation is not corrupted mid-read.
ssh rampant-pi 'sudo install -m 0755 /tmp/rampant-new /usr/local/bin/rampant && rm -f /tmp/rampant-new'

echo "installed:"
ssh rampant-pi 'stat -c "  %y  %n" /usr/local/bin/rampant'
