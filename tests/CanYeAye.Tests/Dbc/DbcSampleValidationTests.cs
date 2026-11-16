using CanYeAye.Core.Dbc;
using Xunit.Abstractions;

namespace CanYeAye.Tests.Dbc;

/// <summary>
/// Parses every DBC placed in <c>DBCsamples</c> (copied into <c>Fixtures/DbcSamples</c>). These
/// are real-world files, so this is the broadest grammar check we have: no faults, a non-empty
/// message list, and a byte-stable save.
///
/// The samples are third-party files and are git-ignored, so the checks only run against
/// whatever is present on the machine; they are a no-op in a fresh clone or on CI.
/// </summary>
public class DbcSampleValidationTests
{
    private readonly ITestOutputHelper _output;

    public DbcSampleValidationTests(ITestOutputHelper output) => _output = output;

    private static string SamplesDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "DbcSamples");

    [Fact]
    public void ParsesWithoutFaults()
    {
        foreach (var path in Samples())
        {
            var fileName = Path.GetFileName(path);
            var file = DbcFileReader.Load(path, out var faults);

            Assert.True(
                faults.Count == 0,
                $"{fileName}: {faults.Count} fault(s), first: {(faults.Count > 0 ? faults[0].Message : string.Empty)}");

            // Sample.dbc is SavvyCAN's blank file: valid, but deliberately message-free.
            if (!string.Equals(fileName, "Sample.dbc", StringComparison.OrdinalIgnoreCase))
            {
                Assert.NotEmpty(file.Messages);
            }
        }
    }

    [Fact]
    public void SaveRoundTripIsStable()
    {
        foreach (var path in Samples())
        {
            var fileName = Path.GetFileName(path);
            var file = DbcFileReader.Load(path, out _);

            var first = DbcFileWriter.Serialize(file);
            var second = DbcFileWriter.Serialize(DbcFileReader.Parse(first, out _));

            Assert.True(first == second, $"{fileName}: writing then re-reading changed the file");
        }
    }

    private IEnumerable<string> Samples()
    {
        if (!Directory.Exists(SamplesDirectory))
        {
            _output.WriteLine(
                $"No DBC samples in {SamplesDirectory}: the folder is git-ignored and absent, so there is nothing to validate.");
            return [];
        }

        return Directory.EnumerateFiles(SamplesDirectory, "*.dbc").OrderBy(p => p);
    }
}
