# Test-only image: su, sudo and setuid-probe deliberately permit unsafe controls.
FROM debian:bookworm-slim@sha256:734a72b3d6142a71993b4bc580ca01d8ca687f77a2c422d96a961d3376b179bc AS probe-build
RUN apt-get update && apt-get install -y --no-install-recommends gcc libc6-dev \
    && rm -rf /var/lib/apt/lists/*
COPY setuid-probe.c /tmp/setuid-probe.c
RUN gcc -Wall -Wextra -Werror -O2 -o /tmp/setuid-probe /tmp/setuid-probe.c

FROM debian:bookworm-slim@sha256:734a72b3d6142a71993b4bc580ca01d8ca687f77a2c422d96a961d3376b179bc
ARG TASK_PACKAGES=""
RUN set -eu; set -f; \
    for package in $TASK_PACKAGES; do \
        case "$package" in -*|*[!a-zA-Z0-9.+:~=-]*) echo "Invalid package: $package" >&2; exit 1;; esac; \
    done; \
    apt-get update; \
    apt-get install -y --no-install-recommends util-linux passwd sudo $TASK_PACKAGES; \
    rm -rf /var/lib/apt/lists/*; \
    groupadd --gid 1000 workload; \
    useradd --uid 1000 --gid 1000 --create-home workload; \
    printf '%s\n' "$TASK_PACKAGES" > /etc/task-packages; \
    dpkg-query -W > /etc/image-packages; \
    printf 'workload ALL=(root) NOPASSWD: /usr/bin/id\n' > /etc/sudoers.d/spike-control; \
    chmod 0440 /etc/sudoers.d/spike-control; \
    printf 'auth sufficient pam_permit.so\naccount required pam_permit.so\nsession required pam_permit.so\n' > /etc/pam.d/su
COPY --from=probe-build /tmp/setuid-probe /usr/local/bin/setuid-probe
COPY drop-workload check-state probe /opt/spike/
RUN chown root:root /usr/local/bin/setuid-probe \
    && chmod 4755 /usr/local/bin/setuid-probe \
    && chmod 0555 /opt/spike/*
ENTRYPOINT ["/opt/spike/drop-workload"]
CMD ["/opt/spike/probe", "agent"]
