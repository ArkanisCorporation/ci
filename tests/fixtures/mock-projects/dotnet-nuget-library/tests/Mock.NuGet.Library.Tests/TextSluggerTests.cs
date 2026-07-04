namespace Mock.NuGet.Library.Tests;

using Library;

public sealed class TextSluggerTests
{
    [Fact]
    public void ToSlugTrimsLowercasesAndCollapsesWordSeparators()
    {
        var slug = TextSlugger.ToSlug("  Shared CI Workflows  ");

        Assert.Equal("shared-ci-workflows", slug);
    }

    [Fact]
    public void ToSlugRemovesPunctuationRuns()
    {
        var slug = TextSlugger.ToSlug("NuGet: verify, pack, publish?");

        Assert.Equal("nuget-verify-pack-publish", slug);
    }

    [Fact]
    public void ToSlugRejectsBlankInput()
    {
        var exception = Assert.Throws<ArgumentException>(() => TextSlugger.ToSlug("   "));

        Assert.Equal("value", exception.ParamName);
    }
}
