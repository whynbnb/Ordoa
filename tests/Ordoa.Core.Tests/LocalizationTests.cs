using Ordoa.Core.Localization;
using Ordoa.Core.Util;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Ordoa.Core.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void English_TranslatesKnownStrings()
    {
        var previous = L10n.Language;
        try
        {
            L10n.SetLanguage(UiLanguage.English);
            Assert.True(L10n.IsEnglish);
            Assert.Equal("Settings", L10n.T("设置"));
            Assert.Equal("Open in New Tab", L10n.T("在新标签页中打开"));
            Assert.Equal("Use system title bar (non-immersive)", L10n.T("使用系统标题栏（非沉浸式）"));
            Assert.Equal("Batch Rename", L10n.T("批量重命名"));
            Assert.Equal("Find", L10n.T("查找"));
            Assert.Equal("2 items", L10n.Format("{0} 项", 2));
        }
        finally
        {
            L10n.SetLanguage(previous);
        }
    }

    [Fact]
    public void Chinese_ReturnsSourceStrings()
    {
        var previous = L10n.Language;
        try
        {
            L10n.SetLanguage(UiLanguage.Chinese);
            Assert.Equal("设置", L10n.T("设置"));
            Assert.Equal("2 项", L10n.Format("{0} 项", 2));
        }
        finally
        {
            L10n.SetLanguage(previous);
        }
    }

    [Fact]
    public void UnknownKeys_FallBackToSource()
    {
        var previous = L10n.Language;
        try
        {
            L10n.SetLanguage(UiLanguage.English);
            Assert.Equal("一个不存在的键", L10n.T("一个不存在的键"));
            Assert.Equal("", L10n.T(""));
        }
        finally
        {
            L10n.SetLanguage(previous);
        }
    }

    [Fact]
    public void English_Humanize_UsesEnglishNames()
    {
        var previous = L10n.Language;
        try
        {
            L10n.SetLanguage(UiLanguage.English);
            var today = Humanize.Date(DateTimeOffset.Now);
            Assert.StartsWith("Today", today);
            Assert.Equal("Aug 10, 2025", Humanize.Date(new DateTimeOffset(2025, 8, 10, 12, 0, 0, TimeSpan.Zero)));
        }
        finally
        {
            L10n.SetLanguage(previous);
        }
    }
}
