using System;
using System.IO;
using System.Xml.Linq;

namespace UpdateServer.Classes
{
    public class SentryConfig
    {
        #region Properties
        public string Dsn { get; private set; }
        public string Environment { get; private set; }
        public string Release { get; private set; }
        public bool Debug { get; private set; }
        public double TracesSampleRate { get; private set; }
        #endregion

        #region Singleton Pattern
        private static SentryConfig _instance;
        public static SentryConfig Instance => _instance ?? (_instance = Load());

        private SentryConfig() { }
        #endregion

        #region Configuration Loading
        private static SentryConfig Load()
        {
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SentrySettings.config");
            
            if (!File.Exists(configPath))
            {
                CreateDefaultConfig(configPath);
            }

            return LoadFromFile(configPath);
        }

        private static void CreateDefaultConfig(string configPath)
        {
            var defaultConfig = new XDocument(
                new XElement("SentrySettings",
                    new XElement("Dsn", ""),
                    new XElement("Environment", "Development"),
                    new XElement("Release", "UpdateServer@1.0.0"),
                    new XElement("Debug", "false"),
                    new XElement("TracesSampleRate", "0.1")
                )
            );
            defaultConfig.Save(configPath);
        }

        private static SentryConfig LoadFromFile(string configPath)
        {
            var doc = XDocument.Load(configPath);
            var root = doc.Element("SentrySettings");
            
            return new SentryConfig
            {
                Dsn = root.Element("Dsn")?.Value,
                Environment = root.Element("Environment")?.Value ?? "Development",
                Release = root.Element("Release")?.Value ?? "UpdateServer@1.0.0",
                Debug = bool.TryParse(root.Element("Debug")?.Value, out var dbg) && dbg,
                TracesSampleRate = double.TryParse(root.Element("TracesSampleRate")?.Value, out var tsr) ? tsr : 0.1
            };
        }
        #endregion
    }
}
