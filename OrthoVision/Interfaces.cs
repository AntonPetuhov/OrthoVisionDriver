using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OrthoVision
{
    // Парсер протокола ASTM (работа с фреймами, управляющими символами, контрольными суммами)
    public interface IAstmProtocolParser
    {
        //bool TryExtractFullFrame(byte[] buffer, int length, out byte[] rawFrame);
        bool TryExtractFullFrame(MemoryStream accumulator, out byte[] rawFrame);

        bool VerifyChecksum(byte[] rawFrame);
        byte CalculateChecksum(byte[] data, int start, int end);

        //byte[] BuildFrame(byte[] data); // для отправки ответов

        //bool IsControlByte(byte b);
        //string ControlByteToString(byte b);
        bool IsHostQuery(string message);
        bool IsResultMessage(string message);
        string ExtractSampleId(string message);

        //string ProcessReceivedFullMessage(string message);

    }

    // TCP-хост, управляющий подключениями анализатора
    public interface ITcpHost : IDisposable
    {
        void Start(IPAddress ipAddress, int port);
        Task<TcpClient> AcceptClientAsync(CancellationToken cancellationToken);
        void Stop();
    }

    // Работа с БД
    public interface IDBProvider
    {
        //Task<OrderData> GetOrderForSampleAsync(string sampleId, CancellationToken cancellationToken);
        string TranslateLISCodeToProfileCode(string lisTestCode);
        string TranslateToPSMCodes(string analyzerTestCode);

        //string GetRequestFromDB(string rid);
        OrderData GetRequestFromDB(string rid);

    }

    // Обработчик результатов (парсинг, создание выходных файлов)
    public interface IResultHandler
    {
        Task ProcessResultFileAsync(string filePath, CancellationToken cancellationToken);
        void StartMonitoring(CancellationToken cancellationToken);
    }
}
