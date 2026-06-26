using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Options;
using OrthoVisionDriver.Interfaces;
using OrthoVisionDriver.Models;
using OrthoVisionDriver.Services;
using System;
using System.Text.Json;

namespace OrthoVisionDriver
{
    public class Worker : BackgroundService
    {
        private readonly AnalyzerManager analyzerManager;
        private readonly ILoggerService logger;
        private AnalyzerSettings analyzerSettings;

        public Worker(AnalyzerManager analyzerManager)
        {
            this.analyzerManager = analyzerManager;
        }

        // Запуск службы и начало работы
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                //logger.LogService("Служба драйвера запущена.");

                // чтение настроек анализатора из json
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OrthoVisionSettings.json");
                analyzerSettings = GetSettingsFromJson(configPath);

                // создаем объект анализатора и запускаем его
                Analyzer analyzerToRun = analyzerManager.CreateAnalyzer(analyzerSettings);

                await analyzerToRun.StartAsync();

                // Ожидаем сигнал остановки, не расходуя ресурсы
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (Exception ex) 
            {  
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log"), ex.ToString());
            }
            
        }

        // Остановка службы
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await analyzerManager.StopAllAsync();
            await base.StopAsync(cancellationToken);
        }

        // получение данных из JSON
        public AnalyzerSettings GetSettingsFromJson(string jsonPath)
        {
            if (string.IsNullOrWhiteSpace(jsonPath))
                throw new ArgumentException("Путь не может быть пустым.", nameof(jsonPath));

            if (!File.Exists(jsonPath))
                throw new FileNotFoundException($"Файл конфигурации не найден: {jsonPath}");

            try
            {
                // Настройка десериализации: не учитывать регистр имён свойств
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                string jsonString = File.ReadAllText(jsonPath);

                // Десериализация JSON в объект AnalyzerSettings
                AnalyzerSettings settings = JsonSerializer.Deserialize<AnalyzerSettings>(jsonString, options);

                if (settings is null)
                    throw new InvalidOperationException("Десериализация вернула null. Проверьте содержимое JSON.");

                return settings;
            }
            catch (Exception ex)
            {
                throw;
            }

        }
    }
}
