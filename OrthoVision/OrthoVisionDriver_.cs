using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrthoVisionDriver.Interfaces;
using OrthoVisionDriver.Models;
using OrthoVisionDriver.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using System.Threading.Tasks;

namespace OrthoVision
{
    public class OrthoVisionDriver_ : IAnalyzerDriver
    {
        private ILoggerService? logger;
        private AnalyzerSettings? settings;
        private CancellationTokenSource? cts;

        private TcpListener? listener;

        private Task? communicationTask;

        //NetworkStream stream; // поток для работы с данными, полученным сокетом

        public static string AnalyzerCode = "914";                   // код из аналайзер конфигурейшн, который связывает прибор в PSMV2
        public static string AnalyzerConfigurationCode = "ORTHOVSN"; // код прибора из аналайзер конфигурейшн

        #region Управляющие символы ASTM
        private const byte ENQ = 0x05;
        private const byte ACK = 0x06;
        private const byte NAK = 0x15;
        private const byte EOT = 0x04;
        private const byte STX = 0x02;
        private const byte ETX = 0x03;
        private const byte CR = 0x0D;
        private const byte LF = 0x0A;

        //private byte[] ACK = { 0x06 };
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
                    logger.LogTcp($"Состояние и свойства TCP-сервера. Available: {client.Available}, LocalEndPoint: {client.Client.LocalEndPoint}, RemoteEndPoint: {client.Client.RemoteEndPoint}," +
                        $"Blocking: {client.Client.Blocking}, Connected: {client.Connected}, Client.Connected: {client.Client.Connected}, " +
                        $"SelectRead: {client.Client.Poll(1, SelectMode.SelectRead)}," +
                        $"SelectWrite: {client.Client.Poll(1, SelectMode.SelectWrite)};" +
                        $"SelectError: {client.Client.Poll(1, SelectMode.SelectError)};");
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
        }

        /// <summary>
        /// Обрабатывает одно подключение прибора: читает сообщения, отправляет ответы.
        /// </summary>
        private async Task ExchangeWithClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            using (var stream = client.GetStream()) // получаем объект NetworkStream для взаимодействия с клиентом
            //using (stream = client.GetStream()) // получаем объект NetworkStream для взаимодействия с клиентом
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                // Создаем связанные токен, который сработает либо при отмене через внешний ct (остановке сервера), либо при автоматическом таймауте
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

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
                    //while (!ct.IsCancellationRequested && client.Connected)
                    while (!ct.IsCancellationRequested)
                    {
                        try
                        {
                            //logger.LogExchange($"Ожидание данных...");
                            // считываем данные
                            //received_bytes = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                            received_bytes = await stream.ReadAsync(buffer, 0, buffer.Length, linkedCts.Token);

                            if (received_bytes == 0)
                            {
                                //logger.LogExchange($"Ничего не получено");
                                break; // соединение закрыто
                            }

                            // Добавляем прочитанное в накопитель
                            accumulator.Write(buffer, 0, received_bytes);

                            // Пытаемся извлечь все полные фреймы из накопителя
                            while (TryExtractFullFrame(accumulator, out byte[] rawFrame))
                            {
                                // rawFrame содержит STX ... ETX + 2 байта checksum
                                if (rawFrame.Length < 4) continue; // слишком короткий фрейм

                                logger.LogExchange($"{Encoding.UTF8.GetString(rawFrame)}");
                                // Проверяем контрольную сумму
                                if (!VerifyChecksum(rawFrame)) // если не свопали полученная и расчетная контрольная сумма
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

                            //logger.LogExchange($"Analyzer: (FULL message) {messageFromAnalyzerFull}");

                            // После извлечения всех фреймов проверяем, не пришёл ли EOT (возможно, отдельно или внутри)
                            if (HasControlByte(accumulator, EOT))
                            {
                                logger.LogExchange($"Analyzer: <EOT>");
                                logger.LogExchange($"Analyzer: <{EOT}>");
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
                        catch (TimeoutException)
                        {
                            logger.LogExchange("Таймаут чтения, закрываем соединение");
                            break;
                        }
                        catch (Exception ex)
                        {
                            logger.LogTcp($"Ошибка: {ex}");
                            logger.LogTcp($"TCP сервер продолжает слушать порт...");
                            //break; // выходим
                        }
                    }

                    logger.LogExchange($"Analyzer: (FULL message) {messageFromAnalyzerFull}");
                    //logger.LogExchange($"Здесь определим тип сообщения от прибора");
                }
                catch(Exception ex)
                {
                    logger.LogExchange(ex.ToString());
                }
                finally
                {
                    accumulator.Dispose();
                }

                logger.LogExchange($"Здесь определим тип сообщения от прибора");


                // Обрабатываем полное ASTM-сообщение и формируем ответ
                // Если строка не пустая, значит функция ProcessReceivedFullMessage вернула строку с заданием
                string response = ProcessReceivedFullMessage(messageFromAnalyzerFull.ToString());
                //ProcessReceivedFullMessage(messageFromAnalyzerFull.ToString());

                if (!string.IsNullOrEmpty(response))
                {
                    // отправляем задание анализатору


                }

            }
        }

