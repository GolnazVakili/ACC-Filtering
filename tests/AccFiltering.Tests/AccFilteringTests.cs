using AccFiltering.Config;
using Xunit;
using AccFiltering.Models;
using AccFiltering.Services;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;

namespace AccFiltering.Tests;

public class NameMatcherTests
{
    private readonly NameMatcher _matcher = new();

    [Fact]
    public void Excel_name_matches_nwc_display_name()
    {
        var result = _matcher.Match(
            ["A49_A_DM_ARCH_C"],
            [new AccItem("item-1", "A49_A_DM_ARCH_C.nwc", "urn:version")]);

        var match = Assert.Single(result.Matched);
        Assert.Equal("A49_A_DM_ARCH_C.nwc", match.Item.DisplayName);
        Assert.Empty(result.Unmatched);
    }

    [Theory]
    [InlineData("a49_a_dm_arch_c", "A49_A_DM_ARCH_C.NWC")]
    [InlineData("  A49_A_DM_ARCH_C  ", "A49_A_DM_ARCH_C.nwc")]
    [InlineData("A49_A_DM_ARCH_C.nwc", "A49_A_DM_ARCH_C.nwc")]
    [InlineData("A49_A_DM_ARCH_C.nwc", "  a49_a_dm_arch_c.NWC  ")]
    public void Normalization_ignores_case_whitespace_and_extension(string excelName, string displayName)
    {
        var result = _matcher.Match([excelName], [new AccItem("item-1", displayName)]);

        Assert.Single(result.Matched);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void Different_name_is_not_a_match()
    {
        var result = _matcher.Match(
            ["A49_A_DM_ARCH_C"],
            [new AccItem("item-1", "B12_S_DM_STRUCT_C.nwc")]);

        Assert.Empty(result.Matched);
        Assert.Single(result.Unmatched);
        Assert.Null(result.Unmatched[0].SuggestionName);
    }

    [Fact]
    public void Close_name_is_only_a_suggestion()
    {
        var result = _matcher.Match(
            ["A49_A_DM_ARCH_C"],
            [new AccItem("item-1", "A49_A_DM_ARCH_D.nwc")]);

        Assert.Empty(result.Matched);
        var unmatched = Assert.Single(result.Unmatched);
        Assert.Equal("A49_A_DM_ARCH_D.nwc", unmatched.SuggestionName);
        Assert.True(unmatched.SuggestionScore >= NameMatcher.SuggestionThreshold);
    }
}

public class FolderKindDetectorTests
{
    [Fact]
    public void Project_Files_parent_uses_file_extension()
    {
        var folder = Folder("04.Coordination", "normal", ["Project Files"], ["items:autodesk.bim360:File"]);

        Assert.Equal(DocsFolderKind.ProjectFiles, FolderKindDetector.Detect(folder));
    }

    [Fact]
    public void Plans_parent_is_not_a_file_folder()
    {
        var folder = Folder("Level 1", "normal", ["Plans"], ["items:autodesk.bim360:Document"]);

        Assert.Equal(DocsFolderKind.Plans, FolderKindDetector.Detect(folder));
    }

    [Fact]
    public void Folder_named_Plans_under_Project_Files_stays_a_file_folder_when_visible_type_is_file()
    {
        var folder = Folder("Plans", "normal", ["Project Files"], ["items:autodesk.bim360:File"]);

        Assert.Equal(DocsFolderKind.ProjectFiles, FolderKindDetector.Detect(folder));
    }

    [Fact]
    public void Plan_folder_type_is_plans()
    {
        var folder = Folder("Sheets", "plan", [], []);

        Assert.Equal(DocsFolderKind.Plans, FolderKindDetector.Detect(folder));
    }

    [Fact]
    public void Missing_metadata_is_unsupported()
    {
        var folder = Folder("Mystery", null, [], []);

        Assert.Equal(DocsFolderKind.Unsupported, FolderKindDetector.Detect(folder));
    }

    private static AccFolder Folder(
        string name,
        string? folderType,
        IReadOnlyList<string> parents,
        IReadOnlyList<string> visibleTypes) =>
        new("folder-id", name, folderType, [], visibleTypes, parents);
}

public class ApsContractTests
{
    [Theory]
    [InlineData("c8b0c73d-3ae9", "b.c8b0c73d-3ae9")]
    [InlineData("b.already-prefixed", "b.already-prefixed")]
    [InlineData("B.UPPER", "b.UPPER")]
    public void Hub_and_project_ids_get_a_b_prefix_when_missing(string input, string expected)
    {
        Assert.Equal(expected, ApsIdentifiers.EnsureDataManagementPrefix(input));
    }

