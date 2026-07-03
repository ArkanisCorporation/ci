// Summary:
//   semantic-release config for the CI platform repository.
// Remarks:
//   This repository publishes GitHub release metadata only.
//   Package publishing, image publishing, and deployment stay in separate workflows.

module.exports = {
  branches: ["main"],
  tagFormat: "v${version}",
  plugins: [
    "@semantic-release/commit-analyzer",
    "@semantic-release/release-notes-generator",
    "@semantic-release/github",
  ],
};
