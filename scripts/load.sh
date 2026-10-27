#!/usr/bin/env bash
# Runs the Round 2 load profile (tests/load/round2.js) against a deployed sandbox and writes the
# report to artifacts/load/. Creates one creator and enough backers in the sandbox's Cognito user
# pool: one live pledge per backer per campaign means a launch of N pledges needs N backers.
#
# Usage: scripts/load.sh <scale>        (0.02 = 60 pledges a second; 1 = the loop's full 3,000)
# Needs: AWS credentials for the sandbox, OpenTofu outputs of infra/env, k6, jq.
set -euo pipefail
scale="${1:?usage: load.sh <scale>}"
out() { "${IAC:-tofu}" -chdir=infra/env output -raw "$1"; }

api=$(out api_url)
public=$(out public_reads_url 2>/dev/null || echo "$api")
pool=$(out user_pool_id)
client=$(out user_pool_client_id)
backers=$(awk -v s="$scale" 'BEGIN { n = int(3000 * s * 60 + 0.5); print (n < 50 ? 50 : n) }')
workers="${LOAD_USER_WORKERS:-8}" # parallel Cognito calls; Cognito throttles user creation per account
dir=artifacts/load
mkdir -p "$dir"
# The tokens are valid for an hour; never keep them, whatever happens.
trap 'rm -f "$dir/tokens.json" "$dir/backer-tokens.txt"' EXIT

# One user, its group, and an access token. Prints the token.
user_token() {
  local email="$1" group="$2" password="Load-${GITHUB_RUN_ID:-local}-$3-Aa1!"
  for attempt in 1 2 3 4 5; do
    if aws cognito-idp admin-create-user --user-pool-id "$pool" --username "$email" --message-action SUPPRESS >/dev/null 2>&1 \
       || aws cognito-idp admin-get-user --user-pool-id "$pool" --username "$email" >/dev/null 2>&1; then
      aws cognito-idp admin-set-user-password --user-pool-id "$pool" --username "$email" --password "$password" --permanent \
        && aws cognito-idp admin-add-user-to-group --user-pool-id "$pool" --username "$email" --group-name "$group" \
        && aws cognito-idp initiate-auth --client-id "$client" --auth-flow USER_PASSWORD_AUTH \
             --auth-parameters "USERNAME=$email,PASSWORD=$password" --query 'AuthenticationResult.AccessToken' --output text \
        && return 0
    fi
    sleep $((attempt * 2)) # throttled: back off and try again
  done
  echo "could not sign in $email" >&2
  return 1
}
export -f user_token
export pool client

echo "Creating 1 creator and $backers backers in $pool ..."
creator=$(user_token "load-creator@example.com" creator 0)
seq 1 "$backers" | xargs -P "$workers" -I{} bash -c 'user_token "load-backer-{}@example.com" backer {}' > "$dir/backer-tokens.txt"
test "$(wc -l < "$dir/backer-tokens.txt")" -ge "$backers"

jq -n --arg creator "$creator" --rawfile tokens "$dir/backer-tokens.txt" \
  '{creator: $creator, backers: ($tokens | split("\n") | map(select(length > 0)))}' > "$dir/tokens.json"

# k6 opens files relative to the script, so the tokens file is given as an absolute path.
API_URL="$api" PUBLIC_URL="$public" SCALE="$scale" TOKENS_FILE="$PWD/$dir/tokens.json" REPORT_DIR="$dir" \
  k6 run --summary-export "$dir/k6-summary.json" "${LOAD_SCRIPT:-tests/load/round2.js}"
