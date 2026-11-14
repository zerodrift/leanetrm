# Produce dependency bytes in a disposable, stock Ubuntu amd64 container.
FROM ubuntu:24.04@sha256:2198e4c65076e9cb0b3d695219df667c0e3b0072da64ca3c2648aca17dcecaf5
ARG POSTGRES_PACKAGE_VERSION
ENV DEBIAN_FRONTEND=noninteractive
WORKDIR /build
COPY tools/ci/pgdg.asc tools/ci/postgresql.sources tools/ci/build_postgresql_bundle.py ./
# Keep the pristine base's status: build tools must not hide runtime dependencies.
RUN cp /var/lib/dpkg/status /build/base-status && \
    apt-get -o APT::Update::Error-Mode=any update && \
    apt-get install --no-install-recommends -y ca-certificates gnupg python3 && \
    test "$(gpg --batch --show-keys --with-colons pgdg.asc | awk -F: '$1 == "pub" { primary=1 } $1 == "fpr" && primary { print $10; primary=0 }')" = B97B0AFCAA1A47F044F244A07FCC7D46ACCC4CF8 && \
    install -D -m 0644 pgdg.asc /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc
RUN test -n "$POSTGRES_PACKAGE_VERSION" && \
    mkdir -p /build/lists/partial /build/archives/partial && \
    python3 build_postgresql_bundle.py "$POSTGRES_PACKAGE_VERSION"
CMD ["/bin/true"]
