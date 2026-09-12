namespace ClashTray.Core.Tests;

[TestClass]
public sealed class ErrorSanitizerTests
{
    [TestMethod]
    public void SensitiveErrorPartsAreRedacted()
    {
        string message =
            "下载 https://subscription.invalid/config?token=secret-token&user=alice 失败；"
            + "Authorization: Bearer top-secret；"
            + @"C:\Users\Zen\AppData\Local\ClashTray\settings.json";

        string sanitized = ErrorSanitizer.Sanitize(message);

        Assert.IsFalse(sanitized.Contains("secret-token", StringComparison.Ordinal));
        Assert.IsFalse(sanitized.Contains("top-secret", StringComparison.Ordinal));
        Assert.IsFalse(sanitized.Contains(@"C:\Users\Zen", StringComparison.Ordinal));
        StringAssert.Contains(sanitized, "https://subscription.invalid/config?[已隐藏]", StringComparison.Ordinal);
        StringAssert.Contains(sanitized, "Authorization: [已隐藏]", StringComparison.Ordinal);
        StringAssert.Contains(sanitized, @"%USERPROFILE%\AppData\Local\ClashTray", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ErrorTextIsBoundedAndNullableValuesStayNullable()
    {
        string sanitized = ErrorSanitizer.Sanitize(new string('x', 2048));

        Assert.AreEqual(1024, sanitized.Length);
        Assert.IsNull(ErrorSanitizer.SanitizeNullable(null));
        Assert.AreEqual("发生未知错误。", ErrorSanitizer.Sanitize((string?)null));
    }
}
