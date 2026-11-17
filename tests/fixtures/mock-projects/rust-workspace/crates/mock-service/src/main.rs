//! Mock service binary. Prints its greeting and the version stamped at build time.

/// Release version injected by the image build (`ARG VERSION` -> `ENV APP_VERSION`), or the
/// Cargo package version for local builds.
const VERSION: &str = match option_env!("APP_VERSION") {
    Some(version) => version,
    None => env!("CARGO_PKG_VERSION"),
};

fn main() {
    println!("{} (mock-service {VERSION})", mock_greeting::greeting("CI"));
}
