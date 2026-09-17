// SPDX-License-Identifier: Apache-2.0
using WireifyCore.Bridge;

namespace WireifyCore.Tests;

public class ScriptSignatureTests
{
    [Fact]
    public void Parses_a_plain_sdk_signature()
    {
        var found = ScriptSignature.TryGetParams("class C:\n    def RunScript(self, x, y):\n        pass\n", out var names);
        Assert.True(found);
        Assert.Equal(new[] { "x", "y" }, names);
    }

    [Fact]
    public void Strips_annotations_and_defaults()
    {
        var src = "def RunScript(self, count: int, name: str = \"a\", flag=True):\n    pass";
        Assert.True(ScriptSignature.TryGetParams(src, out var names));
        Assert.Equal(new[] { "count", "name", "flag" }, names);
    }

    [Fact]
    public void A_self_only_signature_is_zero_params()
    {
        Assert.True(ScriptSignature.TryGetParams("def RunScript(self):\n    pass", out var names));
        Assert.Empty(names);
    }

    [Fact]
    public void Script_mode_source_has_nothing_to_check()
        => Assert.False(ScriptSignature.TryGetParams("a = x + y\nout = a", out _));

    [Fact]
    public void Varargs_defer_to_the_engine()
        => Assert.False(ScriptSignature.TryGetParams("def RunScript(self, *args):\n    pass", out _));

    [Fact]
    public void Indented_method_inside_a_class_still_parses()
    {
        var src = "import System\n\nclass MyComponent(component):\n    def RunScript(self, s, t, u):\n        return s\n";
        Assert.True(ScriptSignature.TryGetParams(src, out var names));
        Assert.Equal(new[] { "s", "t", "u" }, names);
    }

    [Fact]
    public void Null_or_empty_source_is_not_sdk_mode()
    {
        Assert.False(ScriptSignature.TryGetParams("", out _));
        Assert.False(ScriptSignature.TryGetParams(null!, out _));
    }
}
