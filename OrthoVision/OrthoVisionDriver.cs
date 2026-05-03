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
                    // получаем подключение
                    var client = await listener.AcceptTcpClientAsync(cts.Token);
                    logger.LogTcp($"Входящее подключение: {client.Client.RemoteEndPoint}");

                    _ = Task.Run(() => ExchangeWithClientAsync(client, cts.Token), cts.Token);

                    // тут логировать состояние сокета?

                }
                catch(OperationCanceledException) { break; }
                catch(Exception ex) { logger.LogTcp($"Ошибка: {ex}"); }
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

        private async Task ExchangeWithClientAsync(TcpClient client, CancellationToken ct)
        {


            using (client)
            using (var stream = client.GetStream()) // получаем объект NetworkStream для взаимодействия с клиентом
            {
                //int ServerCount = 0; // счетчик

                // буфер для получения данных
                var buffer = new byte[4096];
                // количество полученных байтов
                int received_bytes = 0;
                // считываем данные
                received_bytes = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                // преобразуем полученный набор байтов в строку
                var receivedMessage = Encoding.UTF8.GetString(buffer, 0, received_bytes);
                
            }
        }

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
