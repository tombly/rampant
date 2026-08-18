# SDK image, not runtime/aspnet - the mutable agent code must keep rebuilding itself inside the
# running container indefinitely. A deliberate ~800MB+ size trade-off; see PLAN.md.
FROM mcr.microsoft.com/dotnet/sdk:10.0

RUN apt-get update && apt-get install -y --no-install-recommends \
    git \
    ca-certificates \
    curl \
    && rm -rf /var/lib/apt/lists/*

# Three users, and that split is what makes every boundary in PLAN.md real rather than
# conventional:
#
#   root       the supervisor (pid 1). Holds ANTHROPIC_API_KEY, the Signal socket and the spend
#              ledger. Owns nothing inside /workspace.
#   builder    Claude Code, git and dotnet build. Owns /workspace/{agent,build,.nuget}.
#   agentrunner the agent. Owns /workspace/{inbox,outbox,logs,data,requests/in} and nothing else.
#
# uid/gid are pinned rather than left to useradd's next-free counter: the bind-mounted workspace on
# the host carries these numbers, and a shifted uid silently breaks every permission below. Change
# either number and every existing local-workspace needs chown-ing to match.
RUN groupadd -g 1655 agentrunner && useradd -m -u 1655 -g 1655 agentrunner \
 && groupadd -g 1656 builder     && useradd -m -u 1656 -g 1656 builder \
 && chmod 0700 /home/builder

# Claude Code CLI, installed as builder into /home/builder/.local/bin. It runs under that uid and
# nothing else can reach it: the home is 0700 and belongs to a different user than the agent.
#
# It must not run as root. Claude Code refuses --dangerously-skip-permissions outright when it
# detects root privileges - and beyond that refusal, a root Claude Code could rewrite
# /opt/supervisor below, which would quietly void the first of PLAN.md's four boundaries. Under its
# own uid it can write the agent's source and nothing else.
USER builder
RUN curl -fsSL https://claude.ai/install.sh | bash
USER root

# Supervisor: built once at image-build time, baked into /opt, root-owned and read-only to
# agentrunner - even if Claude Code were ever misdirected outside its own working directory, it
# cannot modify the supervisor.
WORKDIR /src
COPY Rampant.Supervisor/ ./Rampant.Supervisor/
RUN dotnet publish Rampant.Supervisor/Rampant.Supervisor.csproj -c Release -o /opt/supervisor

# Genesis agent template - copied into /workspace/agent on first boot by SeedBootstrap, then
# built/run through the exact same path as every subsequent self-modification.
COPY Rampant.Seed/ /opt/seed/

# NuGet cache lives on the persistent volume so a container rebuild doesn't force re-downloading
# every package. Owned by root: the supervisor is the only thing that builds.
ENV NUGET_PACKAGES=/workspace/.nuget/packages

RUN mkdir -p /workspace
VOLUME /workspace

# Runs as root and stays root: the entrypoint lays out /workspace ownership (which needs CHOWN
# against a bind mount compose creates as root:root), then execs the supervisor, which drops to
# agentrunner only for the agent process it spawns. See docker-entrypoint.sh.
COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod 0755 /usr/local/bin/docker-entrypoint.sh

WORKDIR /workspace
ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
