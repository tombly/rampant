#!/bin/sh
set -e

# Lays out /workspace so the ownership in PLAN-V2.md -> "Filesystem layout" actually holds, then
# hands off to the supervisor. Runs as root every boot, and is idempotent.
#
# This also permanently fixes a V1 deploy gotcha: `docker compose up` auto-creates a missing
# bind-mount source as root:root, so every full wipe of local-workspace needed a manual
# `sudo chown -R 1655:1655 local-workspace` or the agent crash-looped on UnauthorizedAccessException
# forever. Doing it here means a wipe is just a wipe.

AGENT_UID=1655
AGENT_GID=1655
BUILDER_UID=1656
BUILDER_GID=1656

# Agent-owned: the only places the agent can write. /workspace/data is deliberately empty at
# genesis - it exists so that the first self-built tool that needs to persist anything has
# somewhere to put it. Without it that tool deploys cleanly and then fails at runtime, and the
# agent (having no way to read its own crash) would just ask for the capability again.
for dir in inbox inbox/.processed outbox logs data requests/in; do
    mkdir -p "/workspace/$dir"
    chown "$AGENT_UID:$AGENT_GID" "/workspace/$dir"
    chmod 0755 "/workspace/$dir"
done

# Builder-owned, agent-readable (0755): the agent's own source, its compiled binary, and the NuGet
# cache. Readable on purpose - full transparency about what it is costs nothing once the boundaries
# are ownership rather than obscurity - but not writable by the agent, which is what stops it
# editing its own source or swapping its own binary.
for dir in agent build .nuget; do
    mkdir -p "/workspace/$dir"
    chown "$BUILDER_UID:$BUILDER_GID" "/workspace/$dir"
    chmod 0755 "/workspace/$dir"
done

# Root-owned: the supervisor's answers and its own bookkeeping. Readable by everyone, writable by
# nobody but the supervisor - the agent can see what it is allowed to spend without being able to
# change it. The one thing it cannot read anywhere is /proc/1/environ, which is where
# ANTHROPIC_API_KEY lives.
for dir in requests requests/out state; do
    mkdir -p "/workspace/$dir"
    chown root:root "/workspace/$dir"
    chmod 0755 "/workspace/$dir"
done

exec dotnet /opt/supervisor/Rampant.Supervisor.dll "$@"
