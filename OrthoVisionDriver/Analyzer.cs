using OrthoVisionDriver.Interfaces;
using OrthoVisionDriver.Models;
using OrthoVisionDriver.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime;
using System.Text;
using System.Threading.Tasks;


namespace OrthoVisionDriver
{
    public class Analyzer : IDisposable 
    {
        private readonly AnalyzerSettings analyzerSettings;
        private readonly ILoggerService logger;

        private readonly string analyzerPath;
        private readonly string analyzerResultsPath;

        private IAnalyzerDriver? driver;
        private CancellationTokenSource cts; // объект, кот. управляет и посылает уведомление об отмене токену

        private bool isStarted = false; // быть может это будет флаг activeStatus?

        private Task? communicationTask;
        private Task? resultHandlerTask;
        private Task? monitorThreadsTask;

        public Analyzer (IAnalyzerLoggerFactory loggerFactory, AnalyzerSettings analyzerSettings) // Анализатор, который получает фабрику логгеров и создаёт свой логгер
        {
            this.analyzerSettings = analyzerSettings;

            // папка анализатора
            analyzerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, analyzerSettings.analyzerName);
            // папка для результатов анализатора
            analyzerResultsPath = Path.Combine(analyzerPath, analyzerSettings.resultsFolder);
            // создание логгера для анализатора
            logger = loggerFactory.CreateLogger(analyzerPath);
        }

        #region запуск анализатора, запуск соответствующих потоков
        public async Task StartAsync()
        {
            if (isStarted)
            {
                logger.LogService($"Анализатор {analyzerSettings.analyzerName} уже запущен.");
                return;
            }

            cts = new CancellationTokenSource();
            logger.LogService($"Запуск анализатора {analyzerSettings.analyzerName}");

            if (analyzerSettings.isdll)
            {
                // если подключен с помощью dll, загружаем 
                if (LoadDll())
                {
                    // если прибор активен и драйвер инициализирован
                    if (analyzerSettings.activeStatus && driver != null)
                    {
                        // Если статус обмена данными активен
                        if (analyzerSettings.workStatus)
                        {
                            communicationTask = Task.Run(() => RunCommunicationLoop(cts.Token), cts.Token); // передаем токен отмены во внешний метод
                            logger.LogService("Поток обмена сообщениями запущен.");

                        }
                        // Если статус обработки результатов активен
                        if (analyzerSettings.resultHandlerStatus)
                        {
                            // здесь будет поток обработки файлов с результатами
                        }

                        // Запускаем мониторинг потоков
                        monitorThreadsTask = Task.Run(() => MonitorThreads(cts.Token), cts.Token);

                        isStarted = true; // флаг, что потоки драйвера запущены
                    }
                    else
                    {
                        logger.LogService($"Active status: {analyzerSettings.activeStatus}. Анализатор не будет запущен.");
                    }
                }
                else
                {
                    logger.LogService("Не удалось загрузить драйвер. Запуск невозможен.");
                    cts?.Dispose();
                    cts = null;
                    return;
                }
            }
            else if (!analyzerSettings.isdll)
            {
                logger.LogService("Работа без DLL не реализована.");
                cts?.Dispose();
                cts = null;
                return;
            }
        }
        #endregion

        #region Основной цикл обмена сообщениями с анализатором (вызывает методы DLL)
        private async Task RunCommunicationLoop(CancellationToken ct)
        {
            try
            {
                logger.LogService("Запускаем Цикл обмена сообщениями...");
                await driver.StartCommunicationAsync(ct); // бесконечный цикл внутри

            }
            catch (OperationCanceledException)
            {
                logger.LogService("Цикл обмена сообщениями остановлен");
            }
            catch (Exception ex) 
            {
                logger.LogService($"Ошибка при запуске цикла обмена сообщениями. {ex}");
            }
        }
        #endregion

        #region Цикл обработки результатов
        private async Task RunResultHandlerLoop(CancellationToken ct)
        {

        }
        #endregion

