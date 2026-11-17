//! Tiny library crate so the fixture has a unit test for cargo-nextest.

/// Builds the greeting the mock service prints.
#[must_use]
pub fn greeting(name: &str) -> String {
    format!("Hello, {name}!")
}

#[cfg(test)]
mod tests {
    use super::greeting;

    #[test]
    fn greets_by_name() {
        assert_eq!(greeting("Arkanis"), "Hello, Arkanis!");
    }
}
