using Nogrammerly.Core;
using Xunit;

public class AppExclusionsTests
{
    [Fact]
    public void SecretAndNonProseAppsAreLeftAloneFromTheStart()
    {
        var apps = new AppExclusions();
        Assert.All(new[] { "1Password", "keepassxc", "MSTSC", "WindowsTerminal" }, app => Assert.True(apps.IsExcluded(app), app));
        Assert.All(new[] { "WINWORD", "notepad", "slack" }, app => Assert.False(apps.IsExcluded(app), app));
    }

    [Fact]
    public void TheUserCanAddAnAppAndTakeItOffAgain()
    {
        var apps = new AppExclusions();
        apps.Set("Slack", true);
        Assert.True(apps.IsExcluded("slack"));
        Assert.Equal(["Slack"], apps.ChosenByUser);
        apps.Set("SLACK", false);
        Assert.False(apps.IsExcluded("Slack"));
        Assert.Empty(apps.ChosenByUser);
    }

    [Fact]
    public void ABuiltInAppCanBeSwitchedBackOnAndOffAgain()
    {
        var apps = new AppExclusions();
        apps.Set("WindowsTerminal", false);
        Assert.False(apps.IsExcluded("windowsterminal"));
        apps.Set("WindowsTerminal", true);
        Assert.True(apps.IsExcluded("WindowsTerminal"));
        Assert.Empty(apps.ChosenByUser);
    }

    [Fact]
    public void ChoicesAreSavedAndABadFileFallsBackToTheBuiltInList()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "apps.json");
        try
        {
            var apps = new AppExclusions(path);
            apps.Set("Slack", true);
            apps.Set("cmd", false);
            var again = new AppExclusions(path);
            Assert.True(again.IsExcluded("Slack"));
            Assert.False(again.IsExcluded("cmd"));
            Assert.True(again.IsExcluded("1Password"));

            File.WriteAllText(path, "{ not json");
            var broken = new AppExclusions(path);
            Assert.False(broken.IsExcluded("Slack"));
            Assert.True(broken.IsExcluded("cmd"));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }
}
