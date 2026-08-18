# signal-cli JSON-RPC daemon sidecar - see PLAN.md for the full rationale. Its own container
# (not baked into the main rampant image) so the .NET side never needs a JVM, keeping to one thing
# per sidecar. Java version matters: signal-cli 0.14.6 is compiled for Java 25 (class file
# version 69) - Java 21 fails with UnsupportedClassVersionError, confirmed during setup.
FROM eclipse-temurin:25-jre

ARG SIGNAL_CLI_VERSION=0.14.6
# signal-cli's official release only bundles a precompiled libsignal-client (the Rust JNI crypto
# library) for x86_64 - running it as-is on arm64 (this Pi, and Apple Silicon) fails at runtime
# with "Missing required native library dependency: libsignal-client", confirmed during setup.
# exquo/signal-libs-build publishes community arm64 builds; the version below must match the
# libsignal-client-<version>.jar bundled inside the signal-cli release above (check
# /opt/signal-cli/lib/ if upgrading SIGNAL_CLI_VERSION).
ARG LIBSIGNAL_VERSION=0.96.3

RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates zip \
    && rm -rf /var/lib/apt/lists/*

RUN curl -sL -o /tmp/signal-cli.tar.gz \
      "https://github.com/AsamK/signal-cli/releases/download/v${SIGNAL_CLI_VERSION}/signal-cli-${SIGNAL_CLI_VERSION}.tar.gz" \
    && mkdir -p /opt/signal-cli \
    && tar -xzf /tmp/signal-cli.tar.gz -C /opt/signal-cli --strip-components=1 \
    && rm /tmp/signal-cli.tar.gz \
    && ln -s /opt/signal-cli/bin/signal-cli /usr/local/bin/signal-cli

RUN curl -sL -o /tmp/libsignal-arm64.tar.gz \
      "https://github.com/exquo/signal-libs-build/releases/download/libsignal_v${LIBSIGNAL_VERSION}/libsignal_jni.so-v${LIBSIGNAL_VERSION}-aarch64-unknown-linux-gnu.tar.gz" \
    && tar -xzf /tmp/libsignal-arm64.tar.gz -C /tmp \
    && jar="/opt/signal-cli/lib/libsignal-client-${LIBSIGNAL_VERSION}.jar" \
    && zip -d "$jar" '*signal_jni*' \
    && zip -uj "$jar" /tmp/libsignal_jni.so \
    && rm /tmp/libsignal-arm64.tar.gz /tmp/libsignal_jni.so

# Registration data (identity keys, session state) persists on a named volume - without this,
# every container rebuild would force re-registering the phone number from scratch, needing a
# fresh SMS/voice code each time.
VOLUME /root/.local/share/signal-cli

# Not published to the host - reachable by the rampant container only, over the internal Docker
# network compose creates automatically (service name "signal-cli").
EXPOSE 7583

COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
ENTRYPOINT ["/entrypoint.sh"]
