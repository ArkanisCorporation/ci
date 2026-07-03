using Mock.NuGet.Library;

namespace Mock.NuGet.Library.Tests;

public sealed class TextSluggerTests
{
    [Fact]
    public void ToSlug_TrimsLowercasesAndCollapsesWordSeparators()
    {
        var slug = TextSlugger.ToSlug("  Shared CI Workflows  ");

        Assert.Equal("shared-ci-workflows", slug);
    }

    [Fact]
    public void ToSlug_RemovesPunctuationRuns()
    {
        var slug = TextSlugger.ToSlug("NuGet: verify, pack, publish?");

        Assert.Equal("nuget-verify-pack-publish", slug);
    }

    [Fact]
    public void ToSlug_RejectsBlankInput()
    {
        var exception = Assert.Throws<ArgumentException>(() => TextSlugger.ToSlug("   "));

        Assert.Equal("value", exception.ParamName);
    }
}
