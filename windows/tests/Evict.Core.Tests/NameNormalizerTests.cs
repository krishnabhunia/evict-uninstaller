using Evict.Core.Models;
using Evict.Core.Util;
using Xunit;

namespace Evict.Core.Tests;

public class NameNormalizerTests
{
    [Theory]
    [InlineData("Google Chrome", "googlechrome")]
    [InlineData("Google Chrome (x64) 118.0.5993.117", "googlechrome")]
    [InlineData("Notepad++ (64-bit x64)", "notepad")]
    [InlineData("7-Zip 23.01 (x64)", "7zip")]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33130", "microsoftvisualc20152022redistributable")]
    [InlineData("Python 3.12.1 (64-bit)", "python")]
    [InlineData("VLC media player", "vlcmediaplayer")]
    [InlineData("Adobe Acrobat Reader DC", "adobeacrobatreaderdc")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void ToKey_StripsNoise(string? input, string expected) => Assert.Equal(expected, NameNormalizer.ToKey(input));

    [Fact]
    public void Tokens_RemovesStopWordsAndVersions()
    {
        var t = NameNormalizer.Tokens("Adobe Acrobat Reader DC 2024.001");
        Assert.Equal(new[] { "adobe", "acrobat", "dc" }, t);   // "reader" is a stop word
    }

    [Fact]
    public void Tokens_KeepsPlusSigns()
    {
        Assert.Equal(new[] { "notepad++" }, NameNormalizer.Tokens("Notepad++ (64-bit x64)"));
    }

    [Fact]
    public void CandidateKeys_OrderedBySpecificity()
    {
        var keys = NameNormalizer.CandidateKeys("Google Chrome");
        Assert.Equal("googlechrome", keys[0].Key);
        Assert.Equal(LeftoverConfidence.High, keys[0].Confidence);
        Assert.Contains(keys, k => k.Key == "chrome" && k.Confidence == LeftoverConfidence.Low);
        Assert.Contains(keys, k => k.Key == "google" && k.Confidence == LeftoverConfidence.Low);
    }

    [Fact]
    public void CandidateKeys_DoesNotEmitStopWordsAlone()
    {
        var keys = NameNormalizer.CandidateKeys("Epic Games Launcher");
        Assert.DoesNotContain(keys, k => k.Key == "games");
        Assert.DoesNotContain(keys, k => k.Key == "launcher");
        Assert.Contains(keys, k => k.Key == "epic");
        Assert.Equal("epicgameslauncher", keys[0].Key);
    }

    [Fact]
    public void Match_ExactWholeName_IsHigh()
    {
        var keys = NameNormalizer.CandidateKeys("Google Chrome");
        Assert.Equal(LeftoverConfidence.High, NameNormalizer.Match("Google Chrome", keys));
        Assert.Equal(LeftoverConfidence.High, NameNormalizer.Match("google-chrome", keys));
    }

    [Fact]
    public void Match_SingleToken_IsLow()
    {
        var keys = NameNormalizer.CandidateKeys("Google Chrome");
        Assert.Equal(LeftoverConfidence.Low, NameNormalizer.Match("Chrome", keys));
    }

    [Fact]
    public void Match_ProtectedName_NeverMatches()
    {
        var keys = NameNormalizer.CandidateKeys("Google Chrome");
        Assert.Null(NameNormalizer.Match("Google", keys));      // publisher folder is protected
        Assert.Null(NameNormalizer.Match("Microsoft", NameNormalizer.CandidateKeys("Microsoft")));
        Assert.Null(NameNormalizer.Match("Windows", NameNormalizer.CandidateKeys("Windows")));
    }

    [Fact]
    public void Match_GenericWords_NeverMatch()
    {
        var keys = NameNormalizer.CandidateKeys("Photos Editor");
        Assert.Null(NameNormalizer.Match("Photos", keys));
        Assert.Null(NameNormalizer.Match("Documents", keys));
        Assert.Null(NameNormalizer.Match("Games", NameNormalizer.CandidateKeys("Epic Games Launcher")));
    }

    [Fact]
    public void Match_Fuzzy_RequiresLength()
    {
        var keys = NameNormalizer.CandidateKeys("VLC media player");
        Assert.Null(NameNormalizer.Match("VLC", keys));          // 3 letters: too short for fuzzy
        var keys2 = NameNormalizer.CandidateKeys("Notepad++");
        Assert.Equal(LeftoverConfidence.High, NameNormalizer.Match("Notepad++", keys2));
    }

    [Fact]
    public void Match_Unrelated_ReturnsNull()
    {
        var keys = NameNormalizer.CandidateKeys("Google Chrome");
        Assert.Null(NameNormalizer.Match("Mozilla Firefox", keys));
        Assert.Null(NameNormalizer.Match("Zoom", keys));
    }

    [Theory]
    [InlineData("Google LLC", "google")]
    [InlineData("Microsoft Corporation", "microsoft")]
    [InlineData("Advanced Micro Devices, Inc.", "advancedmicrodevices")]
    [InlineData("NVIDIA Corporation", "nvidia")]
    [InlineData("The Document Foundation", "documentfoundation")]
    [InlineData("JetBrains s.r.o.", "jetbrains")]
    [InlineData("", "")]
    public void PublisherKey_DropsLegalSuffixes(string input, string expected) => Assert.Equal(expected, NameNormalizer.PublisherKey(input));

    [Fact]
    public void RemoveDiacritics_Works() => Assert.Equal("Cafe Muller", NameNormalizer.RemoveDiacritics("Café Müller"));
}
