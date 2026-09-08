using LLama.Native;
using Xunit;
using Xunit.Abstractions;

namespace MeetingRecorder.Core.Tests;

/// <summary>
/// Proves the native inference backends actually load in this process.
/// </summary>
/// <remarks>
/// The publish step rearranges llama.cpp's per-instruction-set folders, and a
/// mistake there would not surface until a user pressed "generate minutes" and
/// got an opaque native failure. LLamaSharp's DryRun performs the real load -
/// picking an instruction-set variant and calling into the library - without
/// needing a model file, so it is the cheapest honest check that the backend is
/// present and usable.
/// </remarks>
public class NativeBackendTests
{
    private readonly ITestOutputHelper _output;

    public NativeBackendTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void LlamaCppNativeLibraryLoads()
    {
        var loaded = NativeLibraryConfig.All.DryRun(out var llama, out var llava);

        _output.WriteLine($"llama.cpp DryRun succeeded: {loaded}");
        _output.WriteLine($"  llama backend: {llama?.Metadata.ToString() ?? "(none)"}");
        _output.WriteLine($"  llava backend: {llava?.Metadata.ToString() ?? "(none)"}");

        // The boolean is the load result. The out-parameters carry descriptive
        // metadata that this version does not always populate, so they are
        // logged rather than asserted on.
        Assert.True(
            loaded,
            "llama.cpp native backend failed to load; the minutes feature would fall back to extraction on every machine.");
    }
}
