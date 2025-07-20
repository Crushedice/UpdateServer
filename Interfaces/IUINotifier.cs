namespace UpdateServer.Interfaces
{
    public interface IUINotifier
    {
        void UpdateClientStatus(string ipPort, int indicator, string PFiles = "");
        void LogMessage(string message);
        void ShowPoolThreads(System.Collections.Generic.List<string> threadList);
    }
}