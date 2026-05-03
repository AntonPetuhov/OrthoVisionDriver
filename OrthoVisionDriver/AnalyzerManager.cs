using OrthoVisionDriver.Models;
using OrthoVisionDriver.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OrthoVisionDriver.Services;
using OrthoVisionDriver.Interfaces;

namespace OrthoVisionDriver
{
    // менеджер анализаторов, фабрика анализаторов, фабричный подход
    public class AnalyzerManager
    {
        private readonly IAnalyzerLoggerFactory loggerFactory; // DI
        private readonly List<Analyzer> analyzersList = new();

        public AnalyzerManager(IAnalyzerLoggerFactory loggerFactory)
        {
            this.loggerFactory = loggerFactory;
        }

        // создание объекта анализатора
        public Analyzer CreateAnalyzer (AnalyzerSettings analyzersettings)
        {
            var analyzer = new Analyzer(loggerFactory, analyzersettings);
            analyzersList.Add(analyzer);
            return analyzer;
        }

        // потребуется исключить попытку повторного запуска анализатора, для проекта по анализаторам, метод GetStatus?
        public async Task StartAllAsync()
        {
            foreach (var analyzer in analyzersList)
            {
                // добавить проверкуц activeStatus
                await analyzer.StartAsync();
            }
        }

        public async Task StopAllAsync()
        {
            /*
            foreach (var analyzer in analyzersList) 
            {
                await analyzer.StopAsync();
            }
            */
            foreach (var analyzer in analyzersList)
            {
                try
                {
                    await analyzer.StopAsync();
                }
                catch (Exception ex) { }
                finally
                {
                    analyzer.Dispose(); // переиспользовать объект анализатора будет нельзя, только создать новый, тк освобождаем ресурсы
                }
            }
        }
    }
}
