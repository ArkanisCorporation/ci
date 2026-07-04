import assert from "node:assert/strict";
import {describe, it} from "node:test";
import {formatGreeting} from "./greeting.js";

describe("formatGreeting", () => {
    it("trims the project name and formats the CI greeting", () => {
        assert.equal(formatGreeting("  arkanis ci  "), "Hello, Arkanis Ci!");
    });

    it("rejects blank project names", () => {
        assert.throws(() => formatGreeting("   "), /project name is required/);
    });
});
