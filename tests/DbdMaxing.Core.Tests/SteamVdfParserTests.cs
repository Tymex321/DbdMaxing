using DbdMaxing.Core.Steam;

namespace DbdMaxing.Core.Tests;

public class SteamVdfParserTests
{
    [Fact]
    public void Parses_nested_objects_and_values()
    {
        var root = VdfParser.Parse("""
            "AppState"
            {
                "appid" "381210"
                "InstalledDepots"
                {
                    "381211" { "manifest" "123" }
                }
            }
            """);

        Assert.Equal("381210", root.GetValue("AppState", "appid"));
        Assert.Equal("123", root.GetValue("AppState", "InstalledDepots", "381211", "manifest"));
        Assert.True(root["AppState"]!.IsObject);
    }

    [Fact]
    public void Lookup_is_case_insensitive()
    {
        var root = VdfParser.Parse("\"Users\" { \"MostRecent\" \"1\" }");
        Assert.Equal("1", root.GetValue("users", "mostrecent"));
        Assert.Equal("1", root["USERS"]!["MOSTRECENT"]!.Value);
    }

    [Fact]
    public void Handles_escapes()
    {
        var root = VdfParser.Parse(@"""path"" ""C:\\Program Files (x86)\\Steam"" ""q"" ""say \""hi\""\n\tok""");
        Assert.Equal(@"C:\Program Files (x86)\Steam", root.GetValue("path"));
        Assert.Equal("say \"hi\"\n\tok", root.GetValue("q"));
    }

    [Fact]
    public void Skips_comments_including_trailing_ones()
    {
        var root = VdfParser.Parse("""
            // header comment
            "a" // after key
            {
                "b" "1" // trailing
                // "c" "2"
            }
            // end
            """);
        Assert.Equal("1", root.GetValue("a", "b"));
        Assert.Null(root.Get("a", "c"));
    }

    [Fact]
    public void Comment_markers_inside_quotes_are_kept()
    {
        var root = VdfParser.Parse("\"url\" \"https://example.com\"");
        Assert.Equal("https://example.com", root.GetValue("url"));
    }

    [Fact]
    public void Supports_unquoted_tokens_and_conditionals()
    {
        var root = VdfParser.Parse("""
            root
            {
                key value
                "win" "1" [$WIN32]
                nested{inner 2}
            }
            """);
        Assert.Equal("value", root.GetValue("root", "key"));
        Assert.Equal("1", root.GetValue("root", "win"));
        Assert.Equal("2", root.GetValue("root", "nested", "inner"));
    }

    [Fact]
    public void Empty_values_and_objects_are_fine()
    {
        var root = VdfParser.Parse("\"label\" \"\" \"apps\" { }");
        Assert.Equal("", root.GetValue("label"));
        Assert.Empty(root["apps"]!.Children);
        Assert.True(root["apps"]!.IsObject);
    }

    [Fact]
    public void Duplicate_keys_are_preserved_and_first_wins()
    {
        var root = VdfParser.Parse("\"k\" \"1\" \"K\" \"2\"");
        Assert.Equal(2, root.Children.Count);
        Assert.Equal("1", root.GetValue("k"));
    }

    [Fact]
    public void Handles_bom_and_crlf()
    {
        var root = VdfParser.Parse("\uFEFF\"a\"\r\n{\r\n\t\"b\"\t\t\"c\"\r\n}\r\n");
        Assert.Equal("c", root.GetValue("a", "b"));
    }

    [Theory]
    [InlineData("\"a\" { \"b\" \"c\"")]       // missing close brace
    [InlineData("\"a\" \"b\" }")]              // stray close brace
    [InlineData("\"a\" \"unterminated")]       // unterminated string
    [InlineData("\"a\"")]                      // key without value
    [InlineData("{ \"a\" \"b\" }")]            // object without key
    public void Malformed_input_throws_FormatException(string text)
    {
        Assert.Throws<FormatException>(() => VdfParser.Parse(text));
        Assert.Null(VdfParser.TryParse(text));
    }

    [Fact]
    public void TryParseFile_returns_null_for_missing_file()
    {
        Assert.Null(VdfParser.TryParseFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".vdf")));
    }

    [Fact]
    public void Missing_path_returns_null()
    {
        var root = VdfParser.Parse("\"a\" { \"b\" \"1\" }");
        Assert.Null(root.Get("a", "x", "y"));
        Assert.Null(root.GetValue("a")); // object, not a leaf
    }
}
