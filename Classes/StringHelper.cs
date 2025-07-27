namespace StringHelp
{
    internal static class StringHelper
    {
        #region Public Methods
        /// <summary>
        /// Adds quotes to a file path if it contains spaces and doesn't already have quotes
        /// </summary>
        /// <param name="path">The file path to process</param>
        /// <returns>The path with quotes if needed, or the original path</returns>
        public static string AddQuotesIfRequired(string path)
        {
            return !string.IsNullOrWhiteSpace(path)
                ? path.Contains(" ") && !path.StartsWith("\"") && !path.EndsWith("\"") ? "\"" + path + "\"" : path
                : string.Empty;
        }
        #endregion
    }
}