using OrthoVisionDriver.Models;
using OrthoVisionDriver.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrthoVisionDriver.Interfaces
{
    public interface IAnalyzerDriver : IDisposable
    {
        // Инициализация драйвера (передаём логгер и настройки)
        void Initialize(ILoggerService logger, AnalyzerSettings settings);

        // Запуск работы анализатора
        Task StartCommunicationAsync(CancellationToken cancellationToken);

        // Остановка работы анализатора
        //Task StopCommunicationAsync(CancellationToken cancellationToken);
        Task StopCommunicationAsync();

        // Обработка результатов
        Task ResultsHandlerAsync(CancellationToken cancellationToken);

        // Освобождение ресурсов
        void Dispose();

    }
}
