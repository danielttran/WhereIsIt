// Disable test parallelisation across collections.
//
// The native-engine fixture scans the filesystem and writes a persistent
// `index.dat`; the new RandomFileFixture-backed tests scan their own random
// trees. Running them in parallel slowed the native scanner enough that
// the fixture sometimes published 0 records — the tests then failed
// "Search('subdir') returns empty". Sequential class runs side-step that.
//
// Within a class, xUnit still runs facts serially (default for non-async).
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
