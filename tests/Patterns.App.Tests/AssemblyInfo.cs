using Xunit;

// Every test here boots the whole desk on the one headless Avalonia platform, and a few switch
// process-wide state on (the sync marks' flash, the input bus, the direct-output choice).
// One test at a time keeps a pixel read from ever catching another test's flash.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
