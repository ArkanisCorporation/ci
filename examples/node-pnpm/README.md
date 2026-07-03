# node-pnpm

Minimal consumer wrapper:

```yaml
name: build

on:
  pull_request:
  push:
    branches: [main]

permissions: {}

jobs:
  node:
    name: repo @ ${{ github.head_ref || github.ref_name }}
    uses: ArkanisCorporation/ci/.github/workflows/wf-setup-node.yml@v1
    permissions:
      contents: read
    with:
      runs-on: ubuntu-latest
      runs-on-self-hosted: false
      node-version: 24.x
      package-manager: pnpm
      package-manager-version: "10"
      working-directory: .
      enable-cache: true
```
