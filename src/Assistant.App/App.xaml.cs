using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Assistant.Core;
using Assistant.Core.Diagnostics;
using Assistant.Core.Llm;
using Assistant.Core.Search;
using Assistant.Core.Storage;
using Assistant.Core.Tools;

namespace Assistant.App;

public partial class App : Application
{
    Mutex? singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Launching the app twice must not create a second assistant.
        singleInstance = new Mutex(true, @"Local\Assistant.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            singleInstance.Dispose();
            singleInstance = null;
            MessageBox.Show("The assistant is already running.", "Assistant");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        DiagnosticLog.Info("Starting");

        DataFolder data = DataFolder.Default();
        data.EnsureCreated();
        AppConfig config = AppConfig.Load(data.ConfigFile);

        // Development uses the Ollama already installed; the bundled one arrives with the installer (step 10).
        var ollama = new HttpClient
        {
            BaseAddress = new Uri(config.OllamaUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(2),
        };
        var fileSearch = new EverythingSearch();

        // Optional cloud chat, tried in this order. A provider without a key is skipped.
        CloudEndpoint[] cloudEndpoints =
        [
            new("Groq", new Uri("https://api.groq.com/openai/v1/"), config.GroqApiKey, config.GroqModel, "models")
            {
                // Groq's production chat models, best for conversation first. Used when the configured
                // model is retired or not allowed for this key.
                FallbackModels = ["llama-3.3-70b-versatile", "openai/gpt-oss-120b", "openai/gpt-oss-20b", "llama-3.1-8b-instant"],
            },
            new("OpenRouter", new Uri("https://openrouter.ai/api/v1/"), config.OpenRouterApiKey, config.OpenRouterModel, "key"),
        ];
        var cloudHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var cloud = new CloudChat(
            cloudEndpoints.Where(endpoint => endpoint.HasKey)
                .Select(endpoint => new OpenAiCompatibleProvider(endpoint, cloudHttp))
                .ToList(),
            new SentLog(data.SentFolder));

        var assistant = new AssistantService(
            new OllamaProvider(ollama, config.Model, config.ContextSize),
            [new SearchFilesTool(fileSearch)],
            cloud);
        var dependencies = new DependencyService(ollama, config.Model, fileSearch, cloudEndpoints, cloudHttp);

        var window = new MainWindow(assistant, dependencies, data);
        MainWindow = window;
        window.Show();
    }

    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DiagnosticLog.Error("Unhandled exception", e.Exception);
        MessageBox.Show(
            $"Something went wrong: {e.Exception.Message}\n\nDetails are in {DiagnosticLog.FolderPath}",
            "Assistant");
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
