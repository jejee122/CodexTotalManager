# Account usage ledger retention

The account-usage store is intentionally append-only in `3.0.0-rc.29`.
Monthly files are partitions, not disposable logs. They are the durable source
for all of the following:

- historical token and request totals;
- per-account attribution and audit;
- idempotency and request-membership evidence that prevents a rotated or
  reordered source from being charged twice;
- quota prepare/fact/commit recovery after a crash;
- authenticated projection-checkpoint rebuilds.

## Current policy

No runtime path automatically deletes or rewrites an old monthly ledger,
anomaly ledger, cursor, membership index or projection checkpoint. This is a
deliberate fail-closed policy: disk growth is visible, but historical totals and
deduplication evidence are not silently damaged.

## Required compaction transaction

A future compactor may be enabled only after all of these stages are implemented
and covered by crash/restart tests:

1. Acquire the token, quota, derived-index and identity-domain locks in the same
   global order used by normal reads and writes.
2. Select only closed UTC months; never rewrite the active month.
3. Stream-validate every JSONL row, identity key ID, payload hash, occurrence
   witness and quota prepare/fact/commit relationship.
4. Build a temporary compacted segment plus compact idempotency and membership
   indexes. Preserve the latest 80 detailed attempts and the latest successful
   and failed quota windows needed by the UI.
5. Rebuild a fresh ledger instance from the temporary candidate and compare the
   complete immutable snapshot: account totals, request-scope totals, invalid and
   mismatch counters, stored counts, quota views, anomalies and health state.
6. Re-submit every compacted event identity and prove it is rejected as an
   idempotent duplicate rather than appended again.
7. Bind the candidate to the current key-domain ID and publish a signed manifest
   containing the old/new file hashes and the pre/post snapshot digest.
8. Atomically switch a small generation pointer. Keep the prior generation
   untouched until a second process can reopen and verify the new generation.
9. On any interruption before pointer publication, ignore and remove only the
   owned temporary generation. On any post-publication verification failure,
   roll the pointer back to the prior generation.

Simply gzipping, summarizing or deleting old JSONL files is not a valid
implementation: it would either make totals disappear or lose replay evidence.

## Acceptance tests before enabling automatic retention

- compression preserves all totals and quota views exactly;
- reordered and re-imported source rows remain duplicates;
- crash after every transaction stage recovers one complete generation;
- concurrent reader/writer processes never observe a mixed generation;
- tampering with a compacted segment, manifest, index or pointer fails closed;
- rollback restores the prior generation byte-for-byte;
- the active month and source cursor are never changed by retention.

Until those tests exist, automatic ledger retention remains disabled and must
not be described as implemented.
