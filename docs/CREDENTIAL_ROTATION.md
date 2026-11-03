# Credential rotation procedures

Do **not** rotate credentials automatically from the app. Coordinate outages before changing Firebird or AWS secrets.

## Central Firebird (`CENTRAL_FB_MASTERKEY` / app user)

1. Create or identify a least-privilege Firebird user (prefer `SYNC_APP` over daily use of `SYSDBA`).
2. Grant only the rights needed for `SYNC_*` tables on the central database.
3. Update the local/service `.env` (or Secrets Manager ref) with the new password.
4. Restart the SqlAccountSync Windows service / process.
5. Confirm `/health` returns `{"status":"ok"}` and login still works.
6. Revoke or change the old password after the service is healthy.

Never commit the new password. Never use Firebird’s default `masterkey` outside disposable local sandboxes.

## AWS access keys / Secrets Manager

1. Create a new IAM access key (or rotate the secret version in Secrets Manager).
2. Update the host environment / `.env` (`AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY`) or instance role.
3. Restart the app so the AWS SDK picks up credentials.
4. Disable the old access key only after OTP email and SDK company logins succeed.
5. IAM should be least privilege: `secretsmanager:GetSecretValue` on `proacc/tenant/{TENANT_CODE}/*` only.

## SMTP app password

1. Generate a new SMTP app password in the mail provider.
2. Update the Secrets Manager secret referenced by the tenant (`smtpCredentialsSecretRef` / `smtpAppPasswordSecretRef`).
3. Request a login OTP and confirm delivery.
4. Revoke the previous app password.

## OTP HMAC pepper (multi-instance)

1. Generate a long random string and store it in Secrets Manager or the host environment.
2. Set `Sync__OtpHashPepperSecretRef=env:OTP_HMAC_PEPPER` (or `sm:your-secret-id`).
3. Restart every app instance so they share the same pepper.
4. Do not commit the pepper value.

## AllowedHosts / reverse proxy

1. Production **requires** an explicit `AllowedHosts` value (semicolon-separated public DNS names). Startup refuses `*` or empty.
2. List only trusted reverse-proxy addresses under `ForwardedHeaders:KnownProxies` / `KnownNetworks` (CIDR). Do not clear the default loopback trust to “allow all”.
3. Auth cookies use `Secure` in Production — TLS (or trusted `X-Forwarded-Proto`) is required.

## If secrets appeared in Git history

1. Rotate the exposed credential immediately (do not wait for history cleanup).
2. Remove the file from the current tree (already done for `_buildcheck/`, `_run.log`, `_api500.html`).
3. History rewrite (`git filter-repo` / BFG) is a coordinated ops task — **do not** force-push without team agreement.