        #region Работа с накопленными данными, проверка контрольной суммы
        /// <summary>
        /// Пытаемся извлечь один полный фрейм из начала потока-накопителя.
        /// Каждый фрейм: STX ... ETX + Checksum (2 hex) + CR + LF
        /// Возвращает true и байты фрейма (включая STX, ETX и 2 байта контрольной суммы),
        /// и удаляет эти байты из накопителя.
        /// </summary>
        private bool TryExtractFullFrame(MemoryStream accumulator, out byte[] frame)
        {
            frame = null;
            byte[] data = accumulator.ToArray(); // получаем все накопленные данные

            // Ищем позицию STX
            int stxIndex = -1;

            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] == STX)
                {
                    stxIndex = i;
                    break;
                }
            }
            if (stxIndex == -1) return false;

            // Ищем ETX после STX
            int etxIndex = -1;
            for (int i = stxIndex + 1; i < data.Length; i++)
            {
                if (data[i] == ETX)
                {
                    etxIndex = i;
                    break;
                }
            }
            if (etxIndex == -1) return false; // данных пока недостаточно (кадр не завершён)

            // После ETX должны быть ещё 2 байта (контрольная сумма)
            if (etxIndex + 2 >= data.Length) return false; // недостаточно данных

            // Вычисляем длину фрейма: от STX до контрольной суммы включительно
            int frameLength = (etxIndex + 2) - stxIndex + 1;
            frame = new byte[frameLength];
            Array.Copy(data, stxIndex, frame, 0, frameLength); // Копирование кадра в выходной массив frame из массива data

            // Удаляем обработанные байты из накопителя
            var remaining = new byte[data.Length - (stxIndex + frameLength)];
            Array.Copy(data, stxIndex + frameLength, remaining, 0, remaining.Length);
            accumulator.SetLength(0); // очищаем accumulator
            accumulator.Write(remaining, 0, remaining.Length); // записываем в него оставшиеся байты
            accumulator.Position = 0; // Позиция потока сбрасывается в 0 для последующего чтения

            return true;
        }

        /// <summary>
        /// Проверка контрольной суммы ASTM.
        /// Фрейм: STX + данные + ETX + два ASCII символа (hex) – арифметическая сумма всех байт от STX+1 до ETX включительно. (хотя обычно XOR)
        /// Затем из этой суммы берётся младший байт (что эквивалентно остатку от деления на 256 - mod 256), т.к. сумма байт может быть больше 255, но 
        /// для передачи контрольной суммы выделен один байт (два hex-символа). Один байт может хранить значения только от 0 до 255.
        /// </summary>
        /// 
        private bool VerifyChecksum(byte[] rawFrame)
        {
            if (rawFrame.Length < 4) return false; // Кадр должен содержать хотя бы: STX + ETX + два символа контрольной суммы
            if (rawFrame[0] != STX) return false;  // Первый байт обязательно должен быть STX

            //logger.LogExchange($"rawFrame hex: {BitConverter.ToString(rawFrame)}"); // сообщение от прибора в шестнадцатиричном виде последовательности байтов

            // Ищем байт ETX
            int etxPos = -1;
            for (int i = 1; i < rawFrame.Length - 2; i++)
            {
                if (rawFrame[i] == ETX)
                {
                    etxPos = i;
                    break;
                }
            }
            if (etxPos == -1) return false;

            // Вычисление ожидаемой контрольной суммы (сложение побайтово) от байта после STX до ETX включительно
            // Берём байты с индекса 1 (сразу после STX) до индекса etxPos (включая сам ETX) и выполняем над ними операцию сложения

            //logger.LogExchange("Calculating XOR from index 1 to " + etxPos);
            byte calculatedChecksum = 0;
            for (int i = 1; i <= etxPos; i++)
            {
                calculatedChecksum += rawFrame[i];
                //logger.LogExchange($"i={i} byte=0x{rawFrame[i]:X2} xor_so_far=0x{calculatedChecksum:X2}");
            }
            //logger.LogExchange($"Final calculatedChecksum = 0x{calculatedChecksum:X2}");
            calculatedChecksum = (byte)(calculatedChecksum & 0xFF); // младший байт = calculatedChecksum % 256
            //logger.LogExchange($"calculated checksum: {calculatedChecksum}");

            // Извлечение принятой контрольной суммы из кадра
            // Получаем два hex-символа из фрейма (после ETX)
            char highNibble = (char)rawFrame[etxPos + 1];
            char lowNibble = (char)rawFrame[etxPos + 2];
            byte receivedChecksum = (byte)((HexCharToByte(highNibble) << 4) | HexCharToByte(lowNibble));

            //logger.LogExchange($"received checksum: {Encoding.UTF8.GetString([receivedChecksum])}");
            //logger.LogExchange($"received checksum: {receivedChecksum}");

            //logger.LogExchange($"VerifyChecksum: {calculatedChecksum == receivedChecksum}");

            return calculatedChecksum == receivedChecksum; // Если вычисленная сумма равна той, что пришла в кадре – true, иначе false
        }

        private bool VerifyChecksum_(byte[] rawFrame)
        {
            if (rawFrame.Length < 4) return false; // Кадр должен содержать хотя бы: STX + ETX + два символа контрольной суммы
            if (rawFrame[0] != STX) return false;  // Первый байт обязательно должен быть STX

            logger.LogExchange($"rawFrame hex: {BitConverter.ToString(rawFrame)}");

            // Ищем байт ETX
            int etxPos = -1;
            for (int i = 1; i < rawFrame.Length - 2; i++)
            {
                if (rawFrame[i] == ETX)
                {
                    etxPos = i;
                    break;
                }
            }
            if (etxPos == -1) return false;

            // Вычисление ожидаемой контрольной суммы (XOR) от байта после STX до ETX включительно
            // Берём байты с индекса 1 (сразу после STX) до индекса etxPos (включая сам ETX) и выполняем над ними операцию XOR

            // для тестирования какая же все таки контрольная сумма
            int sumAll = 0;
            int sumWithoutCR = 0;
            int sumWithoutETX = 0;
            int sumWithoutCRETX = 0;

            for (int i = 1; i <= etxPos; i++)
            {
                sumAll += rawFrame[i];
                if (rawFrame[i] != 0x0D) sumWithoutCR += rawFrame[i];
                if (rawFrame[i] != 0x03) sumWithoutETX += rawFrame[i];
                if (rawFrame[i] != 0x0D && rawFrame[i] != 0x03) sumWithoutCRETX += rawFrame[i];
            }

            logger.LogExchange($"sumAll = {sumAll & 0xFF} (0x{(sumAll & 0xFF):X2})");
            logger.LogExchange($"sumWithoutCR = {sumWithoutCR & 0xFF} (0x{(sumWithoutCR & 0xFF):X2})");
            logger.LogExchange($"sumWithoutETX = {sumWithoutETX & 0xFF} (0x{(sumWithoutETX & 0xFF):X2})");
            logger.LogExchange($"sumWithoutCRETX = {sumWithoutCRETX & 0xFF} (0x{(sumWithoutCRETX & 0xFF):X2})");

            logger.LogExchange("---------------------------------------------------");
            logger.LogExchange("Calculating XOR from index 1 to " + etxPos);
            byte calculatedChecksum = 0;
            for (int i = 1; i <= etxPos; i++)
            {
                calculatedChecksum ^= rawFrame[i];
                logger.LogExchange($"i={i} byte=0x{rawFrame[i]:X2} xor_so_far=0x{calculatedChecksum:X2}");
            }
            logger.LogExchange($"Final XOR calculatedChecksum = 0x{calculatedChecksum:X2}");
            

            logger.LogExchange($"calculated checksum_encoding: {Encoding.UTF8.GetString([ calculatedChecksum ])}");
            logger.LogExchange($"calculated checksum: {calculatedChecksum}");

            // Извлечение принятой контрольной суммы из кадра
            // Получаем два hex-символа из фрейма (после ETX)
            char highNibble = (char)rawFrame[etxPos + 1];
            char lowNibble = (char)rawFrame[etxPos + 2];
            byte receivedChecksum = (byte)((HexCharToByte(highNibble) << 4) | HexCharToByte(lowNibble));

            logger.LogExchange($"received checksum: {Encoding.UTF8.GetString([receivedChecksum])}");
            logger.LogExchange($"received checksum: {receivedChecksum}");

            logger.LogExchange($"VerifyChecksum: {calculatedChecksum == receivedChecksum}");

            

            return calculatedChecksum == receivedChecksum; // Если вычисленная сумма равна той, что пришла в кадре – true, иначе false

        }

        private byte HexCharToByte(char c)
        {
            if (c >= '0' && c <= '9') return (byte)(c - '0');
            if (c >= 'A' && c <= 'F') return (byte)(c - 'A' + 10);
            if (c >= 'a' && c <= 'f') return (byte)(c - 'a' + 10);
            throw new ArgumentException("Недопустимый символ в контрольной сумме");
        }

        // отправка управляющего символа
        private async Task SendControlCharAsync(NetworkStream stream, byte controlChar, CancellationToken token)
        {
            byte[] buffer = { controlChar };
            await stream.WriteAsync(buffer, 0, 1, cts.Token);
            logger.LogExchange($"HOST: <{Encoding.UTF8.GetString(buffer)}>");
        }

        // проверка на наличие контрольного символа
        private bool HasControlByte(MemoryStream accumulator, byte controlByte)
        {
            byte[] data = accumulator.ToArray();
            foreach (byte b in data)
                if (b == controlByte) return true;
            return false;
        }

        // удаление контрольных байтов из накопителя
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

        #endregion

        #region Работа с полученным от анализатора сообщением, определение типа
        /// <summary>
        /// Обработка полного ASTM-сообщения (от H до L).
        /// Возвращает строку ответного сообщения (кадрированного), либо null.
        /// </summary>
        /// 
        private string ProcessReceivedFullMessage(string message)
        {
            logger.LogExchange($"IsResultMessage {IsResultMessage(message)}");
            // Определяем тип сообщения
            if (IsHostQuery(message))
            {
                // Это запрос заказов (Host Query) - формируем ответ с заказами
                //logger.LogExchange("Получен запрос задания анализатором.");
                string sampleId = ExtractSampleId(message);
                logger.LogExchange($"Получен запрос задания для образца: {sampleId}");

                //GetRequestFromDB(sampleId); // сделать асинхронным?
            }
            else if (IsResultMessage(message))
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

        /// <summary>
        /// Определяет, является ли сообщение Host Query (Q-запись с кодом O).
        /// </summary>
        public bool IsHostQuery(string message)
        {
            // Ищем Q запись и проверяем 13-е поле (Request Information Status Codes) == 'O'
            //string queryPattern = @"^Q\|.*\|O";
            string queryPattern = @"Q\|.*\|O";
            //var qMatch = Regex.Match(message, @"^Q\|.*\|O", RegexOptions.Multiline);
            //return qMatch.Success;
            return Regex.IsMatch(message, queryPattern, RegexOptions.Multiline);
        }

        /// <summary>
        /// Определяет, содержит ли сообщение результаты (R-записи).
        /// </summary>
        public bool IsResultMessage(string message)
        {
            //string resultPattern = @"^\d+R\|";
            string resultPattern = @"\d+R\|";
            return Regex.IsMatch(message, resultPattern, RegexOptions.Multiline);
        }

        /// <summary>
        /// Извлекает Sample ID из сообщения.
        /// Для Host Query: из Q записи поле 3.2 (после разделителя |)
        /// Для результата: из O записи поле 3.
        /// </summary>
        public string ExtractSampleId(string message)
        {
            // Пробуем найти Q запись
            //var qMatch = Regex.Match(message, @"^Q\|[^|]*\|([^|]*)", RegexOptions.Multiline);
            var qMatch = Regex.Match(message, @"Q\|[^|]*\|([^|]*)", RegexOptions.Multiline);
            if (qMatch.Success)
            {
                string specimenId = qMatch.Groups[1].Value;
                // Может содержать компоненты через ^, берём первый
                return specimenId.Split('^')[0];
            }

            // Иначе ищем O запись
            //var oMatch = Regex.Match(message, @"^\dO\|\d+\|([^|]*)", RegexOptions.Multiline);
            var oMatch = Regex.Match(message, @"\dO\|\d+\|([^|]*)", RegexOptions.Multiline);
            if (oMatch.Success)
            {
                return oMatch.Groups[1].Value;
            }

            return string.Empty;
        }

        #endregion

        #region Формирование задания и его отправка
        /*
        // Простой пример ответа на Host Query (заказ образца)
        // В реальной системе ответ должен формироваться из БД
        private readonly string _sampleOrderTemplate =
            "H|\\^&|||LIS_SERVER|||||||P|LIS2-A|{0}\r\n" +
            "P|1|PID123456||NID123456^MID123456^OID123456|Brown^Bobby^B|White|196501020304|U||||||||||||||||||||||||||\r\n" +
            "O|1|{1}||Type && Screen|N|{0}|||||||||CENTBLOOD|||||||||||||||\r\n" +
            "L|\r\n";

        // Формируем сообщение заказа согласно документации Ortho Vision
        string orderMessage = "H|\\^&|||LIS|...|LIS2-A|20250320120000\r\n" +
                              "P|1|PID123|||Smith^John\r\n" +
                              "O|1|SAMPLE123||ABO|N|20250320120000||||||||CENTBLOOD\r\n" +
                              "L|";
        */

        /*
        private byte[] GetRequestFromDB(string RIDPar)
        {

            string user = "mielogrammauser";
            string password = "Qw123456";

            string connectionString = "Server=CGM-APP11\\SQLCGMAPP11;Database=KDLPROD;MultipleActiveResultSets=true;TrustServerCertificate=True;";
            connectionString = string.Concat(connectionString, $"User Id = {user}; Password = {password}");

            try
            {
                using (SqlConnection CGMconnection = new SqlConnection(connectionString))
                {
                    CGMconnection.Open();

                    // переменные для данных из CGM
                    string PID = "";
                    string PatientSurname = "";
                    string PatientName = "";
                    string FullName = "";
                    string PatientSex = "";
                    string PatientBirthDay = "";
                    string LISTestCode = "";
                    DateTime PatientBirthDayDate = new DateTime();
                    DateTime RegistrationDateDate = DateTime.Now;
                    DateTime SampleDateDate = DateTime.Now;
                    string SampleDate = "";
                    bool RIDExists = false;
                    // строка с заданиями для прибора
                    string orderString = "";
                    DateTime now = DateTime.Now;
                    string omlDate = now.ToString("yyyyMMddHHmmss");

                    List<string> testProfiles = new List<string>();

                    #region ищем RID и получаем данные по нему из БД
                    //ищем RID в базе
                    SqlCommand RequetDataCommand = new SqlCommand(
                       "SELECT TOP 1" +
                         "p.pop_pid AS PID, p.pop_enamn AS PatientSurname, p.pop_fnamn AS PatientName, p.pop_fdatum AS PatientBirthday, " +
                         "CASE WHEN p.pop_kon = 'K' THEN 'F' ELSE 'M' END AS PatientSex, " +
                         "r.rem_ank_dttm AS RegistrationDate " +
                       "FROM dbo.remiss (NOLOCK) r " +
                         "INNER JOIN dbo.pop (NOLOCK) p ON p.pop_pid = r.pop_pid " +
                       "WHERE r.rem_deaktiv = 'O' " +
                         $"AND r.rem_rid IN ('{RIDPar}') " +
                         "AND r.rem_ank_dttm IS NOT NULL ", CGMconnection);
                    SqlDataReader Reader = RequetDataCommand.ExecuteReader();

                    // если такой ШК есть
                    if (Reader.HasRows)
                    {
                        RIDExists = true;
                        // получаем данные по заявке
                        while (Reader.Read())
                        {
                            if (!Reader.IsDBNull(0)) { PID = Reader.GetString(0); }
                            if (!Reader.IsDBNull(1)) { PatientSurname = Reader.GetString(1); }
                            if (!Reader.IsDBNull(2)) { PatientName = Reader.GetString(2); }
                            if (!Reader.IsDBNull(3))
                            {
                                PatientBirthDayDate = Reader.GetDateTime(3);
                                //PatientBirthDay = PatientBirthDayDate.Year + CheckZero(PatientBirthDayDate.Month) + CheckZero(PatientBirthDayDate.Day);
                                PatientBirthDay = PatientBirthDayDate.Year + CheckZero(PatientBirthDayDate.Month) + CheckZero(PatientBirthDayDate.Day) + CheckZero(PatientBirthDayDate.Hour)
                                                  + CheckZero(PatientBirthDayDate.Minute) + CheckZero(PatientBirthDayDate.Second); ;
                            }
                            if (!Reader.IsDBNull(4)) { PatientSex = Reader.GetString(4); }

                            if (!Reader.IsDBNull(5))
                            {
                                RegistrationDateDate = Reader.GetDateTime(5);
                            }
                        }
                    }
                    Reader.Close();
                    #endregion

                    #region есть ли тесты в задании - формируем строку с тестами для отправки в сообщении 
                    // если шк есть, получаем тесты
                    if (RIDExists)
                    {
                        // в качестве задания нужно получить тесты которые не отвалидированы
                        // либо тесты с которых снята валидация (Reject) - b.bes_svarstat = 'U, 
                        // либо новые тесты (b.bes_svarstat IS NULL AND b.bes_antalomg = 0), зарегистрированные и без результата

                        SqlCommand TestCodeCommand = new SqlCommand(
                            "SELECT b.ana_analyskod, prov.pro_provdat " +
                            "FROM dbo.remiss (NOLOCK) r " +
                              "INNER JOIN dbo.bestall (NOLOCK) b ON b.rem_id = r.rem_id " +
                              "INNER JOIN dbo.prov (NOLOCK) prov ON prov.pro_id = b.pro_id " +
                            "WHERE r.rem_deaktiv = 'O' " +
                            $"AND r.rem_rid IN('{RIDPar}') " +
                            "AND r.rem_ank_dttm IS NOT NULL " +
                            "AND b.bes_t_dttm IS NULL " +  // bes_t_dttm дата теста, если ее нет, нет и результата, берем только эти тесты
                                                           //либо тесты с которых снята валидация (Reject), либо новые тесты (b.bes_svarstat IS NULL AND b.bes_antalomg = 0)
                            "AND (b.bes_svarstat = 'U' OR (b.bes_svarstat IS NULL AND b.bes_antalomg = 0))", CGMconnection);

                        SqlDataReader TestsReader = TestCodeCommand.ExecuteReader();

                        logger.LogExchange("RID exists.");

                        // Если задания есть
                        if (TestsReader.HasRows)
                        {
                            while (TestsReader.Read())
                            {
                                if (!TestsReader.IsDBNull(0))
                                {
                                    LISTestCode = TestsReader.GetString(0);
                                    // преобразуем код теста в код, понятный анализатору
                                    //string AnalyzerTestCode = TranslateToAnalyzerCodes(LISTestCode);

                                    testProfiles.Add(TestMatchWithProfiles(LISTestCode));
                                    if (AnalyzerTestCode != "")
                                    {
                                        logger.LogExchange($"Test code {LISTestCode} converted to {AnalyzerTestCode}");

                                        if (orderString == "")
                                        {
                                            orderString = OMLCreateTestString(AnalyzerTestCode, omlDate);
                                        }
                                        else
                                        {
                                            omlOrderString = omlOrderString + '\r' + OMLCreateTestString(AnalyzerTestCode, omlDate);
                                        }
                                    }
                                    else
                                    {
                                        ExchangeLog($"Test code {LISTestCode} could not be converted. The test is not configured to be transmitted to analyzer.");
                                    }

                                }
                                // Sample date from prov table
                                SampleDateDate = TestsReader.GetDateTime(1);
                                SampleDate = SampleDateDate.Year + CheckZero(SampleDateDate.Month) + CheckZero(SampleDateDate.Day) + CheckZero(SampleDateDate.Hour)
                                            + CheckZero(SampleDateDate.Minute) + CheckZero(SampleDateDate.Second);
                            }
                        }
                        TestsReader.Close();
                    }
                    #endregion
                }
            }
            catch(Exception ex)
            {
                
            }
        }

        private string CreateTestString(int i, string rid, string test)
        {
            // шаблон ASTM
            string Ostr = $@"O|{i}|{rid}||ABO|N|20210309142633|||||||||CENTBLOOD|||||||||||||||";
        }

        private string CreateResponseMessage()
        {

        }

        private string TestMatchWithProfiles(string test)
        {
            switch (test) 
            {
                case "Ф0210":
                    return "4_ABO/Rh";
                case "Ф0215":
                    return "4_ABO/Rh";
                case "Ф0218":
                    return "7_Pheno/Kell";
                case "Ф0219":
                    return "7_Pheno/Kell";
                default:
                    return null;
            }
        }

        // преобразование кода теста в код теста, понятный прибору, для отправки задания
        public static string TranslateToAnalyzerCodes(string CGMTestCodesPar)
        {
            string BackTestCode = "";
            try
            {
                string user = "mielogrammauser";
                string password = "Qw123456";

                string connectionString = "Server=CGM-APP11\\SQLCGMAPP11;Database=KDLPROD;MultipleActiveResultSets=true;TrustServerCertificate=True;";
                connectionString = string.Concat(connectionString, $"User Id = {user}; Password = {password}");
                using (SqlConnection Connection = new SqlConnection(connectionString))
                {
                    Connection.Open();
                    //ищем код теста в analyzer configuration
                    SqlCommand TestCodeCommand = new SqlCommand(
                       "SELECT TOP 1 k.amt_analyskod FROM konvana k " +
                       $"WHERE k.ins_maskin = '{AnalyzerConfigurationCode}' AND k.met_kod = '{CGMTestCodesPar}' ", Connection);
                    SqlDataReader Reader = TestCodeCommand.ExecuteReader();

                    if (Reader.HasRows) // если есть данные
                    {
                        while (Reader.Read())
                        {
                            if (!Reader.IsDBNull(0)) { BackTestCode = Reader.GetString(0); }
                            ;
                        }
                    }
                    Reader.Close();
                    Connection.Close();
                }
            }
            catch (Exception error)
            {
                ExchangeLog($"Error: {error}");
            }
            return BackTestCode;
        }

        */
        /*
        // отправка задания
        private async Task SendOrderToAnalyzerAsync(byte[] order)
        {
            await stream.WriteAsync(order, 0, 1, cts.Token);
            logger.LogExchange($"HOST: {Encoding.UTF8.GetString(order)}");
        }

        */
        #endregion

        #region Вспомогательные функции
        /// <summary>
        /// Создаем файл с результатом, отправленным анализатором
        /// </summary>
        /// 
        private void MakeAnalyzerResultFile(string AllMessagePar)
        {
            // папка для результатов анализатора
            string analyzerResultPath = Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, settings.analyzerName), "Results");

            if (!Directory.Exists(analyzerResultPath))
            {
                Directory.CreateDirectory(analyzerResultPath);
            }
            DateTime now = DateTime.Now;
            string filename = analyzerResultPath + "\\Results_" + ExtractSampleId(AllMessagePar) + "_" + now.Year + CheckZero(now.Month) + CheckZero(now.Day) + CheckZero(now.Hour) + CheckZero(now.Minute) + CheckZero(now.Second) + CheckZero(now.Millisecond) + ".res";
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

        static string TranslateBytes(byte BytePar)
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
                    return BytePar.ToString();
            }
        }

        /// <summary>
        /// Дописываем к номеру месяца ноль если нужно
        /// </summary>
        /// 
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

        // Функция преобразования кода теста прибора в код теста PSMV2 в CGM
        public static string TranslateToPSMCodes(string AnalyzerTestCodesPar)
        {
            string BackTestCode = "";
            try
            {
                string user = "mielogrammauser";
                string password = "Qw123456";

                string connectionString = "Server=CGM-APP11\\SQLCGMAPP11;Database=KDLPROD;MultipleActiveResultSets=true;TrustServerCertificate=True;";
                connectionString = string.Concat(connectionString, $"User Id = {user}; Password = {password}");

                using (SqlConnection Connection = new SqlConnection(connectionString))
                {
                    Connection.Open();
                    // Ищем только тесты, которые настроены для прибора exias и настроены для PSMV2
                    SqlCommand TestCodeCommand = new SqlCommand(
                       "SELECT k1.amt_analyskod  FROM konvana k " +
                       "LEFT JOIN konvana k1 ON k1.met_kod = k.met_kod AND k1.ins_maskin = 'PSMV2' " +
                       $"WHERE k.ins_maskin = '{AnalyzerConfigurationCode}' AND k.amt_analyskod = '{AnalyzerTestCodesPar}' ", Connection);
                    SqlDataReader Reader = TestCodeCommand.ExecuteReader();

                    if (Reader.HasRows) // если есть данные
                    {
                        while (Reader.Read())
                        {
                            if (!Reader.IsDBNull(0)) { BackTestCode = Reader.GetString(0); }
                        }
                    }
                    Reader.Close();
                    Connection.Close();
                }
            }
            catch (Exception error)
            {
                
            }

            return BackTestCode;
        }
        #endregion

        /// <summary>
        /// Обрабатывает файлы с результатами и создает файлов для службы, которая разберет файл и запишет данные в CGM
        /// </summary>
        public async Task ResultsHandlerAsync(CancellationToken cancellationToken)
        {
            logger.LogResult("Начало работы обработчика файлов с результатами.");

            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    #region папки архива, результатов и ошибок

                    string? OutFolder = "E:\\Services\\FileGetterService\\Exchange";

                    // папка для результатов анализатора
                    string analyzerResultPath = Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, settings.analyzerName), "Results");

                    //string OutFolder = analyzerResultPath + @"\CGM";

                    // архивная папка
                    string ArchivePath = analyzerResultPath + @"\Archive";
                    // папка для ошибок
                    string ErrorPath = analyzerResultPath + @"\Error";
                    // папка для файлов с результатами для CGM
                    string CGMPath = analyzerResultPath + @"\CGM";

                    if (!Directory.Exists(ArchivePath))
                    {
                        Directory.CreateDirectory(ArchivePath);
                    }

                    if (!Directory.Exists(ErrorPath))
                    {
                        Directory.CreateDirectory(ErrorPath);
                    }

                    if (!Directory.Exists(CGMPath))
                    {
                        Directory.CreateDirectory(CGMPath);
                    }
                    #endregion

                    // строки для формирования файла (psm файла) с результатами для службы,
                    // которая разбирает файлы и записывает результаты в CGM
                    string MessageHead = "";
                    string MessageTest = "";
                    string AllMessage = "";

                    // поолучаем список всех файлов в текущей папке
                    string[] Files = Directory.GetFiles(analyzerResultPath, "*.res");

                    // шаблоны регулярных выражений для поиска данных
                    string RIDPattern = @"\d+O\|(?:\d+)\|([^|]+)\|";

                    string TestAndResultPattern = @"\d*R\|(?:\d+)\|([^|]+)\|([^|]+)\|";

                    //string TestPattern = @"OBX[|]\d+[|]ST[|](?<Test>.+)[@]99ABT[|][0-9]";
                    //string ResultPattern = @"OBX[|]\d+[|]ST[|].*[|][0-9][|](?<Result>[<>]?\s?\S+)[|]\S+UCUM";

                    // пробегаем по файлам
                    foreach (string file in Files)
                    {
                        logger.LogResult(file);

                        string[] lines = System.IO.File.ReadAllLines(file);
                        string RID = "";
                        string Test = "";

                        // обнулим переменные
                        MessageHead = "";
                        MessageTest = "";

                        // обрезаем только имя текущего файла
                        string FileName = file.Substring(analyzerResultPath.Length + 1);
                        // название файла .ок, который должен создаваться вместе с результирующим для обработки службой FileGetterService
                        string OkFileName = "";

                        // проходим по строкам в файле
                        foreach (string line in lines)
                        {
                            var RIDMatch = Regex.Match(line, RIDPattern);
                            if (RIDMatch.Success) 
                            {
                                RID = RIDMatch.Groups[1].Value;
                                logger.LogResult($"Заявка № {RID}");
                                MessageHead = $"O|1|{RID}||ALL|R|{DateTime.Now.ToString("yyyyMMddHHmmss")}|||||X||||ALL||||||||||F";
                            }

                            var TestAndResultMatch = Regex.Match(line, TestAndResultPattern);
                            if (TestAndResultMatch.Success)
                            {
                                Test = TestAndResultMatch.Groups[1].Value;

                                string PSMTestCode = TranslateToPSMCodes(Test);
                                string Result = TestAndResultMatch.Groups[2].Value; ;

                                if (PSMTestCode == "")
                                {
                                    logger.LogResult($"Код анализатора {Test} не интерпретирован в PSMV2 код.");
                                    logger.LogResult($"{Test} - результат: {Result}");
                                }
                                else
                                {
                                    logger.LogResult($"PSMV2 код: {PSMTestCode}");
                                    logger.LogResult($"{Test} - результат: {Result}");
                                }

                                // если код тест был интерпретирован
                                if ((PSMTestCode != "") && (Result != ""))
                                {
                                    // формируем строку с ответом для результирующего файла
                                    MessageTest = MessageTest + $"R|1|^^^{PSMTestCode}^^^^{AnalyzerCode}|{ResultInterpretation(Result)}|||N||F||ORTHOVISION^||20260101000001|{AnalyzerCode}" + "\r";
                                }
                            }

                            
                        }

                        // получаем название файла .ок на основании файла с результатом
                        if (FileName.IndexOf(".") != -1)
                        {
                            OkFileName = FileName.Split('.')[0] + ".ok";
                        }

                        // если строки с результатами и с ШК не пустые, значит формируем результирующий файл
                        if (MessageHead != "" && MessageTest != "")
                        {
                            try
                            {
                                // собираем полное сообщение с результатом
                                AllMessage = MessageHead + "\r" + MessageTest;

                                logger.LogResult(AllMessage);

                                /*
                                // создаем файл для записи результата в папке для рез-тов
                                if (!File.Exists(OutFolder + @"\" + FileName))
                                {
                                    using (StreamWriter sw = File.CreateText(OutFolder + @"\" + FileName))
                                    {
                                        foreach (string msg in AllMessage.Split('\r'))
                                        {
                                            sw.WriteLine(msg);
                                        }
                                    }
                                }
                                else
                                {
                                    File.Delete(OutFolder + @"\" + FileName);
                                    using (StreamWriter sw = File.CreateText(OutFolder + @"\" + FileName))
                                    {
                                        foreach (string msg in AllMessage.Split('\r'))
                                        {
                                            sw.WriteLine(msg);
                                        }
                                    }
                                }
                                */

                                string fullPath = Path.Combine(OutFolder, FileName);

                                using (StreamWriter sw = new StreamWriter(fullPath, false, Encoding.GetEncoding(1251)))
                                {
                                    foreach (string msg in AllMessage.Split('\r'))
                                    {
                                        sw.WriteLine(msg);
                                    }
                                }

                                /*
                                // создаем файл для записи результата в папке для рез-тов
                                if (!File.Exists(OutFolder + @"\" + FileName))
                                {
                                    using (StreamWriter sw = new StreamWriter(fullPath, false, Encoding.GetEncoding(1251)))
                                    {
                                        foreach (string msg in AllMessage.Split('\r'))
                                        {
                                            sw.WriteLine(msg);
                                        }
                                    }
                                }
                                else
                                {
                                    File.Delete(OutFolder + @"\" + FileName);
                                    using (StreamWriter sw = new StreamWriter(fullPath, false, Encoding.GetEncoding(1251)))
                                    {
                                        foreach (string msg in AllMessage.Split('\r'))
                                        {
                                            sw.WriteLine(msg);
                                        }
                                    }
                                }
                                */


                                // создаем .ok файл в папке для рез-тов
                                if (OkFileName != "")
                                {
                                    if (!File.Exists(OutFolder + @"\" + OkFileName))
                                    {
                                        using (StreamWriter sw = File.CreateText(OutFolder + @"\" + OkFileName))
                                        {
                                            sw.WriteLine("ok");
                                        }
                                    }
                                    else
                                    {
                                        File.Delete(OutFolder + OkFileName);
                                        using (StreamWriter sw = File.CreateText(OutFolder + @"\" + OkFileName))
                                        {
                                            sw.WriteLine("ok");
                                        }
                                    }
                                }

                                // помещение файла в архивную папку
                                if (File.Exists(ArchivePath + @"\" + FileName))
                                {
                                    File.Delete(ArchivePath + @"\" + FileName);
                                }
                                File.Move(file, ArchivePath + @"\" + FileName);

                                logger.LogResult("Файл обработан и перемещен в папку Archive");
                                logger.LogResult("");
                            }
                            catch (Exception ex)
                            {
                                logger.LogResult(ex.ToString());
                                // помещение файла в папку с ошибками
                                if (File.Exists(ErrorPath + @"\" + FileName))
                                {
                                    File.Delete(ErrorPath + @"\" + FileName);
                                }
                                File.Move(file, ErrorPath + @"\" + FileName);

                                logger.LogResult("Ошибка обработки файла. Файл перемещен в папку Error");
                                logger.LogResult("");
                            }
                        }
                        else
                        {
                            // помещение файла в папку с ошибками
                            if (File.Exists(ErrorPath + @"\" + FileName))
                            {
                                File.Delete(ErrorPath + @"\" + FileName);
                            }
                            File.Move(file, ErrorPath + @"\" + FileName);

                            logger.LogResult("Ошибка обработки файла. Файл перемещен в папку Error");
                            logger.LogResult("");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    logger.LogResult($"Обработчик результатов остановлен по сигналу отмены.");
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogResult($"Ошибка: {ex}");
                }

                await Task.Delay(3000, cts.Token);
            }
        }

        public static string ResultInterpretation(string AnalyzerResult)
        {
            switch (AnalyzerResult)
            {
                case "CCEE": return "C(+)c(-)E(+)e(-)";
                case "CCEe": return "C(+)c(-)E(+)e(+)";
                case "CCeE": return "C(+)c(-)E(-)e(-)";
                case "CCee": return "C(+)c(-)E(-)e(+)";

                case "CcEE": return "C(+)c(+)E(+)e(-)";
                case "CcEe": return "C(+)c(+)E(+)e(+)";
                case "CceE": return "C(+)c(+)E(-)e(-)";
                case "Ccee": return "C(+)c(+)E(-)e(+)";

                case "cCEE": return "C(-)c(-)E(+)e(-)";
                case "cCEe": return "C(-)c(-)E(+)e(+)";
                case "cCeE": return "C(-)c(-)E(-)e(-)";
                case "cCee": return "C(-)c(-)E(-)e(+)";

                case "ccEE": return "C(-)c(+)E(+)e(-)";
                case "ccEe": return "C(-)c(+)E(+)e(+)";
                case "cceE": return "C(-)c(+)E(-)e(-)";
                case "ccee": return "C(-)c(+)E(-)e(+)";
                case "A": return "Группа А (II)";
                case "AB": return "Группа АВ (IV)";
                case "B": return "Группа В (III)";
                case "O": return "Группа 0 (I)";
                case "A2": return "Группа А2(II)";
                case "A2B": return "Группа A2B(IV)";
                case "Поз.": return "положительный";
                case "Нег.": return "Отрицательный";
                case "POS": return "Положительный";
                case "NEG": return "Отрицательный";
                default: return AnalyzerResult;
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
