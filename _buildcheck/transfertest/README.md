# Document transfer test suite

Runnable harness that exercises every Type dropdown option on:

- **Master → Sub:** HQ → S1, HQ → S2  
- **Sub → Sub:** S1 → S2  

Each case: create/find source → dry-run → transfer → re-read destination (Firebird, with API fallback).

## Run

```bat
dotnet run --project _buildcheck\transfertest\transfertest.csproj
```

Requires the SqlAccountSync app (or same central DB + secrets) and AWS credentials for SQL API secrets.

## Report

Results are written to:

`_buildcheck/transfertest-results.md`

Open that file after a run. The **Summary** table is the scoreboard; case sections below it show dry-run checks, dest doc numbers, and Firebird re-read details.
