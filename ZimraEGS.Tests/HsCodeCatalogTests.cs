using ZimraEGS.Services;

namespace ZimraEGS.Tests;

public class HsCodeCatalogTests
{
    private static readonly HsCodeCatalog Catalog = new();

    [Fact]
    public void Holds_the_whole_tariff_and_the_services_codes()
    {
        Assert.Equal(6390, Catalog.Count);
        Assert.Equal("Portland cement > Other", Catalog.Find("25232900")!.Description);
        Assert.StartsWith("Provision of services at 15% VAT", Catalog.Find("99001000")!.Description);
        Assert.StartsWith("PORTLAND CEMENT", Catalog.HeadingTitle("2523"));
    }

    [Theory]
    [InlineData("cement", "2523")]
    [InlineData("Portland cement 50kg", "2523")]
    [InlineData("tyres", "401")]          // 40.11 new or 40.12 retreaded and used tyres
    [InlineData("Bread", "1905")]
    [InlineData("sugar", "17")]
    [InlineData("2523", "2523")]
    [InlineData("2523.29", "252329")]
    public void Finds_codes_by_word_or_number(string query, string expectedPrefix)
    {
        var results = Catalog.Search(query, 10);
        Assert.NotEmpty(results);
        Assert.StartsWith(expectedPrefix, results[0].Code);
    }

    [Fact]
    public void Does_not_match_inside_words_and_ignores_empty_queries()
    {
        Assert.DoesNotContain(Catalog.Search("oil", 100), c => c.Description.Contains("foil", StringComparison.OrdinalIgnoreCase) && !c.Description.Contains(" oil", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Catalog.Search("   "));
        Assert.Empty(Catalog.Search("of the and"));
    }
}
