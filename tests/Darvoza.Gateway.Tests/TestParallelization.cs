// The A01-T5 e2e harness (DarvozaWebAppFactory) sets process-global env vars while it builds the real
// gateway host, then restores them. The race it must prevent is CROSS-CLASS: another test class (e.g.
// GatewayOptionsTests) reading DARVOZA_POLICY_PATH during that window. A [Collection]-scoped disable would
// NOT fix this — it only serializes tests within one collection, leaving other collections free to run in
// parallel and read the vars mid-window. Assembly-wide disable is therefore the correct (and simplest)
// isolation here. The whole suite runs in well under two seconds, so the cost is negligible.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
