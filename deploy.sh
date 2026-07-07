#!/usr/bin/env bash
#
# Redeploy the neo4gen demo to Fly.io.
#
#   neo4gen          -> .NET server: API + /agui + the Vite SPA (built into wwwroot)   https://neo4gen.fly.dev
#   neo4gen-copilot  -> Bun/Hono CopilotKit runtime bridge                             https://neo4gen-copilot.fly.dev
#
# Usage:
#   ./deploy.sh            # build + deploy both apps (server first, then the runtime)
#   ./deploy.sh server     # deploy only the server
#   ./deploy.sh copilot    # deploy only the copilot runtime
#
# Notes:
#   * Runtime secrets (ConnectionStrings__Neo4j, OpenAI__ApiKey, AuraAgent__ClientSecret) are NOT
#     managed here. Set them once with `fly secrets set -a neo4gen ...`.
#   * --local-only builds with your local Docker daemon (reuses layer cache). Drop it to use Fly's
#     remote builder instead.
set -euo pipefail

# Always operate from the repo root (the directory this script lives in).
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

deploy_server() {
  echo "==> Deploying server (neo4gen) ..."
  flyctl deploy ./src \
    -a neo4gen \
    -c "$ROOT/fly.server.toml" \
    --dockerfile Dockerfile.server \
    --local-only
}

deploy_copilot() {
  echo "==> Deploying copilot runtime (neo4gen-copilot) ..."
  flyctl deploy ./src/copilot-runtime \
    -a neo4gen-copilot \
    -c "$ROOT/fly.copilot.toml" \
    --dockerfile Dockerfile \
    --local-only
}

verify() {
  echo "==> Verifying public endpoints ..."
  printf '  server  /health : '; curl -fsS -o /dev/null -w '%{http_code}\n' https://neo4gen.fly.dev/health || echo "FAIL"
  printf '  server  /       : '; curl -fsS -o /dev/null -w '%{http_code}\n' https://neo4gen.fly.dev/       || echo "FAIL"
  printf '  copilot /health : '; curl -fsS -o /dev/null -w '%{http_code}\n' https://neo4gen-copilot.fly.dev/health || echo "FAIL"
}

target="${1:-all}"
case "$target" in
  server)  deploy_server ;;
  copilot) deploy_copilot ;;
  all)     deploy_server; deploy_copilot ;;  # server first: the runtime points at its /agui
  *) echo "Unknown target '$target' (use: server | copilot | all)" >&2; exit 1 ;;
esac

verify
echo "==> Done. App: https://neo4gen.fly.dev/"
