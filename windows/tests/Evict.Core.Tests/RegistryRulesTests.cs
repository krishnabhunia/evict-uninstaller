using Evict.Core.Models;
using Evict.Core.Util;
using Xunit;

namespace Evict.Core.Tests;

public class RegistryRulesTests
{
    [Theory]
    [InlineData(@"""C:\Program Files\Foo\foo.exe"" ""%1""", @"C:\Program Files\Foo\foo.exe")]
    [InlineData(@"C:\Program Files\Foo\foo.exe,0", @"C:\Program Files\Foo\foo.exe")]
    [InlineData(@"C:\Program Files\Foo\foo.exe --open %1", @"C:\Program Files\Foo\foo.exe")]
    [InlineData(@"C:\Program Files (x86)\Foo\shellext.dll", @"C:\Program Files (x86)\Foo\shellext.dll")]
    [InlineData(@"v2.31|Action=Allow|Active=TRUE|Dir=In|App=C:\Program Files\Foo\foo.exe|Name=Foo|", @"C:\Program Files\Foo\foo.exe")]
    public void ExtractPaths_FindsTheFile(string data, string expected)
    {
        Assert.Contains(expected, RegistryLeftoverRules.ExtractPaths(data), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReferencesFolder_OnlyInsideTheFolder()
    {
        Assert.True(RegistryLeftoverRules.ReferencesFolder(@"""C:\Program Files\Two Button App\TwoButtonApp.exe"" ""%1""", @"C:\Program Files\Two Button App"));
        Assert.False(RegistryLeftoverRules.ReferencesFolder(@"""C:\Program Files\Two Button App Pro\x.exe""", @"C:\Program Files\Two Button App"));
        Assert.False(RegistryLeftoverRules.ReferencesFolder(@"%SystemRoot%\system32\notepad.exe %1", @"C:\Program Files\Foo"));
        Assert.False(RegistryLeftoverRules.ReferencesFolder(null, @"C:\Program Files\Foo"));
        Assert.False(RegistryLeftoverRules.ReferencesFolder(@"C:\Program Files\Foo\foo.exe", null));
    }

    [Theory]
    [InlineData("TwoButtonApp.Document", "Two Button App", true)]
    [InlineData("TwoButtonApp.File.1", "Two Button App", true)]
    [InlineData("Audacity.Project", "Audacity", true)]
    [InlineData("txtfile", "Two Button App", false)]
    [InlineData("Directory", "Directory Opus", false)]
    [InlineData(".txt", "Two Button App", false)]
    [InlineData("{0000-1111}", "Two Button App", false)]
    [InlineData("Microsoft.Word.Document", "Two Button App", false)]
    [InlineData("Button.Style", "Two Button App", false)]   // a single weak token must not match
    public void MatchProgId(string progId, string program, bool expected)
    {
        var keys = NameNormalizer.CandidateKeys(program);
        Assert.Equal(expected, RegistryLeftoverRules.MatchProgId(progId, keys) is not null);
    }

    [Fact]
    public void EffectiveFolder_FallsBackToExecutableFolderButNeverSystemRoots()
    {
        Assert.Equal(@"C:\Program Files\Foo", RegistryLeftoverRules.EffectiveFolder(null, @"C:\Program Files\Foo\foo.exe", null), ignoreCase: true);
        Assert.Equal(@"C:\Apps\Bar", RegistryLeftoverRules.EffectiveFolder(@"C:\Apps\Bar\", null, null), ignoreCase: true);
        Assert.Null(RegistryLeftoverRules.EffectiveFolder(null, @"C:\Windows\notepad.exe", null));
        Assert.Null(RegistryLeftoverRules.EffectiveFolder(@"C:\Program Files", null, null));
        Assert.Null(RegistryLeftoverRules.EffectiveFolder(null, null, null));
    }
}
