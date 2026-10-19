namespace AccFiltering.Config;

/// <summary>
/// Normalizes hub and project IDs for the Data Management API.
/// </summary>
public static class ApsIdentifiers
{
    /// <summary>
    /// Adds the <c>b.</c> prefix when the value has no hub prefix.
    /// A value that already starts with a letter and a dot, such as <c>b.</c> or <c>a.</c>, is left in place.
    /// </summary>
    public static string EnsureDataManagementPrefix(string value)
    {
        var id = value.Trim().Trim('{', '}');
        if (id.Length >= 2 && id[1] == '.' && char.IsLetter(id[0]))
        {
            return char.ToLowerInvariant(id[0]) + id[1..];
        }

        return "b." + id;
    }
}
