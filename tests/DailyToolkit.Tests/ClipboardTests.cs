using DailyToolkit.Core.Clipboard;

namespace DailyToolkit.Tests;

internal static partial class Program
{
    private static void RegisterClipboardTests()
    {
        Test("ClipRecall preserves original text and classifies link semantics", () =>
        {
            var text = ClipboardContent.FromText("  多行\r\n第二行 📝  ");
            Require(text.Type == ClipboardContentType.Text && text.Text == "  多行\r\n第二行 📝  ");
            Require(ClipboardContent.FromText("https://example.com/a?q=1").Type == ClipboardContentType.Link);
            Require(ClipboardContent.FromText("not a URL https://example.com").Type == ClipboardContentType.Text);
        });
        Test("ClipRecall hashes are stable and typed; exact content verifies matches", () =>
        {
            var first = ClipboardContent.FromText("alpha"); var second = ClipboardContent.FromText("alpha");
            Require(first.StableHash() == second.StableHash() && first.SameContent(second));
            Require(!first.SameContent(ClipboardContent.FromText("Alpha")));
            var paths = ClipboardContent.FromFiles(["D:\\example.txt", "D:\\folder"]);
            Require(!paths.SameContent(ClipboardContent.FromText(paths.DisplayText)) && paths.StableHash() != ClipboardContent.FromText(paths.DisplayText).StableHash());
            Require(!paths.SameContent(ClipboardContent.FromFiles(["D:\\folder", "D:\\example.txt"])));
        });
        Test("ClipRecall size accounting and preview preserve Unicode boundaries", () =>
        {
            var content = ClipboardContent.FromText(new string('a', 219) + "📝" + new string('b', 200));
            Require(content.Preview.Length < 230 && !content.Preview.Contains('\uFFFD'));
            Require(content.ContentBytes == 423 && content.Contains("AAA"));
            Require(ClipboardContent.FromFiles(["D:\\Report.TXT"]).Contains("report.txt"));
        });
        Test("ClipRecall consent, limits, exclusions and own palette normalize safely", () =>
        {
            var settings = new ClipboardHistorySettings { IsEnabled = true, MaximumRecords = -1, RetentionDays = 0,
                MaximumTextKilobytes = int.MaxValue, ExcludedApplications = [" App.exe ", "app.exe", ""],
                PanelColors = new() { ["Window"] = "#123456", ["Text"] = "bad" } }.Normalize();
            Require(!settings.IsEnabled && settings.MaximumRecords == 1 && settings.RetentionDays == 1 && settings.MaximumTextKilobytes == 10240);
            Require(settings.ExcludedApplications.SequenceEqual(["App.exe"]) && settings.PanelColors.Count == 1);
            Require(new ClipboardHistorySettings().ToggleShortcut is { Modifiers: 3, VirtualKey: 0x56 });
            Require(!new ClipboardHistorySettings().IncludeRestoredInWindowsHistory);
        });
    }
}
