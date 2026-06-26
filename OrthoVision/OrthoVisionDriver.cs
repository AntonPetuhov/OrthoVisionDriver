using OrthoVisionDriver;
using OrthoVisionDriver.Interfaces;
using OrthoVisionDriver.Models;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Unicode;

namespace OrthoVision
{
    public class OrthoVisionDriver : IAnalyzerDriver
    {
        private ILoggerService? logger;
        private AnalyzerSettings? settings;
        private CancellationTokenSource? cts;

        private ITcpHost tcpHost;
        private IAstmProtocolParser protocolParser;
        private IDBProvider dbProvider;
        private IResultHandler resultHandler;

        private readonly List<Task> clientTasks = new List<Task>(); // список задач

        public static string AnalyzerCode = "914";                   // код из аналайзер конфигурейшн, который связывает прибор в PSMV2
        public static string AnalyzerConfigurationCode = "ORTHOVSN"; // код прибора из аналайзер конфигурейшн

        #region для задачи мониторинга и счётчика активных клиентов
        private Task? monitoringTask;
        private int activeClientCount = 0;
        private readonly object clientCountLock = new object();
        #endregion

        #region Управляющие символы ASTM
        private const byte ENQ = 0x05;
        private const byte ACK = 0x06;
        private const byte NAK = 0x15;
        private const byte EOT = 0x04;
        private const byte STX = 0x02;
        private const byte ETX = 0x03;
        private const byte CR = 0x0D;
        private const byte LF = 0x0A;

        static byte[] CR_ = { 0x0D }; // <CR>
        static byte[] LF_ = { 0x0A }; // <CR>
        static byte[] STX_ = { 0x02 }; // <STX>
        static byte[] ETX_ = { 0x03 }; // <ETX>
        #endregion

        #region Инициализация
        /// <summary>
        /// Метод будет вызван из Analyzer после загрузки DLL. Инициализирует драйвер анализатора.
        /// </summary>
        public void Initialize(ILoggerService logger, AnalyzerSettings settings)
        {
            this.logger = logger;
            this.settings = settings;

            // Создаём зависимости
            protocolParser = new AstmProtocolParser(logger);
            dbProvider = new DBProvider(logger, settings.connectionString);
            resultHandler = new ResultsHandler(logger, settings, dbProvider);
            tcpHost = new TcpHost(logger);

            logger.LogService("Инициализация драйвера выполнена");
        }
        #endregion

        #region Запуск работы драйвера
        /// <summary>
        /// Запуск работы драйвера анализатора
        /// </summary>
        public async Task StartCommunicationAsync(CancellationToken cancellationToken)
        {
            // Освобождаем старый CTS, если есть
            // Чтобы не было утечки памяти, например после перезапуска через MonitorThreads
            cts?.Dispose();
            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Запускаем обработчик результатов в фоне
            if (settings.resultHandlerStatus)
            {
                resultHandler.StartMonitoring(cts.Token);
            }

            // Запускаем TCP-сервер
            logger.LogTcp("Начало работы TCP сервера анализатора.");

            IPAddress ip = IPAddress.Parse(settings!.ipAddress); // подсетка приборов (либо IPAddress.Any)
            tcpHost.Start(ip, settings.port);

            // Запускаем фоновую задачу для периодического мониторинга состояния
            //monitoringTask = MonitorServerStateAsync(cts.Token);

            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    // получаем подключения клиента (прибора)
                    var client = await tcpHost.AcceptClientAsync(cts.Token);
                    logger.LogTcp($"Входящее подключение:  {client.Client.RemoteEndPoint}");

                    // Увеличиваем счётчик активных клиентов
                    lock (clientCountLock) { activeClientCount++; }
                    logger.LogTcp($"Всего активных клиентов: {activeClientCount}");

                    // Логгируем свойство и состояние сокета сразу после подключения
                    LogSocketProperties(client);

                    _ = Task.Run(() => ExchangeWithClientAsync(client, cts.Token), cts.Token);

                    //clientTasks.Add(clientTask)

                    /*
                    // логгирование состояния сокета
                    logger.LogTcp($"Состояние и свойства TCP-сервера. Available: {client.Available}, LocalEndPoint: {client.Client.LocalEndPoint}, RemoteEndPoint: {client.Client.RemoteEndPoint}," +
                        $"Blocking: {client.Client.Blocking}, Connected: {client.Connected}, Client.Connected: {client.Client.Connected}, " +
                        $"SelectRead: {client.Client.Poll(1, SelectMode.SelectRead)}," +
                        $"SelectWrite: {client.Client.Poll(1, SelectMode.SelectWrite)};" +
                        $"SelectError: {client.Client.Poll(1, SelectMode.SelectError)};");
                    */
                }
                catch (OperationCanceledException)
                {
                    logger.LogTcp($"TCP-сервер остановлен по сигналу отмены.");
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogTcp($"Ошибка TCP сервера: {ex}");
                    logger.LogTcp($"Стек вызовов: {ex.StackTrace}");
                }
            }

