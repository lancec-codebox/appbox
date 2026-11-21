using Xunit;

// Tests change process-wide state: Console.SetOut (to capture a command's output), INDEXNODE_CONFIG_DIR and PATH (tool lookup: ProcessZipStoreTests finds ffmpeg through NodeConfig.ToolsDir()).
// Run in parallel, one test's change leaks into another (empty captured output, "ffmpeg and ffprobe are required", a lookup that finds nothing). So test classes run one after the other;
// the tests inside a class already did. A test that wants speed should not touch process state.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
