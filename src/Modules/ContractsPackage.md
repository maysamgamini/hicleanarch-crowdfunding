# CrowdFunding contracts

The messages one module of the crowdfunding platform publishes for the others: its integration events,
and the commands another service may send over HTTP with their replies.

- `contract/contract.json`: the JSON schema of every event (with the event's `version`) and of every
  remote endpoint's request and reply, as the producer's snapshot test pinned them for this version.
- `contract/examples/`: recorded messages of every version still allowed. A consumer's tests read them
  with its own strict options (`ContractJson.Options` in `CrowdFunding.BuildingBlocks.Application`).

Versions follow SemVer. Within a major version a contract only gains optional fields (nullable, with a
default), new events and new endpoints. Removing, renaming or newly requiring a field, or changing its
type or meaning, is a new major version: a new event type or endpoint path beside the old one, which
stays until every consumer has moved. A reader ignores fields it does not know and refuses a message
that lacks a required one.
