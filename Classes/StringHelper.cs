namespace StringHelp
{
    internal static class StringHelper
    {
        public static string AddQuotesIfRequired(string path)
        {
            return !string.IsNullOrWhiteSpace(path)
                ? path.Contains(" ") && !path.StartsWith("\"") && !path.EndsWith("\"") ? "\"" + path + "\"" : path
                : string.Empty;
        }
    }
}