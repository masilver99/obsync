using System.Globalization;
using System.Text;

namespace ObsidianSync.Server.Domain;

public static class PathRules
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A logical file path is required.", nameof(path));
        }

        var normalized = path.Normalize(NormalizationForm.FormC).Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Length == 0 || segments.Any(segment => string.IsNullOrEmpty(segment) || segment is "." or ".." || segment.Any(char.IsControl)))
        {
            throw new ArgumentException("The logical file path is invalid.", nameof(path));
        }

        var result = string.Join('/', segments);
        if (result.StartsWith('/') || result.EndsWith('/') || result.Contains('\0'))
        {
            throw new ArgumentException("The logical file path is invalid.", nameof(path));
        }

        return result;
    }

    public static string Key(string path) => Normalize(path).ToUpper(CultureInfo.InvariantCulture);
}