            // Ожидаем завершения мониторинга
            if (monitoringTask != null)
                await monitoringTask;
        }
        #endregion

        #region Мониторинг и логгирование свойств и состояния сокета

        private async Task MonitorServerStateAsync(TcpClient client, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(30000, cancellationToken); // каждые 30 секунд
                    LogSocketProperties(client);
                }
                catch (OperationCanceledException)
                {
                    logger.LogTcp("Мониторинг состояния сервера остановлен.");
                    break;
                }
                catch (Exception ex) 
                {
                    logger.LogTcp($"Ошибка при мониторинге: {ex.Message}");
                }
            }
        }
        private void LogSocketProperties(TcpClient client)
        {
            try
            {
                var socket = client.Client;
                if (socket == null)
                {
                    logger.LogTcp("Невозможно получить свойства сокета: client.Client = null");
                    return;
                }

                logger.LogTcp($"Свойства сокета клиента {client.Client.RemoteEndPoint}:");
                logger.LogTcp($"  - Available (непрочитанные данные): {socket.Available} байт");
                logger.LogTcp($"  - Blocking: {socket.Blocking}");
                logger.LogTcp($"  - Connected: {socket.Connected}");
                logger.LogTcp($"  - LocalEndPoint: {socket.LocalEndPoint}");
                logger.LogTcp($"  - RemoteEndPoint: {socket.RemoteEndPoint}");
                logger.LogTcp($"  - LingerState: {socket.LingerState?.Enabled}/{socket.LingerState?.LingerTime}"); // будет ли соединение оставаться открытым после закрытия сокета и как долго

                logger.LogTcp($"  - NoDelay: {socket.NoDelay}"); // применяется ли задержка, когда буферы отправки и получения не заполнены.
                logger.LogTcp($"  - ReceiveBufferSize: {socket.ReceiveBufferSize}"); // размер буфера приема 
                logger.LogTcp($"  - SendBufferSize: {socket.SendBufferSize}"); // размер буфера отправки 
                logger.LogTcp($"  - ReceiveTimeout: {socket.ReceiveTimeout}"); // длительность интервала, в течение которого объект TcpClient будет ожидать получение данных после начала операции чтения
                logger.LogTcp($"  - SendTimeout: {socket.SendTimeout}"); // длительность интервала, в течение которого объект TcpClient будет ожидать успешное завершение отправки данных

                // Проверка состояния сокета с помощью Poll
                logger.LogTcp($"  - Poll (SelectRead): {socket.Poll(1, SelectMode.SelectRead)}");
                logger.LogTcp($"  - Poll (SelectWrite): {socket.Poll(1, SelectMode.SelectWrite)}");
                logger.LogTcp($"  - Poll (SelectError): {socket.Poll(1, SelectMode.SelectError)}");

            }
            catch (Exception ex)
            {
                logger.LogTcp($"Ошибка при логировании свойств сокета: {ex.Message}");
            }
        }
        #endregion

        /// <summary>
        /// Обрабатывает одно подключение прибора: читает сообщения, отправляет ответы.
        /// </summary>
        private async Task ExchangeWithClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            using (var stream = client.GetStream()) // получаем объект NetworkStream для взаимодействия с клиентом
            {
                //using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // можно увеличить таймаут и обрабатывать исключения
                // Создаем связанные токен, который сработает либо при отмене через внешний ct (остановке сервера), либо при автоматическом таймауте
                //using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                // буфер для получения данных
                var buffer = new byte[4096];
                // количество полученных байтов
                int received_bytes = 0;
                // Накопитель для данных, которые ещё не удалось разобрать на полные фреймы
                // можно без него, а просто использовать буффер
                var accumulator = new MemoryStream();
                // полное сообщение от анализатора без управляющих символов
                var messageFromAnalyzerFull = new StringBuilder();

                logger.LogTcp($"Клиент подключен: {client.Client.RemoteEndPoint}");

                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        // Логируем, если есть данные в буфере
                        if (client.Client.Available > 0)
                        {
                            logger.LogTcp($"Данные доступны для чтения: {client.Client.Available} байт");
                            //LogSocketProperties(client);
                        }

                        // считываем данные
                        //received_bytes = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                        received_bytes = await stream.ReadAsync(buffer, 0, buffer.Length, linkedCts.Token);

                        if (received_bytes == 0)
                        {
                            logger.LogExchange($"Ничего не получено");
                            break; // соединение закрыто
                        }

                        // Добавляем прочитанное в накопитель
                        accumulator.Write(buffer, 0, received_bytes);

                        // Пытаемся извлечь все полные фреймы из накопителя
                        while (protocolParser.TryExtractFullFrame(accumulator, out byte[] rawFrame))
                        {
                            // rawFrame содержит STX ... ETX + 2 байта checksum
                            if (rawFrame.Length < 4) continue; // слишком короткий фрейм

                            logger.LogExchange($"{Encoding.UTF8.GetString(rawFrame)}");

                            // Проверяем контрольную сумму
                            if (!protocolParser.VerifyChecksum(rawFrame)) // если не свопали полученная и расчетная контрольная сумма
                            {
                                // Ошибка контрольной суммы – отправить NAK и, возможно, прервать?
                                await SendControlCharAsync(stream, NAK, linkedCts.Token);
                                throw new InvalidDataException("Неверная контрольная сумма фрейма");
                            }
                            else // контрольная сумма расчитана верно
                            {
                                // Извлекаем полезные данные (между STX и ETX)
                                int dataStartPos = 1; // после STX
                                int dataEndPos = rawFrame.Length - 3; // перед ETX (последние 3 байта: ETX + 2 checksum)
                                byte[] frameData = new byte[dataEndPos - dataStartPos];
                                Array.Copy(rawFrame, dataStartPos, frameData, 0, frameData.Length);

                                // Преобразуем в строку
                                // GetString - декодирует последовательность байтов из указанного массива байтов в строку.
                                //string messageFromAnalyzer = Encoding.ASCII.GetString(frameData);
                                string messageFromAnalyzer = Encoding.UTF8.GetString(frameData);
                                messageFromAnalyzerFull.Append(messageFromAnalyzer);

                                // Подтверждаем приём фрейма (ACK после каждого успешного фрейма)
                                await SendControlCharAsync(stream, ACK, linkedCts.Token);
                            }
                        }

                        // После извлечения всех фреймов проверяем, не пришёл ли EOT (возможно, отдельно или внутри)
                        if (HasControlByte(accumulator, EOT))
                        {
                            logger.LogExchange($"Analyzer: <{TranslateByte(EOT)}>");
                            await SendControlCharAsync(stream, ACK, linkedCts.Token);
                            //break; // завершаем сессию

                            // Удаляем EOT из накопителя (и все байты до него, если они есть)
                            RemoveControlByte(accumulator, EOT);
                            // Альтернативно: очистить весь accumulator, т.к. после EOT начинается новый диалог,
                            // но если анализатор отправил EOT + сразу следующий байт (редко), то лучше удалить только до EOT.
                            // Для простоты и надёжности – очищаем полностью:
                            accumulator.SetLength(0);
                            accumulator.Position = 0;

                            // Не выходим из цикла! Продолжаем слушать новые данные от анализатора
                            // (он должен прислать ENQ для следующего запроса)

                            //Или выходим?
                            break; // завершаем сессию
                        }

                        // Если пришёл ENQ (запрос на начало передачи), отвечаем ACK
                        if (HasControlByte(accumulator, ENQ))
                        {
                            logger.LogExchange("Analyzer: <ENQ>");
                            await SendControlCharAsync(stream, ACK, linkedCts.Token);
                            // Очищаем ENQ из накопителя
                            RemoveControlByte(accumulator, ENQ);
                        }
                    }
                }
                /*
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    logger.LogTcp("Таймаут ожидания данных от клиента.");
                    return; // завершаем метод
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    logger.LogTcp("Остановка сервера – прерывание чтения.");
                    return;
                }
                */
                catch (OperationCanceledException)
                {
                    logger.LogTcp("Остановка сервера – прерывание чтения.");
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogExchange(ex.ToString());

                    logger.LogTcp($"Ошибка при обмене с клиентом {client.Client.RemoteEndPoint}: {ex.Message}");
                    // Логируем состояние сокета в момент ошибки
                    if (client.Connected)
                    {
                        logger.LogTcp($"Сокет клиента всё ещё Connected, но возможны проблемы. Состояние сокета при ошибке:");
                        LogSocketProperties(client);
                    }
                    else
                    {
                        logger.LogTcp($"Сокет клиента уже отключён.");
                    }

                    return;

                }
                finally
                {
                    accumulator.Dispose();

                    lock (clientCountLock)
                    {
                        activeClientCount--;
                    }
                    logger.LogTcp($"Клиент {client.Client?.RemoteEndPoint} отключился. Осталось активных: {activeClientCount}");
                }

                // Если дошли сюда – обмен прошёл успешно
                logger.LogExchange($"Здесь определим тип сообщения от прибора");

                // Обрабатываем полное ASTM-сообщение и формируем ответ
                // Если строка не пустая, значит функция ProcessReceivedFullMessage вернула строку с заданием
                logger.LogExchange($"{messageFromAnalyzerFull.ToString()}");
                //string response = ProcessReceivedFullMessage(messageFromAnalyzerFull.ToString());

                List<string> response = ProcessReceivedFullMessage_(messageFromAnalyzerFull.ToString());

                //if (!string.IsNullOrEmpty(response))
                if (response.Count > 0)
                {
                    try
                    {
                        
                        // отправляем задание анализатору
                        // Отправляем ENQ и ждём ACK
                        await SendControlCharAsync(stream, ENQ, linkedCts.Token);
                        logger.LogExchange("Отправлен ENQ");


                        // Сделать WaitACK!!!!!!!!!!!!!!!!!!!!!!!!
                        byte[] ack = new byte[1];
                        if (await ReadBytesAsync(stream, ack, ct) == 0 || ack[0] != 0x06)
                            throw new InvalidOperationException("Не получен ACK на ENQ");
                        logger.LogExchange("Получен ACK");

                        /*
                        logger.LogExchange($"{response}");
                        // 2. Преобразуем строку в байтовый массив (обычно ASCII).
                        //byte[] order_data = Encoding.ASCII.GetBytes(order_);
                        byte[] order_data = Encoding.UTF8.GetBytes(response);
                        byte[] full_order_data = ConcatByteArray(STX_, order_data, ETX_);

                        logger.LogExchange($"Ответ с STX ETX {Encoding.UTF8.GetString(full_order_data)}");

                        // Берём байты с индекса 1 (сразу после STX) до индекса etxPos (включая сам ETX) и выполняем над ними операцию сложения
                        byte calculatedChecksum = protocolParser.CalculateChecksum(full_order_data, 1, full_order_data.Length-1); // от после STX до ETX включительно

                        logger.LogExchange($"calculatedChecksum {calculatedChecksum}");
                        logger.LogExchange($"Сchecksum: {Encoding.UTF8.GetString([calculatedChecksum])}");
                        //logger.LogExchange($"received checksum: {receivedChecksum}");

                        byte[] chsum = { calculatedChecksum };

                        byte[] data_order = ConcatByteArray(full_order_data, chsum, CR_, LF_);

                        logger.LogExchange($"Ответ Полный: {Encoding.UTF8.GetString(data_order)}");

                        // 4. Отправляем данные (синхронно).
                        stream.Write(data_order, 0, data_order.Length);
                        */

                        //SendToAnalyzer(stream, linkedCts.Token);

                        #region ORDER
                    //    string[] records = new string[]
            //{
             //   "H|\\^&|||LIS CGM||||||||LIS2-A|20260626124350",
               // "P|1|000001|||ТЕСТ^ТЕСТ^||20140424|M",
                //"O|1|9000003072||1_ABO/Rh|N|20260626124350|||||N||||CENTBLOOD",
                //"L"
            //};

                        byte sequenceNumber = 1;

                        foreach (string recordContent in response)
                        {
                            // Формируем тело кадра: номер записи + содержимое + CR + ETX
                            string frameBody = sequenceNumber.ToString() + recordContent + "\r" + (char)0x03;
                            byte[] bodyBytes = Encoding.ASCII.GetBytes(frameBody); // или используйте вашу кодировку

                            // Вычисляем контрольную сумму (сумма всех байтов bodyBytes)
                            byte checksum = 0;
                            foreach (byte b in bodyBytes) checksum += b;
                            // младший байт уже в checksum

                            // Преобразуем в два hex-символа
                            string checksumStr = checksum.ToString("X2"); // например "DE"

                            // Собираем полный кадр: STX + тело + checksum + CR + LF
                            string fullFrame = "\x02" + frameBody + checksumStr + "\r\n";

                            // Отправляем fullFrame (как массив байт в кодировке ASCII)
                            byte[] frameBytes = Encoding.ASCII.GetBytes(fullFrame);

                            logger.LogExchange($"Отправка кадра {fullFrame}");

                            stream.Write(frameBytes, 0, frameBytes.Length);

                            // Ждём ACK от анализатора (ожидание до таймаута)
                            //WaitForAck();

                            byte[] ack__ = new byte[1];
                            if (await ReadBytesAsync(stream, ack__, ct) == 0 || ack__[0] != 0x06)
                                throw new InvalidOperationException("Не получен ACK на ENQ");
                            logger.LogExchange("Получен ACK");

                            sequenceNumber++;
                        }


                        #endregion

                        //logger.LogExchange($"Задание отправлено анализатору. Длина: {data_order.Length} байт.");

                    }
                    catch (Exception ex) 
                    {
                        logger.LogExchange($"Ошибка при отправке задания по TCP: {ex.Message}");
                    }
                }
                else
                {
                    //await SendControlCharAsync(stream, ACK, linkedCts.Token); // нужно ли?
                }
            }
        }

        private async void SendToAnalyzer (NetworkStream stream, CancellationToken ct)
        {
            string[] records = new string[]
            {
                "H|\\^&|||LIS CGM||||||||LIS2-A|20260626124350",
                "P|1|000001|||ТЕСТ^ТЕСТ^||20140424|M",
                "O|1|9000003072||1_ABO/Rh|N|20260626124350|||||N||||CENTBLOOD",
                "L"
            };

            byte sequenceNumber = 1;

            foreach (string recordContent in records)
            {
                // Формируем тело кадра: номер записи + содержимое + CR + ETX
                string frameBody = sequenceNumber.ToString() + recordContent + "\r" + (char)0x03;
                byte[] bodyBytes = Encoding.ASCII.GetBytes(frameBody); // или используйте вашу кодировку

                // Вычисляем контрольную сумму (сумма всех байтов bodyBytes)
                byte checksum = 0;
                foreach (byte b in bodyBytes) checksum += b;
                // младший байт уже в checksum

                // Преобразуем в два hex-символа
                string checksumStr = checksum.ToString("X2"); // например "DE"

                // Собираем полный кадр: STX + тело + checksum + CR + LF
                string fullFrame = "\x02" + frameBody + checksumStr + "\r\n";

                // Отправляем fullFrame (как массив байт в кодировке ASCII)
                byte[] frameBytes = Encoding.ASCII.GetBytes(fullFrame);

                logger.LogExchange($"Отправка кадра {fullFrame}");

                stream.Write(frameBytes, 0, frameBytes.Length);

                // Ждём ACK от анализатора (ожидание до таймаута)
                //WaitForAck();

                byte[] ack = new byte[1];
                if (await ReadBytesAsync(stream, ack, ct) == 0 || ack[0] != 0x06)
                    throw new InvalidOperationException("Не получен ACK на ENQ");
                logger.LogExchange("Получен ACK");

                sequenceNumber++;
            }

            
        }

        // Вспомогательные методы для работы с потоком
        private async Task<int> ReadBytesAsync(NetworkStream stream, byte[] buffer, CancellationToken token)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int received = await stream.ReadAsync(buffer, offset, buffer.Length - offset, token);
                if (received == 0) return offset;
                offset += received;
            }
            return offset;
        }

        private async Task<byte> ReadByteAsync(NetworkStream stream, CancellationToken token)
        {
            byte[] b = new byte[1];
            await ReadBytesAsync(stream, b, token);
            return b[0];
        }

        /*
        // отправка задания
        private async Task SendOrderToAnalyzerAsync(NetworkStream stream, string order)
        {
            try
            {
                // Отправляем ENQ и ждём ACK
                await stream.WriteAsync(new byte[] { 0x05 }, 0, 1, token);
                Console.WriteLine("Отправлен ENQ");
            }
            catch(Exception ex)
            {

            }

        }
        */

        /// <summary>
        /// Обработка полного ASTM-сообщения (от H до L).
        /// Возвращает строку ответного сообщения (кадрированного), либо null.
        /// </summary>
        private string ProcessReceivedFullMessage(string message)
        {
            logger.LogExchange($"IsResultMessage {protocolParser.IsResultMessage(message)}");
            // Определяем тип сообщения
            if (protocolParser.IsHostQuery(message))
            {
                logger.LogExchange($"message: {message} ");
                // Это запрос заказов (Host Query) - формируем ответ с заказами
                //logger.LogExchange("Получен запрос задания анализатором.");
                string sampleId = protocolParser.ExtractSampleId(message);
                logger.LogExchange($"Получен запрос задания для образца: {sampleId}");

                //GetRequestFromDB(sampleId); // сделать асинхронным?
                //string order = dbProvider.GetRequestFromDB(sampleId);
                //return order;
            }
            else if (protocolParser.IsResultMessage(message))
            {
                // Это результат
                logger.LogExchange("Получено сообщение с результатами.");
                // сразу запишем в файл
                MakeAnalyzerResultFile(message);
                return null;
            }

            logger.LogExchange("Получено сообщение");
            // Другие типы сообщений игнорируем
            return null;
        }

        private List<string> ProcessReceivedFullMessage_(string message)
        {
            logger.LogExchange($"IsResultMessage {protocolParser.IsResultMessage(message)}");
            // Определяем тип сообщения
            if (protocolParser.IsHostQuery(message))
            {
                logger.LogExchange($"message: {message} ");
                // Это запрос заказов (Host Query) - формируем ответ с заказами
                //logger.LogExchange("Получен запрос задания анализатором.");
                string sampleId = protocolParser.ExtractSampleId(message);
                logger.LogExchange($"Получен запрос задания для образца: {sampleId}");

                //GetRequestFromDB(sampleId); // сделать асинхронным?
                OrderData order = dbProvider.GetRequestFromDB(sampleId);

                List<string> order_ = CreateOrderMessage(order);

                return order_;
            }
            else if (protocolParser.IsResultMessage(message))
            {
                // Это результат
                logger.LogExchange("Получено сообщение с результатами.");
                // сразу запишем в файл
                MakeAnalyzerResultFile(message);
                return null;
            }

            logger.LogExchange("Получено сообщение");
            // Другие типы сообщений игнорируем
            return null;
        }




        private List<string> CreateOrderMessage(OrderData order)
        {
            List<string> frames = new List<string>();
            frames.Add($"H|\\^&|||LIS CGM||||||||LIS2-A|{DateTime.Now:yyyyMMddHHmmss}");
            frames.Add($"P|1|{order.pid}|||{order.patientSurname}^{order.patientName}^||{order.patientBirthDay}|{order.patientSex}");

            int Onum = 0;
            foreach (string profile in order.profiles)
            {
                frames.Add($"O|{++Onum}|{order.rid}||{profile}|N|{DateTime.Now:yyyyMMddHHmmss}|||||N||||CENTBLOOD");
            }

            return frames;
        }



        /// <summary>
        /// Отправка клиенту управляющего символа
        /// </summary>
        private async Task SendControlCharAsync(NetworkStream stream, byte controlChar, CancellationToken token)
        {
            byte[] buffer = { controlChar };
            await stream.WriteAsync(buffer, 0, 1, cts.Token);
            //logger.LogExchange($"HOST: <{Encoding.UTF8.GetString(buffer)}>");
            logger.LogExchange($"HOST: <{TranslateByte(controlChar)}>");
        }

        /// <summary>
        /// Проверка на наличие контрольного символа
        /// </summary>
        private bool HasControlByte(MemoryStream accumulator, byte controlByte)
        {
            byte[] data = accumulator.ToArray();
            foreach (byte b in data)
                if (b == controlByte) return true;
            return false;
        }

        /// <summary>
        /// Удаление контрольных байтов из накопителя
        /// </summary>
        private void RemoveControlByte(MemoryStream accumulator, byte controlByte)
        {
            byte[] data = accumulator.ToArray();
            var result = new List<byte>();
            bool removed = false;
            foreach (byte b in data)
            {
                if (!removed && b == controlByte)
                {
                    removed = true;
                    continue;
                }
                result.Add(b);
            }
            accumulator.SetLength(0);
            accumulator.Write(result.ToArray(), 0, result.Count);
            accumulator.Position = 0;
        }

        
        #region Вспомогательные функции
        /// <summary>
        /// Создаем файл с результатом, отправленным анализатором
        /// </summary>
        private void MakeAnalyzerResultFile(string AllMessagePar)
        {
            // папка для результатов анализатора
            string analyzerResultPath = Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, settings.analyzerName), "Results");

            if (!Directory.Exists(analyzerResultPath))
            {
                Directory.CreateDirectory(analyzerResultPath);
            }
            DateTime now = DateTime.Now;
            string filename = analyzerResultPath + "\\Results_" + protocolParser.ExtractSampleId(AllMessagePar) + "_" + now.Year + CheckZero(now.Month) + CheckZero(now.Day) + CheckZero(now.Hour) + CheckZero(now.Minute) + CheckZero(now.Second) + CheckZero(now.Millisecond) + ".res";
            using (System.IO.FileStream fs = new System.IO.FileStream(filename, FileMode.OpenOrCreate))
            {
                foreach (string res in AllMessagePar.Split('\r'))
                {
                    //byte[] ResByte = Encoding.GetEncoding(1251).GetBytes(res + "\r\n");
                    byte[] ResByte = Encoding.UTF8.GetBytes(res + "\r\n");
                    fs.Write(ResByte, 0, ResByte.Length);
                }
            }
        }

        /// <summary>
        /// Дописываем к номеру месяца ноль если нужно
        /// </summary>
        public static string CheckZero(int CheckPar)
        {
            string BackPar = "";
            if (CheckPar < 10)
            {
                BackPar = $"0{CheckPar}";
            }
            else
            {
                BackPar = $"{CheckPar}";
            }
            return BackPar;
        }

        // Перевод байта в строку (для логов)
        private string TranslateByte(byte BytePar)
        {
            switch (BytePar)
            {
                case 0x02:
                    return "<STX>";
                case 0x03:
                    return "<ETX>";
                case 0x04:
                    return "<EOT>";
                case 0x05:
                    return "<ENQ>";
                case 0x06:
                    return "<ACK>";
                case 0x15:
                    return "<NAK>";
                case 0x16:
                    return "<SYN>";
                case 0x17:
                    return "<ETB>";
                case 0x0A:
                    return "<LF>";
                case 0x0D:
                    return "<CR>";
                default:
                    return "<HZ>";
            }
        }

        //собираем несколько массивов в один
        static Byte[] ConcatByteArray(params Byte[][] ArraysPar)
        {
            // [][] - массив массивов
            Byte[] FinallArray = { };
            for (int i = 0; i < ArraysPar.Length; i++)
            {
                int EndOfGeneralArray = FinallArray.Length;
                Array.Resize(ref FinallArray, FinallArray.Length + ArraysPar[i].Length);
                Array.Copy(ArraysPar[i], 0, FinallArray, EndOfGeneralArray, ArraysPar[i].Length);
            }
            return FinallArray;
        }

        #endregion

        // Остановка работы анализатора
        //public async Task StopCommunicationAsync()
        public Task StopCommunicationAsync()
        {
            logger.LogTcp("Инициирована остановка TCP сервера...");

            cts?.Cancel();
            logger.LogTcp("TCP сервер остановлен?");
            logger.LogTcp("Dispose?");
            tcpHost.Dispose();

            return Task.CompletedTask; // возвращаем завершенный объект Task
        }

        public void Dispose()
        {
            cts?.Dispose();
        }
    }
}
