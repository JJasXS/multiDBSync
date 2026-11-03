# Security protections for env and secret files

This repo blocks confidential `.env` and private-key files from normal Git commits and reduces Cursor indexing/context exposure. **Protection is not absolute.**

## Git ignore

`.gitignore` ignores:

- `.env` and `.env.*` in any directory
- Common key/cert patterns (`*.pem`, `*.pfx`, `*.p12`, `*.key`, `*.ppk`, `id_rsa`, …)
- Existing `secrets/` paths

Templates stay allowed:

- `.env.example`
- `**/.env.example`

## Cursor ignore

Files:

- `.cursorignore` — reduces default AI context inclusion
- `.cursorindexingignore` — reduces indexing

Both exclude real env/secret files, `_buildcheck/`, and generated `bin`/`obj`/`publish`/`out` trees. Application source under `src/` and `tests/` is not excluded.

### Cursor limitations (important)

- Ignored files can still be opened if a path is given explicitly to an agent or tool.
- Terminal / shell commands can still read ignored files.
- Ignore files do **not** replace secret management or key rotation.
- Do not assume Cursor cannot see a local `.env`.

## Pre-commit hook (filename only)

Version-controlled hook: `.githooks/pre-commit`

- Inspects **staged path names only** (never file contents)
- Blocks real `.env` / `.env.*` (except basename `.env.example`)
- Blocks private key / cert filenames
- Also blocks secrets staged with `git add -f`

### Windows setup

From the repo root (`SqlAccountSync`):

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-git-hooks.ps1
```

Or:

```powershell
git config core.hooksPath .githooks
```

Requires Git for Windows (hook runs under `sh`). Confirm:

```powershell
git config --get core.hooksPath
```

Expected: `.githooks`

### Hook bypass limitations

The hook does **not** stop:

- `git commit --no-verify`
- Unsetting / changing `core.hooksPath`
- Commits from clones where the hook was never installed
- Rewriting history or pushing already-created commits
- Secrets pasted into tracked source files under other names

## Local `.env` files

Copy from the template only:

```powershell
copy src\SqlAccountSync\.env.example src\SqlAccountSync\.env
```

Never commit real `.env` files. Never put live credentials in `.env.example`.
