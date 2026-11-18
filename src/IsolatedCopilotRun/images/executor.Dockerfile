# Token-free executor/evaluation image. Task tools are installed at build time
# through TASK_PACKAGES; no su, sudo or setuid fixtures are added.
FROM debian:bookworm-slim@sha256:734a72b3d6142a71993b4bc580ca01d8ca687f77a2c422d96a961d3376b179bc
ARG TASK_PACKAGES=""
RUN set -eu; set -f; \
    for package in $TASK_PACKAGES; do \
        case "$package" in -*|*[!a-zA-Z0-9.+:~=-]*) echo "Invalid package: $package" >&2; exit 1;; esac; \
    done; \
    apt-get update; \
    if [ -n "$TASK_PACKAGES" ]; then apt-get install -y --no-install-recommends $TASK_PACKAGES; fi; \
    rm -rf /var/lib/apt/lists/*; \
    groupadd --gid 1000 workload; \
    useradd --uid 1000 --gid 1000 --create-home workload; \
    printf '%s\n' "$TASK_PACKAGES" > /etc/task-packages; \
    dpkg-query -W > /etc/image-packages
COPY drop-workload check-state /opt/sandbox/
RUN chown -R root:root /opt/sandbox && chmod 0555 /opt/sandbox/*
