using System.Text.Json;
using Codec.Models;
using Codec.Services.Fetching;
using Xunit;

namespace Codec.Tests;

public sealed class SteamDetailsServiceTests
{
    public static TheoryData<string, ControllerSupportLevel, bool> ControllerMetadataCases => new()
    {
        {
            """{"controller_support":"full","categories":[],"pc_requirements":{"minimum":"Additional Notes: Keyboard and mouse supported."}}""",
            ControllerSupportLevel.Full,
            false
        },
        {
            """{"categories":[{"description":"Partial Controller Support"}],"pc_requirements":{"minimum":"Additional Notes: Gamepad or Controller Recommended."}}""",
            ControllerSupportLevel.Partial,
            true
        },
        {
            """{"categories":[],"pc_requirements":{"minimum":"A controller is not recommended for this game."}}""",
            ControllerSupportLevel.NotListed,
            false
        },
        {
            """{"categories":[],"pc_requirements":{"recommended":"Best experienced with a controller."}}""",
            ControllerSupportLevel.NotListed,
            true
        },
        {
            """{"categories":[],"pc_requirements":[]}""",
            ControllerSupportLevel.NotListed,
            false
        }
    };

    [Theory]
    [MemberData(nameof(ControllerMetadataCases))]
    public void ParseControllerMetadata_UsesSteamEvidenceOnly(
        string json,
        ControllerSupportLevel expectedSupport,
        bool expectedRecommendation)
    {
        using var document = JsonDocument.Parse(json);

        var result = SteamDetailsService.ParseControllerMetadata(document.RootElement);

        Assert.Equal(expectedSupport, result.Support);
        Assert.Equal(expectedRecommendation, result.IsRecommended);
    }

    [Theory]
    [InlineData("https://cdn/checked.jpg", "https://cdn/stored.jpg", "https://cdn/checked.jpg")]
    [InlineData(null, "https://cdn/stored.jpg", "https://cdn/stored.jpg")]
    [InlineData(null, null, "https://cdn/primary.jpg")]
    [InlineData(null, " ", "https://cdn/primary.jpg")]
    public void ChooseAssetUrl_KeepsTheStoredUrlWhenTheProbeFails(string? resolved, string? existing, string expected)
    {
        string chosen = SteamDetailsService.ChooseAssetUrl(resolved, existing, "https://cdn/primary.jpg");

        Assert.Equal(expected, chosen);
    }
}
