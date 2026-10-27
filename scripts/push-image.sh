#!/usr/bin/env bash
# Builds and pushes one service's image, unless that tag is already in its repository: tags are
# immutable, and the service's pipeline and the sandbox run may both ask for the same commit.
# Usage: push-image.sh <repository url> <tag> <dockerfile>
set -euo pipefail
repository="${1:?usage: push-image.sh <repository url> <tag> <dockerfile>}"
tag="${2:?usage: push-image.sh <repository url> <tag> <dockerfile>}"
dockerfile="${3:?usage: push-image.sh <repository url> <tag> <dockerfile>}"

if aws ecr describe-images --repository-name "${repository#*/}" --image-ids imageTag="$tag" >/dev/null 2>&1; then
  echo "$repository:$tag is already pushed"
  exit 0
fi
docker build -f "$dockerfile" -t "$repository:$tag" .
docker push "$repository:$tag"
