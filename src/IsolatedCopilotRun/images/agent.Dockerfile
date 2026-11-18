# Agent image: pinned Copilot CLI, mailbox MCP server and privilege-drop wrappers.
# The build context is staged by the host after the CLI tarball SHA-256 is verified.
FROM debian:bookworm-slim@sha256:734a72b3d6142a71993b4bc580ca01d8ca687f77a2c422d96a961d3376b179bc
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates socat \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 1000 workload \
    && useradd --uid 1000 --gid 1000 --create-home workload \
    && dpkg-query -W > /etc/image-packages
COPY copilot mailbox-mcp start-cli drop-workload check-state /opt/copilot/
RUN chown -R root:root /opt/copilot && chmod 0555 /opt/copilot/*
