#!/usr/bin/env bash
# Run before destroying infra/env, as the deploy role: Payments' cluster is protected from deletion
# (CQA-2100), because it holds Payments' only copy of its data. The teardown means to delete it, so it
# turns the protection off. The destroy then leaves a final snapshot named <cluster>-final, one at a
# time: the previous teardown's is deleted here. Copy it under another name first to keep it.
# Usage: STATE_BUCKET=<bucket> release-payments-database.sh   (infra/env initialised)
set -euo pipefail
tf() { "${IAC:-tofu}" -chdir=infra/env "$@"; }

cluster=$(tf output -raw payments_cluster_identifier 2>/dev/null || true)
if [ -z "$cluster" ] || ! aws rds describe-db-clusters --db-cluster-identifier "$cluster" >/dev/null 2>&1; then
  echo "Payments' cluster: nothing to release"
  exit 0
fi

aws rds modify-db-cluster --db-cluster-identifier "$cluster" --no-deletion-protection --apply-immediately >/dev/null
if aws rds describe-db-cluster-snapshots --db-cluster-snapshot-identifier "$cluster-final" >/dev/null 2>&1; then
  aws rds delete-db-cluster-snapshot --db-cluster-snapshot-identifier "$cluster-final" >/dev/null
  aws rds wait db-cluster-snapshot-deleted --db-cluster-snapshot-identifier "$cluster-final"
fi
echo "Payments' cluster $cluster may be deleted; the destroy leaves the snapshot $cluster-final"
