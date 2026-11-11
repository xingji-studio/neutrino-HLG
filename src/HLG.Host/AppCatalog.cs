static class AppCatalog
{
    public record App(string Name, string Path);

    public static IReadOnlyList<App> List(IEnumerable<string> roots)
    {
        var apps = new List<App>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var root in roots.Where(Directory.Exists)) {
            try {
                foreach (var path in Directory.EnumerateFiles(root, "*.lnk", options))
                    apps.Add(new App(System.IO.Path.GetFileNameWithoutExtension(path), path));
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) {
                // Keep entries from accessible roots and directories if a shortcut disappears.
            }
        }
        return apps.GroupBy(app => app.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(app => app.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
