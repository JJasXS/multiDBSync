#!/usr/bin/env bash
# Idempotent repository bootstrap for the SQL Account Sync Cloud Agent environment.
# Installs the .NET 8 SDK and a local Firebird 3.0 server, prepares the central
# database directory, writes a development .env, then restores and builds the solution.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

# Local-dev Firebird SYSDBA password. This is a well-known default used only by the
# throwaway Firebird instance on this isolated dev VM; it is not a production secret.
FB_PASSWORD="masterkey"
CENTRAL_DB_DIR="/var/lib/sqlsync"

echo "==> Installing system packages (.NET 8 SDK + Firebird 3.0 server)"
export DEBIAN_FRONTEND=noninteractive
sudo apt-get update -qq
sudo apt-get install -y --no-install-recommends \
  dotnet-sdk-8.0 \
  firebird3.0-server firebird3.0-utils \
  ca-certificates

echo "==> Configuring Firebird to listen on IPv4 (managed .NET client resolves localhost to 127.0.0.1)"
FB_CONF="/etc/firebird/3.0/firebird.conf"
if [ -f "$FB_CONF" ]; then
  if grep -qE '^\s*#?\s*RemoteBindAddress' "$FB_CONF"; then
    sudo sed -i 's|^\s*#\?\s*RemoteBindAddress\s*=.*|RemoteBindAddress = 0.0.0.0|' "$FB_CONF"
  else
    echo "RemoteBindAddress = 0.0.0.0" | sudo tee -a "$FB_CONF" >/dev/null
  fi
fi

echo "==> Creating shared central-database directory at ${CENTRAL_DB_DIR}"
# Shared by the Firebird server (creates/owns the .FDB) and the app process
# (does a local File.Exists/CreateDirectory check on the same path).
sudo mkdir -p "$CENTRAL_DB_DIR"
sudo chown firebird:firebird "$CENTRAL_DB_DIR"
sudo chmod 0777 "$CENTRAL_DB_DIR"

echo "==> Writing development .env (git-ignored)"
ENV_FILE="src/SqlAccountSync/.env"
if [ ! -f "$ENV_FILE" ]; then
  cat > "$ENV_FILE" <<EOF
# Local development .env for the Cloud Agent (Linux). Never commit real secrets.
ASPNETCORE_ENVIRONMENT=Development
CentralDb__Host=127.0.0.1
CentralDb__Port=3050
CentralDb__Path=${CENTRAL_DB_DIR}/SYNC.FDB
CentralDb__User=SYSDBA
CentralDb__PasswordSecretRef=env:CENTRAL_FB_PASSWORD
CENTRAL_FB_PASSWORD=${FB_PASSWORD}
Sync__AllowProductionConnections=false
Sync__AllowCrossEnvironment=false
Bootstrap__AdminEmail=admin@localhost
EOF
  echo "    wrote ${ENV_FILE}"
else
  echo "    ${ENV_FILE} already exists; leaving it untouched"
fi

# The XML-based .slnx solution format is not supported by the .NET 8 MSBuild that
# ships in Ubuntu's dotnet-sdk-8.0, so restore and build each project directly.
echo "==> Restoring and building the projects"
dotnet build src/SqlAccountSync/SqlAccountSync.csproj -c Debug
dotnet build tests/SqlAccountSync.Tests/SqlAccountSync.Tests.csproj -c Debug

echo "==> install.sh complete"
