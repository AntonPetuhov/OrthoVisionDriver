using OrthoVisionDriver.Interfaces;
using OrthoVisionDriver.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime;
using System.Text;
using System.Threading.Tasks;

namespace OrthoVision
{
    public class OrthoVisionDriver : IAnalyzerDriver
    {
        private ILoggerService? logger;
        private AnalyzerSettings? settings;
        private CancellationTokenSource? cts;

        private TcpListener? listener;

        private Task? communicationTask;

        #region Символы управления ASTM
        // Символы управления ASTM
        private const byte ENQ = 0x05;
        //private const byte ACK = 0x06;
        private const byte NAK = 0x15;
        private const byte EOT = 0x04;
        private const byte STX = 0x02;
        private const byte ETX = 0x03;
        private const byte CR = 0x0D;
        private const byte LF = 0x0A;

        private byte[] ACK = { 0x06 };
        #endregion

        // Этот метод будет вызван из Analyzer после загрузки DLL
        public void Initialize(ILoggerService logger, AnalyzerSettings settings)
        {
            this.logger = logger;
            this.settings = settings;

            logger.LogService("Инициализация драйвера");
        }

        // Запуск работы анализатора
        public async Task StartCommunicationAsync(CancellationToken cancellationToken)
        {
            // Освобождаем старый CTS, если есть
            // Чтобы не было утечки памяти, например после перезапуска через MonitorThreads
            cts?.Dispose();

            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            logger.LogTcp("Начало работы TCP сервера анализатора.");

            IPAddress ip = IPAddress.Parse(settings!.ipAddress);
            listener = new TcpListener(IPAddress.Any, settings!.port);
            //listener = new TcpListener(ip, settings!.port);
            listener.Start();
            logger.LogTcp($"TCP сервер запущен на порту {settings.port}. Ожидание подключений...");

            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    // получаем подключения клиента (прибора)
                    var client = await listener.AcceptTcpClientAsync(cts.Token);
                    logger.LogTcp($"Входящее подключение: {client.Client.RemoteEndPoint}");

                    _ = Task.Run(() => ExchangeWithClientAsync(client, cts.Token), cts.Token);

                    // тут логировать состояние сокета?

                }
                catch(OperationCanceledException) 
                {
                    logger.LogTcp($"TCP-сервер остановлен по сигналу отмены.");
                    break; 
                }
                catch(Exception ex) 
                { 
                    logger.LogTcp($"Ошибка TCP сервера: {ex}"); 
                }
            }

            /*
                try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    logger.LogTcp("tcp listener");
                    logger.LogTcp("проверяем не пришел ли токен отмены");

                    await Task.Delay(1000, cts.Token); // токен прервёт ожидание, и не будет ждать заданное кол-во секунд
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogTcp("Обмен данными по TCP/IP был прерван.");
            }
            */
        }

        /// <summary>
        /// Обрабатывает одно подключение прибора: читает сообщения, отправляет ответы.
        /// </summary>
        private async Task ExchangeWithClientAsync(TcpClient client, CancellationToken ct)
        {
            /*
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            // Создаем связанные токен, который сработает либо при отмене через внешний ct (остановке сервера), либо при автоматическом таймауте
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            */

            using (client)
            using (var stream = client.GetStream()) // получаем объект NetworkStream для взаимодействия с клиентом
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                // Создаем связанные токен, который сработает либо при отмене через внешний ct (остановке сервера), либо при автоматическом таймауте
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                // буфер для получения данных
                var buffer = new byte[4096];
                // количество полученных байтов
                int received_bytes = 0;
                // StringBuilder для склеивания полученных данных в одну строку
                var messageFromAnalyzer = new StringBuilder();

                logger.LogTcp($"Клиент подключен: {client.Client.RemoteEndPoint}");

                while (!ct.IsCancellationRequested && client.Connected)
                {
                    try
                    {
                        logger.LogExchange($"Попытка получить данные");
                        // считываем данные
                        received_bytes = await stream.ReadAsync(buffer, 0, buffer.Length, ct);

                        if (received_bytes == 0)
                        {
                            logger.LogExchange($"Ничего не получено");
                            break;
                        }
                        else if (received_bytes > 0)
                        {
                            // преобразуем полученный набор байтов в строку
                            var receivedMessage = Encoding.UTF8.GetString(buffer, 0, received_bytes);
                            logger.LogExchange($"Получены данные: {receivedMessage}");

                            // Принят запрос на установление связи
                            if (buffer[0] == ENQ)
                            {
                                logger.LogExchange($"Анализатор: < ENQ >");
                                await stream.WriteAsync(ACK, 0, 1, ct);
                                logger.LogExchange($"Драйвер: < ACK >");
                            }
                            // Завершение сессии
                            else if (buffer[0] == EOT)
                            {
                                logger.LogExchange($"Анализатор: < EOT >");
                                await stream.WriteAsync(ACK, 0, 1, ct);
                                logger.LogExchange($"Драйвер: < ACK >");
                            }
                            // Приём ASTM сообщения (один или несколько фреймов)
                            else
                            {
                                if (buffer[0] == STX)
                                {

                                }

                            }
                        }
                        

                    }
                    catch (TimeoutException) 
                    {
                        logger.LogExchange("Таймаут чтения, закрываем соединение");
                        break;
                    }
                    catch(Exception ex)
                    {
                        logger.LogTcp($"Ошибка: {ex.Message}");
                        logger.LogTcp($"Ошибка: {ex}");
                        logger.LogTcp($"TCP сервер продолжает слушать порт...");
                        //break; // выходим
                    }
                }

            }
        }

        /// <summary>
        /// Приём ASTM сообщения, состоящего из нескольких фреймов.
        /// Каждый фрейм: STX ... ETX + Checksum (2 hex) + CR + LF
        /// </summary>
      

        // Остановка работы анализатора
        //public async Task StopCommunicationAsync()
        public Task StopCommunicationAsync()
        {
            cts?.Cancel();
            logger.LogTcp("TCP сервер остановлен");

            return Task.CompletedTask; // возвращаем завершенный объект Task
        }

        public void Dispose() 
        {
            cts?.Dispose();
        }
    }
}