        #region Мониторинг состояния потоков
        private async Task MonitorThreads(CancellationToken ct)
        {
            int restartCount = 0;
            const int maxRestarts = 10; // максимальное количество попыток перезапуска потока

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(10000, ct);
                    // для проверки, выполняется ли сейчас Task
                    // true только если communicationTask не null и его свойство IsCompleted == false 
                    // (т.е. задача ещё не завершена: не выполнена, не отменена, не упала с ошибкой)
                    // Если задача не завершена — значит, поток живой.
                    var communicationTaskAlive = communicationTask?.IsCompleted == false;
                    //var resultHandlerTask = resultHandlerTask?.IsCompleted == false;  поток обработки результатов

                    //logger.LogService($"Мониторинг: Comm={commAlive}, Handler={handlerAlive}");
                    logger.LogService($"Мониторинг. Communication Task: {communicationTaskAlive}.");

                    // При необходимости можно перезапустить упавший поток
                    if (analyzerSettings.workStatus && !communicationTaskAlive && driver != null)
                    {
                        if (restartCount < maxRestarts)
                        {
                            logger.LogService("Поток обмена сообщениями неактивен, перезапуск...");
                            communicationTask = Task.Run(() => RunCommunicationLoop(ct), ct);

                            restartCount++;
                        }
                        else
                        {
                            logger.LogService($"Достигнуто максимальное количество перезапусков ({maxRestarts}). Останавливаем мониторинг.");
                            await StopAsync();
                            break;
                        }
                    }
                    else if (communicationTaskAlive)
                    {
                        // При нормальной работе сбрасываем счётчик перезапусков
                        restartCount = 0;
                    }

                    // Аналогично для обработчика результатов (при необходимости)
                    /*
                    if (analyzerSettings.resultHandlerStatus && !resultHandlerTaskAlive && driver != null)
                    {

                    }
                    */
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogService("Мониторинг потоков остановлен.");
            }
        }
        #endregion

        #region остановка работы анализатора
        public async Task StopAsync()
        {
            if (!isStarted)
            {
                logger.LogService($"Анализатор {analyzerSettings.analyzerName} уже остановлен.");
                return;
            }

            logger.LogService($"Остановка анализатора {analyzerSettings.analyzerName}");

            if (cts != null && !cts.IsCancellationRequested)
            {
                cts.Cancel();
            }

            /*
            // ожидание завершения задачи
            if (communicationTask != null)
            {
                try { await communicationTask; }
                catch { } // игнорируем возможные исключения
            }
            */

            var tasksToWait = new List<Task>();
            if (communicationTask != null) tasksToWait.Add(communicationTask);
            if (resultHandlerTask != null) tasksToWait.Add(resultHandlerTask);
            if (monitorThreadsTask != null) tasksToWait.Add(monitorThreadsTask);

            if (tasksToWait.Any())
            {
                try
                {
                    // Ожидание завершения всех фоновых задач с таймаутом 5 секунд
                    await Task.WhenAll(tasksToWait).WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                    logger.LogService("Не удалось дождаться остановки фоновых задач за 5 секунд.");
                }
                catch (Exception ex)
                {
                    logger.LogService($"Ошибка при ожидании остановки задач: {ex}");
                }
            }

            /*
            driver?.StopCommunicationAsync().Wait(5000); 
            cts?.Dispose();

            logger.LogService($"Анализатор {analyzerSettings.analyzerName} остановлен.");
            */

            if (driver != null)
            {
                try
                {
                    await driver.StopCommunicationAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                    logger.LogService("Таймаут остановки драйвера.");
                }
                catch (Exception ex)
                {
                    logger.LogService($"Ошибка при остановке драйвера: {ex}");
                }
            }

            cts?.Dispose();
            cts = null;
            isStarted = false;

            logger.LogService($"Анализатор {analyzerSettings.analyzerName} остановлен.");
        }

        #endregion

        #region загрузка dll и создание экземпляра драйвера
        public bool LoadDll()
        {
            try
            {
                if (!File.Exists(analyzerSettings.dllPath))
                {
                    logger.LogService($"Файл DLL не найден: {analyzerSettings.dllPath}");
                    return false;
                }

                logger.LogService($"Загрузка dll драйвера анализатора {analyzerSettings.analyzerName} из {analyzerSettings.dllPath}");

                Assembly asm = Assembly.LoadFrom(analyzerSettings.dllPath);
                var driverType = asm.GetTypes().First(t => typeof(IAnalyzerDriver).IsAssignableFrom(t));
                driver = (IAnalyzerDriver)Activator.CreateInstance(driverType)!;
                // инициализация драйвера
                driver.Initialize(logger, analyzerSettings);

                logger.LogService($"Драйвер загружен успешно.");

                return true;
            }
            catch(Exception ex)
            {
                logger.LogService($"Не удалось загрузить драйвер dll. Ошибка: {ex}");
                return false;
            }
        }
        #endregion

        
        public void Dispose()
        {
            logger.LogService("Освобождаем ресурсы. (Вызов Dispose класса Analyzer)");
            driver?.Dispose(); // здесь вызывается Dispose() у OrthoVisionDriver
            cts?.Dispose();
        }
        

        // реализовать метода GetStatus чтобы впоследствии исключить повторный запуска анализатора
    }
}
