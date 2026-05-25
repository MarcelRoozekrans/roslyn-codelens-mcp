// xunit.runner.json sets parallelizeTestCollections=false, but this attribute
// codifies the intent in source and forces strict no-parallel at the xUnit
// attribute level. The *ToolTests classes share a TestSolutionFixture that wraps
// an MSBuildWorkspace; intermittent FindReferences/FindCallers/FindImplementations
// failures on cold CI runners cluster on cross-project symbol lookups (metadata
// references whose first resolution races test-time queries). Running fully serial
// matches the EventSourcing.Telemetry.Tests fix that's been stable for weeks.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
