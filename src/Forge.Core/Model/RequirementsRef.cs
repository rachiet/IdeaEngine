namespace Forge.Core.Model;

/// <summary>
/// Version-stamped pointer into docs/requirements, e.g. "02-todos-read.md@v3".
/// Parse-don't-validate at the DB boundary: malformed text throws, so a
/// RequirementsRef in hand is always well-formed.
/// </summary>
public readonly record struct RequirementsRef(string File, int Version)
{
    public static RequirementsRef Parse(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            var at = text.LastIndexOf('@');
            if (at > 0 && at < text.Length - 1)
            {
                var file = text[..at];
                var version = text[(at + 1)..];
                if (version.Length > 1 && version[0] == 'v' &&
                    int.TryParse(version[1..], out var n) && n > 0)
                {
                    return new RequirementsRef(file, n);
                }
            }
        }
        throw new FormatException(
            $"Malformed requirements ref '{text}': expected '<file>@v<version>' like '02-todos-read.md@v3'.");
    }

    public override string ToString() => $"{File}@v{Version}";
}
