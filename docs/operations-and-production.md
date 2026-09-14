# Operations, privacy, and production readiness

## Preview operating envelope

- Exactly one App Service instance.
- Scheduled private-chat meetings only.
- Small pilot population and non-sensitive meetings.
- Local JSON persistence under persistent App Service `/home` storage.
- No availability SLA beyond the selected Azure service tiers.

Configure App Service scale-out maximum to one. Multiple instances can process the same job, overwrite JSON files, and send duplicate notifications because locking is process-local.

## Monitoring

Alert on:

- App Service 5xx rate, restarts, CPU, memory, file-system usage, and health-check failures.
- Repeated Graph `403`, `429`, and `5xx` responses.
- Jobs reaching `Unavailable` or `Failed`.
- Q&A queue rejection and timeout messages.
- Azure OpenAI throttling, latency, token consumption, quota, and content-filter events.
- Client-secret expiry and role or policy changes.

Set `BUILD_COMMIT` during deployment so `/version` identifies the running revision. Do not log transcript bodies, prompts, answers, access tokens, or secrets.

## Retention and deletion

`TranscriptIngestion__RetentionPeriod` defaults to 30 days. Cleanup deletes expired job JSON and its referenced source document. This is convenience cleanup, not a compliance-grade deletion system: it has no deletion certificate, legal hold, backup purge, per-user erasure API, or immutable audit trail.

Before real use, define:

- meeting notice and participant consent;
- purpose limitation and acceptable use;
- data classification and approved model geography;
- retention by meeting type;
- user access, correction, export, and deletion handling;
- legal hold and eDiscovery requirements;
- incident response and breach notification;
- human review for high-impact decisions.

## Required production architecture work

1. Replace file jobs with a durable queue supporting visibility timeout, poison handling, and idempotency keys.
2. Store transcripts in encrypted object storage and metadata in a transactional database with tenant partitioning.
3. Use distributed leases or transactional outbox semantics so readiness notifications are delivered once.
4. Add private networking, firewall restrictions, Key Vault, secretless bot authentication where supported, and tested credential rotation.
5. Implement explicit authorization for who may ask questions in each meeting scope.
6. Add per-tenant quotas, global admission control, cost budgets, and abuse protection.
7. Add Graph pagination, response-size limits, explicit retry/backoff telemetry, and dependency readiness probes.
8. Move from syntactic citation validation to structured answers and claim-level source verification.
9. Add deletion/export APIs, legal hold, audit records, backup/restore, and disaster recovery tests.
10. Use deployment slots, staged rollout, rollback automation, SBOM/provenance, dependency license review, and security scanning.
11. Complete threat modeling, privacy review, accessibility review, penetration testing, and operational readiness review.

## Known implementation limitations

- Notification and job completion are not transactional; a crash can cause a missing or duplicate readiness card.
- Graph SDK retries may occur, but the app does not persist `Retry-After` or detailed dependency telemetry.
- The readiness endpoint confirms the app started, not that Graph, Azure OpenAI, storage, or Teams are reachable.
- Context selection is lexical rather than semantic.
- Citations prove that labels were selected, not that every model claim is entailed by the cited text.
- Model prompts are bounded by chunk count, not a tokenizer-aware budget.
- The bootstrap credential remains a client secret requiring rotation.

Do not label a deployment production-ready until these risks have owners, decisions, and validation evidence.
