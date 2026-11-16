using CanYeAye.Core.Dbc;

namespace CanYeAye.Tests.Dbc;

/// <summary>
/// Parses every DBC placed in <c>DBCsamples</c> (copied into Fixtures/DbcSamples). These are
/// real-world files, so this is the broadest grammar check we have: no faults, a non-empty
/// message list, and a byte-stable save.
/// </summary>
public class DbcSampleValidationTests
{
    private static string SamplesDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "DbcSamples");

    public static IEnumerable<object[]> Samples()
    {
        foreach (var path in Directory.EnumerateFiles(SamplesDirectory, "*.dbc").OrderBy(p => p))
        {
            yield return [Path.GetFileName(path)];
        }
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void ParsesWithoutFaults(string fileName)
    {
        var path = Path.Combine(SamplesDirectory, fileName);
        var file = DbcFileReader.Load(path, out var faults);

        Assert.True(faults.Count == 0, $"{fileName}: {faults.Count} fault(s), first: {(faults.Count > 0 ? faults[0].Message : string.Empty)}");

        // Sample.dbc is SavvyCAN's blank file: valid, but deliberately message-free.
        if (!string.Equals(fileName, "Sample.dbc", StringComparison.OrdinalIgnoreCase))
        {
            Assert.NotEmpty(file.Messages);
        }
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void SaveRoundTripIsStable(string fileName)
    {
        var path = Path.Combine(SamplesDirectory, fileName);
        var file = DbcFileReader.Load(path, out _);

        var first = DbcFileWriter.Serialize(file);
        var second = DbcFileWriter.Serialize(DbcFileReader.Parse(first, out _));

        Assert.Equal(first, second);
    }
}
