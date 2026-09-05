[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace Survoler.Tests;

[TestClass]
public sealed class TestEnvironment
{
    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        // Existing diagnostic tests describe the neutral English contract, regardless of host OS.
        // Localization tests override CurrentUICulture only within their own execution context.
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture =
            System.Globalization.CultureInfo.GetCultureInfo("en");
    }
}
