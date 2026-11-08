using Lis.Core.Instruments;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests.Instruments;

/// <summary>Đoán mã kênh máy ↔ chỉ số chỉ từ chỉ định của từng barcode.</summary>
public class ChannelMapInferenceTests
{
    private static readonly Guid Glu = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Ast = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Alt = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid Cre = Guid.Parse("00000000-0000-0000-0000-000000000004");

    private static readonly Dictionary<Guid, ValueRange> Ranges = new()
    {
        [Glu] = new(3.9m, 6.4m),
        [Ast] = new(null, 40m),
        [Alt] = new(null, 40m),
        [Cre] = new(62m, 106m),
    };

    private static ChannelObservation Obs(string code, string barcode, decimal value, params Guid[] candidates) =>
        new(code, barcode, value, candidates.ToHashSet());

    [Fact]
    public void SingleUnmappedTest_IsCertain()
    {
        var result = ChannelMapInference.Infer([Obs("001", "A", 5.6m, Glu)], Ranges);

        result.ShouldHaveSingleItem().ShouldBe(new ChannelSuggestion("001", [Glu], 1), new SuggestionComparer());
        result[0].IsCertain.ShouldBeTrue();
    }

    [Fact]
    public void IntersectionAcrossSamples_ThenElimination()
    {
        // Mẫu A: GLU, AST, ALT. Mẫu B: AST, CRE. Mẫu C: ALT, CRE.
        var result = ChannelMapInference.Infer(
        [
            Obs("002", "A", 32m, Glu, Ast, Alt), Obs("003", "A", 28m, Glu, Ast, Alt), Obs("001", "A", 5.6m, Glu, Ast, Alt),
            Obs("002", "B", 25m, Ast, Cre), Obs("010", "B", 80m, Ast, Cre),
            Obs("003", "C", 30m, Alt, Cre), Obs("010", "C", 75m, Alt, Cre),
        ], Ranges);

        result.Single(s => s.ChannelCode == "002").Candidates.ShouldBe([Ast]);
        result.Single(s => s.ChannelCode == "003").Candidates.ShouldBe([Alt]);
        result.Single(s => s.ChannelCode == "010").Candidates.ShouldBe([Cre]);
        // 001 chỉ thấy ở mẫu A (GLU/AST/ALT) → AST, ALT đã thuộc kênh khác → GLU.
        result.Single(s => s.ChannelCode == "001").Candidates.ShouldBe([Glu]);
        result.Single(s => s.ChannelCode == "002").SampleCount.ShouldBe(2);
    }

    [Fact]
    public void ImplausibleValue_RemovesCandidate()
    {
        // 2.1 không thể là creatinin (62–106 µmol/L) nhưng có thể là glucose.
        var result = ChannelMapInference.Infer([Obs("001", "A", 2.1m, Glu, Cre)], Ranges);

        result.Single().Candidates.ShouldBe([Glu]);
    }

    [Fact]
    public void Ambiguous_StaysUncertain()
    {
        var result = ChannelMapInference.Infer([Obs("002", "A", 32m, Ast, Alt)], Ranges);

        result.Single().IsCertain.ShouldBeFalse();
        result.Single().Candidates.ShouldBe([Ast, Alt]);
    }

    [Fact]
    public void NoCandidates_Ignored()
    {
        ChannelMapInference.Infer([new ChannelObservation("001", "QC1", 5m, new HashSet<Guid>())], Ranges).ShouldBeEmpty();
    }

    private sealed class SuggestionComparer : IEqualityComparer<ChannelSuggestion>
    {
        public bool Equals(ChannelSuggestion? x, ChannelSuggestion? y) =>
            x is not null && y is not null && x.ChannelCode == y.ChannelCode && x.SampleCount == y.SampleCount && x.Candidates.SequenceEqual(y.Candidates);

        public int GetHashCode(ChannelSuggestion obj) => obj.ChannelCode.GetHashCode(StringComparison.Ordinal);
    }
}
