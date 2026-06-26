using Microsoft.Data.SqlClient;
using OrthoVisionDriver.Interfaces;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace OrthoVision
{
    public class AstmProtocolParser : IAstmProtocolParser
    {
        private readonly ILoggerService logger;
        private IDBProvider dbProvider;

        #region Управляющие символы ASTM
        private const byte ENQ = 0x05;
        private const byte ACK = 0x06;
        private const byte NAK = 0x15;
        private const byte EOT = 0x04;
        private const byte STX = 0x02;
        private const byte ETX = 0x03;
        private const byte CR = 0x0D;
        private const byte LF = 0x0A;
        #endregion

        public AstmProtocolParser(ILoggerService logger)
        {
            this.logger = logger;
        }

        #region Работа с накопленными данными, проверка контрольной суммы, формирование сообщения с заданием

        #region Извлекаем полный фрейм из накопителя
        /// <summary>
        /// Пытаемся извлечь один полный фрейм из начала потока-накопителя.
        /// Каждый фрейм: STX ... ETX + Checksum (2 hex) + CR + LF
        /// Возвращает true и байты фрейма (включая STX, ETX и 2 байта контрольной суммы),
        /// и удаляет эти байты из накопителя.
        /// </summary>
        public bool TryExtractFullFrame(MemoryStream accumulator, out byte[] frame)
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
        #endregion

        #region Проверка контрольной суммы
        /// <summary>
        /// Проверка контрольной суммы ASTM.
        /// Фрейм: STX + данные + ETX + два ASCII символа (hex) – арифметическая сумма всех байт от STX+1 до ETX включительно. (хотя обычно XOR)
        /// Затем из этой суммы берётся младший байт (что эквивалентно остатку от деления на 256 - mod 256), т.к. сумма байт может быть больше 255, но 
        /// для передачи контрольной суммы выделен один байт (два hex-символа). Один байт может хранить значения только от 0 до 255.
        /// </summary>
        /// 
        public bool VerifyChecksum(byte[] rawFrame)
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
            byte calculatedChecksum = CalculateChecksum(rawFrame, 1, etxPos); // от после STX до ETX включительно

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

        #region Вычисление контрольной суммы
        public byte CalculateChecksum(byte[] data, int start, int end)
        {
            byte checksum = 0;
            for (int i = start; i <= end; i++)
                checksum += data[i];
            return (byte)(checksum & 0xFF); // младший байт = calculatedChecksum % 256
        }
        #endregion

        /// <summary>
        /// HEX to byte
        /// </summary>
        private byte HexCharToByte(char c)
        {
            if (c >= '0' && c <= '9') return (byte)(c - '0');
            if (c >= 'A' && c <= 'F') return (byte)(c - 'A' + 10);
            if (c >= 'a' && c <= 'f') return (byte)(c - 'a' + 10);
            throw new ArgumentException("Недопустимый символ в контрольной сумме");
        }

        #endregion

        #region Работа с полученным от анализатора сообщением, определение типа

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
            logger.LogExchange($"func extract {message}");
            // Пробуем найти Q запись
            //var qMatch = Regex.Match(message, @"^Q\|[^|]*\|([^|]*)", RegexOptions.Multiline);
            var qMatch = Regex.Match(message, @"Q\|[^|]*\|([^|]*)", RegexOptions.Multiline);
            //var qMatch = Regex.Match(message, @"Q\\|[^|]*\\|\\^?(\\d+)", RegexOptions.Multiline); // здесь отмекаем

            logger.LogExchange($"qMatch.Success: {qMatch.Success}");

            if (qMatch.Success)
            {
                string specimenId = qMatch.Groups[1].Value;
                // Может содержать компоненты через ^, берём первый
                //return specimenId.Split('^')[0];
                return specimenId.Split('^')[1];
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

        #endregion

    }
}