    [Fact]
    public void Copy_url_keeps_the_version_query_inside_copyFrom()
    {
        var url = ApsUrls.CreateItem("b.project", "urn:adsk.wipprod:fs.file:vf.abc?version=2");

        Assert.Contains("copyFrom=urn%3Aadsk.wipprod%3Afs.file%3Avf.abc%3Fversion%3D2", url, StringComparison.Ordinal);
        Assert.DoesNotContain("?version=2", url, StringComparison.Ordinal);
        Assert.StartsWith("data/v1/projects/b.project/items?", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_item_body_links_the_tip_and_does_not_upload_storage()
    {
        var json = CopyPayloadBuilder.BuildCreateItemJson(
            "A49_A_DM_ARCH_C.nwc",
            "urn:folder",
            FolderKindDetector.FileItemExtension,
            FolderKindDetector.FileVersionExtension);

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("1.0", root.GetProperty("jsonapi").GetProperty("version").GetString());
        Assert.Equal("items", root.GetProperty("data").GetProperty("type").GetString());
        Assert.Equal("A49_A_DM_ARCH_C.nwc", root.GetProperty("data").GetProperty("attributes").GetProperty("displayName").GetString());
        Assert.Equal("items:autodesk.bim360:File", root.GetProperty("data").GetProperty("attributes").GetProperty("extension").GetProperty("type").GetString());
        Assert.Equal("1", root.GetProperty("data").GetProperty("relationships").GetProperty("tip").GetProperty("data").GetProperty("id").GetString());
        Assert.Equal("urn:folder", root.GetProperty("data").GetProperty("relationships").GetProperty("parent").GetProperty("data").GetProperty("id").GetString());

        var included = Assert.Single(root.GetProperty("included").EnumerateArray());
        Assert.Equal("1", included.GetProperty("id").GetString());
        Assert.Equal("A49_A_DM_ARCH_C.nwc", included.GetProperty("attributes").GetProperty("name").GetString());
        Assert.Equal("versions:autodesk.bim360:File", included.GetProperty("attributes").GetProperty("extension").GetProperty("type").GetString());
        Assert.False(included.TryGetProperty("relationships", out _));
        Assert.DoesNotContain("copyFrom", json, StringComparison.Ordinal);
        Assert.DoesNotContain("storage", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_version_body_targets_the_existing_item()
    {
        var json = CopyPayloadBuilder.BuildCreateVersionJson(
            "A49_A_DM_ARCH_C.nwc",
            "urn:item",
            FolderKindDetector.FileVersionExtension);

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("versions", data.GetProperty("type").GetString());
        Assert.Equal("urn:item", data.GetProperty("relationships").GetProperty("item").GetProperty("data").GetProperty("id").GetString());
        Assert.DoesNotContain("storage", json, StringComparison.Ordinal);
        Assert.DoesNotContain("copyFrom", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Pasted_excel_path_drops_surrounding_quotes()
    {
        Assert.Equal(@"C:\Models\list.xlsx", ConsolePrompter.NormalizePastedPath("\"C:\\Models\\list.xlsx\""));
    }

    [Fact]
    public void Pasted_token_drops_a_bearer_prefix()
    {
        Assert.Equal("abc.def", ConsolePrompter.NormalizeToken("Bearer abc.def"));
    }

    [Fact]
    public void Dry_run_is_the_default()
    {
        Assert.True(CliOptions.TryParse([], out var options, out _, out _));
        Assert.False(options.Execute);
        Assert.False(options.Overwrite);
        Assert.Null(options.Limit);
    }
}

public class ExcelAndPlanTests
{
    [Fact]
    public void Excel_reader_uses_model_name_and_drops_duplicates()
    {
        var path = Path.Combine(Path.GetTempPath(), $"acc-filtering-{Guid.NewGuid():N}.xlsx");
        try
        {
            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.AddWorksheet("Models");
                sheet.Cell(1, 1).Value = "Model Name";
                sheet.Cell(2, 1).Value = " A49_A_DM_ARCH_C ";
                sheet.Cell(3, 1).Value = "";
                sheet.Cell(4, 1).Value = "a49_a_dm_arch_c.nwc";
                sheet.Cell(5, 1).Value = "B12_S_DM_STRUCT_C";
                workbook.SaveAs(path);
            }

            var result = new ExcelReader().Read(path);

            Assert.Equal(["A49_A_DM_ARCH_C", "B12_S_DM_STRUCT_C"], result.Names);
            Assert.Equal(["a49_a_dm_arch_c.nwc"], result.DuplicateNames);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_model_name_column_fails_clearly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"acc-filtering-{Guid.NewGuid():N}.xlsx");
        try
        {
            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.AddWorksheet("Models");
                sheet.Cell(1, 1).Value = "Name";
                workbook.SaveAs(path);
            }

            var exception = Assert.Throws<InvalidOperationException>(() => new ExcelReader().Read(path));
            Assert.Contains("Model Name", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Existing_destination_is_skipped_and_a_suggestion_is_not_copied()
    {
        var service = new CopyService(null!, new AppSettings(), NullLogger<CopyService>.Instance);
        var source = new AccItem("source-1", "A49_A_DM_ARCH_C.nwc", "urn:version");
        var existing = new AccItem("dest-1", "A49_A_DM_ARCH_C.nwc");
        var match = new NameMatcher().Match(
            ["A49_A_DM_ARCH_C", "A49_A_DM_ARCH_D"],
            [source]);
        var destination = new Dictionary<string, AccItem>(StringComparer.Ordinal)
        {
            [NameMatcher.Normalize(existing.DisplayName)] = existing
        };

        var rows = service.BuildPlan(
            ["A49_A_DM_ARCH_C", "A49_A_DM_ARCH_D"],
            match,
            destination,
            execute: true,
            overwrite: false,
            limit: null);

        Assert.Equal(CopyStatus.SkippedExists, rows[0].Status);
        Assert.Equal("dest-1", rows[0].DestinationItemId);
        Assert.Equal(CopyStatus.NotFound, rows[1].Status);
        Assert.Contains("Not copied", rows[1].Error, StringComparison.Ordinal);
    }
}
