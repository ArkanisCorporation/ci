# syntax=docker/dockerfile:1
# Mock Rust service image for wf-verify-publish-container-generic. Shows the build-time version
# stamping pattern: the workflow passes the release version as the VERSION build arg, and the
# build exposes it to rustc as APP_VERSION (read with option_env!).

# Keep in step with rust-toolchain.toml.
ARG RUST_VERSION=1.99.1

FROM rust:${RUST_VERSION}-slim-trixie AS builder
ARG RUST_VERSION
ARG VERSION=0.0.0-dev
ENV RUSTUP_TOOLCHAIN=${RUST_VERSION} \
    APP_VERSION=${VERSION}
WORKDIR /app
COPY . .
RUN cargo build --release --locked --package mock-service

FROM gcr.io/distroless/cc-debian13:nonroot AS runtime
COPY --from=builder /app/target/release/mock-service /usr/local/bin/mock-service
ENTRYPOINT ["/usr/local/bin/mock-service"]
