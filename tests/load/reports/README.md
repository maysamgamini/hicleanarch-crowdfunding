# Local load reports

Reports of `scripts/load-local.sh 0.01`, kept with the tag they were taken at. Each is a local
measurement: one machine, every container on it, its setup and load average in the report's first
lines. They show whether the design's shape holds, not what AWS would do. The costs in them are
design estimates from list prices (`tests/load/cost-design.json`).

- `s5-l15/`: the four services as compose.yaml runs them, behind the gateway, events through Moto.
- `s5-l15-at-s3-l13/`: the same profile against the one host of s3-l13 (`--at s3-l13`).

Each folder has k6's own summary (`four-services.k6.json`) and the report built from it
(`four-services.json`, `four-services.md`), with the cost estimate and the merge-back evidence of
`scripts/merge-back.sh` over `s3-l16..HEAD`.
