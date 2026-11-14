using TheOverseer.Core.Models;

namespace TheOverseer.Tests;

/// <summary>
/// Titles and folder names come from files an agent can write. Invisible
/// characters must not change what a HUD row appears to say.
/// </summary>
public sealed class DisplayTextTests
{
    [Theory]
    [InlineData("Fix login\u202Egnp.exe", "Fix logingnp.exe")]   // right-to-left override
    [InlineData("\u2066Deploy\u2069 now", "Deploy now")]          // isolates
    [InlineData("pay\u200Bment", "payment")]                      // zero-width space
    [InlineData("\uFEFFRelease", "Release")]                      // byte-order mark
    [InlineData("bell\u0007 here", "bell here")]                  // control character
    [InlineData("two\tparts", "two parts")]                       // tab keeps a word gap
    [InlineData("  padded  ", "padded")]
    public void RemovesInvisibleAndControlCharacters(string input, string expected) =>
        Assert.Equal(expected, DisplayText.Clean(input));

    [Fact]
    public void KeepsOrdinaryTextEmojiAndJoinedEmoji()
    {
        const string text = "Ship 🚀 build · 👩\u200D💻 review — café";
        Assert.Same(text, DisplayText.Clean(text));
    }

    [Fact]
    public void DropsLoneSurrogateLeftByTruncation()
    {
        var cut = "Plan 🚀"[..^1]; // ends with a high surrogate
        Assert.Equal("Plan", DisplayText.Clean(cut));
    }

    [Fact]
    public void ProjectNamesAndTaskLabelsAreCleaned()
    {
        var project = new ProjectDescriptor(ProjectId.FromKey("project:test"), "repo\u202Etxt.exe", @"C:\work\repo", ProjectResolution.Unassigned, "main\u200B");
        Assert.Equal("repotxt.exe", project.DisplayName);
        Assert.Equal("main", project.CheckoutLabel);

        var renamed = project with { DisplayName = "\u2067other" };
        Assert.Equal("other", renamed.DisplayName);
    }
}
