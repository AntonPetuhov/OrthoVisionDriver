using OrthoVisionDriver.Interfaces;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OrthoVision
{
    public class TcpHost : ITcpHost
    {
        private TcpListener listener;
        private readonly ILoggerService logger;
        private bool isStarted;

        public TcpHost(ILoggerService logger)
        {
            this.logger = logger;
        }

        /// <summary>
        /// Запуск TCP сервера
        /// </summary>
        public void Start(IPAddress ipAddress, int port)
        {
            listener = new TcpListener(ipAddress, port);
            listener.Start();
            isStarted = true;
            logger.LogTcp($"TCP сервер запущен на  {ipAddress} : {port}. Ожидание подключений...");
        }

        /// <summary>
        /// получает подключения клиента (прибора), если tcp сервер запущен
        /// </summary>
        public async Task<TcpClient> AcceptClientAsync(CancellationToken cancellationToken)
        {
            if (!isStarted) throw new InvalidOperationException("TCP сервер не запущен");
            return await listener.AcceptTcpClientAsync(cancellationToken);
        }

        public void Stop()
        {
            listener?.Stop();
            isStarted = false;
            logger.LogTcp("TCP сервер остановлен");
        }

        public void Dispose() 
        {
            Stop();
        } 
    }
}
