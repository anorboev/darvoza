// The A01-T5 e2e harness (DarvozaWebAppFactory) sets process-global env vars while it builds the real
// gateway host, then restores them. Disabling cross-class parallelization guarantees no other test reads
// those vars during that brief build window — the simplest correct isolation for env-dependent integration
// tests. The whole suite runs in well under two seconds, so the cost is negligible.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
