# SDK image, not runtime/aspnet - the mutable agent code must keep rebuilding itself inside the
# running container indefinitely. A deliberate ~800MB+ size trade-off; see PLAN.md.
FROM mcr.microsoft.com/dotnet/sdk:10.0

RUN apt-get update && apt-get install -y --no-install-recommends \
    git \
    ca-certificates \
    curl \
    && rm -rf /var/lib/apt/lists/*

RUN useradd -m agentrunner

# Claude Code CLI, installed as agentrunner so the binary lands somewhere that user can run it
# (the standalone installer installs into the invoking user's home directory).
USER agentrunner
RUN curl -fsSL https://claude.ai/install.sh | bash
ENV PATH="/home/agentrunner/.local/bin:${PATH}"
USER root

# Supervisor: built once at image-build time, baked into /opt, root-owned and read-only to
# agentrunner - even if Claude Code were ever misdirected outside its own working directory, it
# cannot modify the supervisor.
WORKDIR /src
COPY Directory.Packages.props ./
COPY Rampant.Supervisor/ ./Rampant.Supervisor/
RUN dotnet publish Rampant.Supervisor/Rampant.Supervisor.csproj -c Release -o /opt/supervisor

# Genesis agent template - copied into /workspace/agent on first boot by SeedBootstrap, then
# built/run through the exact same path as every subsequent self-modification.
COPY Rampant.Seed/ /opt/seed/

# NuGet cache lives on the persistent volume so a container rebuild doesn't force re-downloading
# every package the agent has pulled in.
ENV NUGET_PACKAGES=/workspace/.nuget/packages

RUN mkdir -p /workspace && chown -R agentrunner:agentrunner /workspace
VOLUME /workspace

USER agentrunner
WORKDIR /workspace
ENTRYPOINT ["dotnet", "/opt/supervisor/Rampant.Supervisor.dll"]
