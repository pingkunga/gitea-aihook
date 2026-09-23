# Breaking Change Policy FAQ

## What counts as a breaking change?
- Route changes or deletions
- Auth or header contract changes
- Required config or environment variable changes
- Public method or JSON field removals
- Docker or deployment runtime changes

## When do we treat it as critical?
When a change can break the caller, a deployment job, or a protected merge gate.

## When do we treat it as warning?
When a migration is likely but not guaranteed to fail immediately.

## What should the reviewer do?
- Check affected consumers and deployment scripts
- Confirm compatibility notes are included
- Require a migration or rollout plan for critical findings
