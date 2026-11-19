namespace Minichat.Core.Tests;

internal static class TestWorkspace
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not find the Minichat source tree above the test output.");
    }
}
